// Lecture de documents scannés par modèles : l'OCR (Tesseract) et la lecture des PDF (pdf.js) se font dans le navigateur, sans Internet.
// Un modèle par format de document (facture d'un fournisseur, bon de commande d'un client) : texte qui le reconnaît et zone de chaque champ.
// Le document validé est gardé par l'API (base des extensions) en attente d'écriture dans Sage ; rien n'est écrit dans Sage ici.

import { preparerChoix, societeChoisie, nomSociete } from "../commun/societes.js";
import { choisirModele, decalage, texteZone, lireChamp, lireMontant, controlerMontants, trouverAncre, motsDuPdf, motsDeTesseract } from "./lecture.js";

const CLE_REGLAGES = "ocr.reglages";
const CLE_SESSION = "ocr.session";
const CHAMPS = ["numero", "date", "montantHT", "montantTVA", "montantTTC"];
const NOMS = { identification: "Identification", numero: "N°", date: "Date", montantHT: "HT", montantTVA: "TVA", montantTTC: "TTC" };
const MONTANTS = ["montantHT", "montantTVA", "montantTTC"];
const STATUTS = { "a-ecrire": "À écrire dans Sage", ecrit: "Écrit dans Sage", rejete: "Rejeté" };
const TYPES = { "facture-fournisseur": "Facture fournisseur", "commande-client": "Commande client" };
const LIB = new URL("lib/", location.href).href;

const $ = (s) => document.querySelector(s);
const form = $("#form-document");
const etat = {
  onglet: "lire", modeles: [], modele: null, referentiels: null,
  fichier: null, mots: [], largeur: 0, hauteur: 0,
  zones: {}, marquage: false, champActif: null, documents: [],
};

// ---------- Stockage local (protégé : navigation privée ou stockage bloqué) ----------
function lireJson(cle, defaut) {
  try { const v = localStorage.getItem(cle); return v == null ? defaut : JSON.parse(v); } catch { return defaut; }
}
function ecrireJson(cle, valeur) {
  try { valeur == null ? localStorage.removeItem(cle) : localStorage.setItem(cle, JSON.stringify(valeur)); } catch { /* ignoré */ }
}
/** La clé d'API est reprise de la borne ou du CRM s'ils ont déjà été réglés sur ce poste. */
function cleApi() {
  return lireJson(CLE_REGLAGES, null)?.cle || lireJson("crm.reglages", null)?.cle || lireJson("borne.reglages", null)?.cle || "";
}
function session() {
  const s = lireJson(CLE_SESSION, null);
  return s && (!s.expiration || new Date(s.expiration) > new Date()) ? s : null;
}

// ---------- API ----------
class ErreurApi extends Error { constructor(message, statut, donnees) { super(message); this.statut = statut; this.donnees = donnees; } }

function entetes(json) {
  const s = session();
  return {
    "X-Api-Key": cleApi(),
    ...(s?.jeton ? { Authorization: `Bearer ${s.jeton}` } : {}),
    ...(s?.dossier ? { "X-Dossier": s.dossier } : {}),
    ...(json ? { "Content-Type": "application/json" } : {}),
  };
}

async function api(methode, chemin, corps) {
  let r;
  try {
    r = await fetch(`/api/v1${chemin}`, { method: methode, headers: entetes(!!corps), body: corps ? JSON.stringify(corps) : undefined, cache: "no-store" });
  } catch {
    throw new ErreurApi("Serveur injoignable. Vérifiez le réseau.", 0);
  }
  let donnees = null;
  try { donnees = await r.json(); } catch { /* corps vide */ }
  if (r.ok) return donnees;
  if (r.status === 401 && donnees?.erreur && !donnees?.code) {
    $("#form-reglages").cle.value = cleApi();
    afficher("reglages");
    throw new ErreurApi("Clé d'API refusée : vérifiez-la.", 401);
  }
  if ((r.status === 401 && donnees?.code === "CONNEXION_REQUISE") || ["DOSSIER_REQUIS", "DOSSIER_DIFFERENT"].includes(donnees?.code)) {
    ecrireJson(CLE_SESSION, null);
    afficher("connexion");
    throw new ErreurApi("Connexion expirée : reconnectez-vous.", 401);
  }
  const message = donnees?.message || donnees?.erreur || donnees?.erreurs?.join(" ") || donnees?.title || `Erreur ${r.status}`;
  throw new ErreurApi(message, r.status, donnees);
}

// ---------- Mise en forme ----------
const echapper = (t) => String(t ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
const montant = (n) => (n == null || n === "" ? "" : Number(n).toLocaleString("fr-FR", { minimumFractionDigits: 2, maximumFractionDigits: 2 }));
const dateCourte = (d) => (d ? new Date(d).toLocaleDateString("fr-FR") : "");
function uuid() {
  if (crypto.randomUUID) return crypto.randomUUID();
  return "xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx".replace(/[xy]/g, (c) => {
    const r = (crypto.getRandomValues(new Uint8Array(1))[0] & 15);
    return (c === "x" ? r : (r & 3) | 8).toString(16);
  });
}

let minuteurBandeau;
function bandeau(texte, genre = "") {
  const b = $("#bandeau");
  clearTimeout(minuteurBandeau);
  if (!texte) { b.hidden = true; return; }
  b.textContent = texte;
  b.className = `bandeau ${genre}`;
  b.hidden = false;
  if (genre !== "erreur") minuteurBandeau = setTimeout(() => (b.hidden = true), 5000);
}
const erreur = (e) => bandeau(e?.message || String(e), "erreur");

// ---------- Navigation ----------
const ECRANS = ["reglages", "connexion", "lire", "documents", "modeles"];
function afficher(ecran) {
  for (const e of ECRANS) $(`#ecran-${e}`).hidden = e !== ecran;
  const s = session();
  $("#onglets").hidden = !s || !["lire", "documents", "modeles"].includes(ecran);
  for (const b of document.querySelectorAll("#onglets button")) b.classList.toggle("actif", b.dataset.onglet === ecran);
  $("#btn-utilisateur").hidden = !s;
  if (s) $("#btn-utilisateur").textContent = [s.utilisateur, nomSociete(s.dossier)].filter(Boolean).join(" · ");
  if (ecran === "connexion") preparerChoix($("#choix-dossier"), $("#form-connexion").dossier, cleApi());
}

function ouvrir(onglet) {
  etat.onglet = onglet;
  afficher(onglet);
  if (onglet === "documents") chargerDocuments();
  if (onglet === "modeles") chargerModeles().then(dessinerModeles).catch(erreur);
}
$("#onglets").addEventListener("click", (e) => { const b = e.target.closest("[data-onglet]"); if (b) ouvrir(b.dataset.onglet); });

function demarrer() {
  if (!cleApi()) return afficher("reglages");
  if (!session()) return afficher("connexion");
  ouvrir("lire");
  chargerReferentiels();
  chargerModeles().then(remplirChoixModeles).catch(erreur);
}

// ---------- Réglages et connexion ----------
$("#form-reglages").addEventListener("submit", (e) => {
  e.preventDefault();
  ecrireJson(CLE_REGLAGES, { cle: new FormData(e.target).get("cle").trim() });
  bandeau("Réglages enregistrés.", "ok");
  demarrer();
});
$("#btn-ouvrir-reglages").addEventListener("click", () => { $("#form-reglages").cle.value = cleApi(); afficher("reglages"); });

$("#form-connexion").addEventListener("submit", async (e) => {
  e.preventDefault();
  const f = new FormData(e.target);
  const bouton = $("#btn-connexion");
  bouton.disabled = true;
  try {
    const dossier = societeChoisie(e.target.dossier);
    const r = await api("POST", "/connexion", { utilisateur: f.get("utilisateur").trim(), motDePasse: f.get("motDePasse"), ...(dossier ? { dossier } : {}) });
    ecrireJson(CLE_SESSION, r);
    e.target.motDePasse.value = "";
    bandeau("");
    demarrer();
  } catch (err) {
    bandeau(err.message, "erreur");
  } finally {
    bouton.disabled = false;
  }
});
$("#btn-utilisateur").addEventListener("click", () => {
  if (!confirm("Se déconnecter ?")) return;
  ecrireJson(CLE_SESSION, null);
  afficher("connexion");
});

// ---------- Référentiels : tiers, comptes, journaux (lus dans Sage par l'API) ----------
async function chargerReferentiels() {
  try {
    etat.referentiels = await api("GET", "/ocr/referentiels");
    $("#liste-comptes").innerHTML = etat.referentiels.comptes.map((c) => `<option value="${echapper(c.numero)}">${echapper(c.intitule)}</option>`).join("");
    form.journal.innerHTML = `<option value=""></option>` + etat.referentiels.journaux.map((j) => `<option value="${echapper(j.code)}">${echapper(`${j.code} · ${j.intitule ?? ""}`)}</option>`).join("");
  } catch (e) { erreur(e); }
}

let tiersConnus = [];
let minuteurTiers;
async function chercherTiers() {
  const q = form.tiers.value.trim();
  try {
    tiersConnus = await api("GET", `/ocr/tiers?type=${form.typeDocument.value === "commande-client" ? "client" : "fournisseur"}&q=${encodeURIComponent(q)}`);
    $("#liste-tiers").innerHTML = tiersConnus.map((t) => `<option value="${echapper(t.numero)}">${echapper(t.intitule)}</option>`).join("");
    afficherNomTiers();
  } catch (e) { erreur(e); }
}
function afficherNomTiers() {
  const t = tiersConnus.find((x) => x.numero.toUpperCase() === form.tiers.value.trim().toUpperCase());
  $("#nom-tiers").textContent = form.tiers.value.trim() ? (t ? t.intitule : "Compte inconnu dans Sage.") : "";
  return t;
}
form.tiers.addEventListener("input", () => { clearTimeout(minuteurTiers); minuteurTiers = setTimeout(chercherTiers, 250); });
form.typeDocument.addEventListener("change", chercherTiers);

// ---------- Modèles ----------
async function chargerModeles() {
  etat.modeles = await api("GET", "/ocr/modeles");
  return etat.modeles;
}
function remplirChoixModeles() {
  const choisi = etat.modele?.id ?? "";
  $("#modele").innerHTML = `<option value="">— Aucun : saisie à la main —</option>` +
    etat.modeles.map((m) => `<option value="${echapper(m.id)}">${echapper(m.nom)}</option>`).join("");
  $("#modele").value = choisi;
}
$("#modele").addEventListener("change", () => {
  const m = etat.modeles.find((x) => x.id === $("#modele").value) ?? null;
  if (m) appliquerModele(m); else { etat.modele = null; etat.zones = {}; $("#info-modele").textContent = ""; dessinerZones(); }
});

/** Lit chaque zone du modèle sur la page (recalée sur l'ancre) et remplit la saisie. */
function appliquerModele(m) {
  etat.modele = m;
  $("#modele").value = m.id;
  const deca = decalage(m, etat.mots, etat.largeur, etat.hauteur);
  etat.zones = {};
  // Les zones sont gardées dans le repère de cette page : si on les retouche, le modèle est réenregistré pour ce scan.
  const decaler = (z) => ({ x: z.x + deca.dx, y: z.y + deca.dy, largeur: z.largeur, hauteur: z.hauteur });
  if (m.ancre) etat.zones.identification = decaler(m.ancre);
  for (const z of m.zones) {
    etat.zones[z.champ] = decaler(z);
    if (etat.mots.length) remplirChamp(z.champ, lireChamp(z.champ, texteZone(etat.mots, etat.zones[z.champ], etat.largeur, etat.hauteur)));
  }
  form.typeDocument.value = m.typeDocument;
  form.tiers.value = m.tiers;
  form.compteCharge.value = m.defauts?.compteCharge ?? "";
  form.journal.value = m.defauts?.journal ?? "";
  form.libelle.value = m.defauts?.libelle ?? "";
  $("#nom-modele").value = m.nom;
  $("#identification").value = m.identification;
  const recale = Math.abs(deca.dx) > 0.01 || Math.abs(deca.dy) > 0.01;
  $("#info-modele").textContent = `Format reconnu : « ${m.identification} »${recale ? " (scan décalé, zones recalées)" : ""}. Vérifiez les valeurs.`;
  chercherTiers();
  controler();
  dessinerZones();
}

function remplirChamp(champ, valeur) {
  const champForm = form[champ];
  if (!champForm) return;
  champForm.value = MONTANTS.includes(champ) ? montant(valeur) : (valeur ?? "");
}

function dessinerModeles() {
  const t = $("#table-modeles");
  if (!etat.modeles.length) { t.innerHTML = `<tr><td class="vide">Aucun modèle. Lisez un document puis « Marquer les zones ».</td></tr>`; return; }
  t.innerHTML = `<thead><tr><th>Nom</th><th>Type</th><th>Tiers</th><th>Identification</th><th>Champs</th><th>Défauts</th><th class="nombre">Lus</th><th>Modifié le</th><th></th></tr></thead><tbody>` +
    etat.modeles.map((m) => `<tr>
      <td>${echapper(m.nom)}</td><td>${echapper(TYPES[m.typeDocument] ?? m.typeDocument)}</td><td>${echapper(m.tiers)}</td>
      <td>${echapper(m.identification)}</td><td>${echapper(m.zones.map((z) => NOMS[z.champ] ?? z.champ).join(", "))}</td>
      <td>${echapper([m.defauts?.compteCharge, m.defauts?.journal].filter(Boolean).join(" · "))}</td>
      <td class="nombre">${m.utilisations}</td><td>${dateCourte(m.majLe)}</td>
      <td><button type="button" class="danger" data-supprimer-modele="${echapper(m.id)}" title="Supprimer">🗑</button></td></tr>`).join("") + "</tbody>";
}
$("#table-modeles").addEventListener("click", async (e) => {
  const b = e.target.closest("[data-supprimer-modele]");
  if (!b) return;
  const m = etat.modeles.find((x) => x.id === b.dataset.supprimerModele);
  if (!confirm(`Supprimer le modèle « ${m?.nom} » ? Les documents déjà enregistrés restent.`)) return;
  try {
    await api("DELETE", `/ocr/modeles/${encodeURIComponent(b.dataset.supprimerModele)}`);
    if (etat.modele?.id === b.dataset.supprimerModele) etat.modele = null;
    await chargerModeles();
    dessinerModeles();
    remplirChoixModeles();
  } catch (err) { erreur(err); }
});

// ---------- Ouverture du document : image (OCR) ou PDF (texte du PDF, sinon OCR de la page) ----------
let workerOcr = null;
async function ocr(toile) {
  workerOcr ??= await Tesseract.createWorker("fra", 1, {
    workerPath: `${LIB}worker.min.js`, corePath: LIB, langPath: LIB, gzip: false,
    logger: (m) => { if (m.status === "recognizing text") etatLecture(`Lecture du texte… ${Math.round(m.progress * 100)} %`); },
  });
  const { data } = await workerOcr.recognize(toile, {}, { blocks: true });
  return motsDeTesseract(data);
}

const etatLecture = (texte) => ($("#etat-lecture").textContent = texte);

async function ouvrirFichier(fichier) {
  if (!fichier) return;
  etatLecture("Ouverture…");
  if (fichier.size > 15 * 1024 * 1024) return erreur("Fichier trop gros (15 Mo au plus).");
  const estPdf = fichier.type === "application/pdf" || /\.pdf$/i.test(fichier.name);
  if (!estPdf && !fichier.type.startsWith("image/")) return erreur("Choisissez une image (JPG, PNG) ou un PDF.");
  reinitialiser();
  const toile = $("#toile");
  const octets = new Uint8Array(await fichier.arrayBuffer());
  etat.fichier = { nom: fichier.name, type: fichier.type || (estPdf ? "application/pdf" : "image/png"), octets };
  try {
    etatLecture("Ouverture…");
    if (estPdf) {
      const pdfjs = await import(`${LIB}pdf.min.mjs`);
      pdfjs.GlobalWorkerOptions.workerSrc = `${LIB}pdf.worker.min.mjs`;
      const doc = await pdfjs.getDocument({ data: octets.slice() }).promise;
      const page = await doc.getPage(1);
      const viewport = page.getViewport({ scale: 2000 / page.getViewport({ scale: 1 }).width });
      toile.width = Math.round(viewport.width);
      toile.height = Math.round(viewport.height);
      $("#page").classList.remove("vide-page");
      await page.render({ canvasContext: toile.getContext("2d"), viewport }).promise;
      etat.mots = motsDuPdf((await page.getTextContent()).items, viewport, pdfjs.Util);
      if (etat.mots.length < 5) etat.mots = await ocr(toile); // PDF scanné : pas de texte, on lit l'image.
      if (doc.numPages > 1) bandeau(`PDF de ${doc.numPages} pages : seule la première est lue.`);
    } else {
      const image = await createImageBitmap(fichier);
      // Taille d'origine (l'agrandir brouille les chiffres) ; seuls les très grands scans sont réduits et les très petits agrandis.
      const echelle = image.width > 3000 ? 3000 / image.width : image.width < 1000 ? 1600 / image.width : 1;
      toile.width = Math.round(image.width * echelle);
      toile.height = Math.round(image.height * echelle);
      const ctx = toile.getContext("2d");
      ctx.fillStyle = "#fff";
      ctx.fillRect(0, 0, toile.width, toile.height);
      ctx.drawImage(image, 0, 0, toile.width, toile.height);
      $("#page").classList.remove("vide-page");
      etat.mots = await ocr(toile);
    }
    etat.largeur = toile.width;
    etat.hauteur = toile.height;
    etatLecture(`${fichier.name} · ${etat.mots.length} mots lus`);
    await chargerModeles().catch(() => etat.modeles);
    remplirChoixModeles();
    const m = choisirModele(etat.modeles, etat.mots);
    if (m) appliquerModele(m);
    else {
      $("#modele").value = "";
      $("#info-modele").textContent = etat.mots.length
        ? "Format inconnu : saisissez à la main, ou « Marquer les zones » pour le reconnaître la prochaine fois."
        : "Aucun texte lu : saisissez à la main.";
      chercherTiers();
    }
    dessinerZones();
  } catch (e) {
    etatLecture("Lecture impossible.");
    erreur(e);
  }
}

$("#fichier").addEventListener("change", (e) => { ouvrirFichier(e.target.files[0]); e.target.value = ""; });
const page = $("#page");
page.addEventListener("dragover", (e) => { e.preventDefault(); page.classList.add("survol"); });
page.addEventListener("dragleave", () => page.classList.remove("survol"));
page.addEventListener("drop", (e) => { e.preventDefault(); page.classList.remove("survol"); ouvrirFichier(e.dataTransfer.files[0]); });

function reinitialiser() {
  etat.fichier = null; etat.mots = []; etat.modele = null; etat.zones = {};
  form.reset();
  for (const c of [...CHAMPS, "compteCharge", "libelle"]) form[c].value = "";
  $("#identification").value = ""; $("#nom-modele").value = "";
  $("#info-modele").textContent = ""; $("#nom-tiers").textContent = ""; $("#controles").innerHTML = "";
  basculerMarquage(false);
  dessinerZones();
}

// ---------- Zones : affichage et tracé ----------
function dessinerZones(provisoire) {
  const calque = $("#calque");
  const cadres = Object.entries(etat.zones).map(([champ, z]) => ({ champ, z }));
  if (provisoire) cadres.push({ champ: etat.champActif, z: provisoire, actif: true });
  // L'ancre est le cadre serré des mots d'identification : on l'élargit un peu à l'affichage pour ne pas barrer le texte.
  const marge = (champ, z) => (champ === "identification" ? { x: z.x - 0.004, y: z.y - 0.004, largeur: z.largeur + 0.008, hauteur: z.hauteur + 0.008 } : z);
  calque.innerHTML = cadres.map(({ champ, z, actif }) => ({ champ, z: actif ? z : marge(champ, z), actif })).map(({ champ, z, actif }) =>
    `<div class="cadre ${champ === "identification" ? "identification" : ""} ${actif ? "actif" : ""}"
      style="left:${z.x * 100}%;top:${z.y * 100}%;width:${z.largeur * 100}%;height:${z.hauteur * 100}%"><span>${echapper(NOMS[champ] ?? champ)}</span></div>`).join("");
  for (const b of document.querySelectorAll(".zone")) {
    b.classList.toggle("pose", !!etat.zones[b.dataset.champ]);
    b.classList.toggle("actif", b.dataset.champ === etat.champActif);
  }
  calque.classList.toggle("trace", !!etat.champActif);
}

function basculerMarquage(actif) {
  etat.marquage = actif;
  form.classList.toggle("marquage", actif);
  $("#edition").hidden = !actif;
  $("#btn-enregistrer-modele").hidden = !actif;
  $("#btn-marquer").textContent = actif ? "Fermer le marquage" : "Marquer les zones";
  etat.champActif = actif ? (etat.zones.identification ? null : "identification") : null;
  if (actif && !$("#nom-modele").value) $("#nom-modele").value = afficherNomTiers()?.intitule ?? "";
  dessinerZones();
}
$("#btn-marquer").addEventListener("click", () => {
  if (!etat.mots.length && !etat.marquage) return erreur("Ouvrez d'abord un document lisible.");
  basculerMarquage(!etat.marquage);
});
form.addEventListener("click", (e) => {
  const b = e.target.closest(".zone");
  if (!b) return;
  etat.champActif = etat.champActif === b.dataset.champ ? null : b.dataset.champ;
  dessinerZones();
});

let depart = null;
const position = (e) => {
  const r = $("#calque").getBoundingClientRect();
  return { x: Math.min(1, Math.max(0, (e.clientX - r.left) / r.width)), y: Math.min(1, Math.max(0, (e.clientY - r.top) / r.height)) };
};
const cadreEntre = (a, b) => ({ x: Math.min(a.x, b.x), y: Math.min(a.y, b.y), largeur: Math.abs(a.x - b.x), hauteur: Math.abs(a.y - b.y) });
$("#calque").addEventListener("pointerdown", (e) => {
  if (!etat.champActif) return;
  e.preventDefault();
  depart = position(e);
  $("#calque").setPointerCapture(e.pointerId);
});
$("#calque").addEventListener("pointermove", (e) => { if (depart) dessinerZones(cadreEntre(depart, position(e))); });
$("#calque").addEventListener("pointerup", (e) => {
  if (!depart) return;
  const zone = cadreEntre(depart, position(e));
  depart = null;
  if (zone.largeur < 0.005 || zone.hauteur < 0.003) return dessinerZones();
  const champ = etat.champActif;
  etat.zones[champ] = zone;
  const texte = texteZone(etat.mots, zone, etat.largeur, etat.hauteur);
  if (champ === "identification") {
    $("#identification").value = texte.replace(/\s+/g, " ").slice(0, 80);
    if (!texte) bandeau("Aucun texte lu dans ce cadre : tracez-le autour d'un texte net de l'en-tête.", "erreur");
  } else {
    remplirChamp(champ, lireChamp(champ, texte));
    if (!texte) bandeau(`Aucun texte lu dans le cadre « ${NOMS[champ]} ».`, "erreur");
    controler();
  }
  // Champ suivant à marquer.
  etat.champActif = ["identification", ...CHAMPS].find((c) => !etat.zones[c]) ?? null;
  dessinerZones();
});

$("#btn-enregistrer-modele").addEventListener("click", async () => {
  const identification = $("#identification").value.trim();
  const zones = CHAMPS.filter((c) => etat.zones[c]).map((c) => ({ champ: c, ...arrondir(etat.zones[c]) }));
  if (!identification) return erreur("Indiquez le texte d'identification (tracez-le sur l'en-tête).");
  if (!zones.length) return erreur("Tracez au moins une zone (numéro, date, montants).");
  if (!form.tiers.value.trim()) return erreur("Choisissez le tiers Sage de ce format.");
  const ancre = trouverAncre(etat.mots, identification, etat.largeur, etat.hauteur);
  if (!ancre) bandeau("Le texte d'identification n'est pas lu tel quel sur cette page : vérifiez-le, sinon le format ne sera pas reconnu.", "erreur");
  const id = etat.modele?.id ?? uuid();
  try {
    const m = await api("PUT", `/ocr/modeles/${id}`, {
      nom: $("#nom-modele").value.trim() || `${TYPES[form.typeDocument.value]} ${afficherNomTiers()?.intitule ?? form.tiers.value.trim()}`, typeDocument: form.typeDocument.value, tiers: form.tiers.value.trim(),
      identification, ancre: ancre ? { champ: "identification", ...arrondir(ancre) } : null, zones,
      defauts: { compteCharge: form.compteCharge.value.trim(), journal: form.journal.value, libelle: form.libelle.value.trim() },
    });
    await chargerModeles();
    etat.modele = m;
    remplirChoixModeles();
    basculerMarquage(false);
    $("#info-modele").textContent = `Modèle « ${m.nom} » enregistré : les prochains documents de ce format seront lus tout seuls.`;
    if (ancre) bandeau("Modèle enregistré.", "ok");
  } catch (e) { erreur(e); }
});
const arrondir = (z) => ({ x: +z.x.toFixed(4), y: +z.y.toFixed(4), largeur: +z.largeur.toFixed(4), hauteur: +z.hauteur.toFixed(4) });

// ---------- Contrôles et validation ----------
function valeurs() {
  const m = controlerMontants({ montantHT: lireMontant(form.montantHT.value), montantTVA: lireMontant(form.montantTVA.value), montantTTC: lireMontant(form.montantTTC.value) });
  return { ...m, numero: form.numero.value.trim(), date: form.date.value || null, tiers: form.tiers.value.trim() };
}
function controler() {
  const v = valeurs();
  if (form.montantTVA.value.trim() === "" && v.montantTVA != null) form.montantTVA.value = montant(v.montantTVA);
  const lignes = [];
  if (v.ecart) lignes.push(`<span class="alerte">HT + TVA ≠ TTC : écart de ${montant(v.ecart)}</span>`);
  else if (v.montantHT != null && v.montantTTC != null) lignes.push(`<span class="ok">✓ HT + TVA = TTC</span>`);
  if (v.date && new Date(v.date) > new Date()) lignes.push(`<span class="alerte">Date dans le futur.</span>`);
  $("#controles").innerHTML = lignes.join(" · ");
}
for (const c of MONTANTS) {
  form[c].addEventListener("change", () => { const n = lireMontant(form[c].value); if (n != null) form[c].value = montant(n); controler(); });
}
form.date.addEventListener("change", controler);

function base64(octets) {
  let s = "";
  for (let i = 0; i < octets.length; i += 0x8000) s += String.fromCharCode(...octets.subarray(i, i + 0x8000));
  return btoa(s);
}

form.addEventListener("submit", async (e) => {
  e.preventDefault();
  const v = valeurs();
  if (form.typeDocument.value === "facture-fournisseur" && !(v.montantTTC > 0)) return erreur("Le montant TTC est obligatoire.");
  if (v.ecart) return erreur(`HT + TVA ne fait pas le TTC (écart de ${montant(v.ecart)}) : corrigez avant de valider.`);
  const bouton = $("#btn-valider");
  bouton.disabled = true;
  try {
    await api("PUT", `/ocr/documents/${uuid()}`, {
      modele: etat.modele?.id ?? null, typeDocument: form.typeDocument.value, tiers: v.tiers, numero: v.numero, date: v.date,
      montantHT: v.montantHT, montantTVA: v.montantTVA, montantTTC: v.montantTTC,
      champs: { compteCharge: form.compteCharge.value.trim(), journal: form.journal.value, libelle: form.libelle.value.trim() },
      fichier: etat.fichier?.nom ?? null, typeFichier: etat.fichier?.type ?? null, contenu: etat.fichier ? base64(etat.fichier.octets) : null,
      statut: "a-ecrire",
    });
    bandeau(`Document ${v.numero} enregistré : il attend l'écriture dans Sage.`, "ok");
    reinitialiser();
    $("#toile").getContext("2d").clearRect(0, 0, $("#toile").width, $("#toile").height);
    $("#page").classList.add("vide-page");
    etatLecture("Ouvrez le document suivant.");
  } catch (err) {
    erreur(err);
  } finally {
    bouton.disabled = false;
  }
});

$("#btn-abandonner").addEventListener("click", () => {
  if (etat.fichier && !confirm("Abandonner ce document sans l'enregistrer ?")) return;
  reinitialiser();
  $("#page").classList.add("vide-page");
  etatLecture("Image (JPG, PNG) ou PDF. Glissez le fichier ici.");
});

// ---------- Documents enregistrés ----------
async function chargerDocuments() {
  try {
    const statut = $("#filtre-statut").value, tiers = $("#filtre-tiers").value.trim();
    etat.documents = await api("GET", `/ocr/documents?taille=500${statut ? `&statut=${statut}` : ""}${tiers ? `&tiers=${encodeURIComponent(tiers)}` : ""}`);
    dessinerDocuments();
  } catch (e) { erreur(e); }
}
$("#filtre-statut").addEventListener("change", chargerDocuments);
let minuteurFiltre;
$("#filtre-tiers").addEventListener("input", () => { clearTimeout(minuteurFiltre); minuteurFiltre = setTimeout(chargerDocuments, 300); });

function dessinerDocuments() {
  const t = $("#table-documents");
  if (!etat.documents.length) { t.innerHTML = `<tr><td class="vide">Aucun document.</td></tr>`; return; }
  const total = (c) => etat.documents.reduce((s, d) => s + (d[c] ?? 0), 0);
  t.innerHTML = `<thead><tr><th>Date</th><th>Tiers</th><th>N°</th><th class="nombre">HT</th><th class="nombre">TVA</th><th class="nombre">TTC</th>
      <th>Compte</th><th>Journal</th><th>Libellé</th><th>Statut</th><th>Enregistré le</th><th>Par</th><th></th></tr></thead><tbody>` +
    etat.documents.map((d) => `<tr>
      <td>${dateCourte(d.date)}</td><td>${echapper(d.tiers)}</td><td>${echapper(d.numero)}</td>
      <td class="nombre">${montant(d.montantHT)}</td><td class="nombre">${montant(d.montantTVA)}</td><td class="nombre">${montant(d.montantTTC)}</td>
      <td>${echapper(d.champs?.compteCharge)}</td><td>${echapper(d.champs?.journal)}</td><td>${echapper(d.champs?.libelle)}</td>
      <td><span class="etiquette ${d.statut === "ecrit" ? "ok" : d.statut === "rejete" ? "rejete" : ""}">${echapper(STATUTS[d.statut] ?? d.statut)}</span>${d.pieceSage ? ` ${echapper(d.pieceSage)}` : ""}</td>
      <td>${dateCourte(d.creeLe)}</td><td>${echapper(d.utilisateur)}</td>
      <td>${d.fichier ? `<button type="button" class="lien" data-voir="${echapper(d.id)}" title="${echapper(d.fichier)}">Scan</button>` : ""}
        ${d.statut !== "ecrit" ? `<button type="button" class="danger" data-supprimer="${echapper(d.id)}" title="Supprimer">🗑</button>` : ""}</td></tr>`).join("") +
    `</tbody><tfoot><tr><th colspan="3">${etat.documents.length} document(s)</th><th class="nombre">${montant(total("montantHT"))}</th>
      <th class="nombre">${montant(total("montantTVA"))}</th><th class="nombre">${montant(total("montantTTC"))}</th><th colspan="7"></th></tr></tfoot>`;
}

$("#table-documents").addEventListener("click", async (e) => {
  const voir = e.target.closest("[data-voir]");
  if (voir) {
    // Le scan est protégé par la connexion : on le télécharge avec les en-têtes puis on l'ouvre.
    const fenetre = window.open("", "_blank");
    try {
      const r = await fetch(`/api/v1/ocr/documents/${encodeURIComponent(voir.dataset.voir)}/fichier`, { headers: entetes(false) });
      if (!r.ok) throw new Error(`Scan introuvable (${r.status}).`);
      const url = URL.createObjectURL(await r.blob());
      if (fenetre) fenetre.location = url; else location.assign(url);
    } catch (err) { fenetre?.close(); erreur(err); }
    return;
  }
  const sup = e.target.closest("[data-supprimer]");
  if (sup && confirm("Supprimer ce document et son scan ?")) {
    try { await api("DELETE", `/ocr/documents/${encodeURIComponent(sup.dataset.supprimer)}`); chargerDocuments(); } catch (err) { erreur(err); }
  }
});

$("#btn-csv").addEventListener("click", () => {
  const nombre = (n) => (n == null ? "" : String(n).replace(".", ","));
  const champ = (t) => `"${String(t ?? "").replace(/"/g, '""')}"`;
  const lignes = [["Date", "Tiers", "Numéro", "HT", "TVA", "TTC", "Compte", "Journal", "Libellé", "Statut", "Pièce Sage", "Enregistré le", "Par"].join(";")]
    .concat(etat.documents.map((d) => [dateCourte(d.date), champ(d.tiers), champ(d.numero), nombre(d.montantHT), nombre(d.montantTVA), nombre(d.montantTTC),
      champ(d.champs?.compteCharge), champ(d.champs?.journal), champ(d.champs?.libelle), champ(STATUTS[d.statut] ?? d.statut), champ(d.pieceSage),
      dateCourte(d.creeLe), champ(d.utilisateur)].join(";")));
  const a = document.createElement("a");
  a.href = URL.createObjectURL(new Blob(["﻿" + lignes.join("\r\n")], { type: "text/csv;charset=utf-8" }));
  a.download = `documents-ocr-${new Date().toISOString().slice(0, 10)}.csv`;
  a.click();
});

demarrer();
