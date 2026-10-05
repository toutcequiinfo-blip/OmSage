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

// ---------- Utilisateurs Sage connectés sur cette borne ----------
// Pour chaque login : profil et dernier jeton de l'API, plus une empreinte salée (PBKDF2) du mot de passe
// qui permet de se reconnecter hors ligne. Le mot de passe lui-même n'est jamais gardé.

const CLE_UTILISATEURS = "borne.utilisateurs";
const CLE_SESSION = "borne.session";
const cleLogin = (login) => (login || "").trim().toUpperCase();

export function lireUtilisateurs() {
  try {
    return JSON.parse(localStorage.getItem(CLE_UTILISATEURS) || "{}");
  } catch {
    return {};
  }
}

export const utilisateurMemorise = (login) => lireUtilisateurs()[cleLogin(login)] || null;

export function memoriserUtilisateur(u) {
  localStorage.setItem(CLE_UTILISATEURS, JSON.stringify({ ...lireUtilisateurs(), [cleLogin(u.profil.utilisateur)]: u }));
}

/** Jeton le plus récent de cet utilisateur : les ventes faites hors ligne partent avec lui après une reconnexion. */
export const jetonUtilisateur = (login) => utilisateurMemorise(login)?.profil.jeton || null;

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

export const lireCatalogue = () => transaction("catalogue", "readonly", (s) => s.get("courant"));
export const ecrireCatalogue = (c) => transaction("catalogue", "readwrite", (s) => s.put(c, "courant"));

/**
 * Opération de la file : { cle, type: "commande" | "encaissement", idExterne, idCommande?, corps,
 * statut: "attente" | "erreur" | "ok", essais, message, resultat, creeLe, vente?, utilisateur?, jeton? }.
 */
export const ajouterOperation = (op) =>
  transaction("file", "readwrite", (s) => s.add({ statut: "attente", essais: 0, creeLe: Date.now(), ...op }));
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

const CLE_ATTENTE = "borne.attente";

export function lireAttente() {
  try {
    return JSON.parse(localStorage.getItem(CLE_ATTENTE) || "[]");
  } catch {
    return [];
  }
}

export const ecrireAttente = (tickets) => localStorage.setItem(CLE_ATTENTE, JSON.stringify(tickets));

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
