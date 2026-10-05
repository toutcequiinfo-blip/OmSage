// Échanges avec l'API Sage 100 et envoi de la file d'attente.
// Chaque opération porte un idExterne : la renvoyer après une coupure ne crée jamais de doublon dans Sage.

import { lireReglages, lireFile, majOperation, ecrireCatalogue, jetonUtilisateur, lireSession } from "./stockage.js";

// La santé doit répondre vite ; le catalogue et les écritures peuvent attendre Sage (le worker a 60 s).
const DELAI_SANTE_MS = 10000;
const DELAI_MS = 90000;

export class ErreurReseau extends Error {}

/** jeton : connexion de l'utilisateur Sage (POST /connexion), exigée pour les commandes et encaissements. */
export async function appeler(methode, chemin, corps, delaiMs = DELAI_MS, jeton = null) {
  const { cle } = lireReglages();
  const controle = new AbortController();
  const minuteur = setTimeout(() => controle.abort(), delaiMs);
  try {
    const r = await fetch(`/api/v1${chemin}`, {
      method: methode,
      headers: {
        "X-Api-Key": cle,
        ...(jeton ? { Authorization: `Bearer ${jeton}` } : {}),
        ...(corps ? { "Content-Type": "application/json" } : {}),
      },
      body: corps ? JSON.stringify(corps) : undefined,
      signal: controle.signal,
      cache: "no-store",
    });
    let donnees = null;
    try { donnees = await r.json(); } catch { /* corps vide */ }
    return { statut: r.status, donnees };
  } catch (e) {
    throw new ErreurReseau(e.name === "AbortError" ? "Le serveur ne répond pas." : "Serveur injoignable.");
  } finally {
    clearTimeout(minuteur);
  }
}

/** "sage" : tout fonctionne ; "serveur" : API joignable mais Sage indisponible ; "hors-ligne". */
export async function etatConnexion() {
  try {
    const { statut, donnees } = await appeler("GET", "/sante", null, DELAI_SANTE_MS);
    if (statut !== 200) return "hors-ligne";
    return donnees?.worker && !donnees.worker.erreur ? "sage" : "serveur";
  } catch {
    return "hors-ligne";
  }
}

export async function rechargerCatalogue() {
  const { statut, donnees } = await appeler("GET", "/catalogue");
  if (statut === 401) throw new Error("Clé d'API refusée. Vérifiez les réglages.");
  if (statut !== 200) throw new Error(`Catalogue indisponible (code ${statut}).`);
  await ecrireCatalogue(donnees);
  return donnees;
}

/** Bons de commande Sage avec un reste à encaisser ; lève une erreur si le serveur ne répond pas. */
export async function commandesOuvertes(recherche) {
  const q = recherche ? `?recherche=${encodeURIComponent(recherche)}` : "";
  const { statut, donnees } = await appeler("GET", `/commandes-ouvertes${q}`, null, DELAI_SANTE_MS);
  if (statut !== 200) throw new Error(`Commandes indisponibles (code ${statut}).`);
  return donnees;
}

let enCours = null;

/**
 * Envoie les opérations en attente, dans l'ordre où elles ont été saisies.
 * S'arrête dès que le serveur ne répond plus ; les opérations restent en file pour le prochain passage.
 * Retourne { envoyees, restantes, cleRefusee, reconnexions: [logins dont la connexion a expiré] }.
 */
export function synchroniser() {
  enCours ??= envoyerFile().finally(() => (enCours = null));
  return enCours;
}

/**
 * Envoie tout de suite une seule opération (la pièce que le caissier va encaisser), sans attendre le reste de la file.
 * Un envoi déjà en cours se termine d'abord : la même opération ne part jamais deux fois en même temps.
 */
export async function envoyerMaintenant(cle) {
  while (enCours) await enCours.catch(() => {});
  enCours = envoyerFile(cle).finally(() => (enCours = null));
  return enCours;
}

/** Attend la fin d'un envoi en cours : une opération lue ensuite dans la file ne partira plus pendant qu'on la modifie. */
export async function attendreFinEnvoi() {
  while (enCours) await enCours.catch(() => {});
}

async function envoyerFile(seulement = null) {
  const bilan = { envoyees: 0, restantes: 0, cleRefusee: false, reconnexions: [] };
  const ops = await lireFile();
  const commandesOk = new Set(ops.filter((o) => o.type === "commande" && o.statut === "ok").map((o) => o.idExterne));
  let arret = false;

  for (const op of ops) {
    if (op.statut !== "attente" || (seulement && op.cle !== seulement)) continue;
    // Un encaissement attend que sa commande soit dans Sage (sauf s'il vise directement une pièce Sage).
    if (arret || (op.type === "encaissement" && !op.piece && !commandesOk.has(op.idCommande))) {
      bilan.restantes++;
      continue;
    }
    const chemin = op.type === "commande" ? "/commandes"
      : op.piece ? `/commandes/piece/${encodeURIComponent(op.piece)}/encaissements`
      : `/commandes/${encodeURIComponent(op.idCommande)}/encaissements`;
    op.essais++;
    try {
      // Le jeton le plus récent de l'utilisateur, au cas où celui de la vente a expiré pendant une coupure.
      // Une vente saisie avant l'arrivée de la connexion part avec l'utilisateur connecté.
      const jeton = op.utilisateur
        ? jetonUtilisateur(op.utilisateur) || op.jeton || null
        : (lireSession() && jetonUtilisateur(lireSession())) || null;
      const { statut, donnees } = await appeler("POST", chemin, op.corps, DELAI_MS, jeton);
      if (statut === 200 || statut === 201) {
        op.statut = "ok";
        op.resultat = donnees;
        op.message = null;
        if (op.type === "commande") commandesOk.add(op.idExterne);
        bilan.envoyees++;
      } else if (statut === 422) {
        // Refus de Sage (stock, client bloqué...) : il faut une décision humaine.
        op.statut = "erreur";
        op.message = texteErreur(donnees) || "Refusé par Sage.";
      } else if (statut === 403) {
        // Droit refusé (utilisateur non caissier) : à régler dans Sage ou par un caissier.
        op.statut = "erreur";
        op.message = texteErreur(donnees) || "Droit refusé.";
      } else if (statut === 401 && donnees?.code === "CONNEXION_REQUISE") {
        // Connexion absente ou expirée : la vente attend que son utilisateur se reconnecte.
        op.message = `Reconnexion de ${op.utilisateur || "l'utilisateur"} nécessaire pour l'envoyer.`;
        if (op.utilisateur && !bilan.reconnexions.includes(op.utilisateur)) bilan.reconnexions.push(op.utilisateur);
        bilan.restantes++;
      } else if (statut === 401) {
        op.message = "Clé d'API refusée.";
        bilan.cleRefusee = true;
        arret = true;
        bilan.restantes++;
      } else {
        // 409 (déjà en cours), 404 (commande pas encore connue), 503 (Sage indisponible) : on réessaiera.
        op.message = texteErreur(donnees) || `Réponse ${statut}, nouvel essai plus tard.`;
        if (statut >= 500) arret = true;
        bilan.restantes++;
      }
    } catch (e) {
      op.message = e.message;
      arret = true;
      bilan.restantes++;
    }
    await majOperation(op);
  }
  return bilan;
}

function texteErreur(d) {
  if (!d) return null;
  if (Array.isArray(d.erreurs)) return d.erreurs.join(" ");
  return d.message || d.erreur || null;
}

/** Bon de commande Sage et ses lignes (loupe des commandes à encaisser) ; null s'il n'existe plus dans Sage. */
export async function detailCommande(piece) {
  const { statut, donnees } = await appeler("GET", `/commandes-ouvertes/${encodeURIComponent(piece)}`, null, DELAI_SANTE_MS);
  if (statut === 404) return null;
  if (statut !== 200) throw new Error(`Détail indisponible (code ${statut}).`);
  return donnees;
}
