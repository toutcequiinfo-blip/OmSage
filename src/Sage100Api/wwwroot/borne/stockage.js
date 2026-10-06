// Stockage local de la borne : réglages (localStorage), catalogue et file d'envoi (IndexedDB).
// Tout ce qui est vendu est d'abord écrit ici, pour que la borne continue sans serveur.

const CLE_REGLAGES = "borne.reglages";
const REGLAGES_DEFAUT = { borne: "BORNE1", cle: "", clientDefaut: "", prochainNumero: 1, demanderQuantite: true, pave: false };

export function lireReglages() {
  try {
    return { ...REGLAGES_DEFAUT, ...JSON.parse(localStorage.getItem(CLE_REGLAGES) || "{}") };
  } catch {
    return { ...REGLAGES_DEFAUT };
  }
}

export function ecrireReglages(r) {
  localStorage.setItem(CLE_REGLAGES, JSON.stringify(r));
}

/** Numéro de vente local, lisible dans Sage (DO_Ref, 17 caractères max) : BORNE1-000042. */
export function prochainNumeroVente() {
  const r = lireReglages();
  const n = r.prochainNumero || 1;
  ecrireReglages({ ...r, prochainNumero: n + 1 });
  return `${r.borne}-${String(n).padStart(6, "0")}`;
}

/** Identifiant unique de la vente (DO_RefExterne, 51 max) : BORNE1-20261001-093512-K7Q2. */
export function nouvelIdVente(borne) {
  const d = new Date();
  const p = (v) => String(v).padStart(2, "0");
  const horodatage = `${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}-${p(d.getHours())}${p(d.getMinutes())}${p(d.getSeconds())}`;
  const alea = Math.random().toString(36).slice(2, 6).toUpperCase().padEnd(4, "0");
  return `${borne}-${horodatage}-${alea}`;
}

// ---------- Société (base Sage) de la borne ----------
// Une installation peut servir plusieurs sociétés. La liste reçue du serveur est gardée pour se connecter hors ligne.
// Société en cours : "" quand le serveur n'en sert qu'une (tout se passe alors comme avant le multi-société).
// On n'en change qu'en se reconnectant ; ventes, catalogue et tickets en attente restent rattachés à leur société.

const CLE_DOSSIERS = "borne.dossiers";
const CLE_DOSSIER = "borne.dossier";

/** [{ code, intitule, principal }] : sociétés servies par le serveur (dernière liste reçue). */
export function lireDossiers() {
  try {
    return JSON.parse(localStorage.getItem(CLE_DOSSIERS) || "[]");
  } catch {
    return [];
  }
}

export const ecrireDossiers = (liste) => localStorage.setItem(CLE_DOSSIERS, JSON.stringify(liste || []));
export const plusieursSocietes = () => lireDossiers().length > 1;
export const lireDossier = () => (plusieursSocietes() && localStorage.getItem(CLE_DOSSIER)) || "";
export function ecrireDossier(code) {
  if (code) localStorage.setItem(CLE_DOSSIER, code);
  else localStorage.removeItem(CLE_DOSSIER);
}
export const intituleDossier = (code = lireDossier()) => lireDossiers().find((d) => d.code === code)?.intitule || code;

// ---------- Utilisateurs Sage connectés sur cette borne ----------
// Pour chaque login : profil et dernier jeton de l'API, plus une empreinte salée (PBKDF2) du mot de passe
// qui permet de se reconnecter hors ligne. Le mot de passe lui-même n'est jamais gardé.

const CLE_UTILISATEURS = "borne.utilisateurs";
const CLE_SESSION = "borne.session";
// Un même login peut exister dans plusieurs sociétés : il est rangé par société.
const cleLogin = (login, dossier) => (dossier ? `${dossier.toUpperCase()}|` : "") + (login || "").trim().toUpperCase();

export function lireUtilisateurs() {
  try {
    return JSON.parse(localStorage.getItem(CLE_UTILISATEURS) || "{}");
  } catch {
    return {};
  }
}

export const utilisateurMemorise = (login, dossier = lireDossier()) => lireUtilisateurs()[cleLogin(login, dossier)] || null;

/** Logins déjà connectés sur cette borne dans cette société. */
export const loginsConnus = (dossier = lireDossier()) =>
  Object.values(lireUtilisateurs()).filter((u) => (u.dossier || "") === dossier).map((u) => u.profil.utilisateur).sort();

export function memoriserUtilisateur(u, dossier = lireDossier()) {
  localStorage.setItem(CLE_UTILISATEURS, JSON.stringify({ ...lireUtilisateurs(), [cleLogin(u.profil.utilisateur, dossier)]: { ...u, dossier } }));
}

/** Jeton le plus récent de cet utilisateur : les ventes faites hors ligne partent avec lui après une reconnexion. */
export const jetonUtilisateur = (login, dossier = lireDossier()) => utilisateurMemorise(login, dossier)?.profil.jeton || null;

/** Login de l'utilisateur connecté sur la borne (gardé si la tablette recharge la page), ou null. */
export const lireSession = () => localStorage.getItem(CLE_SESSION);
export function ecrireSession(login) {
  if (login) localStorage.setItem(CLE_SESSION, login);
  else localStorage.removeItem(CLE_SESSION);
}

// ---------- IndexedDB ----------

let base;

function ouvrir() {
  if (base) return base;
  base = new Promise((ok, ko) => {
    const req = indexedDB.open("borne-sage100", 1);
    req.onupgradeneeded = () => {
      const db = req.result;
      db.createObjectStore("catalogue");
      const file = db.createObjectStore("file", { keyPath: "cle" });
      file.createIndex("creeLe", "creeLe");
    };
    req.onsuccess = () => ok(req.result);
    req.onerror = () => ko(req.error);
  });
  return base;
}

async function transaction(magasin, mode, action) {
  const db = await ouvrir();
  return new Promise((ok, ko) => {
    const tx = db.transaction(magasin, mode);
    const resultat = action(tx.objectStore(magasin));
    tx.oncomplete = () => ok(resultat && "result" in resultat ? resultat.result : undefined);
    tx.onerror = () => ko(tx.error);
    tx.onabort = () => ko(tx.error);
  });
}

// Un catalogue par société.
const cleCatalogue = (dossier) => (dossier ? `courant:${dossier.toUpperCase()}` : "courant");
export const lireCatalogue = (dossier = lireDossier()) => transaction("catalogue", "readonly", (s) => s.get(cleCatalogue(dossier)));
export const ecrireCatalogue = (c, dossier = lireDossier()) => transaction("catalogue", "readwrite", (s) => s.put(c, cleCatalogue(dossier)));

/**
 * Opération de la file : { cle, type: "commande" | "encaissement", idExterne, idCommande?, corps,
 * statut: "attente" | "erreur" | "ok", essais, message, resultat, creeLe, vente?, utilisateur?, jeton?, dossier }.
 * dossier : société de la vente ; l'opération part toujours vers elle, avec la connexion de son utilisateur dans cette société.
 */
export const ajouterOperation = (op) =>
  transaction("file", "readwrite", (s) => s.add({ statut: "attente", essais: 0, creeLe: Date.now(), dossier: lireDossier(), ...op }));
export const majOperation = (op) => transaction("file", "readwrite", (s) => s.put(op));
export const supprimerOperation = (cle) => transaction("file", "readwrite", (s) => s.delete(cle));

export async function lireFile() {
  const ops = await transaction("file", "readonly", (s) => s.getAll());
  return ops.sort((a, b) => a.creeLe - b.creeLe);
}

/** Supprime les opérations envoyées depuis plus de 7 jours. */
export async function purgerFile() {
  const limite = Date.now() - 7 * 24 * 3600 * 1000;
  for (const op of await lireFile()) {
    if (op.statut === "ok" && op.creeLe < limite) await supprimerOperation(op.cle);
  }
}

// ---------- Tickets en attente ----------
// Ticket mis de côté (client qui revient plus tard) : seulement sur cette borne, rien n'est encore envoyé à Sage.

// Ceux de la société en cours seulement.
const cleAttente = () => (lireDossier() ? `borne.attente.${lireDossier().toUpperCase()}` : "borne.attente");

export function lireAttente() {
  try {
    return JSON.parse(localStorage.getItem(cleAttente()) || "[]");
  } catch {
    return [];
  }
}

export const ecrireAttente = (tickets) => localStorage.setItem(cleAttente(), JSON.stringify(tickets));

// ---------- Affichage en liste ou en boutons (articles, clients, commandes), propre à cette tablette ----------

const CLE_AFFICHAGE = "borne.affichage";
const AFFICHAGE_DEFAUT = { articles: "boutons", clients: "boutons", commandes: "liste" };

export function lireAffichage(liste) {
  try {
    return { ...AFFICHAGE_DEFAUT, ...JSON.parse(localStorage.getItem(CLE_AFFICHAGE) || "{}") }[liste];
  } catch {
    return AFFICHAGE_DEFAUT[liste];
  }
}

export function ecrireAffichage(liste, mode) {
  try {
    localStorage.setItem(CLE_AFFICHAGE, JSON.stringify({ ...JSON.parse(localStorage.getItem(CLE_AFFICHAGE) || "{}"), [liste]: mode }));
  } catch { /* préférence non gardée, l'affichage change quand même */ }
}
