// Livraisons : préparation des tournées au bureau et application du livreur (ordre de passage, itinéraire, preuve de livraison).
// Les pièces à livrer sont lues dans Sage par l'API ; tournées et preuves sont gardées par l'API dans sa propre base, jamais dans Sage.

const CLE_REGLAGES = "livraison.reglages";
const CLE_SESSION = "livraison.session";
const CLE_SEGMENT = "livraison.segment";

const STATUTS = { "a-livrer": ["À livrer", "neutre"], livre: ["Livré", "ok"], partiel: ["Partiel", "alerte"], echec: ["Échec", "erreur"] };
const MOTIFS = { absent: "Client absent", refus: "Refus du client", "adresse-introuvable": "Adresse introuvable", ferme: "Établissement fermé",
  "manque-marchandise": "Marchandise manquante", endommage: "Marchandise endommagée", "erreur-commande": "Erreur de commande", retour: "Retour", autre: "Autre" };
const ETATS_TOURNEE = { preparee: ["Préparée", "neutre"], "en-cours": ["En cours", "alerte"], terminee: ["Terminée", "ok"] };

const $ = (s) => document.querySelector(s);
const etat = {
  segment: lireJson(CLE_SEGMENT, "miennes"), collaborateurs: null, depots: null,
  tournee: null, aLivrer: [], choisies: [], edition: null, arret: null, statut: "livre", retour: "tournees",
};

// ---------- Stockage local (protégé : navigation privée ou stockage bloqué) ----------
function lireJson(cle, defaut) {
  try { const v = localStorage.getItem(cle); return v == null ? defaut : JSON.parse(v); } catch { return defaut; }
}
function ecrireJson(cle, valeur) {
  try { valeur == null ? localStorage.removeItem(cle) : localStorage.setItem(cle, JSON.stringify(valeur)); } catch { /* ignoré */ }
}
/** La clé d'API de la borne ou du CRM est reprise si l'un d'eux a déjà été réglé sur cet appareil. */
// Types de pièces livrables proposés à la préparation (réglage de l'appareil) et période des factures.
const TYPES_LIVRABLES = { commande: "BC", preparation: "PL", livraison: "BL", facture: "FA", "facture-comptabilisee": "FA compta." };
const REGLAGES_DEFAUT = { types: Object.keys(TYPES_LIVRABLES), joursFactures: 30 };
function reglages() {
  const r = lireJson(CLE_REGLAGES, null) ?? {};
  return {
    types: Array.isArray(r.types) ? r.types.filter((t) => t in TYPES_LIVRABLES) : REGLAGES_DEFAUT.types,
    joursFactures: Number(r.joursFactures) > 0 ? Number(r.joursFactures) : REGLAGES_DEFAUT.joursFactures,
  };
}
function remplirReglages() {
  const f = $("#form-reglages");
  const r = reglages();
  f.cle.value = cleApi();
  for (const c of f.querySelectorAll('input[name="types"]')) c.checked = r.types.includes(c.value);
  f.joursFactures.value = r.joursFactures;
}

function cleApi() {
  return lireJson(CLE_REGLAGES, null)?.cle || lireJson("crm.reglages", null)?.cle || lireJson("borne.reglages", null)?.cle || "";
}
function session() {
  const s = lireJson(CLE_SESSION, null);
  return s && (!s.expiration || new Date(s.expiration) > new Date()) ? s : null;
}

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
    remplirReglages();
    afficher("reglages");
    throw new ErreurApi("Clé d'API refusée : vérifiez-la.", 401);
  }
  if (r.status === 401 && donnees?.code === "CONNEXION_REQUISE") {
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
const dateLongue = (d) => new Date(d).toLocaleDateString("fr-FR", { weekday: "long", day: "numeric", month: "long" });
const heure = (d) => new Date(d).toLocaleTimeString("fr-FR", { hour: "2-digit", minute: "2-digit" });
/** Date du jour au format des champs date (heure locale). */
function aujourdhui() {
  const x = new Date();
  x.setMinutes(x.getMinutes() - x.getTimezoneOffset());
  return x.toISOString().slice(0, 10);
}
const etiquette = ([texte, genre]) => `<span class="etiquette ${genre}">${texte}</span>`;
function uuid() {
  if (crypto.randomUUID) return crypto.randomUUID();
  return "xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx".replace(/[xy]/g, (c) => {
    const r = (crypto.getRandomValues(new Uint8Array(1))[0] & 15);
    return (c === "x" ? r : (r & 3) | 8).toString(16);
  });
}
const adresseTexte = (a) => [a.adresse, a.complement, [a.codePostal, a.ville].filter(Boolean).join(" ")].filter(Boolean).join(", ");
/** Destination pour Google Maps : la position si elle est connue, sinon l'adresse. */
const destination = (a) => (a.latitude != null ? `${a.latitude},${a.longitude}` : a.position ? `${a.position.latitude},${a.position.longitude}` : adresseTexte(a));
const lienItineraire = (a) => `https://www.google.com/maps/dir/?api=1&travelmode=driving&destination=${encodeURIComponent(destination(a))}`;
function nomCollaborateur(numero) {
  const c = etat.collaborateurs?.find((x) => x.numero === numero);
  return c ? [c.prenom, c.nom].filter(Boolean).join(" ") : numero ? `Collaborateur ${numero}` : "Sans livreur";
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

/** Position du téléphone, ou null si refusée ou indisponible (la livraison s'enregistre quand même). */
function maPosition(delaiMs = 8000) {
  return new Promise((resolve) => {
    if (!navigator.geolocation) return resolve(null);
    navigator.geolocation.getCurrentPosition(
      (p) => resolve({ latitude: p.coords.latitude, longitude: p.coords.longitude }),
      () => resolve(null),
      { enableHighAccuracy: true, timeout: delaiMs, maximumAge: 60000 });
  });
}

// ---------- Navigation entre écrans ----------
const ECRANS = ["reglages", "connexion", "tournees", "preparation", "tournee", "chargement"];
function afficher(ecran, titre) {
  for (const e of ECRANS) $(`#ecran-${e}`).hidden = e !== ecran;
  const s = session();
  $("#btn-retour").hidden = !["preparation", "tournee", "chargement"].includes(ecran) && !(ecran === "reglages" && etat.reglagesDepuisPreparation);
  $("#btn-utilisateur").hidden = !s;
  if (s) $("#btn-utilisateur").textContent = s.collaborateur ? `${s.collaborateur.prenom ?? ""} ${s.collaborateur.nom ?? ""}`.trim() || s.utilisateur : s.utilisateur;
  $("#titre").textContent = titre ?? { reglages: "Réglages", connexion: "Livraisons", tournees: "Tournées", preparation: "Préparer une tournée", tournee: "Tournée" }[ecran];
  window.scrollTo(0, 0);
}

async function chargerReferentiels() {
  if (etat.collaborateurs && etat.depots) return;
  const [collaborateurs, depots] = await Promise.all([api("GET", "/collaborateurs").catch(() => []), api("GET", "/depots").catch(() => [])]);
  etat.collaborateurs = collaborateurs;
  etat.depots = depots;
}

function demarrer() {
  if (!cleApi()) { remplirReglages(); return afficher("reglages"); }
  if (!session()) return afficher("connexion");
  if (!session().collaborateur && etat.segment === "miennes") etat.segment = "toutes";
  // Lien depuis le tableau de bord : ?tournee=<id>
  const demandee = new URLSearchParams(location.search).get("tournee");
  if (demandee) {
    history.replaceState(null, "", location.pathname);
    return ouvrirTournee(demandee);
  }
  ouvrirTournees();
}

// ---------- Réglages et connexion ----------
$("#form-reglages").addEventListener("submit", (e) => {
  e.preventDefault();
  const f = new FormData(e.target);
  const types = f.getAll("types");
  if (!types.length) return bandeau("Cochez au moins un type de pièce à livrer.", "erreur");
  ecrireJson(CLE_REGLAGES, { cle: f.get("cle").trim(), types, joursFactures: Number(f.get("joursFactures")) || REGLAGES_DEFAUT.joursFactures });
  bandeau("Réglages enregistrés.", "ok");
  if (etat.reglagesDepuisPreparation) return revenirPreparation(true);
  demarrer();
});
$("#btn-ouvrir-reglages").addEventListener("click", () => {
  remplirReglages();
  afficher("reglages");
});
// Depuis la préparation : changer les types proposés sans perdre la tournée en cours.
$("#btn-types-pieces").addEventListener("click", () => {
  etat.reglagesDepuisPreparation = true;
  remplirReglages();
  afficher("reglages");
});
function revenirPreparation(recharger) {
  etat.reglagesDepuisPreparation = false;
  afficher("preparation", etat.edition ? "Modifier la tournée" : "Préparer une tournée");
  if (recharger) chargerALivrer();
}
$("#form-connexion").addEventListener("submit", async (e) => {
  e.preventDefault();
  const f = new FormData(e.target);
  const bouton = $("#btn-connexion");
  bouton.disabled = true;
  try {
    const r = await api("POST", "/connexion", { utilisateur: f.get("utilisateur").trim(), motDePasse: f.get("motDePasse") });
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
$("#btn-retour").addEventListener("click", () => {
  if (!$("#ecran-reglages").hidden && etat.reglagesDepuisPreparation) return revenirPreparation(false);
  if ((!$("#ecran-preparation").hidden || !$("#ecran-chargement").hidden) && etat.edition) return ouvrirTournee(etat.edition);
  ouvrirTournees();
});

// ---------- Liste des tournées ----------
$("#date-tournees").value = aujourdhui();
function choisirSegment(segment) {
  etat.segment = segment;
  ecrireJson(CLE_SEGMENT, segment);
  chargerTournees();
}
$("#seg-miennes").addEventListener("click", () => choisirSegment("miennes"));
$("#seg-toutes").addEventListener("click", () => choisirSegment("toutes"));
$("#date-tournees").addEventListener("change", () => chargerTournees());

function ouvrirTournees() {
  afficher("tournees");
  chargerTournees();
}

async function chargerTournees() {
  $("#seg-miennes").classList.toggle("actif", etat.segment === "miennes");
  $("#seg-toutes").classList.toggle("actif", etat.segment === "toutes");
  const liste = $("#liste-tournees");
  const date = $("#date-tournees").value;
  try {
    await chargerReferentiels();
    const filtre = `${date ? `du=${date}&au=${date}` : ""}${etat.segment === "miennes" ? "&miennes=true" : ""}`;
    const tournees = await api("GET", `/livraisons/tournees?${filtre}`);
    liste.innerHTML = tournees.length
      ? tournees.map((t) => `
        <li><button type="button" class="ligne" data-tournee="${echapper(t.id)}">
          <strong>${echapper(t.nom || `Tournée du ${dateCourte(t.date)}`)} ${etiquette(ETATS_TOURNEE[t.statut])}</strong>
          <span class="droite">${t.nbTraites}/${t.nbArrets}<br>${montant(t.totalTTC)}</span>
          <span class="discret">${echapper(nomCollaborateur(t.livreur))} · ${t.nbArrets} arrêt(s)</span>
        </button></li>`).join("")
      : `<li class="vide">${etat.segment === "miennes" ? "Aucune tournée pour vous ce jour-là." : "Aucune tournée ce jour-là."}</li>`;
  } catch (e) { liste.innerHTML = ""; erreur(e); }
}
$("#liste-tournees").addEventListener("click", (e) => {
  const b = e.target.closest("[data-tournee]");
  if (b) ouvrirTournee(b.dataset.tournee);
});
$("#btn-nouvelle-tournee").addEventListener("click", () => ouvrirPreparation(null));

// ---------- Préparation d'une tournée ----------
const formTournee = $("#form-tournee");

async function ouvrirPreparation(tournee) {
  etat.edition = tournee?.id ?? null;
  afficher("preparation", tournee ? "Modifier la tournée" : "Préparer une tournée");
  $("#btn-supprimer-tournee").hidden = !tournee;
  $("#distance").hidden = true;
  $("#filtre-pieces").value = "";
  await chargerReferentiels();
  formTournee.livreur.innerHTML = `<option value="">—</option>` + etat.collaborateurs.map((c) =>
    `<option value="${c.numero}">${echapper([c.prenom, c.nom].filter(Boolean).join(" ") || c.numero)}</option>`).join("");
  formTournee.depot.innerHTML = `<option value="">Ma position</option>` + etat.depots.map((d) =>
    `<option value="${d.numero}">${echapper(d.intitule || d.numero)}</option>`).join("");
  formTournee.date.value = tournee ? tournee.date.slice(0, 10) : $("#date-tournees").value || aujourdhui();
  formTournee.nom.value = tournee?.nom ?? "";
  formTournee.livreur.value = tournee?.livreur ?? session()?.collaborateur?.numero ?? "";
  formTournee.depot.value = tournee?.depot ?? (etat.depots.length === 1 ? etat.depots[0].numero : "");
  // Les arrêts déjà prévus restent, même si Sage ne les liste plus comme « à livrer » (pièce transformée en BL).
  etat.choisies = (tournee?.arrets ?? []).map((a) => ({ ...a, verrouille: a.statut !== "a-livrer" }));
  dessinerOrdre();
  await chargerALivrer();
}

async function chargerALivrer() {
  const zone = $("#pieces");
  zone.innerHTML = `<p class="discret">Chargement…</p>`;
  try {
    const r = reglages();
    const depuis = new Date(Date.now() - r.joursFactures * 86400000).toISOString().slice(0, 10);
    etat.aLivrer = await api("GET", `/livraisons/a-livrer?types=${encodeURIComponent(r.types.join(","))}&depuis=${depuis}`);
    dessinerPieces();
  } catch (e) { zone.innerHTML = ""; erreur(e); }
}

/** Pièce déjà prévue dans une autre tournée (pas encore livrée) : non sélectionnable. Livrée en partie ou en échec : elle peut repartir. */
const prevueAilleurs = (a) => a.statutLivraison === "a-livrer" && a.tournee !== etat.edition;

function dessinerPieces() {
  const t = $("#filtre-pieces").value.trim().toLowerCase();
  const choisies = new Set(etat.choisies.map((a) => a.piece));
  const liste = etat.aLivrer.filter((a) => !t || [a.piece, a.client, a.intitule, a.ville].some((v) => v?.toLowerCase().includes(t)));
  $("#pieces").innerHTML = liste.length
    ? liste.map((a) => {
      const prevue = prevueAilleurs(a) && !choisies.has(a.piece);
      const suivi = prevue ? ` ${etiquette(["Déjà en tournée", "neutre"])}<br><span class="discret">Prévue le ${dateCourte(a.dateTournee)}</span>`
        : a.statutLivraison === "partiel" ? ` ${etiquette(["Livrée en partie", "alerte"])}`
        : a.statutLivraison === "echec" ? ` ${etiquette(["Échec précédent", "erreur"])}` : "";
      return `
      <label class="piece-choix${prevue ? " prevue" : ""}">
        <input type="checkbox" data-piece="${echapper(a.piece)}" ${choisies.has(a.piece) ? "checked" : ""} ${prevue ? "disabled" : ""}>
        <span><strong>${echapper(a.intitule || a.client)}</strong> ${a.position ? "📍" : ""}${suivi}<br>
          <span class="discret"><span class="type-piece">${echapper(TYPES_LIVRABLES[a.type] ?? a.type)}</span> ${echapper(a.piece)} · ${echapper(a.ville || "ville ?")}${a.dateLivraison ? ` · pour le ${dateCourte(a.dateLivraison)}` : ""}</span></span>
        <span class="nombre">${montant(a.totalTTC)}</span>
      </label>`;
    }).join("")
    : `<p class="vide">${t ? "Aucune pièce ne correspond." : "Aucune pièce à livrer dans Sage pour les types choisis."}</p>`;
}
$("#filtre-pieces").addEventListener("input", dessinerPieces);
$("#pieces").addEventListener("change", (e) => {
  const piece = e.target.dataset.piece;
  if (!piece) return;
  if (e.target.checked) {
    const a = etat.aLivrer.find((x) => x.piece === piece);
    if (prevueAilleurs(a)) { e.target.checked = false; return bandeau(`${piece} est déjà prévue dans une autre tournée.`, "erreur"); }
    etat.choisies.push({ ...a, latitude: a.position?.latitude ?? null, longitude: a.position?.longitude ?? null });
  } else {
    const a = etat.choisies.find((x) => x.piece === piece);
    if (a?.verrouille) { e.target.checked = true; return bandeau(`${piece} est déjà traitée : elle reste dans la tournée.`, "erreur"); }
    etat.choisies = etat.choisies.filter((x) => x.piece !== piece);
  }
  $("#distance").hidden = true;
  dessinerOrdre();
});

function dessinerOrdre() {
  $("#nb-choisies").textContent = etat.choisies.length ? `(${etat.choisies.length})` : "";
  $("#ordre").innerHTML = etat.choisies.length
    ? etat.choisies.map((a, i) => `
      <div class="ordre-ligne">
        <span class="rang">${i + 1}</span>
        <span><strong>${echapper(a.intitule || a.client)}</strong> ${a.latitude != null ? "📍" : ""}${a.verrouille ? ` ${etiquette(STATUTS[a.statut])}` : ""}<br>
          <span class="discret">${echapper(a.piece)} · ${echapper(a.ville || "")}</span></span>
        <span class="fleches">
          <button type="button" data-monter="${i}" aria-label="Monter" ${i === 0 ? "disabled" : ""}>↑</button>
          <button type="button" data-descendre="${i}" aria-label="Descendre" ${i === etat.choisies.length - 1 ? "disabled" : ""}>↓</button>
          ${a.verrouille ? "" : `<button type="button" data-retirer="${i}" aria-label="Retirer">✕</button>`}
        </span>
      </div>`).join("")
    : `<p class="vide">Cochez les pièces à livrer ci-dessous.</p>`;
}
$("#ordre").addEventListener("click", (e) => {
  const b = e.target.closest("button");
  if (!b) return;
  const l = etat.choisies;
  const echanger = (i, j) => { [l[i], l[j]] = [l[j], l[i]]; };
  if (b.dataset.monter) echanger(+b.dataset.monter, +b.dataset.monter - 1);
  else if (b.dataset.descendre) echanger(+b.dataset.descendre, +b.dataset.descendre + 1);
  else if (b.dataset.retirer) { l.splice(+b.dataset.retirer, 1); dessinerPieces(); }
  $("#distance").hidden = true;
  dessinerOrdre();
});

$("#btn-optimiser").addEventListener("click", async () => {
  if (etat.choisies.length < 2) return bandeau("Choisissez au moins deux pièces.", "erreur");
  const bouton = $("#btn-optimiser");
  bouton.disabled = true;
  try {
    const depot = formTournee.depot.value ? Number(formTournee.depot.value) : null;
    const position = depot ? null : await maPosition();
    const r = await api("POST", "/livraisons/optimiser", { pieces: etat.choisies.map((a) => a.piece), depot, ...(position ?? {}) });
    const parPiece = new Map(etat.choisies.map((a) => [a.piece, a]));
    // Les pièces déjà traitées restent en tête, dans leur ordre : on ne réordonne que ce qui reste à livrer.
    const faites = etat.choisies.filter((a) => a.verrouille);
    etat.choisies = [...faites, ...r.ordre.map((p) => parPiece.get(p)).filter((a) => a && !a.verrouille)];
    dessinerOrdre();
    const distance = $("#distance");
    distance.textContent = `Environ ${montant(r.distanceKm)} km à vol d'oiseau${depot ? " depuis le dépôt, retour compris" : position ? " depuis votre position" : ""}.`
      + (r.sansPosition.length ? ` Sans position GPS, placées à la fin : ${r.sansPosition.join(", ")}.` : "");
    distance.hidden = false;
  } catch (e) { erreur(e); } finally { bouton.disabled = false; }
});

$("#btn-enregistrer-tournee").addEventListener("click", async () => {
  if (!formTournee.reportValidity()) return;
  if (!etat.choisies.length) return bandeau("Choisissez au moins une pièce à livrer.", "erreur");
  const bouton = $("#btn-enregistrer-tournee");
  bouton.disabled = true;
  try {
    const id = etat.edition ?? uuid();
    const t = await api("PUT", `/livraisons/tournees/${encodeURIComponent(id)}`, {
      date: formTournee.date.value, nom: formTournee.nom.value.trim() || null,
      livreur: formTournee.livreur.value ? Number(formTournee.livreur.value) : null,
      depot: formTournee.depot.value ? Number(formTournee.depot.value) : null,
      pieces: etat.choisies.map((a) => a.piece),
    });
    bandeau("Tournée enregistrée.", "ok");
    $("#date-tournees").value = t.date.slice(0, 10);
    afficherTournee(t);
  } catch (e) { erreur(e); } finally { bouton.disabled = false; }
});

$("#btn-supprimer-tournee").addEventListener("click", async () => {
  if (!etat.edition || !confirm("Supprimer cette tournée ?")) return;
  try {
    await api("DELETE", `/livraisons/tournees/${encodeURIComponent(etat.edition)}`);
    etat.edition = null;
    bandeau("Tournée supprimée.", "ok");
    ouvrirTournees();
  } catch (e) { erreur(e); }
});

// ---------- Tournée du livreur ----------
async function ouvrirTournee(id) {
  afficher("tournee");
  $("#arrets").innerHTML = "";
  $("#tournee-entete").innerHTML = `<p class="discret">Chargement…</p>`;
  try {
    await chargerReferentiels();
    afficherTournee(await api("GET", `/livraisons/tournees/${encodeURIComponent(id)}`));
  } catch (e) { $("#tournee-entete").innerHTML = ""; erreur(e); }
}

function afficherTournee(t) {
  etat.tournee = t;
  etat.edition = t.id;
  afficher("tournee", t.nom || `Tournée du ${dateCourte(t.date)}`);
  const restants = t.arrets.filter((a) => a.statut === "a-livrer");
  const encaisser = restants.reduce((s, a) => s + a.netAPayer, 0);
  $("#tournee-entete").innerHTML = `
    <strong>${echapper(dateLongue(t.date))}</strong> ${etiquette(ETATS_TOURNEE[t.statut])}
    <div class="discret">${echapper(nomCollaborateur(t.livreur))}${t.depot ? ` · départ ${echapper(etat.depots?.find((d) => d.numero === t.depot)?.intitule ?? `dépôt ${t.depot}`)}` : ""}</div>
    <div class="progression"><span style="width:${t.nbArrets ? Math.round((t.nbTraites / t.nbArrets) * 100) : 0}%"></span></div>
    <div class="discret">${t.nbTraites} arrêt(s) traité(s) sur ${t.nbArrets}${t.nbCourses ? ` · ${t.nbCoursesTraitees}/${t.nbCourses} course(s)` : ""}${encaisser > 0 ? ` · ${montant(encaisser)} à encaisser sur les arrêts restants` : ""}</div>`;
  etatChargement(t);
  dessinerCourses(t);

  // Itinéraire de ce qui reste à livrer (Google Maps accepte 9 étapes en plus de la destination).
  const lien = $("#lien-itineraire");
  const etapes = restants.slice(0, 10);
  if (etapes.length) {
    const dest = etapes.at(-1);
    const via = etapes.slice(0, -1).map(destination).join("|");
    lien.href = `https://www.google.com/maps/dir/?api=1&travelmode=driving&destination=${encodeURIComponent(destination(dest))}${via ? `&waypoints=${encodeURIComponent(via)}` : ""}`;
    lien.removeAttribute("aria-disabled");
  } else {
    lien.removeAttribute("href");
    lien.setAttribute("aria-disabled", "true");
  }

  const suivant = restants[0]?.piece;
  $("#arrets").innerHTML = t.arrets.map((a) => {
    const traite = a.statut !== "a-livrer";
    const tel = a.telephone?.replace(/\s/g, "");
    return `<li class="arret${traite ? " traite" : ""}${a.statut === "echec" ? " echec" : ""}${a.piece === suivant ? " suivant" : ""}" data-arret="${echapper(a.piece)}">
      <span class="numero">${traite ? (a.statut === "echec" ? "✕" : "✓") : a.ordre}</span>
      <div>
        <strong>${echapper(a.intitule || a.client)}</strong> ${etiquette(STATUTS[a.statut])}
        <div class="adresse">${echapper(adresseTexte(a) || "Adresse non renseignée")}</div>
        <div class="infos">${echapper(a.piece)}${a.reference ? ` · ${echapper(a.reference)}` : ""} · ${montant(a.totalTTC)} TTC${a.netAPayer > 0 ? ` · <strong>net à payer ${montant(a.netAPayer)}</strong>` : ""}</div>
        ${a.contact ? `<div class="infos">Contact : ${echapper(a.contact)}</div>` : ""}
        ${traite ? `<div class="infos">${heure(a.heure)}${a.receptionnaire ? ` · reçu par ${echapper(a.receptionnaire)}` : ""}${a.motif ? ` · ${echapper(MOTIFS[a.motif] ?? a.motif)}` : ""}${a.signe ? " · signé" : ""}</div>` : ""}
      </div>
      ${a.articles.length ? `<div class="articles"><ul>${a.articles.map((l) => {
        const reference = l.quantiteChargee ?? l.quantite;
        const manque = l.quantiteLivree != null && l.quantiteLivree < reference;
        return `<li class="${manque ? "manque" : ""}"><span>${echapper(l.designation || l.article)}${l.gamme1 ? ` (${echapper(l.gamme1)})` : ""}</span>
          <span>${l.quantiteLivree != null ? `${qte(l.quantiteLivree)} / ` : ""}${qte(reference)}${unite(l, reference)}${manque && l.motif ? ` · ${echapper(MOTIFS[l.motif] ?? l.motif)}` : ""}</span></li>`;
      }).join("")}</ul></div>` : ""}
      <div class="actions">
        ${tel ? `<a href="tel:${echapper(tel)}">📞 Appeler</a>` : `<a aria-disabled="true" style="opacity:.4">📞 Appeler</a>`}
        <a href="${echapper(lienItineraire(a))}" target="_blank" rel="noopener">🧭 Y aller</a>
        <button type="button" class="${traite ? "secondaire" : "principal"}" data-compte-rendu="${echapper(a.piece)}">${traite ? "Corriger" : "Livrer"}</button>
      </div>
    </li>`;
  }).join("") || `<li class="vide">Aucun arrêt.</li>`;
}

$("#btn-modifier-tournee").addEventListener("click", () => etat.tournee && ouvrirPreparation(etat.tournee));
$("#arrets").addEventListener("click", (e) => {
  const b = e.target.closest("[data-compte-rendu]");
  if (b) ouvrirArret(etat.tournee.arrets.find((a) => a.piece === b.dataset.compteRendu));
});

// ---------- Signature sur l'écran (réceptionnaire, responsable du dépôt) ----------
function zoneSignature(toile) {
  const ctx = toile.getContext("2d");
  let trace = null;
  const zone = {
    signee: false,
    preparer() {
      const ratio = window.devicePixelRatio || 1;
      const { width, height } = toile.getBoundingClientRect();
      toile.width = Math.round(width * ratio);
      toile.height = Math.round(height * ratio);
      ctx.setTransform(ratio, 0, 0, ratio, 0, 0);
      ctx.lineWidth = 2.2;
      ctx.lineCap = "round";
      ctx.lineJoin = "round";
      ctx.strokeStyle = "#1b2430";
      zone.signee = false;
    },
    effacer() { ctx.clearRect(0, 0, toile.width, toile.height); zone.signee = false; },
    image() { return zone.signee ? toile.toDataURL("image/png") : null; },
  };
  const point = (e) => { const r = toile.getBoundingClientRect(); return { x: e.clientX - r.left, y: e.clientY - r.top }; };
  toile.addEventListener("pointerdown", (e) => {
    e.preventDefault();
    toile.setPointerCapture(e.pointerId);
    trace = point(e);
    ctx.beginPath();
    ctx.moveTo(trace.x, trace.y);
  });
  toile.addEventListener("pointermove", (e) => {
    if (!trace) return;
    const p = point(e);
    ctx.lineTo(p.x, p.y);
    ctx.stroke();
    trace = p;
    zone.signee = true;
  });
  const fin = () => { trace = null; };
  toile.addEventListener("pointerup", fin);
  toile.addEventListener("pointercancel", fin);
  return zone;
}

const nomArticle = (l) => `${echapper(l.designation || l.article)}<small>${echapper(l.article ?? "")}${l.gamme1 ? ` · ${echapper(l.gamme1)}` : ""}${l.gamme2 ? ` ${echapper(l.gamme2)}` : ""}</small>`;
const qte = (n) => (Number(n) || 0).toLocaleString("fr-FR", { maximumFractionDigits: 3 });
/**
 * Unité de la ligne à côté d'une quantité (en unité de vente) : « Pièce », et le conditionnement saisi dans Sage
 * s'il est différent (« · 2 Carton de 12 » pour 24 pièces), recalculé pour la quantité affichée.
 */
function unite(l, n = l.quantite) {
  const u = l.unite || (l.quantiteConditionnement === l.quantite ? l.conditionnement : null);
  let texte = u ? ` <span class="unite">${echapper(u)}</span>` : "";
  const qc = Number(l.quantiteConditionnement);
  if (l.conditionnement && l.conditionnement !== u && qc > 0 && Number(l.quantite) > 0 && qc !== Number(l.quantite))
    texte += ` <span class="unite">· ${qte((Number(n) * qc) / Number(l.quantite))} ${echapper(l.conditionnement)}</span>`;
  return texte;
}

// ---------- Contrôle du chargement au dépôt ----------
const signatureChargement = zoneSignature($("#signature-chargement"));
$("#btn-effacer-signature-chargement").addEventListener("click", () => signatureChargement.effacer());

function etatChargement(t) {
  const zone = $("#tournee-chargement");
  const avecArticles = t.arrets.some((a) => a.articles.length);
  if (!avecArticles) { zone.hidden = true; return; }
  zone.hidden = false;
  const c = t.chargement;
  zone.innerHTML = c
    ? `<div>✅ <strong>Chargement contrôlé</strong> à ${heure(c.heure)}${c.responsable ? ` avec ${echapper(c.responsable)}` : ""}${c.signe ? " · signé" : ""}
        ${c.ecarts ? `<br><span class="etiquette alerte">${c.ecarts} écart(s) de quantité</span>` : ""}</div>
       <button type="button" class="secondaire" data-chargement>Revoir</button>`
    : `<div>📦 <strong>Chargement à contrôler</strong><br><span class="discret">Vérifiez les articles reçus du dépôt avant de partir.</span></div>
       <button type="button" class="principal" data-chargement>Contrôler</button>`;
}
$("#tournee-chargement").addEventListener("click", (e) => { if (e.target.closest("[data-chargement]")) ouvrirChargement(); });

function ouvrirChargement() {
  const t = etat.tournee;
  afficher("chargement", "Contrôle du chargement");
  etat.retourChargement = true;
  $("#chargement-lignes").innerHTML = t.arrets.filter((a) => a.articles.length).map((a) => `
    <section class="carte groupe-piece">
      <h3><span>${a.ordre}. ${echapper(a.intitule || a.client)}</span><span class="discret">${echapper(a.piece)}</span></h3>
      ${a.articles.map((l) => {
        const charge = l.quantiteChargee ?? l.quantite;
        const coche = l.quantiteChargee != null && l.quantiteChargee === l.quantite;
        return `<div class="article-ligne${l.quantiteChargee != null && charge !== l.quantite ? " ecart" : ""}" data-piece="${echapper(a.piece)}" data-ligne="${l.ligne}" data-commande="${l.quantite}">
          <input type="checkbox" aria-label="Reçu" ${coche ? "checked" : ""}>
          <span class="nom">${nomArticle(l)}</span>
          <span class="qte"><span>Cdé ${qte(l.quantite)}${unite(l)}</span><input type="number" inputmode="decimal" min="0" step="any" value="${charge}" aria-label="Quantité reçue"></span>
        </div>`;
      }).join("")}
    </section>`).join("");
  $("#chargement-responsable").value = t.chargement?.responsable ?? "";
  signatureChargement.preparer();
}

$("#chargement-lignes").addEventListener("change", (e) => {
  const ligne = e.target.closest(".article-ligne");
  if (!ligne) return;
  const saisie = ligne.querySelector("input[type=number]");
  const commande = Number(ligne.dataset.commande);
  if (e.target.type === "checkbox" && e.target.checked) saisie.value = commande;
  if (e.target.type === "number") ligne.querySelector("input[type=checkbox]").checked = true;
  ligne.classList.toggle("ecart", Number(saisie.value) !== commande);
});
$("#btn-tout-cocher").addEventListener("click", () => {
  for (const l of document.querySelectorAll("#chargement-lignes .article-ligne")) {
    const c = l.querySelector("input[type=checkbox]");
    if (!c.checked) { c.checked = true; l.querySelector("input[type=number]").value = l.dataset.commande; l.classList.remove("ecart"); }
  }
});

$("#btn-valider-chargement").addEventListener("click", async () => {
  const lignes = [...document.querySelectorAll("#chargement-lignes .article-ligne")];
  const nonCochees = lignes.filter((l) => !l.querySelector("input[type=checkbox]").checked);
  if (nonCochees.length) {
    nonCochees[0].scrollIntoView({ block: "center" });
    return bandeau(`${nonCochees.length} article(s) pas encore coché(s). Cochez-les, en corrigeant la quantité si besoin (0 si non reçu).`, "erreur");
  }
  const bouton = $("#btn-valider-chargement");
  bouton.disabled = true;
  try {
    const t = etat.tournee;
    await api("PUT", `/livraisons/tournees/${encodeURIComponent(t.id)}/chargement`, {
      lignes: lignes.map((l) => ({ piece: l.dataset.piece, ligne: Number(l.dataset.ligne), quantite: Number(l.querySelector("input[type=number]").value) || 0 })),
      responsable: $("#chargement-responsable").value.trim() || null,
      signature: signatureChargement.image(),
    });
    const ecarts = lignes.filter((l) => Number(l.querySelector("input[type=number]").value) !== Number(l.dataset.commande)).length;
    bandeau(ecarts ? `Chargement validé avec ${ecarts} écart(s).` : "Chargement validé : tout est conforme.", ecarts ? "" : "ok");
    afficherTournee(await api("GET", `/livraisons/tournees/${encodeURIComponent(t.id)}`));
  } catch (e) { erreur(e); } finally { bouton.disabled = false; }
});

// ---------- Compte rendu d'un arrêt : articles livrés, réceptionnaire, signature ----------
const dialogue = $("#dialogue-arret");
const formArret = $("#form-arret");
const signatureClient = zoneSignature($("#signature"));
$("#btn-effacer-signature").addEventListener("click", () => signatureClient.effacer());
const OPTIONS_MOTIF = `<option value="">Raison ?</option>` + Object.entries(MOTIFS).filter(([m]) => !["absent", "adresse-introuvable", "ferme"].includes(m))
  .map(([m, nom]) => `<option value="${m}">${nom}</option>`).join("");

function choisirStatut(statut) {
  const articles = etat.arret?.articles ?? [];
  etat.statut = statut;
  for (const b of $("#arret-statuts").children) b.classList.toggle("actif", b.dataset.statut === statut);
  // Avec des articles, « Livré » donne la liste à cocher et le partiel se déduit des quantités.
  $("#arret-statuts [data-statut=partiel]").hidden = articles.length > 0;
  $("#arret-statuts").classList.toggle("trois", !articles.length);
  $("#arret-articles").hidden = statut === "echec" || !articles.length;
  $("#champ-motif").hidden = statut === "livre";
  $("#champ-receptionnaire").hidden = statut === "echec";
  $("#zone-signature").hidden = statut === "echec";
  formArret.motif.required = statut === "echec";
  if (statut !== "echec") signatureClient.preparer();
}
$("#arret-statuts").addEventListener("click", (e) => { const b = e.target.closest("[data-statut]"); if (b) choisirStatut(b.dataset.statut); });

function dessinerArticlesLivraison(a) {
  $("#arret-articles").innerHTML = a.articles.length ? `<p class="discret">Cochez les articles remis au client. Sinon, corrigez la quantité et donnez la raison.</p>` +
    a.articles.map((l) => {
      const reference = l.quantiteChargee ?? l.quantite;
      const livree = l.quantiteLivree ?? reference;
      const manque = livree < reference;
      return `<div class="article-ligne${manque ? " ecart" : ""}" data-ligne="${l.ligne}" data-reference="${reference}">
        <input type="checkbox" aria-label="Livré" ${!manque ? "checked" : ""}>
        <span class="nom">${nomArticle(l)}</span>
        <span class="qte"><span>${l.quantiteChargee != null ? "Chargé" : "Cdé"} ${qte(reference)}${unite(l, reference)}</span><input type="number" inputmode="decimal" min="0" max="${reference}" step="any" value="${livree}" aria-label="Quantité livrée"></span>
        <select aria-label="Raison" ${manque ? "" : "hidden"}>${OPTIONS_MOTIF}</select>
      </div>`;
    }).join("") : "";
  for (const l of a.articles) {
    const select = $(`#arret-articles .article-ligne[data-ligne="${l.ligne}"] select`);
    if (select && l.motif) select.value = l.motif;
  }
}
$("#arret-articles").addEventListener("change", (e) => {
  const ligne = e.target.closest(".article-ligne");
  if (!ligne) return;
  const saisie = ligne.querySelector("input[type=number]");
  const reference = Number(ligne.dataset.reference);
  if (e.target.type === "checkbox") saisie.value = e.target.checked ? reference : 0;
  if (e.target.type === "number") ligne.querySelector("input[type=checkbox]").checked = Number(saisie.value) >= reference;
  const manque = Number(saisie.value) < reference;
  ligne.classList.toggle("ecart", manque);
  ligne.querySelector("select").hidden = !manque;
});

function ouvrirArret(a) {
  if (!a) return;
  etat.arret = a;
  formArret.reset();
  $("#arret-titre").textContent = `${a.ordre}. ${a.intitule || a.client}`;
  $("#arret-detail").textContent = `${a.piece} · ${montant(a.totalTTC)} TTC${a.netAPayer > 0 ? ` · net à payer ${montant(a.netAPayer)}` : ""}`;
  formArret.motif.value = a.motif ?? "";
  formArret.receptionnaire.value = a.receptionnaire ?? a.contact ?? "";
  formArret.commentaire.value = a.commentaire ?? "";
  $("#btn-remettre").hidden = a.statut === "a-livrer";
  dessinerArticlesLivraison(a);
  dialogue.showModal();
  choisirStatut(a.statut === "echec" ? "echec" : a.statut === "partiel" && !a.articles.length ? "partiel" : "livre");
}
$("#btn-annuler-arret").addEventListener("click", () => dialogue.close());

async function envoyerCompteRendu(corps) {
  const a = etat.arret;
  const r = await api("PUT", `/livraisons/tournees/${encodeURIComponent(a.tournee)}/arrets/${encodeURIComponent(a.piece)}`, corps);
  dialogue.close();
  afficherTournee(await api("GET", `/livraisons/tournees/${encodeURIComponent(a.tournee)}`));
  return r;
}

formArret.addEventListener("submit", async (e) => {
  e.preventDefault();
  if (!formArret.reportValidity()) return;
  const statut = etat.statut;
  const avecArticles = statut !== "echec" && etat.arret.articles.length > 0;
  const lignes = avecArticles ? [...document.querySelectorAll("#arret-articles .article-ligne")].map((l) => ({
    ligne: Number(l.dataset.ligne), quantite: Number(l.querySelector("input[type=number]").value) || 0,
    motif: l.querySelector("select").hidden ? null : l.querySelector("select").value || null,
  })) : null;
  const sansRaison = (lignes ?? []).find((l) => l.motif === null && l.quantite < Number($(`#arret-articles [data-ligne="${l.ligne}"]`).dataset.reference));
  if (sansRaison) {
    $(`#arret-articles [data-ligne="${sansRaison.ligne}"] select`).focus();
    return bandeau("Indiquez la raison pour chaque article non livré ou retourné.", "erreur");
  }
  const bouton = $("#btn-valider-arret");
  bouton.disabled = true;
  try {
    const position = await maPosition();
    const r = await envoyerCompteRendu({
      statut, motif: statut === "livre" ? null : formArret.motif.value || null,
      receptionnaire: statut === "echec" ? null : formArret.receptionnaire.value.trim() || null,
      commentaire: formArret.commentaire.value.trim() || null,
      signature: statut !== "echec" ? signatureClient.image() : null,
      lignes,
      ...(position ?? {}),
    });
    const messages = { echec: "Échec noté : la pièce pourra être remise dans une autre tournée.", partiel: "Livraison partielle enregistrée.", livre: "Livraison enregistrée." };
    bandeau(messages[r?.statut ?? statut], r?.statut === "livre" ? "ok" : "");
  } catch (err) { erreur(err); } finally { bouton.disabled = false; }
});

$("#btn-remettre").addEventListener("click", async () => {
  if (!confirm("Remettre cet arrêt « à livrer » ? Le compte rendu et la signature seront effacés.")) return;
  try {
    await envoyerCompteRendu({ statut: "a-livrer" });
    bandeau("Arrêt remis à livrer.", "ok");
  } catch (e) { erreur(e); }
});

// ---------- Autres courses de la tournée ----------
const TYPES_COURSE = { livrer: ["📦", "À livrer"], recuperer: ["↩️", "À récupérer"], autre: ["📝", "Autre"] };
const STATUTS_COURSE = { "a-faire": ["À faire", "neutre"], "en-cours": ["En cours", "alerte"], fait: ["Fait", "ok"], reporte: ["Reporté", "discret-etiquette"],
  annule: ["Annulé", "erreur"] };
const dialogueCourse = $("#dialogue-course");
const formCourse = $("#form-course");

function dessinerCourses(t) {
  $("#nb-courses").textContent = t.courses.length ? `(${t.courses.filter((k) => ["fait", "reporte", "annule"].includes(k.statut)).length}/${t.courses.length})` : "";
  $("#courses").innerHTML = t.courses.length
    ? t.courses.map((k) => `
      <li class="course" data-course="${echapper(k.id)}">
        <span class="icone-type" aria-hidden="true">${TYPES_COURSE[k.type]?.[0] ?? "📝"}</span>
        <div><strong>${echapper(k.description)}</strong>
          <div class="texte">${echapper([TYPES_COURSE[k.type]?.[1], k.client, k.adresse, k.contact].filter(Boolean).join(" · "))}${k.heure ? ` · ${heure(k.heure)}` : ""}</div>
          ${k.commentaire ? `<div class="texte">${echapper(k.commentaire)}</div>` : ""}</div>
        ${etiquette(STATUTS_COURSE[k.statut])}
      </li>`).join("")
    : `<li class="vide">Aucune autre course. Ajoutez ce qu'il faut livrer ou récupérer en route, hors commandes Sage.</li>`;
}

function choisirTypeCourse(type) {
  etat.typeCourse = type;
  for (const b of $("#course-types").children) b.classList.toggle("actif", b.dataset.type === type);
}
function choisirStatutCourse(statut) {
  etat.statutCourse = statut;
  for (const b of $("#course-statuts").children) b.classList.toggle("actif", b.dataset.statut === statut);
}
$("#course-types").addEventListener("click", (e) => { const b = e.target.closest("[data-type]"); if (b) choisirTypeCourse(b.dataset.type); });
$("#course-statuts").addEventListener("click", (e) => { const b = e.target.closest("[data-statut]"); if (b) choisirStatutCourse(b.dataset.statut); });

function ouvrirCourse(k = null) {
  etat.course = k;
  formCourse.reset();
  $("#course-titre").textContent = k ? "Course" : "Nouvelle course";
  choisirTypeCourse(k?.type ?? "livrer");
  choisirStatutCourse(k?.statut ?? "a-faire");
  for (const champ of ["description", "client", "telephone", "adresse", "contact", "commentaire"]) formCourse[champ].value = k?.[champ] ?? "";
  $("#btn-supprimer-course").hidden = !k;
  dialogueCourse.showModal();
}
$("#btn-nouvelle-course").addEventListener("click", () => ouvrirCourse());
$("#courses").addEventListener("click", (e) => {
  const li = e.target.closest("[data-course]");
  if (li) ouvrirCourse(etat.tournee.courses.find((k) => k.id === li.dataset.course));
});
$("#btn-annuler-course").addEventListener("click", () => dialogueCourse.close());

formCourse.addEventListener("submit", async (e) => {
  e.preventDefault();
  if (!formCourse.reportValidity()) return;
  const t = etat.tournee;
  const k = etat.course;
  const bouton = $("#btn-enregistrer-course");
  bouton.disabled = true;
  try {
    const termine = etat.statutCourse !== "a-faire" && etat.statutCourse !== k?.statut;
    const position = termine ? await maPosition() : null;
    const valeur = (champ) => formCourse[champ].value.trim() || null;
    await api("PUT", `/livraisons/tournees/${encodeURIComponent(t.id)}/courses/${encodeURIComponent(k?.id ?? uuid())}`, {
      type: etat.typeCourse, description: formCourse.description.value.trim(), client: valeur("client"), adresse: valeur("adresse"),
      contact: valeur("contact"), telephone: valeur("telephone"), statut: etat.statutCourse, commentaire: valeur("commentaire"), ...(position ?? {}),
    });
    dialogueCourse.close();
    bandeau("Course enregistrée.", "ok");
    afficherTournee(await api("GET", `/livraisons/tournees/${encodeURIComponent(t.id)}`));
  } catch (err) { erreur(err); } finally { bouton.disabled = false; }
});

$("#btn-supprimer-course").addEventListener("click", async () => {
  const k = etat.course;
  if (!k || !confirm("Supprimer cette course ?")) return;
  try {
    await api("DELETE", `/livraisons/tournees/${encodeURIComponent(etat.tournee.id)}/courses/${encodeURIComponent(k.id)}`);
    dialogueCourse.close();
    afficherTournee(await api("GET", `/livraisons/tournees/${encodeURIComponent(etat.tournee.id)}`));
  } catch (err) { erreur(err); }
});

demarrer();
