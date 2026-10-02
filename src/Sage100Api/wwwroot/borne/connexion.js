// Connexion des utilisateurs avec leur login Sage.
// En ligne, c'est Sage qui vérifie le mot de passe (via l'API) ; hors ligne, la borne reconnaît les utilisateurs
// qui se sont déjà connectés sur elle, grâce à une empreinte salée de leur mot de passe.

import { appeler, ErreurReseau } from "./synchro.js";
import { memoriserUtilisateur, utilisateurMemorise } from "./stockage.js";

const ITERATIONS = 150000;

export class ErreurConnexion extends Error {}

/**
 * Retourne le profil : { utilisateur, jeton, expiration, administrateur, collaborateur, vendeur, caissier, peutEncaisser, horsLigne }.
 * Lève ErreurConnexion si le login est refusé ou impossible à vérifier.
 */
export async function seConnecter(login, motDePasse) {
  login = login.trim();
  let reponse;
  try {
    reponse = await appeler("POST", "/connexion", { utilisateur: login, motDePasse });
  } catch (e) {
    if (e instanceof ErreurReseau) return connexionHorsLigne(login, motDePasse);
    throw e;
  }
  const { statut, donnees } = reponse;
  if (statut === 200) {
    const profil = { ...donnees, horsLigne: false };
    await memoriser(profil, motDePasse);
    return profil;
  }
  if (donnees?.code === "ACCES_REFUSE" || donnees?.code === "TROP_D_ESSAIS") throw new ErreurConnexion(donnees.message);
  if (statut === 401) throw new ErreurConnexion("Clé d'API refusée. Vérifiez les réglages.");
  // Serveur joignable mais Sage indisponible : même règle que sans réseau.
  return connexionHorsLigne(login, motDePasse);
}

async function connexionHorsLigne(login, motDePasse) {
  const m = utilisateurMemorise(login);
  if (!m) throw new ErreurConnexion(`Sage est injoignable et ${login} ne s'est jamais connecté sur cette borne : connexion impossible hors ligne.`);
  if (!crypto.subtle) throw new ErreurConnexion("Connexion hors ligne impossible : la borne doit être ouverte en HTTPS.");
  if ((await empreinte(motDePasse, m.sel)) !== m.empreinte) throw new ErreurConnexion("Mot de passe incorrect.");
  return { ...m.profil, horsLigne: true };
}

async function memoriser(profil, motDePasse) {
  // Sans HTTPS, pas de chiffrement dans le navigateur : la connexion marche, mais pas hors ligne.
  if (!crypto.subtle) return;
  const sel = hex(crypto.getRandomValues(new Uint8Array(16)));
  memoriserUtilisateur({ sel, empreinte: await empreinte(motDePasse, sel), profil });
}

async function empreinte(motDePasse, sel) {
  const cle = await crypto.subtle.importKey("raw", new TextEncoder().encode(`borne:${motDePasse}`), "PBKDF2", false, ["deriveBits"]);
  const bits = await crypto.subtle.deriveBits(
    { name: "PBKDF2", hash: "SHA-256", salt: new TextEncoder().encode(sel), iterations: ITERATIONS }, cle, 256);
  return hex(new Uint8Array(bits));
}

const hex = (octets) => [...octets].map((o) => o.toString(16).padStart(2, "0")).join("");
