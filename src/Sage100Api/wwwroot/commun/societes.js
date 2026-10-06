// Sociétés (bases Sage) servies par l'API, pour l'écran de connexion des applications (CRM, livraison, tableaux de bord).
// Le choix n'apparaît que si le serveur en sert plusieurs. La société est ensuite portée par le jeton de connexion :
// pour en changer, on se déconnecte et on se reconnecte.

const CLE_LISTE = "societes.liste";
const CLE_DERNIERE = "societes.derniere";

function lire(cle, defaut) {
  try { const v = localStorage.getItem(cle); return v == null ? defaut : JSON.parse(v); } catch { return defaut; }
}
function ecrire(cle, valeur) {
  try { localStorage.setItem(cle, JSON.stringify(valeur)); } catch { /* ignoré */ }
}

/** [{ code, intitule, principal }] ; hors ligne, la dernière liste reçue. */
export async function listerSocietes(cleApi) {
  try {
    const r = await fetch("/api/v1/dossiers", { headers: { "X-Api-Key": cleApi }, cache: "no-store" });
    if (r.ok) {
      const liste = await r.json();
      if (Array.isArray(liste)) ecrire(CLE_LISTE, liste);
    }
  } catch { /* hors ligne */ }
  return lire(CLE_LISTE, []);
}

/** Remplit la liste de choix (cachée s'il n'y a qu'une société) et propose la dernière société choisie sur cet appareil. */
export async function preparerChoix(conteneur, select, cleApi) {
  const liste = await listerSocietes(cleApi);
  conteneur.hidden = liste.length < 2;
  const choisi = select.value || lire(CLE_DERNIERE, "") || liste[0]?.code || "";
  select.replaceChildren(...liste.map((d) => {
    const o = document.createElement("option");
    o.value = d.code;
    o.textContent = d.intitule || d.code;
    return o;
  }));
  if (liste.some((d) => d.code === choisi)) select.value = choisi;
  return liste;
}

/** Société choisie à envoyer à la connexion (null s'il n'y en a qu'une) ; elle est retenue pour la prochaine fois. */
export function societeChoisie(select) {
  const liste = lire(CLE_LISTE, []);
  if (liste.length < 2 || !select.value) return null;
  ecrire(CLE_DERNIERE, select.value);
  return select.value;
}

/** Nom affichable d'une société, ou null s'il n'y en a qu'une (rien à afficher). */
export function nomSociete(code) {
  const liste = lire(CLE_LISTE, []);
  if (liste.length < 2 || !code) return null;
  return liste.find((d) => d.code.toUpperCase() === String(code).toUpperCase())?.intitule || code;
}
