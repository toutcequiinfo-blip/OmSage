// Tableau de bord Sage 100 : comptable et commercial, multidimensionnels.
// Les chiffres viennent de l'instantané de l'API (relu dans Sage aux heures prévues) ; le détail d'une cellule est lu dans Sage au clic.
import { barres, courbes, barresH, anneau, miniCourbe, nombre, COULEURS } from "./graphiques.js";
import { preparerChoix, societeChoisie, nomSociete } from "../commun/societes.js";

const $ = (s, r = document) => r.querySelector(s);
const echapper = (t) => String(t ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
// Montants : 1 234 567,00 partout (tuiles, tableaux, détails, infobulles) ; seuls les axes des graphiques restent abrégés.
const montant = (n) => nombre(n, 2);
const date = (d) => (d ? new Date(d).toLocaleDateString("fr-FR") : "");
const heure = (d) => (d ? new Date(d).toLocaleTimeString("fr-FR", { hour: "2-digit", minute: "2-digit" }) : "");
const MOIS = ["janv.", "févr.", "mars", "avr.", "mai", "juin", "juil.", "août", "sept.", "oct.", "nov.", "déc."];
const moisCourt = (cle) => (/^\d{4}-\d{2}$/.test(cle) ? `${MOIS[Number(cle.slice(5)) - 1]} ${cle.slice(2, 4)}` : cle);

// ---------- Réglages du poste et session ----------
function lire(cle) { try { return JSON.parse(localStorage.getItem(cle)); } catch { return null; } }
function ecrire(cle, v) { try { localStorage.setItem(cle, JSON.stringify(v)); } catch { /* navigation privée */ } }
const reglages = () => lire("tdb.reglages") ?? {};
const majReglages = (r) => ecrire("tdb.reglages", { ...reglages(), ...r });
const cleApi = () => reglages().cle || lire("borne.reglages")?.cle || lire("crm.reglages")?.cle || lire("livraison.reglages")?.cle || "";
const session = () => { const s = lire("tdb.session"); return s && new Date(s.expiration) > new Date() ? s : null; };

class ErreurApi extends Error { constructor(message, statut, code) { super(message); this.statut = statut; this.code = code; } }

async function api(chemin, { methode = "GET", corps } = {}) {
  let r;
  try {
    r = await fetch(`/api/v1${chemin}`, {
      method: methode, cache: "no-store",
      headers: { "X-Api-Key": cleApi(), ...(session() ? { Authorization: `Bearer ${session().jeton}` } : {}),
        ...(session()?.dossier ? { "X-Dossier": session().dossier } : {}), ...(corps ? { "Content-Type": "application/json" } : {}) },
      body: corps ? JSON.stringify(corps) : undefined,
    });
  } catch { throw new ErreurApi("Serveur injoignable. Vérifiez le réseau.", 0); }
  if (r.status === 204) return null;
  const d = await r.json().catch(() => null);
  if (r.status === 401) {
    ouvrirConnexion(d?.erreur ? "Clé d'API absente ou refusée." : null, !!d?.erreur);
    throw new ErreurApi(d?.message || d?.erreur || "Connexion requise.", 401, d?.code);
  }
  if (d?.code === "DOSSIER_REQUIS" || d?.code === "DOSSIER_DIFFERENT") {
    // Plusieurs sociétés : la connexion choisit la société.
    localStorage.removeItem("tdb.session");
    ouvrirConnexion(null);
    throw new ErreurApi(d.message, 401, d.code);
  }
  if (!r.ok) throw new ErreurApi(d?.message || d?.erreurs?.join(" ") || `Erreur ${r.status}`, r.status, d?.code);
  return d;
}

// ---------- État ----------
const ONGLETS = {
  compta: [
    ["synthese", "Synthèse", "compta"], ["explorateur", "Explorateur", "compta"], ["balance", "Balance", "compta"], ["charges", "Charges & produits", "compta"],
    ["tresorerie", "Trésorerie", "compta"], ["recouvrement", "Recouvrement clients", "clients"], ["fournisseurs", "Fournisseurs", "fournisseurs"],
    ["rapprochement", "Rapprochement", "compta"],
  ],
  commercial: [
    ["direction", "Direction", "commercial"], ["ventes", "Explorateur", "commercial"], ["clients", "Clients", "commercial"], ["articles", "Articles & marges", "commercial"],
    ["commandes", "Commandes", "commercial"], ["transformation", "Satisfaction client", "commercial"], ["stock", "Stock", "commercial"], ["recouvrement", "Recouvrement", "clients"], ["achats", "Achats", "achats"],
    ["objectifs", "Objectifs", "commercial"],
  ],
};
const etat = { meta: null, tableau: null, onglet: null, exercice: null, cache: new Map(), explorateurs: {}, genere: null, rendu: 0 };

function bandeau(texte, info = false) {
  const b = $("#bandeau");
  b.hidden = !texte;
  b.textContent = texte ?? "";
  b.className = `bandeau${info ? " info" : ""}`;
}

/** Lecture mise en cache jusqu'à la prochaine actualisation (les écrans ne relisent pas l'instantané à chaque clic). */
async function lireCache(chemin) {
  if (!etat.cache.has(chemin)) etat.cache.set(chemin, api(chemin).catch((e) => { etat.cache.delete(chemin); throw e; }));
  return etat.cache.get(chemin);
}
const exercice = () => etat.meta.exercices.find((e) => e.cle === etat.exercice) ?? etat.meta.exercices[0];
const moisDe = (d) => String(d).slice(0, 7);
const jourDe = (d) => String(d).slice(0, 10);
// Dernier jour du mois « 2026-02 » -> « 2026-02-28 ».
// Semaine ISO « 2026-S41 » : du lundi au dimanche.
const lundiSemaine = (cle) => { const a = Number(cle.slice(0, 4)), n = Number(cle.slice(6)); const j4 = new Date(Date.UTC(a, 0, 4));
  return new Date(j4.getTime() + ((n - 1) * 7 - ((j4.getUTCDay() + 6) % 7)) * 86400000); };
const isoJour = (d) => d.toISOString().slice(0, 10);
const finDuMois = (m) => { const [a, n] = m.split("-").map(Number); return `${m}-${String(new Date(a, n, 0).getDate()).padStart(2, "0")}`; };
const qs = (o) => Object.entries(o).filter(([, v]) => v !== undefined && v !== null && v !== "").map(([k, v]) => `${k}=${encodeURIComponent(v)}`).join("&");

// ---------- Démarrage, connexion ----------
function ouvrirConnexion(message, demanderCle = false) {
  const d = $("#connexion");
  $("#champ-cle").hidden = !demanderCle && !!cleApi();
  $("#form-connexion").cle.value = cleApi();
  $("#form-connexion").utilisateur.value = session()?.utilisateur ?? reglages().utilisateur ?? "";
  const err = $("#erreur-connexion");
  err.hidden = !message;
  err.textContent = message ?? "";
  if (!d.open) d.showModal();
  if (cleApi()) preparerChoix($("#choix-dossier"), $("#form-connexion").dossier, cleApi());
}

$("#form-connexion").addEventListener("submit", async (e) => {
  e.preventDefault();
  const f = e.target;
  const bouton = $("#btn-connexion");
  bouton.disabled = true;
  try {
    if (f.cle.value.trim()) majReglages({ cle: f.cle.value.trim() });
    // Clé tout juste saisie : la liste des sociétés n'était pas encore connue, il faut en choisir une.
    const choixCache = $("#choix-dossier").hidden;
    if ((await preparerChoix($("#choix-dossier"), f.dossier, cleApi())).length > 1 && choixCache) throw new Error("Choisissez la société.");
    localStorage.removeItem("tdb.session");
    const dossier = societeChoisie(f.dossier);
    const r = await api("/connexion", { methode: "POST", corps: { utilisateur: f.utilisateur.value.trim(), motDePasse: f.motDePasse.value, ...(dossier ? { dossier } : {}) } });
    ecrire("tdb.session", { jeton: r.jeton, utilisateur: r.utilisateur, expiration: r.expiration, dossier: r.dossier });
    etat.cache.clear();
    majReglages({ utilisateur: r.utilisateur });
    f.motDePasse.value = "";
    $("#connexion").close();
    bandeau(null);
    await demarrer();
  } catch (err) {
    $("#erreur-connexion").hidden = false;
    $("#erreur-connexion").textContent = err.message;
    if (err.statut === 401 && !session()) $("#champ-cle").hidden = false;
  } finally {
    bouton.disabled = false;
  }
});
$("#connexion").addEventListener("cancel", (e) => e.preventDefault());

$("#btn-utilisateur").addEventListener("click", () => {
  if (!session()) return ouvrirConnexion(null);
  if (!confirm(`Se déconnecter (${session().utilisateur}) ?`)) return;
  localStorage.removeItem("tdb.session");
  location.reload();
});

function appliquerTheme(theme) {
  document.documentElement.dataset.theme = theme === "clair" ? "clair" : "";
  $('meta[name="theme-color"]').content = theme === "clair" ? "#ffffff" : "#0f141b";
}
$("#btn-theme").addEventListener("click", () => {
  const t = reglages().theme === "clair" ? "sombre" : "clair";
  majReglages({ theme: t });
  appliquerTheme(t);
  afficherOnglet();
});
appliquerTheme(reglages().theme);

$("#btn-actualiser").addEventListener("click", async () => {
  try {
    await api("/tableau-de-bord/actualiser", { methode: "POST" });
    bandeau("Actualisation lancée : les chiffres se mettront à jour dans quelques instants.", true);
    setTimeout(suivreEtat, 2000);
  } catch (e) { bandeau(e.message); }
});

// Documents comptés dans le CA (réglage de la société, modifiable par la Direction), dans l'ordre de Sage.
const DOCUMENTS = [
  ["livraisons", "Bons de livraison", "non encore facturés"],
  ["retours", "Bons de retour", "en négatif"],
  ["avoirsFinanciers", "Bons d'avoir financier", "en négatif"],
  ["factures", "Factures", "et factures comptabilisées"],
  ["facturesRetour", "Factures de retour", "en négatif"],
  ["facturesAvoir", "Factures d'avoir", "en négatif"],
];
const texteDocuments = (d) => DOCUMENTS.filter(([cle]) => d?.[cle] ?? ["factures", "facturesRetour", "facturesAvoir"].includes(cle)).map(([, libelle]) => libelle).join(", ") || "aucun document";

$("#btn-reglages").addEventListener("click", () => {
  const d = etat.meta.documents ?? {};
  ouvrirDetail("Réglages du tableau de bord", nomSociete(session()?.dossier) ?? "", `
    <form id="form-reglages" class="reglages">
      <p><b>Documents comptés dans le CA, les quantités et les marges</b> (ventes et achats)</p>
      ${DOCUMENTS.map(([cle, libelle, note]) => `<label class="case"><input type="checkbox" name="${cle}" ${d[cle] ? "checked" : ""}> ${libelle} <span class="discret">(${note})</span></label>`).join("")}
      <p class="discret">Les bons ont déjà fait bouger le stock. Pas de double compte : un bon transformé en facture n'existe plus comme bon dans Sage.
        Le rapprochement avec la comptabilité ne compte que les factures.</p>
      <div class="boutons"><button type="submit" class="principal">Enregistrer</button></div>
    </form>`);
  $("#form-reglages").addEventListener("submit", async (e) => {
    e.preventDefault();
    const f = e.target;
    try {
      await api("/tableau-de-bord/reglages", { methode: "PUT", corps: { documents: Object.fromEntries(DOCUMENTS.map(([cle]) => [cle, f[cle].checked])) } });
      etat.meta = await api("/tableau-de-bord/etat");
      etat.cache.clear();
      $("#detail").close();
      bandeau(`CA calculé sur : ${texteDocuments(etat.meta.documents)}.`, true);
      afficherOnglet();
    } catch (err) { bandeau(err.message); }
  });
});

$("#btn-etat").addEventListener("click", () => {
  const m = etat.meta;
  ouvrirDetail("Actualisation des données", "", `
    <p>Dernière lecture de Sage : <b>${m.genere ? new Date(m.genere).toLocaleString("fr-FR") : "pas encore faite"}</b>${m.dureeSecondes ? ` en ${nombre(m.dureeSecondes, 1)} s` : ""}.</p>
    <p>Prochaine actualisation automatique : ${m.prochaine ? new Date(m.prochaine).toLocaleString("fr-FR") : "–"}.</p>
    <p>Documents comptés dans le CA : ${echapper(texteDocuments(m.documents))}.</p>
    ${m.erreurs?.length ? `<p class="erreur">Parties non relues (version précédente gardée) :</p><ul>${m.erreurs.map((x) => `<li>${echapper(x)}</li>`).join("")}</ul>` : "<p>Toutes les parties ont été lues.</p>"}
    <p class="discret">Profil : ${echapper(m.profil)}${m.utilisateur ? ` (${echapper(m.utilisateur)})` : ""}. Lecture seule : rien n'est écrit dans Sage.</p>`);
});

async function suivreEtat() {
  try {
    const m = await api("/tableau-de-bord/etat");
    const change = m.genere !== etat.genere;
    etat.meta = m;
    afficherEtat();
    if (change && etat.genere !== null) { etat.cache.clear(); bandeau(null); afficherOnglet(); }
    etat.genere = m.genere;
    if (m.enCours || !m.genere) setTimeout(suivreEtat, 3000);
  } catch { /* hors ligne : on réessaiera */ }
}
setInterval(suivreEtat, 60000);

function afficherEtat() {
  const m = etat.meta;
  const b = $("#btn-etat");
  b.className = `etat${m.enCours || !m.genere ? " encours" : m.erreurs?.length ? " attention" : ""}`;
  $("#etat-texte").textContent = m.enCours || !m.genere ? "Lecture de Sage…" : `Actualisé à ${heure(m.genere)}`;
  $("#btn-actualiser").hidden = !m.droits.actualiser;
  $("#btn-reglages").hidden = !m.droits.reglages;
  $("#btn-utilisateur").textContent = m.utilisateur ? `👤 ${[m.utilisateur, nomSociete(session()?.dossier)].filter(Boolean).join(" · ")}` : "Se connecter";
  $("#btn-utilisateur").hidden = !m.utilisateur && !session();
}

async function demarrer() {
  try {
    etat.meta = await api("/tableau-de-bord/etat");
  } catch (e) {
    if (e.statut === 403) { bandeau(e.message); ouvrirConnexion(e.message); }
    else if (e.statut !== 401) bandeau(e.message);
    return;
  }
  etat.genere = etat.meta.genere;
  afficherEtat();
  const sel = $("#exercice");
  sel.innerHTML = etat.meta.exercices.map((e) => `<option value="${e.cle}">${echapper(e.intitule)}</option>`).join("");
  etat.exercice = etat.meta.exercices.some((e) => e.cle === reglages().exercice) ? reglages().exercice : etat.meta.exerciceCourant;
  sel.value = etat.exercice;
  const d = etat.meta.droits;
  for (const b of document.querySelectorAll("[data-tableau]")) b.hidden = b.dataset.tableau === "compta" ? !d.compta : !d.commercial;
  if (!etat.meta.genere || etat.meta.enCours) { bandeau("Première lecture de Sage en cours : les écrans se rempliront dès qu'elle sera terminée.", true); setTimeout(suivreEtat, 3000); }
  naviguer(location.hash);
}

$("#exercice").addEventListener("change", (e) => {
  etat.exercice = e.target.value;
  majReglages({ exercice: etat.exercice });
  etat.explorateurs = {};
  afficherOnglet();
});

// ---------- Navigation ----------
function ongletsVisibles(tableau) {
  const d = etat.meta.droits;
  return ONGLETS[tableau].filter(([, , droit]) => d[droit]);
}
function naviguer(hash) {
  const [t, o] = (hash || "").replace(/^#/, "").split("/");
  const d = etat.meta.droits;
  const permis = (x) => ONGLETS[x] && (x === "compta" ? d.compta : d.commercial);
  const tableau = [t, reglages().tableau].find(permis) ?? (d.compta ? "compta" : "commercial");
  const visibles = ongletsVisibles(tableau);
  const onglet = visibles.some(([c]) => c === o) ? o : visibles[0]?.[0];
  etat.tableau = tableau;
  etat.onglet = onglet;
  majReglages({ tableau });
  history.replaceState(null, "", `#${tableau}/${onglet}`);
  for (const b of document.querySelectorAll("[data-tableau]")) b.classList.toggle("actif", b.dataset.tableau === tableau);
  $("#onglets").innerHTML = visibles.map(([c, n]) => `<button type="button" data-onglet="${c}" class="${c === onglet ? "actif" : ""}">${n}</button>`).join("");
  afficherOnglet();
}
document.querySelectorAll("[data-tableau]").forEach((b) => b.addEventListener("click", () => naviguer(`#${b.dataset.tableau}`)));
$("#onglets").addEventListener("click", (e) => {
  const b = e.target.closest("[data-onglet]");
  if (b) naviguer(`#${etat.tableau}/${b.dataset.onglet}`);
});
window.addEventListener("hashchange", () => etat.meta && naviguer(location.hash));

const ECRANS = {
  "compta/synthese": syntheseCompta, "compta/explorateur": (c) => explorateur(c, "compta-explorateur"), "compta/balance": (c) => explorateur(c, "balance"),
  "compta/charges": chargesProduits, "compta/tresorerie": tresorerie, "compta/recouvrement": (c) => recouvrement(c, "clients"),
  "compta/fournisseurs": (c) => recouvrement(c, "fournisseurs"), "compta/rapprochement": rapprochement,
  "commercial/direction": direction, "commercial/ventes": (c) => explorateur(c, "ventes"), "commercial/clients": clients,
  "commercial/articles": (c) => explorateur(c, "articles"), "commercial/commandes": commandes, "commercial/transformation": (c) => explorateur(c, "transformation"), "commercial/stock": stock,
  "commercial/recouvrement": (c) => recouvrement(c, "clients"), "commercial/achats": (c) => explorateur(c, "achats"), "commercial/objectifs": objectifs,
};

async function afficherOnglet() {
  if (!etat.meta || !etat.onglet) return;
  const numero = ++etat.rendu;
  const c = $("#contenu");
  c.innerHTML = '<p class="vide">Chargement…</p>';
  try {
    const conteneur = document.createElement("div");
    conteneur.className = "contenu-ecran";
    conteneur.style.display = "contents";
    await ECRANS[`${etat.tableau}/${etat.onglet}`](conteneur);
    if (numero !== etat.rendu) return;
    c.innerHTML = "";
    c.appendChild(conteneur);
    conteneur.dispatchEvent(new Event("affiche"));
  } catch (e) {
    if (numero === etat.rendu) c.innerHTML = `<p class="vide erreur">${echapper(e.message)}</p>`;
  }
}

// ---------- Éléments communs ----------
function variation(v, inverse = false) {
  if (v == null) return "";
  const bon = inverse ? v <= 0 : v >= 0;
  return `<span class="badge ${bon ? "pos" : "neg"}">${v >= 0 ? "▲" : "▼"} ${nombre(Math.abs(v), 1)} %</span>`;
}
function tuile({ libelle, valeur, pied = "", niveau = "", mini = "", vers, titre = "" }) {
  return `<div class="tuile ${niveau} ${vers ? "cliquable" : ""}" ${vers ? `data-vers="${vers}"` : ""} title="${echapper(titre)}">
    <span class="libelle">${libelle}</span><span class="valeur">${valeur}</span><span class="pied">${pied}</span>${mini}</div>`;
}
function alertes(liste) {
  if (!liste?.length) return "";
  return `<div class="alertes">${liste.map((a) => `<span class="alerte ${a.niveau}">${echapper(a.message)}</span>`).join("")}</div>`;
}
function carte(classe, titre, id, aDroite = "") {
  return `<article class="carte ${classe}"><h3><span>${titre}</span>${aDroite}</h3><div id="${id}"></div></article>`;
}
function relierTuiles(c) {
  c.addEventListener("click", (e) => {
    const t = e.target.closest("[data-vers]");
    if (t) naviguer(`#${t.dataset.vers}`);
  });
}
function ouvrirDetail(titre, sousTitre, html) {
  $("#detail-titre").textContent = titre;
  $("#detail-sous-titre").textContent = sousTitre ?? "";
  $("#detail-corps").innerHTML = html;
  if (!$("#detail").open) $("#detail").showModal();
}
$("#detail").addEventListener("click", (e) => { if (e.target.closest("[data-fermer]") || e.target === $("#detail")) $("#detail").close(); });

function exporterCsv(nom, entetes, lignes) {
  const champ = (v) => { const t = typeof v === "number" ? String(v).replace(".", ",") : String(v ?? ""); return /[;"\n]/.test(t) ? `"${t.replace(/"/g, '""')}"` : t; };
  const csv = "﻿" + [entetes, ...lignes].map((l) => l.map(champ).join(";")).join("\r\n");
  const a = document.createElement("a");
  a.href = URL.createObjectURL(new Blob([csv], { type: "text/csv;charset=utf-8" }));
  a.download = `${nom}.csv`;
  a.click();
  setTimeout(() => URL.revokeObjectURL(a.href), 1000);
}
/** Filtres clients : catégorie tarifaire et qualité (CT_Qualite) ; une liste n'apparaît que si la base en contient. */
const selectCollaborateurs = (id, valeur, libelleTous = "Tous les commerciaux") =>
  `<select id="${id}"><option value="">${libelleTous}</option>${etat.meta.collaborateurs.map((c) => `<option value="${c.numero}" ${String(valeur) === String(c.numero) ? "selected" : ""}>${echapper(c.intitule)}</option>`).join("")}</select>`;

// ---------- Filtres à choix multiple ----------
// Valeur d'un filtre : "" (tout), "A,B" (garder A et B) ou "!A,B" (tout sauf A et B). "~" = non renseigné.
// Le bouton porte la valeur dans son attribut value et émet « change » quand on l'applique, comme un select.
const CHOIX = {};
const VIDE = "~";
function lireChoix(v) {
  const t = String(v ?? "").trim();
  const exclure = t.startsWith("!");
  return { exclure, valeurs: t.replace(/^!/, "").split(",").map((x) => x.trim()).filter(Boolean) };
}
const ecrireChoix = ({ exclure, valeurs }) => (valeurs.length ? `${exclure ? "!" : ""}${valeurs.join(",")}` : "");
function resumeChoix(id) {
  const d = CHOIX[id];
  const { exclure, valeurs } = lireChoix(d.valeur);
  if (!valeurs.length) return d.tous;
  const nom = (v) => (v === VIDE ? "(non renseigné)" : d.noms.get(v) ?? v);
  const texte = valeurs.length <= 2 ? valeurs.map(nom).join(", ") : `${valeurs.length} sélectionnés`;
  return exclure ? `Sauf ${texte}` : texte;
}
/**
 * Bouton de filtre à cases à cocher. options : [{ valeur, intitule }] ; source : liste cherchée sur le serveur
 * (articles, clients, fournisseurs, tiers) pour les longues listes ; vide : propose « (non renseigné) ».
 */
function choix(id, { options = [], source, valeur = "", tous = "Tous", vide = false } = {}) {
  const noms = new Map(options.map((o) => [String(o.valeur), o.intitule ?? String(o.valeur)]));
  CHOIX[id] = { options: options.map((o) => ({ valeur: String(o.valeur), intitule: o.intitule ?? String(o.valeur) })), source, valeur: valeur ?? "", tous, vide, noms };
  const actif = lireChoix(valeur).valeurs.length > 0;
  return `<button type="button" class="choix ${actif ? "actif" : ""} ${lireChoix(valeur).exclure ? "exclu" : ""}" id="${id}" value="${echapper(valeur ?? "")}" data-choix="${id}"
    title="${echapper(resumeChoix(id))}"><span>${echapper(resumeChoix(id))}</span><span class="fleche">▾</span></button>`;
}
function ouvrirChoix(bouton) {
  fermerChoix();
  const d = CHOIX[bouton.dataset.choix];
  if (!d) return;
  const etatChoix = lireChoix(bouton.value);
  const coches = new Set(etatChoix.valeurs);
  let exclure = etatChoix.exclure;
  let affichees = d.options;
  const p = document.createElement("div");
  p.className = "choix-panneau";
  p.innerHTML = `
    <div class="segments"><button type="button" data-mode="garder">Garder</button><button type="button" data-mode="exclure">Exclure</button></div>
    <input type="search" class="choix-cherche" placeholder="${d.source ? "Rechercher (code ou nom)" : "Filtrer la liste"}">
    <div class="choix-actions"><button type="button" class="lien" data-tout>Tout cocher</button><button type="button" class="lien" data-rien>Tout décocher</button></div>
    <div class="choix-liste"></div>
    <div class="choix-pied"><span class="discret choix-compte"></span><span class="espace"></span><button type="button" data-effacer>Effacer</button><button type="button" class="principal" data-appliquer>Appliquer</button></div>`;
  document.body.appendChild(p);
  const r = bouton.getBoundingClientRect();
  p.style.top = `${Math.min(r.bottom + 4, window.innerHeight - 40)}px`;
  p.style.left = `${Math.max(8, Math.min(r.left, window.innerWidth - p.offsetWidth - 8))}px`;
  const liste = $(".choix-liste", p);
  const cherche = $(".choix-cherche", p);
  const modes = () => p.querySelectorAll("[data-mode]").forEach((b) => b.classList.toggle("actif", (b.dataset.mode === "exclure") === exclure));
  const compte = () => { $(".choix-compte", p).textContent = coches.size ? `${coches.size} ${exclure ? "exclu(s)" : "coché(s)"}` : "aucun filtre"; };
  const dessiner = () => {
    const q = cherche.value.trim().toLowerCase();
    const visibles = (d.source ? affichees : affichees.filter((o) => !q || `${o.valeur} ${o.intitule}`.toLowerCase().includes(q)));
    // Les valeurs cochées absentes de la liste affichée (recherche) restent visibles en tête.
    const enPlus = [...coches].filter((v) => !visibles.some((o) => o.valeur === v)).map((v) => ({ valeur: v, intitule: v === VIDE ? "(non renseigné)" : d.noms.get(v) ?? v }));
    const lignes = [...enPlus, ...(d.vide && !q && !enPlus.some((o) => o.valeur === VIDE) ? [{ valeur: VIDE, intitule: "(non renseigné)" }] : []), ...visibles];
    liste.innerHTML = lignes.map((o) => `<label class="case"><input type="checkbox" value="${echapper(o.valeur)}" ${coches.has(o.valeur) ? "checked" : ""}>
      <span>${o.intitule && o.intitule !== o.valeur && d.source ? `<b>${echapper(o.valeur)}</b> ${echapper(o.intitule)}` : echapper(o.intitule)}</span></label>`).join("")
      || '<p class="discret" style="padding:6px">Aucune valeur.</p>';
    compte();
  };
  let minuterie;
  const charger = async () => {
    if (!d.source) return dessiner();
    try {
      affichees = await api(`/tableau-de-bord/valeurs/${d.source}?${qs({ q: cherche.value.trim(), limite: 200 })}`);
      affichees = affichees.map((o) => ({ valeur: o.valeur, intitule: o.intitule ?? o.valeur }));
      affichees.forEach((o) => d.noms.set(o.valeur, o.intitule ? `${o.valeur} ${o.intitule}` : o.valeur));
    } catch (e) { affichees = []; }
    dessiner();
  };
  cherche.addEventListener("input", () => { clearTimeout(minuterie); minuterie = setTimeout(charger, d.source ? 250 : 0); });
  liste.addEventListener("change", (e) => { const v = e.target.value; if (e.target.checked) coches.add(v); else coches.delete(v); compte(); });
  p.querySelectorAll("[data-mode]").forEach((b) => b.addEventListener("click", () => { exclure = b.dataset.mode === "exclure"; modes(); compte(); }));
  $("[data-tout]", p).addEventListener("click", () => { liste.querySelectorAll("input").forEach((x) => { x.checked = true; coches.add(x.value); }); compte(); });
  $("[data-rien]", p).addEventListener("click", () => { liste.querySelectorAll("input").forEach((x) => { x.checked = false; coches.delete(x.value); }); compte(); });
  const appliquer = (valeur) => {
    fermerChoix();
    if (valeur === bouton.value) return;
    bouton.value = valeur;
    d.valeur = valeur;
    bouton.dispatchEvent(new Event("change", { bubbles: true }));
  };
  $("[data-effacer]", p).addEventListener("click", () => appliquer(""));
  $("[data-appliquer]", p).addEventListener("click", () => appliquer(ecrireChoix({ exclure, valeurs: [...coches] })));
  p.addEventListener("keydown", (e) => { if (e.key === "Escape") fermerChoix(); if (e.key === "Enter") $("[data-appliquer]", p).click(); });
  modes();
  charger();
  cherche.focus();
}
function fermerChoix() { document.querySelectorAll(".choix-panneau").forEach((x) => x.remove()); }
document.addEventListener("click", (e) => {
  const b = e.target.closest("button.choix");
  if (b) { e.preventDefault(); return document.querySelector(".choix-panneau") && CHOIX.ouvert === b.id ? (fermerChoix(), (CHOIX.ouvert = null)) : (ouvrirChoix(b), (CHOIX.ouvert = b.id)); }
  if (!e.target.closest(".choix-panneau")) { fermerChoix(); CHOIX.ouvert = null; }
});
window.addEventListener("resize", fermerChoix);

const choixClients = (prefixe, f) => [
  etat.meta.categories.length ? `<label>Catégorie ${choix(`${prefixe}-categorie`, { options: etat.meta.categories.map((x) => ({ valeur: x.numero, intitule: x.intitule })), valeur: f.categorie, tous: "Toutes", vide: true })}</label>` : "",
  (etat.meta.qualites ?? []).length ? `<label>Qualité ${choix(`${prefixe}-qualite`, { options: etat.meta.qualites.map((q) => ({ valeur: q, intitule: q })), valeur: f.qualite, tous: "Toutes", vide: true })}</label>` : "",
].join("");
const choixCollaborateurs = (id, valeur, tous = "Tous") =>
  choix(id, { options: etat.meta.collaborateurs.map((c) => ({ valeur: c.numero, intitule: c.intitule })), valeur, tous, vide: true });

// ==================== Tableau comptable ====================
async function syntheseCompta(c) {
  const s = await lireCache(`/tableau-de-bord/compta/synthese?exercice=${etat.exercice}`);
  const cr = s.creancesClients, de = s.dettesFournisseurs;
  c.innerHTML = `
    ${alertes(s.alertes)}
    <section class="tuiles">
      ${tuile({ libelle: "Produits", valeur: montant(s.produits), pied: `${variation(s.variationProduits)} N-1 ${montant(s.produitsN1)}`, vers: "compta/charges", mini: miniCourbe(s.mois.map((m) => m.produits)) })}
      ${tuile({ libelle: "Charges", valeur: montant(s.charges), pied: `${variation(s.variationCharges, true)} N-1 ${montant(s.chargesN1)}`, vers: "compta/charges", mini: miniCourbe(s.mois.map((m) => m.charges), "var(--c4)") })}
      ${tuile({ libelle: "Résultat", valeur: `<span class="${s.resultat < 0 ? "neg" : ""}">${montant(s.resultat)}</span>`, pied: `N-1 ${montant(s.resultatN1)}`, niveau: s.resultat < 0 ? "attention" : "" })}
      ${tuile({ libelle: "Trésorerie", valeur: montant(s.tresorerie), pied: `${variation(s.variationTresorerie)} N-1 ${montant(s.tresorerieN1)}`, vers: "compta/tresorerie",
        niveau: s.alertes.some((a) => a.code === "tresorerie") ? "critique" : "", mini: miniCourbe(s.mois.filter((m) => m.mois <= moisDe(s.au)).map((m) => m.tresorerie), "var(--c3)") })}
      ${tuile({ libelle: "Encaissements", valeur: montant(s.encaissements), pied: "banques et caisses" })}
      ${tuile({ libelle: "Décaissements", valeur: montant(s.decaissements), pied: "banques et caisses" })}
      ${tuile({ libelle: "Créances clients", valeur: montant(cr.total), pied: `échu ${montant(cr.echu)} · +90 j ${montant(cr.plus90)}`, vers: "compta/recouvrement", niveau: cr.plus90 > 0 ? "attention" : "" })}
      ${de ? tuile({ libelle: "Dettes fournisseurs", valeur: montant(de.total), pied: `échu ${montant(de.echu)}`, vers: "compta/fournisseurs" }) : ""}
    </section>
    <section class="grille">
      ${carte("l8", "Produits, charges et résultat cumulé", "g-resultat", `<span class="discret">${date(s.du)} – ${date(s.au)}</span>`)}
      ${carte("l4", "Charges par nature", "g-charges")}
      ${carte("l8", "Trésorerie fin de mois, N et N-1", "g-treso")}
      ${carte("l4", "Produits par nature", "g-produits")}
    </section>`;
  relierTuiles(c);
  c.addEventListener("affiche", () => {
    const cat = s.mois.map((m) => moisCourt(m.mois));
    barres($("#g-resultat"), { categories: cat, series: [
      { nom: "Produits", valeurs: s.mois.map((m) => m.produits), couleur: "var(--c1)" },
      { nom: "Charges", valeurs: s.mois.map((m) => m.charges), couleur: "var(--c4)" },
      { nom: "Résultat cumulé", type: "ligne", valeurs: s.mois.map((m) => (m.mois <= moisDe(s.au) ? m.resultatCumule : null)), couleur: "var(--c3)" },
      { nom: "Résultat cumulé N-1", type: "ligne", pointille: true, valeurs: s.mois.map((m) => m.resultatCumuleN1), couleur: "var(--discret)" },
    ] });
    courbes($("#g-treso"), { categories: cat, series: [
      { nom: "Trésorerie", valeurs: s.mois.map((m) => (m.mois <= moisDe(s.au) ? m.tresorerie : null)), couleur: "var(--c3)" },
      { nom: "N-1", valeurs: s.mois.map((m) => m.tresorerieN1), couleur: "var(--discret)", pointille: true },
    ] });
    anneau($("#g-charges"), s.charges2.map((x) => ({ libelle: x.intitule, valeur: x.montant, cle: x.cle })),
      { surClic: (p) => explorerAvec("compta-explorateur", { comptes: p.cle, lignes: "compte", colonnes: "mois" }) });
    anneau($("#g-produits"), s.produits2.map((x) => ({ libelle: x.intitule, valeur: x.montant, cle: x.cle })),
      { surClic: (p) => explorerAvec("compta-explorateur", { comptes: p.cle, lignes: "compte", colonnes: "mois", mesure: "soldeCrediteur" }) });
  });
}

async function chargesProduits(c) {
  const s = await lireCache(`/tableau-de-bord/compta/synthese?exercice=${etat.exercice}`);
  const sens = etat.explorateurs.charges?.comptes === "7" ? "produits" : "charges";
  const haut = document.createElement("div");
  haut.style.display = "contents";
  haut.innerHTML = `<section class="tuiles">
      ${tuile({ libelle: "Produits", valeur: montant(s.produits), pied: variation(s.variationProduits) })}
      ${tuile({ libelle: "Charges", valeur: montant(s.charges), pied: variation(s.variationCharges, true) })}
      ${tuile({ libelle: "Résultat", valeur: montant(s.resultat), pied: `N-1 ${montant(s.resultatN1)}` })}
      ${tuile({ libelle: "Marge sur charges", valeur: s.produits ? `${nombre((s.resultat / s.produits) * 100, 1)} %` : "–", pied: "résultat / produits" })}
    </section>
    <section class="grille">${carte("l12", "Produits et charges par mois, N et N-1", "g-cp")}</section>`;
  c.appendChild(haut);
  const bas = document.createElement("div");
  bas.style.display = "contents";
  c.appendChild(bas);
  await explorateur(bas, "charges", sens);
  c.addEventListener("affiche", () => {
    barres($("#g-cp"), { categories: s.mois.map((m) => moisCourt(m.mois)), series: [
      { nom: "Produits", valeurs: s.mois.map((m) => m.produits), couleur: "var(--c1)" },
      { nom: "Charges", valeurs: s.mois.map((m) => m.charges), couleur: "var(--c4)" },
      { nom: "Produits N-1", type: "ligne", pointille: true, valeurs: s.mois.map((m) => m.produitsN1), couleur: "var(--c1)" },
      { nom: "Charges N-1", type: "ligne", pointille: true, valeurs: s.mois.map((m) => m.chargesN1), couleur: "var(--c4)" },
    ] });
    bas.dispatchEvent(new Event("affiche"));
  });
}

async function tresorerie(c) {
  const [s, rc, rf] = await Promise.all([
    lireCache(`/tableau-de-bord/compta/synthese?exercice=${etat.exercice}`),
    lireCache("/tableau-de-bord/recouvrement/clients"),
    etat.meta.droits.fournisseurs ? lireCache("/tableau-de-bord/recouvrement/fournisseurs") : null,
  ]);
  const haut = document.createElement("div");
  haut.style.display = "contents";
  haut.innerHTML = `
    ${alertes(s.alertes.filter((a) => a.code === "tresorerie"))}
    <section class="tuiles">
      ${tuile({ libelle: "Trésorerie", valeur: montant(s.tresorerie), pied: `${variation(s.variationTresorerie)} N-1 ${montant(s.tresorerieN1)}` })}
      ${tuile({ libelle: "Encaissements", valeur: montant(s.encaissements), pied: "sur la période" })}
      ${tuile({ libelle: "Décaissements", valeur: montant(s.decaissements), pied: "sur la période" })}
      ${tuile({ libelle: "Flux net", valeur: `<span class="${s.encaissements - s.decaissements < 0 ? "neg" : "pos"}">${montant(s.encaissements - s.decaissements)}</span>`, pied: "encaissements − décaissements" })}
    </section>
    <section class="grille">
      ${carte("l8", "Trésorerie fin de mois, N et N-1", "g-treso2")}
      ${carte("l4", "Soldes des banques et caisses", "g-comptes")}
      ${carte("l6", "Encaissements clients par mode (12 mois)", "g-modes-c")}
      ${rf ? carte("l6", "Règlements fournisseurs par mode (12 mois)", "g-modes-f") : ""}
    </section>`;
  c.appendChild(haut);
  const bas = document.createElement("div");
  bas.style.display = "contents";
  c.appendChild(bas);
  await explorateur(bas, "tresorerie");
  c.addEventListener("affiche", () => {
    courbes($("#g-treso2"), { categories: s.mois.map((m) => moisCourt(m.mois)), series: [
      { nom: "Trésorerie", valeurs: s.mois.map((m) => (m.mois <= moisDe(s.au) ? m.tresorerie : null)), couleur: "var(--c3)" },
      { nom: "N-1", valeurs: s.mois.map((m) => m.tresorerieN1), couleur: "var(--discret)", pointille: true },
    ] });
    barresH($("#g-comptes"), s.comptesTresorerie.map((x) => ({ libelle: `${x.compte} ${x.intitule ?? ""}`, valeur: x.solde, couleur: x.solde < 0 ? "var(--erreur)" : "var(--c3)" })));
    anneau($("#g-modes-c"), rc.reglementsParMode.map((m) => ({ libelle: m.intitule, valeur: m.montant })));
    if (rf) anneau($("#g-modes-f"), rf.reglementsParMode.map((m) => ({ libelle: m.intitule, valeur: m.montant })));
    bas.dispatchEvent(new Event("affiche"));
  });
}

async function rapprochement(c) {
  const lignes = await lireCache(`/tableau-de-bord/compta/rapprochement?exercice=${etat.exercice}`);
  const tot = (k) => lignes.reduce((t, l) => t + l[k], 0);
  const ecart = (v, base) => `<td class="num ${Math.abs(v) > 0.5 ? (Math.abs(v) > Math.abs(base) * 0.02 ? "neg" : "") : "pos"}">${montant(v)}</td>`;
  c.innerHTML = `
    <section class="tuiles">
      ${tuile({ libelle: "Écart CA", valeur: montant(tot("ecartCa")), pied: `gestion ${montant(tot("caGestion"))} · compta ${montant(tot("caComptable"))}`, niveau: Math.abs(tot("ecartCa")) > 1 ? "attention" : "" })}
      ${tuile({ libelle: "Écart règlements clients", valeur: montant(tot("ecartReglements")), pied: `gestion ${montant(tot("reglementsGestion"))} · compta ${montant(tot("reglementsComptables"))}` })}
      ${etat.meta.droits.achats ? tuile({ libelle: "Écart achats", valeur: montant(tot("ecartAchats")), pied: `gestion ${montant(tot("achatsGestion"))} · compta ${montant(tot("achatsComptables"))}` }) : ""}
    </section>
    <section class="grille">
      ${carte("l12", "Chiffre d'affaires : gestion commerciale et comptabilité", "g-rappro")}
      <article class="carte l12"><h3><span>Contrôle mensuel</span><button type="button" class="lien" id="csv-rappro">Exporter</button></h3>
        <div class="table-defile"><table class="liste"><thead><tr><th>Mois</th><th class="num">CA gestion</th><th class="num">CA compta (70)</th><th class="num">Écart</th>
          <th class="num">Règl. gestion</th><th class="num">Règl. compta (41)</th><th class="num">Écart</th><th class="num">Achats gestion</th><th class="num">Achats compta (60)</th><th class="num">Écart</th></tr></thead>
        <tbody>${lignes.map((l) => `<tr><td>${moisCourt(l.mois)}</td><td class="num">${montant(l.caGestion)}</td><td class="num">${montant(l.caComptable)}</td>${ecart(l.ecartCa, l.caComptable)}
          <td class="num">${montant(l.reglementsGestion)}</td><td class="num">${montant(l.reglementsComptables)}</td>${ecart(l.ecartReglements, l.reglementsComptables)}
          <td class="num">${montant(l.achatsGestion)}</td><td class="num">${montant(l.achatsComptables)}</td>${ecart(l.ecartAchats, l.achatsComptables)}</tr>`).join("")}</tbody></table></div>
        <p class="discret">Un écart vient en général de factures pas encore comptabilisées, d'écritures saisies directement en comptabilité ou de règlements non transférés.</p>
      </article>
    </section>`;
  $("#csv-rappro", c).addEventListener("click", () => exporterCsv("rapprochement", ["Mois", "CA gestion", "CA compta", "Écart CA", "Règlements gestion", "Règlements compta", "Écart règlements", "Achats gestion", "Achats compta", "Écart achats"],
    lignes.map((l) => [l.mois, l.caGestion, l.caComptable, l.ecartCa, l.reglementsGestion, l.reglementsComptables, l.ecartReglements, l.achatsGestion, l.achatsComptables, l.ecartAchats])));
  c.addEventListener("affiche", () => barres($("#g-rappro"), { categories: lignes.map((l) => moisCourt(l.mois)), series: [
    { nom: "CA gestion commerciale", valeurs: lignes.map((l) => l.caGestion), couleur: "var(--c1)" },
    { nom: "CA comptabilisé", valeurs: lignes.map((l) => l.caComptable), couleur: "var(--c2)" },
    { nom: "Écart", type: "ligne", valeurs: lignes.map((l) => l.ecartCa), couleur: "var(--c5)" },
  ] }));
}

// ==================== Recouvrement (clients ou fournisseurs) ====================
async function recouvrement(c, type) {
  const f = etat.explorateurs[`rec-${type}`] ??= { commercial: "", categorie: "", qualite: "" };
  const r = await lireCache(`/tableau-de-bord/recouvrement/${type}?${qs({ commercial: f.commercial, categorie: f.categorie, qualite: f.qualite })}`);
  const t = r.total;
  const clients = type === "clients";
  const TR = [["nonEchu", "Non échu", "var(--c3)"], ["r1a30", "1 à 30 j", "var(--c7)"], ["r31a60", "31 à 60 j", "var(--c4)"], ["r61a90", "61 à 90 j", "var(--c6)"], ["plus90", "Plus de 90 j", "var(--c5)"]];
  c.innerHTML = `
    ${clients ? `<div class="commandes">${etat.meta.collaborateurs.length > 1 ? `<label>Commercial ${choixCollaborateurs("f-rec-co", f.commercial)}</label>` : ""}
      ${choixClients("f-rec", f)}</div>` : ""}
    <section class="tuiles">
      ${tuile({ libelle: clients ? "Total dû par les clients" : "Total dû aux fournisseurs", valeur: montant(t.total), pied: t.credits ? `dont crédits non affectés ${montant(t.credits)}` : "" })}
      ${tuile({ libelle: "Échu", valeur: montant(r.echu), pied: t.total > 0 ? `${nombre((r.echu / (t.total - t.credits)) * 100, 0)} % du dû` : "", niveau: r.echu > 0 ? "attention" : "" })}
      ${tuile({ libelle: "Plus de 90 jours", valeur: montant(t.plus90), niveau: t.plus90 > 0 ? "critique" : "" })}
      ${tuile({ libelle: "Retard moyen", valeur: `${nombre(r.retardMoyenJours)} j`, pied: "pondéré par les montants échus" })}
      ${tuile({ libelle: clients ? "Clients en retard" : "Fournisseurs en retard", valeur: nombre(r.tiersEnRetard), pied: `sur ${nombre(r.tiers.length)} avec un solde` })}
    </section>
    <section class="grille">
      ${carte("l4", "Balance âgée", "g-tranches")}
      ${clients && r.parCommercial.length ? carte("l4", "Échu par commercial", "g-rec-co") : ""}
      ${carte(clients && r.parCommercial.length ? "l4" : "l8", clients ? "Encaissements des 12 derniers mois" : "Règlements des 12 derniers mois", "g-rec-mois")}
      <article class="carte l12"><h3><span>${clients ? "Clients" : "Fournisseurs"} : soldes par ancienneté</span>
        <span><input id="f-rec-cherche" type="search" placeholder="Rechercher" style="min-height:28px"> <button type="button" class="lien" id="csv-rec">Exporter</button></span></h3>
        <div class="table-defile"><table class="liste" id="t-rec"></table></div></article>
    </section>`;
  const dessinerTable = () => {
    const q = ($("#f-rec-cherche", c)?.value ?? "").toLowerCase();
    const lignes = r.tiers.filter((l) => !q || `${l.tiers} ${l.intitule ?? ""}`.toLowerCase().includes(q));
    $("#t-rec", c).innerHTML = `<thead><tr><th>${clients ? "Client" : "Fournisseur"}</th>${clients ? "<th>Représentant</th>" : ""}${TR.map(([, n]) => `<th class="num">${n}</th>`).join("")}
      <th class="num">Crédits</th><th class="num">Total</th><th class="num">Retard max</th><th>Dernière relance</th><th>Dernière facture</th></tr></thead>
      <tbody>${lignes.slice(0, 500).map((l) => `<tr class="cliquable" data-tiers="${echapper(l.tiers)}"><td><b>${echapper(l.tiers)}</b> ${echapper(l.intitule ?? "")}</td>
        ${clients ? `<td>${echapper(l.representant ?? "")}</td>` : ""}${TR.map(([k]) => `<td class="num ${k === "plus90" && l.tranches[k] > 0 ? "neg" : ""}">${l.tranches[k] ? montant(l.tranches[k]) : ""}</td>`).join("")}
        <td class="num">${l.tranches.credits ? montant(l.tranches.credits) : ""}</td><td class="num"><b>${montant(l.tranches.total)}</b></td>
        <td class="num">${l.retardMaxJours ? `${l.retardMaxJours} j` : ""}</td><td>${date(l.derniereRelance)}</td><td>${date(l.derniereFacture)}</td></tr>`).join("") || `<tr><td colspan="12" class="vide">Aucun solde.</td></tr>`}</tbody>`;
  };
  dessinerTable();
  $("#f-rec-cherche", c).addEventListener("input", dessinerTable);
  $("#t-rec", c).addEventListener("click", async (e) => {
    const tr = e.target.closest("[data-tiers]");
    if (!tr) return;
    const tiers = tr.dataset.tiers;
    const l = r.tiers.find((x) => x.tiers === tiers);
    try {
      const pieces = await api(`/tableau-de-bord/recouvrement/${type}/${encodeURIComponent(tiers)}`);
      ouvrirDetail(`${tiers} ${l?.intitule ?? ""}`, `Solde ${montant(l?.tranches.total)} · échu ${montant((l?.tranches.total ?? 0) - (l?.tranches.nonEchu ?? 0) - (l?.tranches.credits ?? 0))}`,
        `<table class="liste"><thead><tr><th>Date</th><th>Journal</th><th>Pièce</th><th>Libellé</th><th>Échéance</th><th class="num">Montant</th><th class="num">Retard</th><th>Relance</th></tr></thead>
        <tbody>${pieces.map((p) => `<tr><td>${date(p.date)}</td><td>${echapper(p.journal ?? "")}</td><td>${echapper(p.piece ?? "")}</td><td>${echapper(p.libelle ?? "")}</td><td>${date(p.echeance)}</td>
          <td class="num ${p.montant < 0 ? "pos" : ""}">${montant(p.montant)}</td><td class="num ${p.joursRetard > 90 ? "neg" : ""}">${p.joursRetard ? `${p.joursRetard} j` : ""}</td><td>${date(p.derniereRelance)}</td></tr>`).join("")}</tbody></table>`);
    } catch (err) { bandeau(err.message); }
  });
  $("#csv-rec", c).addEventListener("click", () => exporterCsv(`balance-agee-${type}`, ["Tiers", "Intitulé", "Représentant", ...TR.map(([, n]) => n), "Crédits", "Total", "Retard max (j)", "Dernière relance"],
    r.tiers.map((l) => [l.tiers, l.intitule, l.representant, ...TR.map(([k]) => l.tranches[k]), l.tranches.credits, l.tranches.total, l.retardMaxJours, date(l.derniereRelance)])));
  const recharger = () => { etat.explorateurs[`rec-${type}`] = { commercial: $("#f-rec-co", c)?.value ?? "", categorie: $("#f-rec-categorie", c)?.value ?? "", qualite: $("#f-rec-qualite", c)?.value ?? "" }; afficherOnglet(); };
  $("#f-rec-co", c)?.addEventListener("change", recharger);
  $("#f-rec-categorie", c)?.addEventListener("change", recharger);
  $("#f-rec-qualite", c)?.addEventListener("change", recharger);
  c.addEventListener("affiche", () => {
    barresH($("#g-tranches"), TR.map(([k, n, couleur]) => ({ libelle: n, valeur: t[k], couleur })));
    if ($("#g-rec-co")) barresH($("#g-rec-co"), r.parCommercial.slice(0, 10).map((x) => ({ libelle: x.intitule, valeur: x.tranches.r1a30 + x.tranches.r31a60 + x.tranches.r61a90 + x.tranches.plus90, reference: x.tranches.total, couleur: "var(--c4)" })));
    barres($("#g-rec-mois"), { categories: r.reglementsParMois.map((m) => moisCourt(m.mois)), series: [{ nom: clients ? "Encaissements" : "Règlements", valeurs: r.reglementsParMois.map((m) => m.montant), couleur: "var(--c3)" }] });
  });
}

// ==================== Explorateur multidimensionnel ====================
const MESURES = {
  debit: "Débit", credit: "Crédit", solde: "Solde (D − C)", soldeCrediteur: "Solde (C − D)", nombre: "Écritures",
  ca: "CA HT", quantite: "Quantité", cout: "Coût de revient", marge: "Marge", taux: "Taux de marge %",
  commande: "Commandé HT", livre: "Livré HT", encours: "Reste à livrer", nonservi: "Non servi (soldé)", transfo: "Taux de satisfaction %",
  qteCommandee: "Qté commandée", qteLivree: "Qté livrée", transfoQte: "Taux en quantité %",
};
const MESURES_TYPE = {
  compta: ["debit", "credit", "solde", "soldeCrediteur", "nombre"], analytique: ["solde", "soldeCrediteur"], ventes: ["ca", "quantite", "cout", "marge", "taux"],
  transformation: ["transfo", "commande", "livre", "encours", "nonservi", "transfoQte", "qteCommandee", "qteLivree"],
};
const TEMPS = ["exercice", "annee", "trimestre", "mois", "semaine"];
// Intitulé court d'une période (mois « oct. 26 », semaine « S41 ») ; sinon l'intitulé du serveur.
const etiquetteAxe = (axe, cle, intitule, court) => (axe === "mois" ? moisCourt(cle) : axe === "semaine" && /^\d{4}-S\d{2}$/.test(cle) ? (court ? cle.slice(5) : intitule ?? cle) : intitule ?? cle);
// Axe proposé quand on zoome sur une ligne.
const ZOOM = {
  compta: { exercice: "mois", annee: "trimestre", trimestre: "mois", mois: "compte", semaine: "compte", classe: "radical", radical: "compte", compte: "mois", typeJournal: "journal", journal: "compte", tiers: "compte", section: "compte" },
  ventes: { exercice: "mois", annee: "trimestre", trimestre: "mois", mois: "article", semaine: "article", famille: "article", article: "mois", tiers: "article", categorie: "tiers", qualite: "tiers", commercial: "tiers", depot: "article" },
};

function presetExplorateur(nom, sens) {
  const e = exercice();
  const base = { du: jourDe(e.debut), au: jourDe(e.fin), pile: [], vue: "table" };
  const P = {
    "compta-explorateur": { type: "compta", source: "general", lignes: "classe", colonnes: "mois", mesure: "solde" },
    balance: { type: "compta", source: "general", lignes: "compte", colonnes: "", mesure: "solde", aNouveaux: true, titre: "Balance générale (à-nouveaux compris)" },
    charges: sens === "produits"
      ? { type: "compta", source: "general", lignes: "radical", colonnes: "mois", mesure: "soldeCrediteur", comptes: "7", sens: "produits" }
      : { type: "compta", source: "general", lignes: "radical", colonnes: "mois", mesure: "solde", comptes: "6", sens: "charges" },
    tresorerie: { type: "compta", source: "general", lignes: "compte", colonnes: "mois", mesure: "solde", comptes: "5", titre: "Mouvements des comptes de trésorerie par mois" },
    ventes: { type: "ventes", domaine: "ventes", lignes: "famille", colonnes: "mois", mesure: "ca" },
    clients: { type: "ventes", domaine: "ventes", lignes: "tiers", colonnes: "", mesure: "ca", titre: "Clients : CA, marge et quantités" },
    articles: { type: "ventes", domaine: "ventes", lignes: "article", colonnes: "", mesure: "marge", titre: "Articles : CA, coût, marge et taux" },
    achats: { type: "ventes", domaine: "achats", lignes: "tiers", colonnes: "mois", mesure: "ca", titre: "Achats par fournisseur" },
    transformation: { type: "transformation", domaine: "ventes", lignes: "mois", colonnes: "", mesure: "transfo",
      titre: "Satisfaction client : commandes livrées (par date de commande)" },
  };
  return { ...base, ...P[nom] };
}

function explorerAvec(nom, changements) {
  etat.explorateurs[nom] = { ...presetExplorateur(nom), ...changements };
  naviguer(nom === "compta-explorateur" ? "#compta/explorateur" : `#commercial/${nom}`);
}

function paramsCube(cfg) {
  const commun = { lignes: cfg.lignes, colonnes: cfg.colonnes, du: cfg.du, au: cfg.au, tri: cfg.mesure, limite: cfg.limite ?? 300 };
  if (cfg.type === "compta")
    return { ...commun, source: cfg.source === "analytique" ? "analytique" : "", comptes: cfg.comptes, journaux: cfg.journaux, typeJournal: cfg.typeJournal, tiers: cfg.tiers,
      aNouveaux: cfg.aNouveaux ? "true" : "", plan: cfg.source === "analytique" ? cfg.plan : "", sections: cfg.sections };
  return { ...commun, domaine: cfg.domaine, article: cfg.article, famille: cfg.famille, tiers: cfg.tiers, commercial: cfg.commercial, depot: cfg.depot, categorie: cfg.categorie, qualite: cfg.qualite };
}

async function explorateur(c, nom, sens) {
  let cfg = etat.explorateurs[nom];
  if (!cfg || (nom === "charges" && sens && cfg.sens !== sens)) cfg = etat.explorateurs[nom] = presetExplorateur(nom, sens);
  const compta = cfg.type === "compta";
  const axes = compta ? (cfg.source === "analytique" ? etat.meta.axesAnalytiques : etat.meta.axesCompta) : etat.meta.axesVentes;
  if (!axes.some((a) => a.code === cfg.lignes)) cfg.lignes = axes[0].code;
  const transfo = cfg.type === "transformation";
  const mesures = MESURES_TYPE[compta && cfg.source === "analytique" ? "analytique" : cfg.type];
  if (!mesures.includes(cfg.mesure)) cfg.mesure = mesures[0];
  const base = compta ? "/tableau-de-bord/compta" : transfo ? "/tableau-de-bord/commercial/transformation" : "/tableau-de-bord/commercial";
  const cube = await api(`${base}/cube?${qs(paramsCube(cfg))}`);
  const optAxes = (val, vide) => (vide ? `<option value="">${vide}</option>` : "") + axes.map((a) => `<option value="${a.code}" ${a.code === val ? "selected" : ""}>${echapper(a.libelle)}</option>`).join("");
  const filtresTexte = compta
    ? `<label class="court">Comptes <input id="x-comptes" value="${echapper(cfg.comptes ?? "")}" placeholder="ex. 6,7 ou !65" title="Préfixes de comptes séparés par des virgules ; commencer par ! pour les exclure (ex. !65,66)"></label>
       ${cfg.source !== "analytique" ? `<label>Journal ${choix("x-journal", { options: etat.meta.journaux.map((j) => ({ valeur: j.code, intitule: `${j.code} ${j.intitule ?? ""}` })), valeur: cfg.journaux })}</label>
       <label>Tiers ${choix("x-tiers", { source: "tiers", valeur: cfg.tiers })}</label>
       <label title="Inclure les écritures d'à-nouveaux"><span>À-nouveaux</span><input id="x-an" type="checkbox" ${cfg.aNouveaux ? "checked" : ""}></label>`
        : `<label>Plan <select id="x-plan">${etat.meta.plans.map((p) => `<option value="${p.numero}" ${String(cfg.plan) === String(p.numero) ? "selected" : ""}>${echapper(p.intitule)}</option>`).join("")}</select></label>`}`
    : `<label>Famille ${choix("x-famille", { options: etat.meta.familles.map((f) => ({ valeur: f.code, intitule: f.intitule ?? f.code })), valeur: cfg.famille, tous: "Toutes", vide: true })}</label>
       ${cfg.domaine !== "achats" && etat.meta.collaborateurs.length > 1 ? `<label>Commercial ${choixCollaborateurs("x-commercial", cfg.commercial)}</label>` : ""}
       ${cfg.domaine !== "achats" ? choixClients("x", cfg) : ""}
       ${etat.meta.depots.length > 1 ? `<label>Dépôt ${choix("x-depot", { options: etat.meta.depots.map((d) => ({ valeur: d.numero, intitule: d.intitule })), valeur: cfg.depot })}</label>` : ""}
       <label>${cfg.domaine === "achats" ? "Fournisseur" : "Client"} ${choix("x-tiers", { source: cfg.domaine === "achats" ? "fournisseurs" : "clients", valeur: cfg.tiers })}</label>
       <label>Article ${choix("x-article", { source: "articles", valeur: cfg.article })}</label>`;

  c.innerHTML = `
    <article class="carte l12" style="grid-column:1/-1">
      <h3><span>${echapper(cfg.titre ?? (compta ? "Explorateur comptable" : cfg.domaine === "achats" ? "Explorateur des achats" : "Explorateur des ventes"))}</span>
        <span class="discret">${cube.lignes.length} ligne(s)${cube.lignesMasquees ? `, ${cube.lignesMasquees} regroupées dans « Autres »` : ""}</span></h3>
      <div class="commandes">
        ${nom === "charges" ? `<div class="segments"><button type="button" data-sens="charges" class="${cfg.sens !== "produits" ? "actif" : ""}">Charges</button><button type="button" data-sens="produits" class="${cfg.sens === "produits" ? "actif" : ""}">Produits</button></div>` : ""}
        ${compta && etat.meta.plans.length && nom === "compta-explorateur" ? `<div class="segments"><button type="button" data-source="general" class="${cfg.source !== "analytique" ? "actif" : ""}">Générale</button><button type="button" data-source="analytique" class="${cfg.source === "analytique" ? "actif" : ""}">Analytique</button></div>` : ""}
        <label>Lignes <select id="x-lignes">${optAxes(cfg.lignes)}</select></label>
        <label>Colonnes <select id="x-colonnes">${optAxes(cfg.colonnes, "Toutes les mesures")}</select></label>
        ${cfg.colonnes ? `<label>Mesure <select id="x-mesure">${mesures.map((m) => `<option value="${m}" ${m === cfg.mesure ? "selected" : ""}>${MESURES[m]}</option>`).join("")}</select></label>`
          : `<label>Trier par <select id="x-mesure">${mesures.map((m) => `<option value="${m}" ${m === cfg.mesure ? "selected" : ""}>${MESURES[m]}</option>`).join("")}</select></label>`}
        <label>Du <input id="x-du" type="date" value="${cfg.du}"></label>
        <label>Au <input id="x-au" type="date" value="${cfg.au}"></label>
        ${filtresTexte}
        <span class="espace"></span>
        <div class="segments"><button type="button" data-vue="table" class="${cfg.vue !== "graphique" ? "actif" : ""}">Tableau</button><button type="button" data-vue="graphique" class="${cfg.vue === "graphique" ? "actif" : ""}">Graphique</button></div>
        <button type="button" id="x-csv">Exporter</button>
      </div>
      ${cfg.pile.length ? `<div class="fil" style="margin-top:8px"><span class="discret">Zoom :</span>${cfg.pile.map((p, i) => `<span class="puce">${echapper(p.libelle)} <button type="button" data-retour="${i}" title="Retirer">✕</button></span>`).join("")}</div>` : ""}
      <div style="margin-top:8px" id="x-sortie"></div>
      <p class="discret" style="margin:6px 0 0">Cliquez un intitulé pour zoomer dessus, une valeur pour voir ${compta ? "les écritures" : transfo ? "les commandes" : "les lignes de factures"} qui la composent.${transfo
        ? " Taux de satisfaction = livré / commandé : part des commandes clients déjà livrée (BL ou facture). Reste à livrer : bons de commande et préparations non clôturés ; non servi : reliquat des bons soldés." : ""}</p>
    </article>`;

  const changer = (modifs) => { Object.assign(cfg, modifs); afficherOnglet(); };
  const lireChamps = () => {
    const v = (id) => $(id, c)?.value?.trim() ?? undefined;
    const m = { lignes: v("#x-lignes"), colonnes: v("#x-colonnes"), mesure: v("#x-mesure"), du: v("#x-du") || cfg.du, au: v("#x-au") || cfg.au };
    if (compta) Object.assign(m, { comptes: v("#x-comptes"), journaux: v("#x-journal"), tiers: v("#x-tiers"), aNouveaux: $("#x-an", c)?.checked ?? cfg.aNouveaux, plan: v("#x-plan") ?? cfg.plan });
    else Object.assign(m, { famille: v("#x-famille"), commercial: v("#x-commercial"), depot: v("#x-depot"), tiers: v("#x-tiers"), article: v("#x-article"),
      categorie: v("#x-categorie"), qualite: v("#x-qualite") });
    return m;
  };
  c.querySelectorAll(".commandes select, .commandes input[type=date], .commandes input[type=checkbox], .commandes button.choix").forEach((x) => x.addEventListener("change", () => changer(lireChamps())));
  c.querySelectorAll(".commandes input[type=text], .commandes input:not([type])").forEach((x) => x.addEventListener("keydown", (e) => { if (e.key === "Enter") changer(lireChamps()); }));
  c.querySelectorAll(".commandes input:not([type])").forEach((x) => x.addEventListener("change", () => changer(lireChamps())));
  c.querySelectorAll("[data-vue]").forEach((b) => b.addEventListener("click", () => { cfg.vue = b.dataset.vue; dessiner(); c.querySelectorAll("[data-vue]").forEach((x) => x.classList.toggle("actif", x === b)); }));
  c.querySelectorAll("[data-source]").forEach((b) => b.addEventListener("click", () => changer({ source: b.dataset.source, lignes: b.dataset.source === "analytique" ? "section" : "classe", pile: [], plan: cfg.plan ?? etat.meta.plans[0]?.numero })));
  c.querySelectorAll("[data-sens]").forEach((b) => b.addEventListener("click", () => { etat.explorateurs.charges = presetExplorateur("charges", b.dataset.sens); afficherOnglet(); }));
  c.querySelectorAll("[data-retour]").forEach((b) => b.addEventListener("click", () => {
    const i = Number(b.dataset.retour);
    const etape = cfg.pile[i];
    Object.assign(cfg, etape.avant);
    cfg.pile = cfg.pile.slice(0, i);
    afficherOnglet();
  }));

  const colonnes = cube.axeColonnes ? cube.colonnes : null;
  const libMesure = (m) => MESURES[m] ?? m;
  const fmt = (m, v) => (["taux", "transfo", "transfoQte"].includes(m) ? nombre(v, 1) : ["quantite", "qteCommandee", "qteLivree"].includes(m) ? nombre(v, 2) : montant(v));
  const valeursColonnes = colonnes ? colonnes.map((k) => ({ cle: k.cle, entete: etiquetteAxe(cube.axeColonnes, k.cle, k.intitule, true), titre: k.intitule })) : cube.mesures.map((m) => ({ mesure: m, entete: libMesure(m) }));
  const valeur = (ligne, col) => (colonnes ? ligne.cellules[col.cle]?.[cfg.mesure] : ligne.total[col.mesure]);
  const maxCol = valeursColonnes.map((col) => Math.max(1, ...cube.lignes.map((l) => Math.abs(valeur(l, col) ?? 0))));
  const maxTotal = Math.max(1, ...cube.lignes.map((l) => Math.abs(l.total[cfg.mesure] ?? 0)));
  const peutZoomer = (cfg.type === "compta" ? ZOOM.compta : ZOOM.ventes)[cube.axeLignes];

  function dessiner() {
    const sortie = $("#x-sortie", c);
    if (!cube.lignes.length) { sortie.innerHTML = '<p class="vide">Aucune donnée pour ces filtres.</p>'; return; }
    if (cfg.vue === "graphique") return dessinerGraphique(sortie);
    sortie.innerHTML = `<div class="table-defile"><table class="cube"><thead><tr><th>${echapper(axes.find((a) => a.code === cube.axeLignes)?.libelle ?? "")}</th>
      ${valeursColonnes.map((v) => `<th title="${echapper(v.titre ?? "")}">${echapper(v.entete)}</th>`).join("")}${colonnes ? `<th>Total ${echapper(libMesure(cfg.mesure))}</th>` : ""}</tr></thead>
      <tbody>${cube.lignes.map((l, i) => `<tr><th class="${peutZoomer && l.cle !== "*autres*" ? "zoom" : ""}" data-i="${i}" title="${echapper(l.intitule)}">${echapper(etiquetteAxe(cube.axeLignes, l.cle, l.intitule))}</th>
        ${valeursColonnes.map((col, k) => { const v = valeur(l, col); const m = colonnes ? cfg.mesure : col.mesure;
          return `<td class="num clic ${v < 0 ? "neg" : ""}" data-i="${i}" data-k="${k}">${v == null || v === 0 ? "" : fmt(m, v)}${!colonnes && m === cfg.mesure && v ? `<span class="barre" style="width:${(Math.abs(v) / maxCol[k]) * 100}%"></span>` : ""}</td>`; }).join("")}
        ${colonnes ? `<td class="num clic" data-i="${i}"><b>${fmt(cfg.mesure, l.total[cfg.mesure])}</b><span class="barre" style="width:${(Math.abs(l.total[cfg.mesure]) / maxTotal) * 100}%"></span></td>` : ""}</tr>`).join("")}
      <tr class="total"><th>Total</th>${valeursColonnes.map((col) => { const v = colonnes ? cube.total.cellules[col.cle]?.[cfg.mesure] : cube.total.total[col.mesure]; return `<td class="num">${v ? fmt(colonnes ? cfg.mesure : col.mesure, v) : ""}</td>`; }).join("")}
        ${colonnes ? `<td class="num">${fmt(cfg.mesure, cube.total.total[cfg.mesure])}</td>` : ""}</tr></tbody></table></div>`;
    sortie.querySelector("tbody").addEventListener("click", (e) => {
      const th = e.target.closest("th.zoom");
      if (th) return zoomer(cube.lignes[Number(th.dataset.i)]);
      const td = e.target.closest("td.clic");
      if (td) detail(cube.lignes[Number(td.dataset.i)], td.dataset.k != null && colonnes ? colonnes[Number(td.dataset.k)] : null);
    });
  }

  function dessinerGraphique(sortie) {
    sortie.innerHTML = '<div id="x-graphe"></div>';
    const zone = $("#x-graphe", c);
    const etiquette = (l) => etiquetteAxe(cube.axeLignes, l.cle, l.intitule, true);
    if (colonnes && TEMPS.includes(cube.axeColonnes) && cube.lignes.length <= 8) {
      barres(zone, { categories: colonnes.map((k) => etiquetteAxe(cube.axeColonnes, k.cle, k.intitule, true)), empile: !["taux", "transfo", "transfoQte"].includes(cfg.mesure),
        series: cube.lignes.map((l, i) => ({ nom: etiquette(l), valeurs: colonnes.map((k) => l.cellules[k.cle]?.[cfg.mesure] ?? 0), couleur: COULEURS[i % 8] })),
        format: (v) => fmt(cfg.mesure, v) });
    } else if (TEMPS.includes(cube.axeLignes)) {
      barres(zone, { categories: cube.lignes.map(etiquette), series: [{ nom: libMesure(cfg.mesure), valeurs: cube.lignes.map((l) => l.total[cfg.mesure]) }], format: (v) => fmt(cfg.mesure, v) });
    } else {
      barresH(zone, cube.lignes.map((l) => ({ libelle: etiquette(l), valeur: l.total[cfg.mesure], ligne: l, couleur: l.total[cfg.mesure] < 0 ? "var(--erreur)" : undefined })),
        { format: (v) => fmt(cfg.mesure, v), surClic: peutZoomer ? (it) => zoomer(it.ligne) : undefined });
    }
  }

  function zoomer(ligne) {
    if (!ligne || ligne.cle === "*autres*") return;
    const axe = cube.axeLignes, cle = ligne.cle;
    const avant = { lignes: cfg.lignes, colonnes: cfg.colonnes, du: cfg.du, au: cfg.au, comptes: cfg.comptes, journaux: cfg.journaux, typeJournal: cfg.typeJournal, tiers: cfg.tiers,
      sections: cfg.sections, famille: cfg.famille, article: cfg.article, commercial: cfg.commercial, depot: cfg.depot, categorie: cfg.categorie, qualite: cfg.qualite };
    const m = {};
    if (axe === "mois") Object.assign(m, { du: `${cle}-01`, au: finDuMois(cle) });
    else if (axe === "semaine") { const l = lundiSemaine(cle); Object.assign(m, { du: isoJour(l), au: isoJour(new Date(l.getTime() + 6 * 86400000)) }); }
    else if (axe === "annee") Object.assign(m, { du: `${cle}-01-01`, au: `${cle}-12-31` });
    else if (axe === "trimestre") { const t = Number(cle.slice(6)); Object.assign(m, { du: `${cle.slice(0, 4)}-${String(t * 3 - 2).padStart(2, "0")}-01`, au: finDuMois(`${cle.slice(0, 4)}-${String(t * 3).padStart(2, "0")}`) }); }
    else if (axe === "exercice") { const e = etat.meta.exercices.find((x) => x.cle === cle); if (e) Object.assign(m, { du: jourDe(e.debut), au: jourDe(e.fin) }); }
    else if (["classe", "radical", "compte"].includes(axe)) m.comptes = cle;
    else if (axe === "journal") m.journaux = cle;
    else if (axe === "typeJournal") m.typeJournal = cle;
    else if (axe === "section") m.sections = cle;
    else if (["tiers", "famille", "article", "commercial", "depot", "categorie", "qualite"].includes(axe)) m[axe] = cle;
    if (cle === "" && !TEMPS.includes(axe)) return;
    let suivant = (compta ? ZOOM.compta : ZOOM.ventes)[axe];
    if (suivant === cfg.colonnes) cfg.colonnes = "";
    cfg.pile = [...cfg.pile, { libelle: `${axes.find((a) => a.code === axe)?.libelle} : ${etiquetteAxe(axe, cle, ligne.intitule)}`, avant }];
    changer({ ...m, lignes: suivant });
  }

  async function detail(ligne, colonne) {
    if (ligne.cle === "*autres*" || colonne?.cle === "*autres*") return;
    try {
      const p = { ...paramsCube(cfg), cleLigne: ligne.cle, cleColonne: colonne?.cle };
      if (cfg.source === "analytique") return bandeau("Le détail est disponible sur la comptabilité générale.");
      const r = await api(`${base}/detail?${qs(p)}`);
      const titre = `${etiquetteAxe(cube.axeLignes, ligne.cle, ligne.intitule)}${colonne ? ` · ${etiquetteAxe(cube.axeColonnes, colonne.cle, colonne.intitule)}` : ""}`;
      const plein = r.length >= 500 ? " (500 premières)" : "";
      if (transfo) {
        const co = r.reduce((t, x) => t + x.commande, 0), li = r.reduce((t, x) => t + x.livre, 0);
        const classe = { "Livrée": "ok", "Partielle": "sousMini", "En attente": "sousMini", "En préparation": "sousMini", "Non servie": "retard", "Partielle, soldée": "retard" };
        ouvrirDetail(titre, `${r.length} commande(s)${plein} · commandé ${montant(co)} · livré ${montant(li)} · taux ${co ? nombre((li / co) * 100, 1) + " %" : "–"}`,
          `<table class="liste"><thead><tr><th>Commande</th><th>Date</th><th>Client</th><th>Commercial</th><th class="num">Commandé</th><th class="num">Livré</th><th class="num">Reste</th><th class="num">Non servi</th>
            <th class="num">Taux</th><th>Livraison</th><th class="num">Délai (j)</th><th>Statut</th></tr></thead>
          <tbody>${r.map((x) => `<tr><td>${echapper(x.piece)}</td><td>${date(x.date)}</td><td title="${echapper(x.intitule ?? "")}">${echapper(x.tiers)} ${echapper(x.intitule ?? "")}</td><td>${echapper(x.commercial ?? "")}</td>
            <td class="num">${montant(x.commande)}</td><td class="num">${x.livre ? montant(x.livre) : ""}</td><td class="num">${x.enCours ? montant(x.enCours) : ""}</td><td class="num">${x.nonServi ? montant(x.nonServi) : ""}</td>
            <td class="num">${x.taux == null ? "" : nombre(x.taux, 1) + " %"}</td><td>${date(x.derniereLivraison)}</td><td class="num">${x.delai ?? ""}</td>
            <td><span class="statut ${classe[x.statut] ?? ""}">${echapper(x.statut)}</span></td></tr>`).join("")}</tbody></table>`);
      } else if (compta) {
        const d = r.reduce((t, x) => t + x.debit, 0), cr = r.reduce((t, x) => t + x.credit, 0);
        ouvrirDetail(titre, `${r.length} écriture(s)${plein} · débit ${montant(d)} · crédit ${montant(cr)} · solde ${montant(d - cr)}`,
          `<table class="liste"><thead><tr><th>Date</th><th>Journal</th><th>Pièce</th><th>Compte</th><th>Tiers</th><th>Libellé</th><th>Échéance</th><th class="num">Débit</th><th class="num">Crédit</th><th>Lettrage</th></tr></thead>
          <tbody>${r.map((x) => `<tr><td>${date(x.date)}</td><td>${echapper(x.journal)}</td><td>${echapper(x.piece ?? "")}</td><td>${echapper(x.compte)}</td><td>${echapper(x.tiers ?? "")}</td>
            <td>${echapper(x.libelle ?? "")}</td><td>${date(x.echeance)}</td><td class="num">${x.debit ? montant(x.debit) : ""}</td><td class="num">${x.credit ? montant(x.credit) : ""}</td><td>${echapper(x.lettrage ?? "")}</td></tr>`).join("")}</tbody></table>`);
      } else {
        const ca = r.reduce((t, x) => t + x.montantHT, 0), co = r.reduce((t, x) => t + x.cout, 0);
        ouvrirDetail(titre, `${r.length} ligne(s)${plein} · CA ${montant(ca)} · marge ${montant(ca - co)}`,
          `<table class="liste"><thead><tr><th>Date</th><th>Pièce</th><th>Tiers</th><th>Article</th><th>Désignation</th><th class="num">Qté</th><th class="num">Montant HT</th><th class="num">Coût</th><th class="num">Marge</th></tr></thead>
          <tbody>${r.map((x) => `<tr><td>${date(x.date)}</td><td>${echapper(x.piece)}</td><td>${echapper(x.tiers)}</td><td>${echapper(x.article)}</td><td>${echapper(x.designation ?? "")}</td>
            <td class="num">${nombre(x.quantite, 2)}</td><td class="num">${montant(x.montantHT)}</td><td class="num">${montant(x.cout)}</td><td class="num">${montant(x.montantHT - x.cout)}</td></tr>`).join("")}</tbody></table>`);
      }
    } catch (e) { bandeau(e.message); }
  }

  $("#x-csv", c).addEventListener("click", () => {
    const entetes = [axes.find((a) => a.code === cube.axeLignes)?.libelle ?? "", ...valeursColonnes.map((v) => v.entete), ...(colonnes ? [`Total ${libMesure(cfg.mesure)}`] : [])];
    const lignes = cube.lignes.map((l) => [l.intitule ?? l.cle, ...valeursColonnes.map((col) => valeur(l, col) ?? 0), ...(colonnes ? [l.total[cfg.mesure]] : [])]);
    exporterCsv(nom, entetes, lignes);
  });
  c.addEventListener("affiche", dessiner);
}

// ==================== Tableau commercial ====================
async function direction(c) {
  const f = etat.explorateurs.direction ??= { commercial: "" };
  const s = await lireCache(`/tableau-de-bord/commercial/synthese?${qs({ exercice: etat.exercice, commercial: f.commercial })}`);
  const fini = (m) => m.mois <= moisDe(s.au);
  c.innerHTML = `
    ${etat.meta.collaborateurs.length > 1 && etat.meta.profil !== "Vendeur" ? `<div class="commandes"><label>Commercial ${selectCollaborateurs("f-dir-co", f.commercial)}</label></div>` : ""}
    ${alertes(s.alertes)}
    <section class="tuiles">
      ${tuile({ libelle: "CA du jour", valeur: montant(s.caJour), pied: new Date().toLocaleDateString("fr-FR") })}
      ${tuile({ libelle: "CA du mois", valeur: montant(s.caMois), pied: `${variation(s.variationMois)} N-1 ${montant(s.caMoisN1)}`, niveau: s.alertes.some((a) => a.code === "baisseCa") ? "critique" : "" })}
      ${tuile({ libelle: "CA de l'exercice", valeur: montant(s.caExercice), pied: `${variation(s.variation)} N-1 ${montant(s.caN1)}`, mini: miniCourbe(s.mois.filter(fini).map((m) => m.ca)), vers: "commercial/ventes" })}
      ${tuile({ libelle: "Marge brute", valeur: montant(s.marge), pied: `taux ${s.taux == null ? "–" : nombre(s.taux, 1) + " %"} · N-1 ${montant(s.margeN1)}`, niveau: s.alertes.some((a) => a.code === "marge") ? "attention" : "", vers: "commercial/articles", titre: "Taux de marge = marge / coût de revient" })}
      ${tuile({ libelle: "Panier moyen", valeur: montant(s.panierMoyen), pied: `${nombre(s.factures)} factures` })}
      ${tuile({ libelle: "Clients actifs", valeur: nombre(s.clientsActifs), pied: `dont ${nombre(s.nouveauxClients)} nouveaux`, vers: "commercial/clients" })}
      ${s.objectifMois ? tuile({ libelle: "Objectif du mois", valeur: `${nombre(s.atteinteMois, 0)} %`, pied: `${montant(s.caMois)} / ${montant(s.objectifMois)}`, niveau: s.alertes.some((a) => a.code === "objectif") ? "attention" : "", vers: "commercial/objectifs" })
        : etat.meta.droits.objectifs ? tuile({ libelle: "Objectifs", valeur: "–", pied: "à saisir", vers: "commercial/objectifs" }) : ""}
      ${tuile({ libelle: "Valeur du stock", valeur: montant(s.stock.valeur), pied: `${nombre(s.stock.ruptures)} ruptures · ${nombre(s.stock.dormants)} dormants`, vers: "commercial/stock", niveau: s.stock.ruptures ? "attention" : "" })}
      ${tuile({ libelle: "Créances clients", valeur: montant(s.creancesClients.total), pied: `échu ${montant(s.creancesClients.echu)}`, vers: "commercial/recouvrement", niveau: s.creancesClients.plus90 > 0 ? "attention" : "" })}
      ${s.transformation ? tuile({ libelle: "Satisfaction client", valeur: s.transformation.taux == null ? "–" : `${nombre(s.transformation.taux, 1)} %`,
        pied: `${nombre(s.transformation.completes)} / ${nombre(s.transformation.nombre)} BC livrés en entier`,
        titre: `Part des commandes clients de la période déjà livrée (BL ou facture) : ${montant(s.transformation.livre)} sur ${montant(s.transformation.commande)}${s.transformation.delaiMoyen != null ? ` · délai moyen de livraison ${nombre(s.transformation.delaiMoyen, 1)} j` : ""}`,
        vers: "commercial/transformation", niveau: s.transformation.taux != null && s.transformation.taux < 80 ? "attention" : "" }) : ""}
      ${tuile({ libelle: "Commandes en cours", valeur: montant(s.commandes.montant), pied: `${nombre(s.commandes.nombre)} BC · ${nombre(s.commandesEnRetard.nombre)} en retard · devis ${montant(s.devis.montant)}`, vers: "commercial/commandes" })}
    </section>
    <section class="grille">
      ${carte("l8", "Chiffre d'affaires par mois", "g-ca", `<span class="discret">${date(s.du)} – ${date(s.au)}</span>`)}
      ${carte("l4", "Ventes par famille", "g-familles")}
      ${carte("l4", "Top 10 clients", "g-clients", '<span class="discret">trait : N-1</span>')}
      ${carte("l4", "Top 10 articles", "g-articles")}
      ${s.commerciaux.length ? carte("l4", "Par commercial", "g-commerciaux") : carte("l4", "Marge par mois", "g-marge")}
      ${s.commerciaux.length ? carte("l6", "Marge par mois", "g-marge") : ""}
      <article class="carte ${s.commerciaux.length ? "l6" : "l12"}"><h3><span>Clients à relancer</span><span class="discret">habituels sans facture récente</span></h3>
        ${s.aRelancer.length ? `<div class="table-defile" style="max-height:260px"><table class="liste"><thead><tr><th>Client</th><th>Dernière facture</th><th class="num">Jours</th><th class="num">CA 12 mois</th></tr></thead>
        <tbody>${s.aRelancer.map((x) => `<tr><td><b>${echapper(x.tiers)}</b> ${echapper(x.intitule ?? "")}</td><td>${date(x.derniereFacture)}</td><td class="num">${x.jours}</td><td class="num">${montant(x.caDouzeMois)}</td></tr>`).join("")}</tbody></table></div>`
          : '<p class="vide">Aucun client habituel sans commande récente.</p>'}</article>
    </section>`;
  relierTuiles(c);
  $("#f-dir-co", c)?.addEventListener("change", (e) => { f.commercial = e.target.value; afficherOnglet(); });
  c.addEventListener("affiche", () => {
    const cat = s.mois.map((m) => moisCourt(m.mois));
    barres($("#g-ca"), { categories: cat, series: [
      { nom: "CA HT", valeurs: s.mois.map((m) => m.ca), couleur: "var(--c1)" },
      { nom: "N-1", type: "ligne", pointille: true, valeurs: s.mois.map((m) => m.caN1), couleur: "var(--discret)" },
      ...(s.mois.some((m) => m.objectif) ? [{ nom: "Objectif", type: "ligne", valeurs: s.mois.map((m) => m.objectif), couleur: "var(--c4)" }] : []),
    ] });
    barres($("#g-marge"), { categories: cat, series: [
      { nom: "Marge", valeurs: s.mois.map((m) => m.marge), couleur: "var(--c3)" },
      { nom: "N-1", type: "ligne", pointille: true, valeurs: s.mois.map((m) => m.margeN1), couleur: "var(--discret)" },
    ] });
    anneau($("#g-familles"), s.familles.map((x) => ({ libelle: x.intitule ?? x.cle, valeur: x.ca, cle: x.cle })),
      { surClic: (p) => explorerAvec("ventes", { famille: p.cle, lignes: "article", colonnes: "mois" }) });
    barresH($("#g-clients"), s.topClients.map((x) => ({ libelle: x.intitule ?? x.cle, valeur: x.ca, reference: x.caN1, detail: `marge ${montant(x.marge)}`, cle: x.cle })),
      { surClic: (it) => explorerAvec("ventes", { tiers: it.cle, lignes: "article", colonnes: "mois" }) });
    barresH($("#g-articles"), s.topArticles.map((x) => ({ libelle: x.intitule ?? x.cle, valeur: x.ca, reference: x.caN1, detail: `taux ${x.taux ?? "–"} %`, cle: x.cle })),
      { couleur: "var(--c2)", surClic: (it) => explorerAvec("ventes", { article: it.cle, lignes: "tiers", colonnes: "mois" }) });
    if ($("#g-commerciaux")) barresH($("#g-commerciaux"), s.commerciaux.map((x) => ({ libelle: x.intitule ?? x.cle, valeur: x.ca, reference: x.caN1, cle: x.cle })),
      { couleur: "var(--c3)", surClic: (it) => explorerAvec("ventes", { commercial: it.cle, lignes: "tiers", colonnes: "mois" }) });
  });
}

async function clients(c) {
  const s = await lireCache(`/tableau-de-bord/commercial/synthese?exercice=${etat.exercice}`);
  const haut = document.createElement("div");
  haut.style.display = "contents";
  haut.innerHTML = `<section class="tuiles">
      ${tuile({ libelle: "Clients actifs", valeur: nombre(s.clientsActifs), pied: "facturés sur l'exercice" })}
      ${tuile({ libelle: "Nouveaux clients", valeur: nombre(s.nouveauxClients), pied: "première facture sur l'exercice" })}
      ${tuile({ libelle: "Panier moyen", valeur: montant(s.panierMoyen), pied: `${nombre(s.factures)} factures` })}
      ${tuile({ libelle: "À relancer", valeur: nombre(s.aRelancer.length), pied: "habituels sans facture récente", niveau: s.aRelancer.length ? "attention" : "" })}
    </section>`;
  c.appendChild(haut);
  const bas = document.createElement("div");
  bas.style.display = "contents";
  c.appendChild(bas);
  await explorateur(bas, "clients");
  c.addEventListener("affiche", () => bas.dispatchEvent(new Event("affiche")));
}

async function commandes(c) {
  const f = etat.explorateurs.commandes ??= { type: "1" };
  const r = await lireCache("/tableau-de-bord/commercial/commandes");
  const liste = r.pieces.filter((p) => f.type === "" || String(p.type) === f.type);
  c.innerHTML = `
    <section class="tuiles">${r.types.map((t) => tuile({ libelle: t.intitule, valeur: montant(t.montant), pied: `${nombre(t.nombre)} pièce(s)` })).join("")}
      ${tuile({ libelle: "Commandes en retard", valeur: nombre(r.pieces.filter((p) => p.enRetard).length), pied: "date de livraison dépassée", niveau: r.pieces.some((p) => p.enRetard) ? "attention" : "" })}</section>
    <article class="carte l12" style="grid-column:1/-1"><h3><span>Documents en cours</span>
      <span><select id="f-cmd-type"><option value="">Tous</option>${r.types.map((t) => `<option value="${t.type}" ${String(t.type) === f.type ? "selected" : ""}>${t.intitule}</option>`).join("")}</select>
      <button type="button" class="lien" id="csv-cmd">Exporter</button></span></h3>
      <div class="table-defile"><table class="liste"><thead><tr><th>Pièce</th><th>Date</th><th>Livraison prévue</th><th>Client</th><th>Commercial</th><th class="num">Montant HT</th><th class="num">Âge</th><th></th></tr></thead>
      <tbody>${liste.map((p) => `<tr><td>${echapper(p.piece)}</td><td>${date(p.date)}</td><td>${date(p.dateLivraison)}</td><td><b>${echapper(p.tiers)}</b> ${echapper(p.intitule ?? "")}</td>
        <td>${echapper(p.commercial ?? "")}</td><td class="num">${montant(p.montantHT)}</td><td class="num">${p.ageJours} j</td><td>${p.enRetard ? '<span class="statut retard">En retard</span>' : ""}</td></tr>`).join("") || '<tr><td colspan="8" class="vide">Aucun document en cours.</td></tr>'}</tbody></table></div></article>`;
  $("#f-cmd-type", c).addEventListener("change", (e) => { f.type = e.target.value; afficherOnglet(); });
  $("#csv-cmd", c).addEventListener("click", () => exporterCsv("documents-en-cours", ["Type", "Pièce", "Date", "Livraison", "Client", "Intitulé", "Commercial", "Montant HT", "En retard"],
    liste.map((p) => [r.types[p.type]?.intitule, p.piece, date(p.date), date(p.dateLivraison), p.tiers, p.intitule, p.commercial, p.montantHT, p.enRetard ? "oui" : ""])));
}

async function stock(c) {
  const f = etat.explorateurs.stock ??= { depot: "", famille: "", statut: "" };
  const s = await lireCache(`/tableau-de-bord/stock?${qs(f)}`);
  const t = s.totaux;
  const STATUTS = { rupture: "Rupture", sousMini: "Sous le minimum", surstock: "Surstock", dormant: "Dormant", ok: "Normal" };
  c.innerHTML = `
    <div class="commandes">
      ${etat.meta.depots.length > 1 ? `<label>Dépôt <select id="f-st-depot"><option value="">Tous</option>${etat.meta.depots.map((d) => `<option value="${d.numero}" ${String(f.depot) === String(d.numero) ? "selected" : ""}>${echapper(d.intitule)}</option>`).join("")}</select></label>` : ""}
      <label>Famille <select id="f-st-famille"><option value="">Toutes</option>${etat.meta.familles.map((x) => `<option value="${echapper(x.code)}" ${f.famille === x.code ? "selected" : ""}>${echapper(x.intitule ?? x.code)}</option>`).join("")}</select></label>
      <label>Statut <select id="f-st-statut"><option value="">Tous</option>${Object.entries(STATUTS).map(([k, n]) => `<option value="${k}" ${f.statut === k ? "selected" : ""}>${n}</option>`).join("")}</select></label>
    </div>
    <section class="tuiles">
      ${tuile({ libelle: "Valeur du stock", valeur: montant(t.valeur), pied: `${nombre(t.articles)} articles` })}
      ${tuile({ libelle: "Ruptures", valeur: nombre(t.ruptures), niveau: t.ruptures ? "critique" : "", pied: "stock nul avec minimum ou réservé" })}
      ${tuile({ libelle: "Sous le minimum", valeur: nombre(t.sousMini), niveau: t.sousMini ? "attention" : "" })}
      ${tuile({ libelle: "Surstocks", valeur: nombre(t.surstocks), pied: "au-dessus du maximum" })}
      ${tuile({ libelle: "Stock dormant", valeur: montant(t.valeurDormante), pied: `${nombre(t.dormants)} articles sans sortie récente` })}
      ${tuile({ libelle: "Rotation", valeur: s.rotation == null ? "–" : `${nombre(s.rotation, 1)}×`, pied: s.couvertureJours == null ? "" : `couverture ${nombre(s.couvertureJours)} jours` })}
    </section>
    <section class="grille">
      ${carte("l6", "Valeur par famille", "g-st-fam")}
      ${carte("l6", "Valeur par dépôt", "g-st-dep")}
      <article class="carte l12"><h3><span>Articles</span><button type="button" class="lien" id="csv-st">Exporter</button></h3>
        <div class="table-defile"><table class="liste"><thead><tr><th>Article</th><th>Dépôt</th><th class="num">Stock</th><th class="num">Disponible</th><th class="num">Mini</th><th class="num">Maxi</th>
          <th class="num">Valeur</th><th class="num">Vendu 12 mois</th><th class="num">Couverture</th><th>Dernière sortie</th><th>Statut</th></tr></thead>
        <tbody>${s.lignes.map((l) => `<tr><td><b>${echapper(l.article)}</b> ${echapper(l.designation ?? "")}</td><td>${echapper(l.intituleDepot ?? l.depot)}</td>
          <td class="num">${nombre(l.quantite, 2)}</td><td class="num">${nombre(l.disponible, 2)}</td><td class="num">${l.mini ? nombre(l.mini, 2) : ""}</td><td class="num">${l.maxi ? nombre(l.maxi, 2) : ""}</td>
          <td class="num">${montant(l.valeur)}</td><td class="num">${l.ventesDouzeMois ? nombre(l.ventesDouzeMois, 2) : ""}</td><td class="num">${l.couvertureJours == null ? "" : `${l.couvertureJours} j`}</td>
          <td>${date(l.derniereSortie)}</td><td><span class="statut ${l.statut}">${STATUTS[l.statut]}</span></td></tr>`).join("") || '<tr><td colspan="11" class="vide">Aucun article.</td></tr>'}</tbody></table></div></article>
    </section>`;
  const recharger = () => {
    etat.explorateurs.stock = { depot: $("#f-st-depot", c)?.value ?? "", famille: $("#f-st-famille", c).value, statut: $("#f-st-statut", c).value };
    afficherOnglet();
  };
  c.querySelectorAll(".commandes select").forEach((x) => x.addEventListener("change", recharger));
  $("#csv-st", c).addEventListener("click", () => exporterCsv("stock", ["Article", "Désignation", "Dépôt", "Stock", "Disponible", "Mini", "Maxi", "Valeur", "Vendu 12 mois", "Couverture (j)", "Dernière sortie", "Statut"],
    s.lignes.map((l) => [l.article, l.designation, l.intituleDepot, l.quantite, l.disponible, l.mini, l.maxi, l.valeur, l.ventesDouzeMois, l.couvertureJours, date(l.derniereSortie), STATUTS[l.statut]])));
  c.addEventListener("affiche", () => {
    anneau($("#g-st-fam"), s.parFamille.map((x) => ({ libelle: x.intitule, valeur: x.valeur })));
    barresH($("#g-st-dep"), s.parDepot.map((x) => ({ libelle: x.intitule ?? x.cle, valeur: x.valeur })), { couleur: "var(--c2)" });
  });
}

async function objectifs(c) {
  const f = etat.explorateurs.objectifs ??= { commercial: etat.meta.profil === "Vendeur" ? String(etat.meta.collaborateurs[0]?.numero ?? "") : "" };
  const e = exercice();
  const [liste, s] = await Promise.all([
    api(`/tableau-de-bord/objectifs?${qs({ du: moisDe(e.debut), au: moisDe(e.fin) })}`),
    lireCache(`/tableau-de-bord/commercial/synthese?${qs({ exercice: etat.exercice, commercial: f.commercial })}`),
  ]);
  const co = Number(f.commercial || 0);
  const modifiable = etat.meta.droits.objectifs;
  c.innerHTML = `
    <article class="carte l12 objectifs" style="grid-column:1/-1"><h3><span>Objectifs de chiffre d'affaires HT · exercice ${echapper(e.intitule)}</span>
      ${etat.meta.collaborateurs.length > 1 && etat.meta.profil !== "Vendeur" ? `<label class="discret">Pour ${selectCollaborateurs("f-obj-co", f.commercial, "Toute l'entreprise")}</label>` : ""}</h3>
      <div class="table-defile"><table class="liste"><thead><tr><th>Mois</th><th class="num">Objectif</th><th class="num">Réalisé</th><th class="num">Atteinte</th><th class="num">N-1</th></tr></thead>
      <tbody>${s.mois.map((m) => { const o = liste.find((x) => x.mois === m.mois && x.commercial === co)?.montant ?? 0;
        return `<tr><td>${moisCourt(m.mois)}</td><td class="num">${modifiable ? `<input type="number" min="0" step="1000" data-mois="${m.mois}" value="${o || ""}">` : montant(o)}</td>
          <td class="num">${montant(m.ca)}</td><td class="num ${o && m.ca < o ? "neg" : o ? "pos" : ""}">${o ? `${nombre((m.ca / o) * 100, 0)} %` : ""}</td><td class="num">${m.caN1 == null ? "" : montant(m.caN1)}</td></tr>`; }).join("")}</tbody></table></div>
      ${modifiable ? `<div class="boutons" style="margin-top:8px"><button type="button" id="obj-n1" title="Reprendre le CA N-1 de chaque mois, augmenté du pourcentage">Proposer N-1 +</button>
        <input id="obj-pct" type="number" value="10" style="width:70px"> % <button type="button" id="obj-enregistrer" class="principal">Enregistrer</button></div>` : ""}
      <p class="discret">Les objectifs sont gardés par l'API (base des extensions), pas dans Sage. « Toute l'entreprise » sert à la tuile Objectif de l'écran Direction.</p>
    </article>`;
  $("#f-obj-co", c)?.addEventListener("change", (ev) => { f.commercial = ev.target.value; afficherOnglet(); });
  $("#obj-n1", c)?.addEventListener("click", () => {
    const pct = Number($("#obj-pct", c).value || 0);
    c.querySelectorAll("[data-mois]").forEach((i) => { const m = s.mois.find((x) => x.mois === i.dataset.mois); if (m?.caN1) i.value = Math.round((m.caN1 * (1 + pct / 100)) / 1000) * 1000; });
  });
  $("#obj-enregistrer", c)?.addEventListener("click", async () => {
    const corps = [...c.querySelectorAll("[data-mois]")].map((i) => ({ mois: i.dataset.mois, commercial: co, montant: Number(i.value || 0) }));
    try {
      await api("/tableau-de-bord/objectifs", { methode: "PUT", corps });
      [...etat.cache.keys()].filter((k) => k.includes("/commercial/synthese")).forEach((k) => etat.cache.delete(k));
      bandeau("Objectifs enregistrés.", true);
      afficherOnglet();
    } catch (err) { bandeau(err.message); }
  });
}

// ---------- Lancement ----------
if (!cleApi()) ouvrirConnexion(null, true);
else demarrer();
