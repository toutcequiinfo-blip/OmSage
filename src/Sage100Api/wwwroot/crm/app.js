// CRM des commerciaux : portefeuille, vue 360° d'un client, visites, appels et rendez-vous, agenda des actions à faire.
// Les données Sage sont lues par l'API (SQL, lecture seule) ; les activités sont gardées par l'API dans sa propre base, jamais dans Sage.

import { preparerChoix, societeChoisie, nomSociete } from "../commun/societes.js";

const CLE_REGLAGES = "crm.reglages";
const CLE_SESSION = "crm.session";
const CLE_SEGMENT = "crm.segment";

const TYPES = [
  { code: "visite", nom: "Visite", icone: "🚗" },
  { code: "appel", nom: "Appel", icone: "📞" },
  { code: "rendez-vous", nom: "Rendez-vous", icone: "📅" },
  { code: "email", nom: "E-mail", icone: "✉️" },
  { code: "note", nom: "Note", icone: "📝" },
  { code: "tache", nom: "Tâche", icone: "✅" },
];
const NOMS_DOCUMENTS = {
  devis: "Devis", commande: "Commande", preparation: "Préparation", livraison: "Livraison", retour: "Retour", avoir: "Avoir",
  facture: "Facture", "facture-comptabilisee": "Facture",
};

const $ = (s) => document.querySelector(s);
const etat = { segment: lireJson(CLE_SEGMENT, "portefeuille"), portefeuille: null, client: null, synthese: null, activite: null, type: "visite", pile: [] };

// ---------- Stockage local (protégé : navigation privée ou stockage bloqué) ----------
function lireJson(cle, defaut) {
  try { const v = localStorage.getItem(cle); return v == null ? defaut : JSON.parse(v); } catch { return defaut; }
}
function ecrireJson(cle, valeur) {
  try { valeur == null ? localStorage.removeItem(cle) : localStorage.setItem(cle, JSON.stringify(valeur)); } catch { /* ignoré */ }
}
/** La clé d'API de la borne est reprise si la borne a déjà été réglée sur cet appareil. */
function cleApi() { return lireJson(CLE_REGLAGES, null)?.cle || lireJson("borne.reglages", null)?.cle || ""; }
function session() {
  const s = lireJson(CLE_SESSION, null);
  return s && (!s.expiration || new Date(s.expiration) > new Date()) ? s : null;
}
function monCollaborateur() { return session()?.collaborateur?.numero ?? null; }

// ---------- API ----------
class ErreurApi extends Error { constructor(message, statut) { super(message); this.statut = statut; } }

async function api(methode, chemin, corps) {
  const s = session();
  let r;
  try {
    r = await fetch(`/api/v1${chemin}`, {
      method: methode,
      headers: {
        "X-Api-Key": cleApi(),
        ...(s?.jeton ? { Authorization: `Bearer ${s.jeton}` } : {}),
        ...(s?.dossier ? { "X-Dossier": s.dossier } : {}),
        ...(corps ? { "Content-Type": "application/json" } : {}),
      },
      body: corps ? JSON.stringify(corps) : undefined,
      cache: "no-store",
    });
  } catch {
    throw new ErreurApi("Serveur injoignable. Vérifiez le réseau.", 0);
  }
  let donnees = null;
  try { donnees = await r.json(); } catch { /* corps vide */ }
  if (r.ok) return donnees;
  if (r.status === 401 && donnees?.erreur && !donnees?.code) {
    // Refus du middleware de clé d'API.
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
  throw new ErreurApi(message, r.status);
}

// ---------- Mise en forme ----------
const echapper = (t) => String(t ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
const montant = (n) => (Number(n) || 0).toLocaleString("fr-FR", { minimumFractionDigits: 0, maximumFractionDigits: 2 });
const dateCourte = (d) => (d ? new Date(d).toLocaleDateString("fr-FR", { day: "2-digit", month: "2-digit", year: "2-digit" }) : "—");
const heure = (d) => new Date(d).toLocaleTimeString("fr-FR", { hour: "2-digit", minute: "2-digit" });
const jour = (d) => { const x = new Date(d); x.setHours(0, 0, 0, 0); return x; };
const typeActivite = (code) => TYPES.find((t) => t.code === code) ?? TYPES[0];

/** « aujourd'hui », « il y a 3 j », « dans 2 j ». */
function ecart(d) {
  if (!d) return "jamais";
  const j = Math.round((jour(d) - jour(new Date())) / 86400000);
  if (j === 0) return "aujourd'hui";
  if (j === -1) return "hier";
  if (j === 1) return "demain";
  if (j < 0) return -j > 60 ? `il y a ${Math.round(-j / 30)} mois` : `il y a ${-j} j`;
  return j > 60 ? `dans ${Math.round(j / 30)} mois` : `dans ${j} j`;
}
/** Valeur pour un champ datetime-local (heure locale). */
function versChampDate(d) {
  const x = new Date(d);
  x.setMinutes(x.getMinutes() - x.getTimezoneOffset());
  return x.toISOString().slice(0, 16);
}
function uuid() {
  if (crypto.randomUUID) return crypto.randomUUID();
  return "xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx".replace(/[xy]/g, (c) => {
    const r = (crypto.getRandomValues(new Uint8Array(1))[0] & 15);
    return (c === "x" ? r : (r & 3) | 8).toString(16);
  });
}
function lienCarte(position, adresse) {
  if (position) return `https://www.google.com/maps/search/?api=1&query=${position.latitude},${position.longitude}`;
  return adresse ? `https://www.google.com/maps/search/?api=1&query=${encodeURIComponent(adresse)}` : null;
}

let minuteurBandeau;
function bandeau(texte, genre = "") {
  const b = $("#bandeau");
  clearTimeout(minuteurBandeau);
  if (!texte) { b.hidden = true; return; }
  b.textContent = texte;
  b.className = `bandeau ${genre}`;
  b.hidden = false;
  if (genre !== "erreur") minuteurBandeau = setTimeout(() => (b.hidden = true), 4000);
}
const erreur = (e) => bandeau(e?.message || String(e), "erreur");

/** Position du téléphone, ou null si refusée ou indisponible (l'activité s'enregistre quand même). */
function maPosition(delaiMs = 8000) {
  return new Promise((resolve) => {
    if (!navigator.geolocation) return resolve(null);
    navigator.geolocation.getCurrentPosition(
      (p) => resolve({ latitude: p.coords.latitude, longitude: p.coords.longitude, precision: p.coords.accuracy }),
      () => resolve(null),
      { enableHighAccuracy: true, timeout: delaiMs, maximumAge: 60000 });
  });
}

// ---------- Navigation entre écrans ----------
const ECRANS = ["reglages", "connexion", "clients", "agenda", "client"];
function afficher(ecran) {
  for (const e of ECRANS) $(`#ecran-${e}`).hidden = e !== ecran;
  const connecte = !!session();
  $("#navigation").hidden = !connecte || !["clients", "agenda", "client"].includes(ecran);
  $("#btn-retour").hidden = ecran !== "client";
  $("#nav-clients").classList.toggle("actif", ecran === "clients" || ecran === "client");
  $("#nav-agenda").classList.toggle("actif", ecran === "agenda");
  const s = session();
  $("#btn-utilisateur").hidden = !connecte;
  if (s) $("#btn-utilisateur").textContent = [s.collaborateur ? `${s.collaborateur.prenom ?? ""} ${s.collaborateur.nom ?? ""}`.trim() || s.utilisateur : s.utilisateur, nomSociete(s.dossier)].filter(Boolean).join(" · ");
  if (ecran === "connexion") preparerChoix($("#choix-dossier"), $("#form-connexion").dossier, cleApi());
  if (ecran !== "client") $("#titre").textContent = { reglages: "Réglages", connexion: "CRM", clients: "Clients", agenda: "Agenda" }[ecran];
  window.scrollTo(0, 0);
}

function demarrer() {
  if (!cleApi()) return afficher("reglages");
  if (!session()) return afficher("connexion");
  ouvrirClients();
  compterAFaire();
}

// ---------- Réglages et connexion ----------
$("#form-reglages").addEventListener("submit", (e) => {
  e.preventDefault();
  ecrireJson(CLE_REGLAGES, { cle: new FormData(e.target).get("cle").trim() });
  bandeau("Réglages enregistrés.", "ok");
  demarrer();
});
$("#btn-ouvrir-reglages").addEventListener("click", () => {
  $("#form-reglages").cle.value = cleApi();
  afficher("reglages");
});

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
    bandeau(r.collaborateur ? "" : "Votre utilisateur Sage n'est rattaché à aucun collaborateur : le portefeuille sera vide.", r.collaborateur ? "" : "erreur");
    if (!r.collaborateur) etat.segment = "tous";
    etat.portefeuille = null;
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
  etat.portefeuille = null;
  afficher("connexion");
});

// ---------- Clients ----------
function choisirSegment(segment) {
  etat.segment = segment;
  ecrireJson(CLE_SEGMENT, segment);
  $("#seg-portefeuille").classList.toggle("actif", segment === "portefeuille");
  $("#seg-tous").classList.toggle("actif", segment === "tous");
  chargerClients();
}
$("#seg-portefeuille").addEventListener("click", () => choisirSegment("portefeuille"));
$("#seg-tous").addEventListener("click", () => choisirSegment("tous"));

let minuteurRecherche;
$("#recherche").addEventListener("input", () => {
  clearTimeout(minuteurRecherche);
  minuteurRecherche = setTimeout(chargerClients, 300);
});

function ouvrirClients() {
  afficher("clients");
  $("#seg-portefeuille").classList.toggle("actif", etat.segment === "portefeuille");
  $("#seg-tous").classList.toggle("actif", etat.segment === "tous");
  chargerClients();
}

let numeroChargement = 0;
async function chargerClients() {
  const n = ++numeroChargement;
  const texte = $("#recherche").value.trim();
  const liste = $("#liste-clients");
  try {
    let clients;
    if (etat.segment === "portefeuille") {
      if (!monCollaborateur()) {
        liste.innerHTML = `<li class="vide">Votre utilisateur Sage n'est rattaché à aucun collaborateur. Choisissez « Tous les clients ».</li>`;
        return;
      }
      etat.portefeuille ??= await api("GET", "/crm/portefeuille");
      const t = texte.toLowerCase();
      clients = etat.portefeuille.filter((c) => !t || [c.numero, c.intitule, c.ville].some((v) => v?.toLowerCase().includes(t)));
    } else {
      clients = await api("GET", `/clients?taille=100${texte ? `&recherche=${encodeURIComponent(texte)}` : ""}`);
    }
    if (n !== numeroChargement) return;
    liste.innerHTML = clients.length
      ? clients.map((c) => `
        <li><button type="button" class="ligne" data-client="${echapper(c.numero)}">
          <strong>${echapper(c.intitule || c.numero)}${c.sommeil ? '<span class="etiquette alerte">En sommeil</span>' : ""}</strong>
          <span class="droite">${etat.segment === "portefeuille" ? `Dernière activité<br>${ecart(c.derniereActivite)}` : ""}</span>
          <span class="discret">${echapper([c.numero, c.ville].filter(Boolean).join(" · "))}</span>
        </button></li>`).join("")
      : `<li class="vide">${texte ? "Aucun client ne correspond." : "Aucun client dans votre portefeuille."}</li>`;
  } catch (e) {
    if (n === numeroChargement) { liste.innerHTML = ""; erreur(e); }
  }
}
$("#liste-clients").addEventListener("click", (e) => {
  const b = e.target.closest("[data-client]");
  if (b) ouvrirClient(b.dataset.client);
});

// ---------- Vue 360° d'un client ----------
async function ouvrirClient(numero, depuis = null) {
  if (depuis) etat.pile.push(depuis);
  etat.client = numero;
  afficher("client");
  $("#titre").textContent = numero;
  for (const id of ["client-entete", "client-indicateurs", "client-afaire", "client-activites", "client-articles", "client-documents", "client-contacts", "client-adresses"])
    $(`#${id}`).innerHTML = "";
  $("#client-entete").innerHTML = `<p class="discret">Chargement…</p>`;
  try {
    const [synthese, documents] = await Promise.all([
      api("GET", `/crm/clients/${encodeURIComponent(numero)}/synthese`),
      api("GET", `/clients/${encodeURIComponent(numero)}/documents?taille=10`).catch(() => []),
    ]);
    if (etat.client !== numero) return;
    etat.synthese = synthese;
    dessinerClient(synthese, documents);
  } catch (e) {
    $("#client-entete").innerHTML = `<p class="vide">${e.statut === 404 ? "Client introuvable." : "Impossible de charger le client."}</p>`;
    erreur(e);
  }
}

function dessinerClient({ fiche: f, indicateurs: i, recouvrement: r, prochainesActions, activitesRecentes }, documents) {
  $("#titre").textContent = f.intitule || f.numero;
  const adresse = [f.adresse, f.complement, [f.codePostal, f.ville].filter(Boolean).join(" "), f.pays].filter(Boolean).join(", ");
  const carte = lienCarte(f.position, adresse);
  const depasse = f.encoursAutorise > 0 && r.solde > f.encoursAutorise;
  $("#client-entete").innerHTML = `
    <h2>${echapper(f.intitule || f.numero)}${f.sommeil ? '<span class="etiquette alerte">En sommeil</span>' : ""}${r.echu > 0 ? '<span class="etiquette erreur">Impayés</span>' : ""}</h2>
    <div class="coord">${echapper(f.numero)}${adresse ? ` · ${echapper(adresse)}` : ""}${f.commercialNom ? `<br>Commercial : ${echapper(f.commercialNom)}` : ""}${f.contact ? `<br>Contact : ${echapper(f.contact)}` : ""}</div>
    <div class="contact-rapide">
      ${f.telephone ? `<a href="tel:${echapper(f.telephone.replace(/\s/g, ""))}">📞 ${echapper(f.telephone)}</a>` : ""}
      ${f.email ? `<a href="mailto:${echapper(f.email)}">✉️ E-mail</a>` : ""}
      ${carte ? `<a href="${echapper(carte)}" target="_blank" rel="noopener">🗺️ ${f.position ? "Itinéraire" : "Carte"}</a>` : ""}
    </div>
    ${f.commentaire ? `<p class="discret">${echapper(f.commentaire)}</p>` : ""}`;

  const evolution = i.caDouzeMoisPrecedents > 0 ? Math.round(((i.caDouzeMois - i.caDouzeMoisPrecedents) / i.caDouzeMoisPrecedents) * 100) : null;
  const ind = (titre, valeur, detail = "", classe = "") => `<div class="indicateur ${classe}"><span>${titre}</span><strong>${valeur}</strong><small>${detail}</small></div>`;
  $("#client-indicateurs").innerHTML = [
    ind("CA HT 12 mois", montant(i.caDouzeMois),
      evolution == null ? `${i.facturesDouzeMois} facture(s)` : `${evolution >= 0 ? "▲" : "▼"} ${Math.abs(evolution)} % vs 12 mois avant`,
      evolution == null ? "" : evolution >= 0 ? "hausse" : "baisse"),
    ind("Solde dû", montant(r.solde), r.echu > 0 ? `Échu ${montant(r.echu)} · ${r.retardMaxJours} j de retard` : "Rien d'échu",
      r.echu > 0 || depasse ? "attention" : ""),
    ind("Commandes en cours", i.commandesEnCours, i.commandesEnCours ? `${montant(i.montantCommandesEnCours)} HT` : `Dernière : ${dateCourte(i.derniereCommande)}`),
    ind("Devis en cours", i.devisEnCours, i.devisEnCours ? `${montant(i.montantDevisEnCours)} HT` : ""),
  ].join("") + (f.encoursAutorise > 0 ? ind("Encours autorisé", montant(f.encoursAutorise), depasse ? "Dépassé" : "", depasse ? "attention" : "") : "")
    + (r.derniereRelance ? ind("Dernière relance", dateCourte(r.derniereRelance)) : "");

  $("#client-afaire").innerHTML = prochainesActions.length
    ? prochainesActions.map((a) => ligneActivite(a, true)).join("") : `<li class="vide">Rien de prévu.</li>`;
  $("#client-activites").innerHTML = activitesRecentes.length
    ? activitesRecentes.map((a) => ligneActivite(a, false)).join("") : `<li class="vide">Aucune activité enregistrée.</li>`;

  $("#client-articles").innerHTML = i.articlesLesPlusAchetes.length
    ? `<table class="tableau"><thead><tr><th>Article</th><th class="nombre">Qté</th><th class="nombre">HT</th></tr></thead><tbody>${
      i.articlesLesPlusAchetes.map((a) => `<tr><td>${echapper(a.designation || a.article)}<br><span class="discret">${echapper(a.article)}</span></td>
        <td class="nombre">${montant(a.quantite)}</td><td class="nombre">${montant(a.montantHT)}</td></tr>`).join("")}</tbody></table>`
    : `<p class="vide">Aucun achat sur 12 mois.</p>`;

  $("#client-documents").innerHTML = documents.length
    ? `<table class="tableau"><thead><tr><th>Date</th><th>Pièce</th><th class="nombre">TTC</th></tr></thead><tbody>${
      documents.map((d) => `<tr><td>${dateCourte(d.date)}</td><td>${echapper(NOMS_DOCUMENTS[d.type] ?? d.type)} ${echapper(d.piece)}${
        d.reference ? `<br><span class="discret">${echapper(d.reference)}</span>` : ""}</td><td class="nombre">${montant(d.totalTTC)}</td></tr>`).join("")}</tbody></table>`
    : `<p class="vide">Aucun document.</p>`;

  $("#client-contacts").innerHTML = f.contacts.length
    ? f.contacts.map((c) => {
      const tel = c.portable || c.telephone;
      return `<li><strong>${echapper([c.prenom, c.nom].filter(Boolean).join(" ") || "Contact")}</strong>${c.fonction ? ` · <span class="discret">${echapper(c.fonction)}</span>` : ""}
        <div class="contact-rapide" style="margin-top:6px">${tel ? `<a href="tel:${echapper(tel.replace(/\s/g, ""))}">📞 ${echapper(tel)}</a>` : ""}${
          c.email ? `<a href="mailto:${echapper(c.email)}">✉️ ${echapper(c.email)}</a>` : ""}</div></li>`;
    }).join("") : `<li class="vide">Aucun contact.</li>`;

  $("#client-adresses").innerHTML = f.adressesLivraison.length
    ? f.adressesLivraison.map((a) => {
      const texte = [a.adresse, a.complement, [a.codePostal, a.ville].filter(Boolean).join(" ")].filter(Boolean).join(", ");
      const lien = lienCarte(a.position, texte);
      return `<li><strong>${echapper(a.intitule || "Adresse")}</strong>${a.principale ? ' <span class="discret">(principale)</span>' : ""}<br>
        <span class="discret">${echapper(texte)}</span>${lien ? ` <a href="${echapper(lien)}" target="_blank" rel="noopener">🗺️</a>` : ""}</li>`;
    }).join("") : `<li class="vide">Aucune adresse de livraison.</li>`;
}

function ligneActivite(a, aFaire) {
  const t = typeActivite(a.type);
  const date = aFaire ? a.datePrevue : a.dateRealisee ?? a.datePrevue ?? a.creeLe;
  const retard = aFaire && new Date(a.datePrevue) < new Date();
  return `<li class="activite${retard ? " retard" : ""}" data-activite="${echapper(a.id)}">
    <span class="icone-type" aria-hidden="true">${t.icone}</span>
    <div><strong>${echapper(a.sujet || t.nom)}</strong>${a.statut === "annule" ? ' <span class="etiquette alerte">Annulé</span>' : ""}
      <div class="quand">${dateCourte(date)} ${heure(date)} · ${ecart(date)}${a.utilisateur ? ` · ${echapper(a.utilisateur)}` : ""}</div></div>
    ${aFaire ? `<button type="button" class="fait secondaire" data-fait="${echapper(a.id)}">Fait</button>` : ""}
    ${a.compteRendu ? `<div class="texte">${echapper(a.compteRendu)}</div>` : ""}
  </li>`;
}

/** Clic sur une activité : la modifier ; sur « Fait » : la marquer faite tout de suite. */
async function clicActivite(e, sources) {
  const fait = e.target.closest("[data-fait]");
  const ligne = e.target.closest("[data-activite]");
  if (!ligne) return;
  const id = fait?.dataset.fait ?? ligne.dataset.activite;
  const activite = sources().find((a) => a.id === id) ?? await api("GET", `/crm/activites/${encodeURIComponent(id)}`).catch(erreur);
  if (!activite) return;
  if (fait) {
    fait.disabled = true;
    try {
      await enregistrer(activite.id, { ...versRequete(activite), statut: "fait", dateRealisee: new Date().toISOString() });
      bandeau("Activité marquée faite.", "ok");
      rafraichir();
    } catch (err) { erreur(err); fait.disabled = false; }
  } else {
    ouvrirDialogue(activite);
  }
}
$("#client-afaire").addEventListener("click", (e) => clicActivite(e, () => etat.synthese?.prochainesActions ?? []));
$("#client-activites").addEventListener("click", (e) => clicActivite(e, () => etat.synthese?.activitesRecentes ?? []));

$("#btn-retour").addEventListener("click", () => (etat.pile.pop() === "agenda" ? ouvrirAgenda() : ouvrirClients()));

$("#btn-position-client").addEventListener("click", async () => {
  const f = etat.synthese?.fiche;
  if (!f) return;
  if (f.position && !confirm("Ce client a déjà une position. La remplacer par votre position actuelle ?")) return;
  const bouton = $("#btn-position-client");
  bouton.disabled = true;
  try {
    const p = await maPosition(15000);
    if (!p) return bandeau("Position indisponible : autorisez la localisation pour ce site.", "erreur");
    await api("PUT", `/geolocalisation/client/${encodeURIComponent(f.numero)}`, { ...p, source: "crm" });
    bandeau("Position du client enregistrée.", "ok");
    ouvrirClient(f.numero);
  } catch (e) { erreur(e); } finally { bouton.disabled = false; }
});

// ---------- Saisie d'une activité ----------
const dialogue = $("#dialogue-activite");
const form = $("#form-activite");

$("#activite-types").innerHTML = TYPES.map((t) => `<button type="button" data-type="${t.code}">${t.icone} ${t.nom}</button>`).join("");
function choisirType(code) {
  etat.type = code;
  for (const b of $("#activite-types").children) b.classList.toggle("actif", b.dataset.type === code);
}
$("#activite-types").addEventListener("click", (e) => { const b = e.target.closest("[data-type]"); if (b) choisirType(b.dataset.type); });

function ouvrirDialogue(activite = null) {
  const f = etat.synthese?.fiche;
  etat.activite = activite;
  form.reset();
  $("#activite-titre").textContent = activite ? "Modifier l'activité" : `Nouvelle activité${f ? ` · ${f.intitule || f.numero}` : ""}`;
  choisirType(activite?.type ?? "visite");
  form.sujet.value = activite?.sujet ?? "";
  form.compteRendu.value = activite?.compteRendu ?? "";
  form.statut.value = activite?.statut ?? "fait";
  form.date.value = versChampDate(activite ? (activite.statut === "a-faire" ? activite.datePrevue : activite.dateRealisee ?? activite.datePrevue ?? activite.creeLe) : new Date());
  const contacts = (etat.client === activite?.client || !activite) ? f?.contacts ?? [] : [];
  form.contact.innerHTML = `<option value="">—</option>` + contacts.map((c) =>
    `<option value="${c.numero}">${echapper([c.prenom, c.nom].filter(Boolean).join(" ") || c.numero)}${c.fonction ? ` (${echapper(c.fonction)})` : ""}</option>`).join("");
  form.contact.value = activite?.contact ?? "";
  form.gps.checked = !activite;
  form.querySelector(".suite").hidden = !!activite;
  $("#btn-supprimer-activite").hidden = !activite;
  dialogue.showModal();
}
$("#btn-nouvelle-activite").addEventListener("click", () => ouvrirDialogue());
$("#btn-annuler-activite").addEventListener("click", () => dialogue.close());

/** Activité lue -> corps de PUT, pour la renvoyer modifiée sans rien perdre. */
function versRequete(a) {
  return {
    client: a.client, type: a.type, sujet: a.sujet, compteRendu: a.compteRendu, statut: a.statut, datePrevue: a.datePrevue,
    dateRealisee: a.dateRealisee, collaborateur: a.collaborateur, contact: a.contact, document: a.document, latitude: a.latitude, longitude: a.longitude,
  };
}
const enregistrer = (id, corps) => api("PUT", `/crm/activites/${encodeURIComponent(id)}`, corps);

form.addEventListener("submit", async (e) => {
  e.preventDefault();
  const a = etat.activite;
  const client = a?.client ?? etat.synthese?.fiche?.numero;
  if (!client) return;
  const date = new Date(form.date.value).toISOString();
  const statut = form.statut.value;
  const bouton = $("#btn-enregistrer-activite");
  bouton.disabled = true;
  try {
    const position = form.gps.checked ? await maPosition() : null;
    const corps = {
      ...(a ? versRequete(a) : {}),
      client, type: etat.type, sujet: form.sujet.value.trim() || null, compteRendu: form.compteRendu.value.trim() || null, statut,
      contact: form.contact.value ? Number(form.contact.value) : null,
      datePrevue: statut === "a-faire" ? date : a?.datePrevue ?? null,
      dateRealisee: statut === "fait" ? date : null,
      ...(position ? { latitude: position.latitude, longitude: position.longitude } : {}),
    };
    await enregistrer(a?.id ?? uuid(), corps);
    // Prochaine action : une seconde activité « à faire », pour l'agenda.
    const suiteSujet = form.suiteSujet.value.trim();
    if (!a && suiteSujet) {
      const quand = form.suiteDate.value ? new Date(form.suiteDate.value) : new Date(Date.now() + 7 * 86400000);
      await enregistrer(uuid(), { client, type: "tache", sujet: suiteSujet, statut: "a-faire", datePrevue: quand.toISOString(), contact: corps.contact });
    }
    dialogue.close();
    bandeau(position || !form.gps.checked ? "Activité enregistrée." : "Activité enregistrée (position indisponible).", "ok");
    etat.portefeuille = null;
    rafraichir();
  } catch (err) {
    bandeau(err.message, "erreur");
  } finally {
    bouton.disabled = false;
  }
});

$("#btn-supprimer-activite").addEventListener("click", async () => {
  const a = etat.activite;
  if (!a || !confirm("Supprimer cette activité ?")) return;
  try {
    await api("DELETE", `/crm/activites/${encodeURIComponent(a.id)}`);
    dialogue.close();
    bandeau("Activité supprimée.", "ok");
    rafraichir();
  } catch (e) { erreur(e); }
});

/** Recharge l'écran visible après une modification. */
function rafraichir() {
  compterAFaire();
  if (!$("#ecran-agenda").hidden) chargerAgenda();
  else if (!$("#ecran-client").hidden && etat.client) ouvrirClient(etat.client);
}

// ---------- Agenda ----------
let agenda = [];
async function lireAFaire() {
  const co = monCollaborateur();
  const filtre = co ? `collaborateur=${co}` : `utilisateur=${encodeURIComponent(session()?.utilisateur ?? "")}`;
  return api("GET", `/crm/activites?statut=a-faire&${filtre}&taille=300`);
}
async function compterAFaire() {
  try {
    const liste = await lireAFaire();
    const n = liste.filter((a) => jour(a.datePrevue) <= jour(new Date())).length;
    $("#nb-afaire").textContent = n;
    $("#nb-afaire").hidden = n === 0;
  } catch { /* la pastille attendra */ }
}

function ouvrirAgenda() {
  afficher("agenda");
  chargerAgenda();
}
async function chargerAgenda() {
  const zone = $("#agenda");
  try {
    agenda = await lireAFaire();
    const auj = jour(new Date()).getTime();
    const groupes = [
      { titre: "En retard", classe: "retard", liste: agenda.filter((a) => jour(a.datePrevue) < auj) },
      { titre: "Aujourd'hui", classe: "", liste: agenda.filter((a) => jour(a.datePrevue).getTime() === auj) },
      { titre: "À venir", classe: "", liste: agenda.filter((a) => jour(a.datePrevue) > auj) },
    ].filter((g) => g.liste.length);
    zone.innerHTML = groupes.length
      ? groupes.map((g) => `<section class="agenda-groupe ${g.classe}"><h3>${g.titre} (${g.liste.length})</h3><ul class="liste">${
        g.liste.map((a) => ligneActivite(a, true).replace("<strong>", `<strong><span class="discret">${echapper(a.client)}</span> · `)).join("")}</ul></section>`).join("")
      : `<p class="vide">Rien à faire. Les prochaines actions saisies sur vos clients apparaîtront ici.</p>`;
  } catch (e) { zone.innerHTML = ""; erreur(e); }
}
$("#agenda").addEventListener("click", (e) => {
  if (e.target.closest("[data-fait]")) return clicActivite(e, () => agenda);
  const ligne = e.target.closest("[data-activite]");
  const a = ligne && agenda.find((x) => x.id === ligne.dataset.activite);
  if (a) ouvrirClient(a.client, "agenda");
});

$("#nav-clients").addEventListener("click", ouvrirClients);
$("#nav-agenda").addEventListener("click", ouvrirAgenda);

demarrer();
