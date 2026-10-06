// Tableau de bord des livraisons : indicateurs, graphiques et détail, tous sur les mêmes filtres.
// Cliquer un jour, un livreur ou un statut filtre tout le tableau. Les filtres restent dans l'adresse de la page (partageable).

const $ = (s) => document.querySelector(s);
const STATUTS = [
  { code: "livre", nom: "Livré", couleur: "var(--s-livre)" },
  { code: "partiel", nom: "Partiel", couleur: "var(--s-partiel)" },
  { code: "echec", nom: "Échec", couleur: "var(--s-echec)" },
  { code: "a-livrer", nom: "À livrer", couleur: "var(--s-alivrer)" },
];
const STATUTS_COURSE = { "a-faire": ["À faire", "var(--s-alivrer)"], "en-cours": ["En cours", "var(--s-partiel)"], fait: ["Fait", "var(--s-livre)"],
  reporte: ["Reporté", "var(--s-reporte)"], annule: ["Annulé", "var(--s-echec)"] };
const MOTIFS = { absent: "Client absent", refus: "Refus du client", "adresse-introuvable": "Adresse introuvable", ferme: "Établissement fermé",
  "manque-marchandise": "Marchandise manquante", endommage: "Marchandise endommagée", "erreur-commande": "Erreur de commande", retour: "Retour", autre: "Autre" };

function lireJson(cle) { try { return JSON.parse(localStorage.getItem(cle)); } catch { return null; } }
const cleApi = () => lireJson("livraison.reglages")?.cle || lireJson("crm.reglages")?.cle || lireJson("borne.reglages")?.cle || "";
const jeton = () => lireJson("livraison.session")?.jeton;
// Société de la connexion faite dans l'application des livraisons.
const dossier = () => lireJson("livraison.session")?.dossier;

async function api(chemin) {
  let r;
  try {
    r = await fetch(`/api/v1${chemin}`, { headers: { "X-Api-Key": cleApi(), ...(jeton() ? { Authorization: `Bearer ${jeton()}` } : {}), ...(dossier() ? { "X-Dossier": dossier() } : {}) }, cache: "no-store" });
  } catch { throw new Error("Serveur injoignable. Vérifiez le réseau."); }
  const d = await r.json().catch(() => null);
  if (d?.code === "DOSSIER_REQUIS" || d?.code === "DOSSIER_DIFFERENT") throw new Error("Connectez-vous d'abord dans l'application des livraisons : c'est elle qui choisit la société.");
  if (r.status === 401 && d?.erreur) throw new Error("Clé d'API absente ou refusée : ouvrez d'abord l'application des livraisons pour la régler.");
  if (!r.ok) throw new Error(d?.message || d?.erreurs?.join(" ") || `Erreur ${r.status}`);
  return d;
}

const echapper = (t) => String(t ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
const nombre = (n, d = 0) => (Number(n) || 0).toLocaleString("fr-FR", { maximumFractionDigits: d });
const dateCourte = (d) => new Date(d).toLocaleDateString("fr-FR", { day: "2-digit", month: "2-digit" });
const dateLongue = (d) => new Date(d).toLocaleDateString("fr-FR", { weekday: "short", day: "numeric", month: "short" });
const heure = (d) => (d ? new Date(d).toLocaleTimeString("fr-FR", { hour: "2-digit", minute: "2-digit" }) : "");
function iso(d) { const x = new Date(d); x.setMinutes(x.getMinutes() - x.getTimezoneOffset()); return x.toISOString().slice(0, 10); }

function bandeau(texte) {
  const b = $("#bandeau");
  b.hidden = !texte;
  b.textContent = texte ?? "";
  b.className = "bandeau erreur";
}

// ---------- Filtres (dans l'adresse de la page) ----------
const CHAMPS = { du: "#f-du", au: "#f-au", livreur: "#f-livreur", depot: "#f-depot", statut: "#f-statut", client: "#f-client" };
const ref = { collaborateurs: [], depots: [], donnees: null, tri: { colonne: "date", sens: -1 } };

function lireFiltres() {
  const f = {};
  for (const [cle, sel] of Object.entries(CHAMPS)) { const v = $(sel).value.trim(); if (v) f[cle] = v; }
  return f;
}
function appliquerFiltres(f) {
  for (const [cle, sel] of Object.entries(CHAMPS)) $(sel).value = f[cle] ?? "";
}
function periode(code) {
  const auj = new Date();
  if (code === "tout") return { du: "", au: "" };
  if (code === "jour") return { du: iso(auj), au: iso(auj) };
  if (code === "mois") return { du: iso(new Date(auj.getFullYear(), auj.getMonth(), 1)), au: iso(auj) };
  return { du: iso(new Date(auj.getTime() - (Number(code) - 1) * 86400000)), au: iso(auj) };
}
function marquerPeriode() {
  const { du, au } = lireFiltres();
  for (const b of document.querySelectorAll("[data-periode]")) {
    const p = periode(b.dataset.periode);
    b.classList.toggle("actif", (p.du || undefined) === du && (p.au || undefined) === au);
  }
}

let minuteur;
function changer(maintenant = false) {
  clearTimeout(minuteur);
  minuteur = setTimeout(charger, maintenant ? 0 : 300);
}
for (const sel of Object.values(CHAMPS)) $(sel).addEventListener(sel === "#f-client" ? "input" : "change", () => changer(sel !== "#f-client"));
$(".periodes").addEventListener("click", (e) => {
  const b = e.target.closest("[data-periode]");
  if (!b) return;
  const p = periode(b.dataset.periode);
  $("#f-du").value = p.du;
  $("#f-au").value = p.au;
  changer(true);
});
$("#btn-reinitialiser").addEventListener("click", () => { appliquerFiltres(periode("30")); changer(true); });
$("#btn-actualiser").addEventListener("click", () => changer(true));

const nomLivreur = (n) => {
  const c = ref.collaborateurs.find((x) => x.numero === n);
  return c ? [c.prenom, c.nom].filter(Boolean).join(" ") : n ? `Collaborateur ${n}` : "Sans livreur";
};
const nomDepot = (n) => ref.depots.find((d) => d.numero === n)?.intitule ?? (n ? `Dépôt ${n}` : "—");

// ---------- Chargement ----------
async function charger() {
  const f = lireFiltres();
  history.replaceState(null, "", `?${new URLSearchParams(f)}`);
  marquerPeriode();
  try {
    const d = await api(`/livraisons/tableau-de-bord?${new URLSearchParams(f)}`);
    ref.donnees = d;
    bandeau(null);
    remplirListes(d);
    dessiner(d, f);
  } catch (e) { bandeau(e.message); }
}

function remplirListes(d) {
  const garder = (sel, options) => {
    const v = $(sel).value;
    $(sel).innerHTML = `<option value="">Tous</option>` + options.map(([val, nom]) => `<option value="${val}">${echapper(nom)}</option>`).join("");
    $(sel).value = v;
  };
  garder("#f-livreur", d.livreurs.map((n) => [n, nomLivreur(n)]));
  garder("#f-depot", d.depots.map((n) => [n, nomDepot(n)]));
}

// ---------- Dessin ----------
function dessiner(d, f) {
  const i = d.indicateurs;
  const morceaux = [];
  if (f.du || f.au) morceaux.push(f.du === f.au ? `le ${dateLongue(f.du)}` : `du ${f.du ? dateLongue(f.du) : "début"} au ${f.au ? dateLongue(f.au) : "aujourd'hui"}`);
  if (f.livreur) morceaux.push(nomLivreur(Number(f.livreur)));
  if (f.depot) morceaux.push(nomDepot(Number(f.depot)));
  if (f.statut) morceaux.push(STATUTS.find((s) => s.code === f.statut)?.nom);
  if (f.client) morceaux.push(`client « ${f.client} »`);
  $("#resume-filtres").textContent = morceaux.length ? `Filtré : ${morceaux.join(" · ")}` : "Toutes les tournées";

  const tuile = (titre, valeur, detail = "", couleur = null) =>
    `<div class="tuile"><span>${couleur ? `<i class="pastille-statut" style="background:${couleur}"></i>` : ""}${titre}</span><strong>${valeur}</strong><small>${detail}</small></div>`;
  $("#indicateurs").innerHTML = [
    tuile("Tournées", nombre(i.tournees), `${nombre(i.arrets)} arrêt(s)`),
    tuile("Taux de réussite", `${nombre(i.tauxReussite, 1)} %`, "livrés en entier / arrêts traités"),
    tuile("Livrés", nombre(i.livres), "", STATUTS[0].couleur),
    tuile("Partiels", nombre(i.partiels), `${nombre(i.lignesNonLivrees)} ligne(s) non livrée(s)`, STATUTS[1].couleur),
    tuile("Échecs", nombre(i.echecs), `${nombre(i.montantNonLivre)} TTC non livré`, STATUTS[2].couleur),
    tuile("À livrer", nombre(i.aLivrer), "", STATUTS[3].couleur),
    tuile("Montant livré", nombre(i.montantLivre), "TTC, livrés et partiels"),
    tuile("Autres courses", `${nombre(i.coursesFaites)} / ${nombre(i.courses)}`, i.coursesEnAttente ? `${nombre(i.coursesEnAttente)} en attente` : "faites"),
    tuile("Chargements contrôlés", nombre(i.chargementsControles), i.ecartsChargement ? `${nombre(i.ecartsChargement)} écart(s) de quantité` : "aucun écart"),
  ].join("");

  dessinerJours(d.parJour, f);
  barres("#g-livreurs", d.parLivreur.map((l) => ({
    nom: nomLivreur(l.livreur), total: l.arrets, valeur: `${l.livres}/${l.arrets}`,
    segments: [[l.livres, STATUTS[0]], [l.partiels, STATUTS[1]], [l.echecs, STATUTS[2]], [l.arrets - l.livres - l.partiels - l.echecs, STATUTS[3]]]
      .map(([n, s]) => ({ n, couleur: s.couleur, nom: s.nom })),
    detail: `${l.tournees} tournée(s) · ${nombre(l.montantLivre)} TTC livré`, filtre: l.livreur != null ? { livreur: String(l.livreur) } : null,
  })), "Aucune tournée sur ces filtres.");
  barres("#g-motifs", d.motifs.map((m) => ({ nom: MOTIFS[m.cle] ?? m.cle, total: m.nombre, valeur: nombre(m.nombre), segments: [{ n: m.nombre, couleur: "var(--s-serie)", nom: "Nombre" }] })),
    "Aucun échec ni retour.");
  barres("#g-courses", d.coursesParStatut.map((c) => ({ nom: STATUTS_COURSE[c.cle]?.[0] ?? c.cle, total: c.nombre, valeur: nombre(c.nombre),
    segments: [{ n: c.nombre, couleur: STATUTS_COURSE[c.cle]?.[1] ?? "var(--s-serie)", nom: "Courses" }] })), "Aucune autre course.");
  barres("#g-villes", d.parVille.map((v) => ({ nom: v.cle, total: v.nombre, valeur: nombre(v.nombre), segments: [{ n: v.nombre, couleur: "var(--s-serie)", nom: "Arrêts" }] })),
    "Aucun arrêt.");
  dessinerTableau();
}

/** Arrêts par jour, empilés par statut. Un clic sur une colonne filtre ce jour ; la légende filtre un statut. */
function dessinerJours(jours, f) {
  $("#legende-jours").innerHTML = STATUTS.map((s) =>
    `<button type="button" data-statut="${s.code}" class="${f.statut && f.statut !== s.code ? "eteint" : ""}"><i style="background:${s.couleur}"></i>${s.nom}</button>`).join("");
  const zone = $("#g-jours");
  if (!jours.length) { zone.innerHTML = `<p class="vide discret" style="grid-column:1/-1">Aucun arrêt sur ces filtres.</p>`; return; }
  const total = (j) => j.livres + j.partiels + j.echecs + j.aLivrer;
  const max = Math.max(1, ...jours.map(total));
  const pas = max <= 5 ? 1 : Math.ceil(max / 4);
  const haut = Math.ceil(max / pas) * pas;
  const graduations = Array.from({ length: haut / pas + 1 }, (_, k) => k * pas);
  zone.innerHTML = `
    <div class="axe">${graduations.map((g) => `<span style="bottom:${(g / haut) * 100}%">${g}</span>`).join("")}</div>
    <div class="zone">
      ${graduations.slice(1).map((g) => `<div class="ligne-grille" style="bottom:${(g / haut) * 100}%"></div>`).join("")}
      ${jours.map((j, k) => `<div class="colonne" data-jour="${k}" role="button" tabindex="0" aria-label="${dateLongue(j.date)} : ${total(j)} arrêt(s)">
        ${[["livres", 0], ["partiels", 1], ["echecs", 2], ["aLivrer", 3]].filter(([c]) => j[c] > 0)
          .map(([c, s]) => `<i style="height:calc(${(j[c] / haut) * 100}% - 2px);background:${STATUTS[s].couleur}"></i>`).join("")}
      </div>`).join("")}
    </div>
    <div class="dates">${jours.map((j, k) => `<span>${jours.length <= 16 || k % Math.ceil(jours.length / 12) === 0 ? dateCourte(j.date) : ""}</span>`).join("")}</div>`;
  zone.querySelectorAll(".colonne").forEach((col) => {
    const j = jours[Number(col.dataset.jour)];
    infobulle(col, () => `<b>${dateLongue(j.date)}</b>${STATUTS.map((s, n) => `<div><span><i style="background:${s.couleur}"></i>${s.nom}</span><span>${[j.livres, j.partiels, j.echecs, j.aLivrer][n]}</span></div>`).join("")}<div><span>Cliquer pour voir ce jour</span></div>`);
    const filtrer = () => { $("#f-du").value = j.date.slice(0, 10); $("#f-au").value = j.date.slice(0, 10); changer(true); };
    col.addEventListener("click", filtrer);
    col.addEventListener("keydown", (e) => { if (e.key === "Enter") filtrer(); });
  });
}
$("#legende-jours").addEventListener("click", (e) => {
  const b = e.target.closest("[data-statut]");
  if (!b) return;
  $("#f-statut").value = $("#f-statut").value === b.dataset.statut ? "" : b.dataset.statut;
  changer(true);
});

/** Barres horizontales (empilées si plusieurs segments). Une barre avec filtre est cliquable. */
function barres(sel, lignes, vide) {
  const zone = $(sel);
  if (!lignes.length) { zone.innerHTML = `<p class="vide discret">${vide}</p>`; return; }
  const max = Math.max(1, ...lignes.map((l) => l.total));
  zone.innerHTML = `<div class="barres">${lignes.map((l, k) => {
    const balise = l.filtre ? "button" : "div";
    return `<${balise} ${l.filtre ? 'type="button"' : ""} class="barre-h" data-k="${k}">
      <span class="nom">${echapper(l.nom)}</span>
      <span class="piste">${l.segments.filter((s) => s.n > 0).map((s) => `<i style="width:${(s.n / max) * 100}%;background:${s.couleur}"></i>`).join("")}</span>
      <span class="valeur">${l.valeur}</span>
    </${balise}>`;
  }).join("")}</div>`;
  zone.querySelectorAll(".barre-h").forEach((b) => {
    const l = lignes[Number(b.dataset.k)];
    infobulle(b, () => `<b>${echapper(l.nom)}</b>${l.segments.filter((s) => s.n > 0).map((s) => `<div><span><i style="background:${s.couleur}"></i>${s.nom}</span><span>${nombre(s.n)}</span></div>`).join("")}${l.detail ? `<div><span>${echapper(l.detail)}</span></div>` : ""}`);
    if (l.filtre) b.addEventListener("click", () => {
      for (const [cle, v] of Object.entries(l.filtre)) $(CHAMPS[cle]).value = $(CHAMPS[cle]).value === v ? "" : v;
      changer(true);
    });
  });
}

function infobulle(el, contenu) {
  const bulle = $("#infobulle");
  const placer = (e) => {
    const x = Math.min(e.clientX + 14, window.innerWidth - bulle.offsetWidth - 8);
    const y = Math.min(e.clientY + 14, window.innerHeight - bulle.offsetHeight - 8);
    bulle.style.left = `${x}px`;
    bulle.style.top = `${y}px`;
  };
  el.addEventListener("pointerenter", (e) => { bulle.innerHTML = contenu(); bulle.hidden = false; placer(e); });
  el.addEventListener("pointermove", placer);
  el.addEventListener("pointerleave", () => { bulle.hidden = true; });
}

// ---------- Détail des arrêts : tri, ouverture de la tournée, export ----------
const valeurTri = (l, c) => (c === "livreur" ? nomLivreur(l.livreur) : c === "intitule" ? l.intitule || l.client : l[c] ?? "");
function lignesTriees() {
  const { colonne, sens } = ref.tri;
  return [...(ref.donnees?.arrets ?? [])].sort((a, b) => {
    const x = valeurTri(a, colonne), y = valeurTri(b, colonne);
    return (typeof x === "number" ? x - y : String(x).localeCompare(String(y), "fr")) * sens;
  });
}
function dessinerTableau() {
  const lignes = lignesTriees();
  const total = ref.donnees.indicateurs.arrets;
  $("#nb-lignes").textContent = `(${lignes.length}${total > lignes.length ? ` sur ${total}` : ""})`;
  for (const th of document.querySelectorAll("#t-arrets th")) {
    th.classList.toggle("tri-asc", th.dataset.tri === ref.tri.colonne && ref.tri.sens > 0);
    th.classList.toggle("tri-desc", th.dataset.tri === ref.tri.colonne && ref.tri.sens < 0);
  }
  const statut = (c) => STATUTS.find((s) => s.code === c);
  $("#t-arrets tbody").innerHTML = lignes.map((l) => `<tr data-tournee="${echapper(l.tournee)}">
    <td>${dateCourte(l.date)}</td><td>${echapper(l.nomTournee ?? "")}</td><td>${echapper(nomLivreur(l.livreur))}</td><td>${echapper(l.piece)}</td>
    <td>${echapper(l.intitule || l.client)}</td><td>${echapper(l.ville ?? "")}</td>
    <td><i class="pastille-statut" style="display:inline-block;width:9px;height:9px;border-radius:2px;margin-right:6px;background:${statut(l.statut)?.couleur}"></i>${statut(l.statut)?.nom ?? l.statut}</td>
    <td>${echapper(MOTIFS[l.motif] ?? l.motif ?? "")}</td><td>${heure(l.heure)}</td><td class="nombre">${nombre(l.totalTTC, 2)}</td>
  </tr>`).join("") || `<tr><td colspan="10" class="vide">Aucun arrêt sur ces filtres.</td></tr>`;
}
$("#t-arrets thead").addEventListener("click", (e) => {
  const th = e.target.closest("[data-tri]");
  if (!th) return;
  ref.tri = { colonne: th.dataset.tri, sens: ref.tri.colonne === th.dataset.tri ? -ref.tri.sens : 1 };
  dessinerTableau();
});
$("#t-arrets tbody").addEventListener("click", (e) => {
  const tr = e.target.closest("[data-tournee]");
  if (tr) location.href = `./?tournee=${encodeURIComponent(tr.dataset.tournee)}`;
});
$("#btn-export").addEventListener("click", () => {
  const cellule = (v) => `"${String(v ?? "").replace(/"/g, '""')}"`;
  const lignes = lignesTriees().map((l) => [l.date.slice(0, 10), l.nomTournee, nomLivreur(l.livreur), nomDepot(l.depot), l.piece, l.client, l.intitule, l.ville,
    STATUTS.find((s) => s.code === l.statut)?.nom ?? l.statut, MOTIFS[l.motif] ?? l.motif, heure(l.heure), l.receptionnaire, String(l.totalTTC).replace(".", ",")].map(cellule).join(";"));
  const entete = ["Date", "Tournée", "Livreur", "Dépôt", "Pièce", "Code client", "Client", "Ville", "Statut", "Raison", "Heure", "Reçu par", "TTC"].map(cellule).join(";");
  // BOM UTF-8 + point-virgule : Excel en français l'ouvre directement.
  const blob = new Blob(["﻿" + [entete, ...lignes].join("\r\n")], { type: "text/csv;charset=utf-8" });
  const a = document.createElement("a");
  a.href = URL.createObjectURL(blob);
  a.download = `livraisons-${iso(new Date())}.csv`;
  a.click();
  URL.revokeObjectURL(a.href);
});

// ---------- Démarrage ----------
(async () => {
  const p = Object.fromEntries(new URLSearchParams(location.search));
  appliquerFiltres(Object.keys(p).length ? p : periode("30"));
  [ref.collaborateurs, ref.depots] = await Promise.all([api("/collaborateurs").catch(() => []), api("/depots").catch(() => [])]);
  // Les listes de filtres sont remplies par le tableau ; on garde la valeur demandée dans l'adresse.
  for (const cle of ["livreur", "depot"]) if (p[cle]) $(CHAMPS[cle]).innerHTML += `<option value="${echapper(p[cle])}">${echapper(p[cle])}</option>`;
  appliquerFiltres(Object.keys(p).length ? p : periode("30"));
  charger();
})();
