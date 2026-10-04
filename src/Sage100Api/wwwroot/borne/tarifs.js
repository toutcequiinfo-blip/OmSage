// Prix de vente comme Sage, calculés sur la borne (aussi hors ligne) à partir du catalogue.
// Mêmes règles que l'API (Lectures/Tarifs.cs, Tarification.Calculer), qui recalcule les prix envoyés à Sage :
//  1. prix : tarif propre au client, sinon celui de sa catégorie tarifaire, sinon prix de la fiche article ;
//  2. une valeur de gamme ou un conditionnement peuvent avoir leur propre prix (client, sinon catégorie) ;
//     sans prix propre, un « Carton de 12 » vaut 12 fois l'unité ;
//  3. remises : remise générale du tarif, ou tranches par quantité / montant, ou prix net par quantité.

const index = new WeakMap();

/** Tarifs du catalogue regroupés par article (calculé une fois par catalogue). */
function tarifsDe(catalogue, reference) {
  let parArticle = index.get(catalogue);
  if (!parArticle) {
    parArticle = new Map();
    const t = catalogue.tarifs || {};
    const ajouter = (liste, cle) => {
      for (const x of liste || []) {
        const ref = (x.article || "").toUpperCase();
        if (!parArticle.has(ref)) parArticle.set(ref, { articles: [], gammes: [], conditionnements: [], tarifsConditionnement: [], quantites: [] });
        parArticle.get(ref)[cle].push(x);
      }
    };
    ajouter(t.articles, "articles");
    ajouter(t.gammes, "gammes");
    ajouter(t.conditionnements, "conditionnements");
    ajouter(t.tarifsConditionnement, "tarifsConditionnement");
    ajouter(t.quantites, "quantites");
    index.set(catalogue, parArticle);
  }
  return parArticle.get((reference || "").toUpperCase()) || { articles: [], gammes: [], conditionnements: [], tarifsConditionnement: [], quantites: [] };
}

const egal = (a, b) => (a || "").trim().toUpperCase() === (b || "").trim().toUpperCase();
const arrondi4 = (v) => Math.round(v * 10000) / 10000;

/** Conditionnements de l'article (Carton de 12...), du plus petit au plus grand. */
export const conditionnementsDe = (catalogue, article) =>
  tarifsDe(catalogue, article.reference).conditionnements.slice().sort((a, b) => a.quantite - b.quantite);

/** Catégorie tarifaire du client (intitulé), ou null. */
export function categorieDe(catalogue, client) {
  const k = client?.categorieTarif;
  return (catalogue.tarifs?.categories || []).find((c) => c.numero === k) || null;
}

/**
 * Prix d'une ligne. quantite : nombre d'unités vendues (de conditionnements le cas échéant).
 * Retour : { prix (unité vendue, avant remise), prixUnitaire (par unité de vente), ttc, remises, prixNet, origine }.
 */
export function prixLigne(catalogue, client, article, { enumere = null, conditionnement = null, quantite = 1 } = {}) {
  const t = tarifsDe(catalogue, article.reference);
  const numero = client?.numero || "";
  const categorie = client?.categorieTarif ?? 1;
  const vise = (x, pourClient) => (pourClient ? !!x.client && egal(x.client, numero) : !x.client && x.categorie === categorie);

  const ligneClient = t.articles.find((x) => vise(x, true));
  const ligneCategorie = t.articles.find((x) => vise(x, false));
  let prix, ttc, origine;
  if (ligneClient && ligneClient.prix > 0) [prix, ttc, origine] = [ligneClient.prix, !!ligneClient.prixTTC, "client"];
  else if (ligneCategorie && ligneCategorie.prix > 0) [prix, ttc, origine] = [ligneCategorie.prix, !!ligneCategorie.prixTTC, "categorie"];
  else [prix, ttc, origine] = [article.prixVenteHT || 0, !!article.prixTTC, "article"];
  const regle = ligneClient || ligneCategorie;
  const pourClient = !!ligneClient;

  if (enumere?.gamme1) {
    const meme = (g) => egal(g.gamme1, enumere.gamme1) && egal(g.gamme2 || "", enumere.gamme2 || "");
    const g = t.gammes.find((x) => meme(x) && vise(x, true)) || t.gammes.find((x) => meme(x) && vise(x, false));
    if (g) [prix, origine] = [g.prix, g.client ? "client" : "categorie"];
  }

  const contenu = conditionnement?.quantite > 0 ? conditionnement.quantite : 1;
  if (conditionnement) {
    const meme = (c) => c.conditionnement === conditionnement.numero;
    const c = t.tarifsConditionnement.find((x) => meme(x) && vise(x, true)) || t.tarifsConditionnement.find((x) => meme(x) && vise(x, false));
    if (c) [prix, origine] = [c.prix, c.client ? "client" : "categorie"];
    else prix *= contenu;
  }

  let remises = [];
  if (regle) {
    if (regle.qteMont >= 1 && regle.qteMont <= 3) {
      const tranches = t.quantites.filter((x) => vise(x, pourClient)).sort((a, b) => a.borneSup - b.borneSup);
      const valeur = regle.qteMont === 2 ? prix * quantite : quantite * contenu;
      const tranche = tranches.find((x) => valeur <= x.borneSup) || tranches[tranches.length - 1];
      if (tranche) {
        if (regle.qteMont === 3) { if (tranche.prixNet > 0) prix = tranche.prixNet * contenu; }
        else remises = tranche.remises || [];
      }
    } else if (!regle.horsRemise && regle.remise) {
      remises = [{ type: 1, valeur: regle.remise }];
    }
  }
  return { prix: arrondi4(prix), prixUnitaire: arrondi4(prix / contenu), ttc, remises, prixNet: arrondi4(net(prix, remises, contenu)), origine };
}

/** Remises en cascade : un pourcentage sur le prix déjà remisé, un montant par unité de vente. */
export function net(prix, remises, contenu = 1) {
  let v = prix;
  for (const r of remises || []) {
    if (r.type === 1) v *= 1 - r.valeur / 100;
    else if (r.type === 0) v -= r.valeur * contenu;
  }
  return Math.max(0, v);
}

/** Texte court des remises : « -5 % », « -5 % -2 % », « -1,50 / u ». */
export function texteRemises(remises) {
  return (remises || []).filter((r) => r.type === 0 || r.type === 1).map((r) =>
    r.type === 1 ? `-${String(r.valeur).replace(".", ",")} %` : `-${r.valeur.toFixed(2).replace(".", ",")} / u`).join(" ");
}

/** Montant HT d'un prix (un tarif TTC est ramené en HT avec le taux de TVA des réglages de la borne). */
export const enHT = (montant, ttc, tauxTva) => (ttc ? montant / (1 + (tauxTva || 0) / 100) : montant);
