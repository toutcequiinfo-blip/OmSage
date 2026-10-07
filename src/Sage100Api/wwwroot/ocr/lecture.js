// Lecture d'un document par modèle : mots reconnus (OCR ou texte du PDF) → texte de chaque zone → valeurs (numéro, date, montants).
// Fonctions pures, sans accès à la page : testables à part.

/** Minuscules, sans accents ni espaces ni ponctuation : « N° FA-001 » → « nfa001 ». */
export function normaliser(texte) {
  return String(texte ?? "").normalize("NFD").replace(/[̀-ͯ]/g, "").toLowerCase().replace(/[^a-z0-9]/g, "");
}

/** Texte complet de la page, dans l'ordre de lecture des mots. */
export const texteComplet = (mots) => mots.map((m) => m.texte).join(" ");

/** Mots du texte d'identification, normalisés, avec leur position (en caractères) dans le texte normalisé. */
function motsIdentification(identification) {
  let pos = 0;
  return String(identification ?? "").split(/\s+/).map((m) => normaliser(m)).filter(Boolean).map((n) => {
    const r = { n, pos };
    pos += n.length;
    return r;
  });
}

/**
 * Ressemblance entre le texte d'identification et la page : 1 s'il s'y trouve tel quel, sinon la part de ses caractères
 * portée par des mots retrouvés (l'OCR lit parfois mal un chiffre ou deux : « 46101112010 » lu « 461011120110 »).
 */
export function ressemblance(identification, pageNormalisee) {
  const cible = normaliser(identification);
  if (cible.length < 3) return 0;
  if (pageNormalisee.includes(cible)) return 1;
  const mots = motsIdentification(identification).filter((m) => m.n.length >= 2);
  const trouves = mots.filter((m) => pageNormalisee.includes(m.n));
  // Au moins un mot distinctif (4 caractères ou plus) doit être retrouvé.
  if (!trouves.some((m) => m.n.length >= 4)) return 0;
  return trouves.reduce((s, m) => s + m.n.length, 0) / cible.length;
}

/**
 * Modèle qui reconnaît la page : son texte d'identification s'y retrouve, tel quel ou au moins à moitié.
 * À égalité, le texte retrouvé le plus long (le plus précis), puis le modèle le plus utilisé.
 */
export function choisirModele(modeles, mots) {
  const page = normaliser(texteComplet(mots));
  return modeles
    .map((m) => ({ m, r: ressemblance(m.identification, page) }))
    .filter((x) => x.r >= 0.5)
    .sort((a, b) => b.r * normaliser(b.m.identification).length - a.r * normaliser(a.m.identification).length || (b.m.utilisations ?? 0) - (a.m.utilisations ?? 0))[0]?.m ?? null;
}

/**
 * Cadre (en fractions de la page) des mots qui portent le texte d'identification : la plus courte suite de mots qui le contient.
 * Sert d'ancre pour recaler les zones d'un scan décalé.
 */
export function trouverAncre(mots, identification, largeur, hauteur) {
  const cible = normaliser(identification);
  if (!cible) return null;
  let meilleur = null;
  for (let i = 0; i < mots.length; i++) {
    let texte = "";
    for (let j = i; j < mots.length && j < i + 30; j++) {
      texte += normaliser(mots[j].texte);
      if (texte.includes(cible)) {
        if (!meilleur || j - i < meilleur[1] - meilleur[0]) meilleur = [i, j];
        break;
      }
      if (texte.length > cible.length * 3 + 20) break;
    }
  }
  if (!meilleur) return null;
  const choisis = mots.slice(meilleur[0], meilleur[1] + 1);
  const x0 = Math.min(...choisis.map((m) => m.x0)), y0 = Math.min(...choisis.map((m) => m.y0));
  const x1 = Math.max(...choisis.map((m) => m.x1)), y1 = Math.max(...choisis.map((m) => m.y1));
  return { x: x0 / largeur, y: y0 / hauteur, largeur: (x1 - x0) / largeur, hauteur: (y1 - y0) / hauteur };
}

/**
 * Décalage à appliquer aux zones du modèle : écart entre l'ancre du modèle et le texte d'identification sur la page.
 * Si l'OCR n'a pas relu ce texte à l'identique, on se repère sur son mot le plus long retrouvé, placé au prorata dans l'ancre.
 */
export function decalage(modele, mots, largeur, hauteur) {
  const zero = { dx: 0, dy: 0 };
  if (!modele?.ancre) return zero;
  const ancre = modele.ancre;
  let dx, dy;
  const a = trouverAncre(mots, modele.identification, largeur, hauteur);
  if (a) {
    dx = a.x - ancre.x; dy = a.y - ancre.y;
  } else {
    const ids = motsIdentification(modele.identification);
    const total = ids.reduce((s, m) => s + m.n.length, 0) || 1;
    let repere = null;
    for (const id of ids.filter((m) => m.n.length >= 4).sort((x, y) => y.n.length - x.n.length)) {
      const attenduX = ancre.x + (id.pos / total) * ancre.largeur;
      // Parmi les mots identiques, le plus proche de la place attendue.
      const candidat = mots.filter((m) => normaliser(m.texte) === id.n)
        .map((m) => ({ dx: m.x0 / largeur - attenduX, dy: m.y0 / hauteur - ancre.y }))
        .sort((p, q) => Math.hypot(p.dx, p.dy) - Math.hypot(q.dx, q.dy))[0];
      if (candidat) { repere = candidat; break; }
    }
    if (!repere) return zero;
    ({ dx, dy } = repere);
  }
  // Un écart énorme veut dire que le texte a été trouvé ailleurs (pied de page...) : on ne recale pas.
  return Math.abs(dx) > 0.25 || Math.abs(dy) > 0.25 ? zero : { dx, dy };
}

/** Mots dont le centre tombe dans la zone (fractions de la page), rangés par ligne puis de gauche à droite. */
export function motsDansZone(mots, zone, largeur, hauteur, { dx = 0, dy = 0 } = {}) {
  const x0 = (zone.x + dx) * largeur, y0 = (zone.y + dy) * hauteur;
  const x1 = x0 + zone.largeur * largeur, y1 = y0 + zone.hauteur * hauteur;
  const dedans = mots.filter((m) => {
    const cx = (m.x0 + m.x1) / 2, cy = (m.y0 + m.y1) / 2;
    return cx >= x0 && cx <= x1 && cy >= y0 && cy <= y1;
  });
  // Lignes : mots dont les centres verticaux sont proches (moins d'une demi-hauteur de mot).
  const lignes = [];
  for (const m of dedans.sort((a, b) => (a.y0 + a.y1) - (b.y0 + b.y1))) {
    const cy = (m.y0 + m.y1) / 2, h = Math.max(1, m.y1 - m.y0);
    const ligne = lignes.find((l) => Math.abs(l.cy - cy) < Math.max(h, l.h) / 2);
    if (ligne) ligne.mots.push(m); else lignes.push({ cy, h, mots: [m] });
  }
  return lignes.map((l) => l.mots.sort((a, b) => a.x0 - b.x0));
}

export function texteZone(mots, zone, largeur, hauteur, deca) {
  return motsDansZone(mots, zone, largeur, hauteur, deca).map((l) => l.map((m) => m.texte).join(" ")).join("\n").trim();
}

// ---------- Valeurs ----------

// Confusions courantes de l'OCR dans les nombres : O pour 0, l ou I pour 1, S pour 5, B pour 8.
const corrigerChiffres = (t) => t.replace(/(?<=[\d\s.,])[Oo](?=[\d\s.,]|$)|^[Oo](?=\d)/g, "0").replace(/(?<=\d)[lI|](?=\d)|(?<=\d[\s.,]?)[lI](?=\d)/g, "1")
  .replace(/(?<=\d)S(?=\d)/g, "5").replace(/(?<=\d)B(?=\d)/g, "8");

/**
 * Montant écrit à la française ou à l'anglaise : « 1 234 567,00 », « 1.234.567,00 », « 1,234,567.00 », « 1234567 Ar ».
 * Plusieurs nombres dans la zone (« Total HT 1 200 000 ») : le dernier, avec ses groupes de milliers séparés par des espaces,
 * même mal découpés par l'OCR (« 1200 000,00 »).
 */
export function lireMontant(texte) {
  const t = corrigerChiffres(String(texte ?? "").replace(/[\u00a0\u202f]/g, " "));
  const morceaux = [...t.matchAll(/\d[\d.,']*/g)].map((m) => ({ texte: m[0].replace(/[.,']+$/, ""), debut: m.index, fin: m.index + m[0].length }));
  if (!morceaux.length) return null;
  let i = morceaux.length - 1, s = morceaux[i].texte;
  // On remonte tant que le morceau de gauche commence par un groupe de 3 chiffres et n'est séparé du précédent que par des espaces.
  while (i > 0 && /^\d{3}(?:[.,']|$)/.test(morceaux[i].texte) && /^ +$/.test(t.slice(morceaux[i - 1].fin, morceaux[i].debut))) {
    i--;
    s = morceaux[i].texte + s;
  }
  const negatif = /-\s*$/.test(t.slice(0, morceaux[i].debut));
  s = s.replace(/'/g, "");
  const virgule = s.lastIndexOf(","), point = s.lastIndexOf(".");
  if (virgule >= 0 && point >= 0) {
    // Le dernier des deux est le séparateur décimal.
    s = virgule > point ? s.replace(/\./g, "").replace(",", ".") : s.replace(/,/g, "");
  } else if (virgule >= 0 || point >= 0) {
    const sep = virgule >= 0 ? "," : ".";
    const parts = s.split(sep);
    // Un seul séparateur suivi de 1 ou 2 chiffres : décimales ; groupes de 3 chiffres : milliers.
    s = parts.length === 2 && parts[1].length <= 2 ? parts.join(".") : parts.join("");
  }
  const n = Number(s);
  return Number.isFinite(n) ? (negatif ? -n : n) : null;
}

const MOIS = ["janvier", "fevrier", "mars", "avril", "mai", "juin", "juillet", "aout", "septembre", "octobre", "novembre", "decembre"];

/** Date « 07/10/2026 », « 7-10-26 », « 07.10.2026 », « 2026-10-07 » ou « 7 octobre 2026 » → « 2026-10-07 ». */
export function lireDate(texte) {
  const t = corrigerChiffres(String(texte ?? ""));
  const iso = (a, m, j) => {
    const an = a < 100 ? 2000 + a : a;
    if (m < 1 || m > 12 || j < 1 || j > 31 || an < 1990 || an > 2100) return null;
    const d = new Date(Date.UTC(an, m - 1, j));
    return d.getUTCDate() === j ? d.toISOString().slice(0, 10) : null;
  };
  let r = t.match(/(\d{4})[-/.](\d{1,2})[-/.](\d{1,2})/);
  if (r) return iso(+r[1], +r[2], +r[3]);
  r = t.match(/(\d{1,2})\s*[-/.]\s*(\d{1,2})\s*[-/.]\s*(\d{2,4})/);
  if (r) return iso(+r[3], +r[2], +r[1]);
  const sansAccent = t.normalize("NFD").replace(/[̀-ͯ]/g, "").toLowerCase();
  r = sansAccent.match(/(\d{1,2})(?:er)?\s+([a-z]+)\.?\s+(\d{2,4})/);
  if (r) {
    const m = MOIS.findIndex((x) => x.startsWith(r[2].slice(0, 3)) && (r[2].length <= x.length));
    if (m >= 0) return iso(+r[3], m + 1, +r[1]);
  }
  return null;
}

/** Numéro de document : sans les libellés (« Facture N° : », « Réf. ») ; le premier mot qui contient un chiffre et ce qui le suit sans espace. */
export function lireNumero(texte) {
  let t = String(texte ?? "").replace(/\n/g, " ").trim();
  if (t.includes(":")) t = t.slice(t.lastIndexOf(":") + 1).trim();
  t = t.replace(/^(?:(?:facture|invoice|bon|de|commande|n[°o]|no\.?|num[ée]ro|r[ée]f(?:[ée]rence)?\.?|pi[èe]ce)\s*[:.#-]?\s*)+/i, "").trim();
  const mot = t.split(/\s+/).find((m) => /\d/.test(m));
  return (mot ?? t).replace(/^[^A-Za-z0-9]+|[^A-Za-z0-9]+$/g, "") || null;
}

/** Valeur d'un champ à partir du texte de sa zone. */
export function lireChamp(champ, texte) {
  if (["montantHT", "montantTVA", "montantTTC"].includes(champ)) return lireMontant(texte);
  if (champ === "date") return lireDate(texte);
  if (champ === "numero") return lireNumero(texte);
  return String(texte ?? "").replace(/\s+/g, " ").trim() || null;
}

/** Montants complétés et contrôlés : TVA = TTC − HT quand elle manque ; écart si HT + TVA ≠ TTC (tolérance 1). */
export function controlerMontants({ montantHT, montantTVA, montantTTC }) {
  const ht = montantHT ?? null, ttc = montantTTC ?? null;
  let tva = montantTVA ?? null;
  if (tva == null && ht != null && ttc != null) tva = Math.round((ttc - ht) * 100) / 100;
  const ecart = ht != null && ttc != null ? Math.abs(ht + (tva ?? 0) - ttc) : null;
  return { montantHT: ht, montantTVA: tva, montantTTC: ttc, ecart: ecart != null && ecart > 1 ? ecart : 0 };
}

/**
 * Mots d'une page PDF à partir de son texte (pdf.js getTextContent), dans le repère de l'image rendue.
 * Chaque morceau de texte est découpé en mots, la largeur répartie au prorata des caractères.
 */
export function motsDuPdf(items, viewport, Util) {
  const mots = [];
  for (const it of items) {
    if (!it.str || !it.str.trim()) continue;
    const tx = Util.transform(viewport.transform, it.transform);
    const h = Math.hypot(tx[2], tx[3]);
    const x = tx[4], yBas = tx[5];
    const largeur = it.width * viewport.scale;
    const n = it.str.length;
    let pos = 0;
    for (const morceau of it.str.split(/(\s+)/)) {
      if (morceau.trim()) {
        const x0 = x + (pos / n) * largeur, x1 = x + ((pos + morceau.length) / n) * largeur;
        mots.push({ texte: morceau, x0, y0: yBas - h, x1, y1: yBas, confiance: 100 });
      }
      pos += morceau.length;
    }
  }
  // Ordre de lecture : par ligne, puis de gauche à droite.
  return mots.sort((a, b) => (Math.abs(a.y1 - b.y1) < 3 ? a.x0 - b.x0 : a.y1 - b.y1));
}

/** Mots du résultat de Tesseract (blocs → paragraphes → lignes → mots). */
export function motsDeTesseract(data) {
  const mots = [];
  for (const b of data.blocks ?? [])
    for (const p of b.paragraphs ?? [])
      for (const l of p.lines ?? [])
        for (const w of l.words ?? [])
          if (w.text?.trim()) mots.push({ texte: w.text.trim(), x0: w.bbox.x0, y0: w.bbox.y0, x1: w.bbox.x1, y1: w.bbox.y1, confiance: w.confidence });
  return mots;
}
