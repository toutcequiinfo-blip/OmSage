// Échanges avec l'API Sage 100 et envoi de la file d'attente.
// Chaque opération porte un idExterne : la renvoyer après une coupure ne crée jamais de doublon dans Sage.

import { lireReglages, lireFile, majOperation, ecrireCatalogue } from "./stockage.js";

const DELAI_MS = 15000;

export class ErreurReseau extends Error {}

async function appeler(methode, chemin, corps) {
  const { cle } = lireReglages();
  const controle = new AbortController();
  const minuteur = setTimeout(() => controle.abort(), DELAI_MS);
  try {
    const r = await fetch(`/api/v1${chemin}`, {
      method: methode,
      headers: { "X-Api-Key": cle, ...(corps ? { "Content-Type": "application/json" } : {}) },
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
    const { statut, donnees } = await appeler("GET", "/sante");
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

let enCours = null;

/**
 * Envoie les opérations en attente, dans l'ordre où elles ont été saisies.
 * S'arrête dès que le serveur ne répond plus ; les opérations restent en file pour le prochain passage.
 * Retourne { envoyees, restantes, cleRefusee }.
 */
export function synchroniser() {
  enCours ??= envoyerFile().finally(() => (enCours = null));
  return enCours;
}

async function envoyerFile() {
  const bilan = { envoyees: 0, restantes: 0, cleRefusee: false };
  const ops = await lireFile();
  const commandesOk = new Set(ops.filter((o) => o.type === "commande" && o.statut === "ok").map((o) => o.idExterne));
  let arret = false;

  for (const op of ops) {
    if (op.statut !== "attente") continue;
    // Un encaissement attend que sa commande soit dans Sage.
    if (arret || (op.type === "encaissement" && !commandesOk.has(op.idCommande))) {
      bilan.restantes++;
      continue;
    }
    const chemin = op.type === "commande" ? "/commandes" : `/commandes/${encodeURIComponent(op.idCommande)}/encaissements`;
    op.essais++;
    try {
      const { statut, donnees } = await appeler("POST", chemin, op.corps);
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
