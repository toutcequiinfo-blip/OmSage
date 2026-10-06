// Borne de prise de commande et d'encaissement (Sage 100).
// Parcours : connexion (login Sage) -> paramètres de saisie (pièce, souche, dépôt) -> client -> caisse (ticket, articles,
// pavé numérique) -> encaissement -> ticket suivant, vide, pour le même client.
// Sur téléphone, la caisse passe en deux onglets : Articles et Ticket.
// Fonctionne hors ligne grâce à la file d'envoi.

import {
  lireReglages, ecrireReglages, prochainNumeroVente, nouvelIdVente,
  lireCatalogue, ajouterOperation, majOperation, supprimerOperation, lireFile, purgerFile,
  lireSession, ecrireSession, utilisateurMemorise, loginsConnus, lireAttente, ecrireAttente,
  lireAffichage, ecrireAffichage, lireDossiers, lireDossier, ecrireDossier, plusieursSocietes, intituleDossier,
} from "./stockage.js";
import { etatConnexion, rechargerCatalogue, chargerDossiers, synchroniser, envoyerMaintenant, attendreFinEnvoi, appeler, commandesOuvertes, detailCommande } from "./synchro.js";
import { seConnecter } from "./connexion.js";
import { prixLigne, conditionnementsDe, categorieDe, texteRemises, enHT } from "./tarifs.js";

const $ = (s) => document.querySelector(s);
const euros = new Intl.NumberFormat("fr-FR", { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const arrondi = (v) => Math.round(v * 100) / 100;
const MODES_ESPECES = /esp[eè]ce/i;

let catalogue = null;
let connexion = "hors-ligne";
// { id, numero, client, lignes: Map(cle -> {article, enumere, cond, quantite}), paiements: [], validee, typeDocument }
// cond : conditionnement vendu (Carton de 12...), la quantité est alors un nombre de cartons.
// Commande déjà enregistrée : existante, piece (Sage) ou idCommande (encore dans la file), totalTTC, dejaRegle.
let vente = null;
let famille = null;
let modeChoisi = null;
let utilisateur = null; // profil renvoyé par la connexion (voir connexion.js)
let ligneChoisie = null; // clé de la ligne du ticket sur laquelle agit le pavé numérique
let saisie = ""; // chiffres tapés sur le pavé
let changementClient = false; // l'écran client change le client du ticket en cours au lieu d'en ouvrir un nouveau
let dernierTicket = null; // ticket de la dernière vente terminée, imprimable tant que le ticket suivant est vide
let dernierClient = null; // client du dernier ticket : le ticket suivant le reprend, jusqu'à ce qu'on change de client

// ---------- Écrans ----------

function afficher(ecran) {
  for (const e of document.querySelectorAll(".ecran")) e.hidden = e.id !== `ecran-${ecran}`;
  // Sur la caisse, le client est affiché en tête du ticket.
  $("#client-actuel").textContent = vente?.client && ecran !== "vente" ? `${vente.client.numero} · ${vente.client.intitule}` : "";
  if (ecran !== "client") changementClient = false;
  if (ecran === "client") { $("#btn-retour-ticket").hidden = !changementClient; $("#recherche-client").value = ""; dessinerClients(); $("#recherche-client").focus(); }
  if (ecran === "vente") { dessinerFamilles(); dessinerArticles(); dessinerPanier(); majRaccourcis(); }
  if (ecran === "paiement") dessinerPaiement();
  if (ecran === "reglages") dessinerReglages();
  if (ecran === "saisie") dessinerSaisie();
  if (ecran === "connexion") dessinerConnexion();
  if (ecran === "commandes") { $("#recherche-commande").value = ""; dessinerCommandes(); }
  majBoutonCommandes();
}

function bandeau(texte, type = "info") {
  const b = $("#bandeau");
  b.textContent = texte || "";
  b.className = `bandeau ${type}`;
  b.hidden = !texte;
}

function element(balise, attributs = {}, ...enfants) {
  const el = document.createElement(balise);
  for (const [k, v] of Object.entries(attributs)) {
    if (k.startsWith("on")) el.addEventListener(k.slice(2), v);
    else if (v !== false && v != null) el.setAttribute(k, v === true ? "" : v);
  }
  el.append(...enfants.filter((e) => e != null));
  return el;
}

// ---------- Paramètres de saisie : pièce créée dans Sage, souche, dépôt ----------
// Bon de livraison et facture font sortir le stock dans Sage dès leur enregistrement ; le bon de commande le réserve.

const TYPES = {
  commande: { libelle: "Bon de commande", valider: "Valider la commande", detail: "Réserve le stock. Livraison et facture se font ensuite dans Sage." },
  livraison: { libelle: "Bon de livraison", valider: "Valider le bon de livraison", detail: "Le stock sort du dépôt dès l'enregistrement dans Sage." },
  facture: { libelle: "Facture", valider: "Valider la facture", detail: "Le stock sort du dépôt dès l'enregistrement dans Sage. Facture à valider dans Sage." },
};

// Souches et dépôts diffèrent d'une société à l'autre : avec plusieurs sociétés, ces choix sont gardés par société.
function reglagesSaisie() {
  const r = lireReglages();
  return lireDossier() ? r.societes?.[lireDossier()] || {} : r;
}

function ecrireReglagesSaisie(valeurs) {
  const r = lireReglages();
  const d = lireDossier();
  ecrireReglages(d ? { ...r, societes: { ...r.societes, [d]: { ...r.societes?.[d], ...valeurs } } } : { ...r, ...valeurs });
}

/** { typeDocument, souche, depot } de cette tablette (souche et dépôt null : ceux que Sage choisit). */
function parametres() {
  const r = reglagesSaisie();
  return { typeDocument: TYPES[r.typeDocument] ? r.typeDocument : "commande", souche: r.souche ?? null, depot: r.depot ?? null };
}

const nomSouche = (n) => (n == null ? "Souche par défaut" : (catalogue?.souches || []).find((x) => x.numero === n)?.intitule || `Souche ${n + 1}`);
const nomDepot = (n) => (n == null ? "Dépôt du client" : (catalogue?.depots || []).find((x) => x.numero === n)?.intitule || `Dépôt ${n}`);

let choixSaisie = null; // choix en cours sur l'écran, enregistrés par le bouton Enregistrer

function dessinerSaisie() {
  choixSaisie ??= parametres();
  const option = (actif, titre, detail, onclick) => element("button", { type: "button", class: actif ? "actif" : "", "aria-pressed": String(actif), onclick },
    element("strong", {}, titre), detail ? element("span", {}, detail) : null);
  const redessiner = (champ, valeur) => () => { choixSaisie[champ] = valeur; dessinerSaisie(); };
  $("#choix-type").replaceChildren(...Object.entries(TYPES).map(([cle, t]) =>
    option(choixSaisie.typeDocument === cle, t.libelle, cle === "commande" ? "Le stock est réservé" : "Le stock bouge en temps réel", redessiner("typeDocument", cle))));
  $("#info-type").textContent = TYPES[choixSaisie.typeDocument].detail;
  const souches = catalogue?.souches || [];
  $("#choix-souche").replaceChildren(
    option(choixSaisie.souche == null, "Souche par défaut", "Celle de Sage", redessiner("souche", null)),
    ...souches.map((x) => option(choixSaisie.souche === x.numero, x.intitule, `N° ${x.numero + 1}`, redessiner("souche", x.numero))));
  const depots = catalogue?.depots || [];
  $("#choix-depot").replaceChildren(
    option(choixSaisie.depot == null, "Dépôt du client", "Celui de la fiche client ou le principal", redessiner("depot", null)),
    ...depots.map((d) => option(choixSaisie.depot === d.numero, d.intitule || `Dépôt ${d.numero}`, [`N° ${d.numero}`, d.ville].filter(Boolean).join(" · "), redessiner("depot", d.numero))));
  $("#btn-annuler-saisie").hidden = !reglagesSaisie().saisieReglee;
}

function enregistrerSaisie(ev) {
  ev.preventDefault();
  const c = choixSaisie || parametres();
  ecrireReglagesSaisie({ typeDocument: c.typeDocument, souche: c.souche, depot: c.depot, saisieReglee: true });
  choixSaisie = null;
  bandeau(`${TYPES[c.typeDocument].libelle} · ${nomSouche(c.souche)} · ${nomDepot(c.depot)}`, "ok");
  revenirALaVente();
}

function ouvrirSaisie() {
  if (vente?.validee) return bandeau("Terminez d'abord l'encaissement en cours.", "info");
  choixSaisie = null;
  afficher("saisie");
}

/** Retour à l'écran de travail : la vente en cours, sinon une nouvelle vente. */
function revenirALaVente() {
  if (vente?.validee) afficher("paiement");
  else if (vente) afficher("vente");
  else nouvelleVente();
}

// ---------- Prix, unités et stock ----------

/** Prix de la ligne pour le client du ticket (tarif client, catégorie tarifaire, gamme, conditionnement, remises). */
const tarif = (l) => prixLigne(catalogue, vente?.client, l.article, { enumere: l.enumere, conditionnement: l.cond, quantite: l.quantite });
const indexTva = new WeakMap();
/**
 * Taux de TVA de l'article dans Sage pour la catégorie comptable du client (article, sinon sa famille).
 * Article sans taux connu : le taux le plus courant du catalogue. Sert seulement à l'estimation hors ligne :
 * en ligne, le montant à payer est celui que Sage calcule sur la pièce.
 */
function tauxTva(article) {
  let idx = indexTva.get(catalogue);
  if (!idx) {
    const parCle = new Map(), parArticle = new Map(), frequence = new Map();
    for (const t of catalogue.tauxTva || []) {
      parCle.set(`${t.article.toUpperCase()}|${t.categorie}`, t.taux);
      if (!parArticle.has(t.article.toUpperCase())) parArticle.set(t.article.toUpperCase(), t.taux);
      frequence.set(t.taux, (frequence.get(t.taux) || 0) + 1);
    }
    const courant = [...frequence.entries()].sort((a, b) => b[1] - a[1])[0]?.[0] ?? 0;
    idx = { parCle, parArticle, courant };
    indexTva.set(catalogue, idx);
  }
  const ref = (article?.reference || "").toUpperCase();
  return idx.parCle.get(`${ref}|${vente?.client?.categorieCompta ?? 1}`) ?? idx.parArticle.get(ref) ?? idx.courant;
}
/** Prix HT de l'unité vendue après remise (un tarif TTC est ramené en HT avec le taux de TVA de l'article dans Sage). */
const prixNetHT = (p, article) => enHT(p.prixNet, p.ttc, tauxTva(article));
const montantLigne = (l) => arrondi(prixNetHT(tarif(l), l.article) * l.quantite);
/** Quantité en unités de vente : 3 cartons de 12 = 36. */
const contenu = (cond) => (cond?.quantite > 0 ? cond.quantite : 1);
const uniteDe = (article) => article.unite || "";
const libelleUnite = (l) => (l.cond ? l.cond.enumere : uniteDe(l.article));

const stocksIndex = new WeakMap();
/** Lignes de stock du dépôt choisi pour cet article, ou null si aucun dépôt n'est choisi (stock de tous les dépôts). */
function stocksDuDepot(article) {
  const depot = parametres().depot;
  if (depot == null || !catalogue.stocksDepots) return null;
  let parArticle = stocksIndex.get(catalogue);
  if (!parArticle) {
    parArticle = new Map();
    for (const s of catalogue.stocksDepots) {
      if (!parArticle.has(s.article)) parArticle.set(s.article, []);
      parArticle.get(s.article).push(s);
    }
    stocksIndex.set(catalogue, parArticle);
  }
  return (parArticle.get(article.reference) || []).filter((s) => s.depot === depot);
}
const dispo = (s) => s.stock - s.stockReserve;
/** Stock disponible de l'article dans le dépôt choisi (tous dépôts sinon), en unités de vente. */
function stockArticle(article) {
  const lignes = stocksDuDepot(article);
  return lignes ? lignes.filter((s) => !s.gamme1).reduce((t, s) => t + dispo(s), 0) : article.stockDisponible;
}
/** Stock disponible d'une valeur de gamme dans le dépôt choisi, ou null si inconnu (ancien catalogue). */
function stockValeur(article, e) {
  const lignes = stocksDuDepot(article);
  if (!lignes) return e.stockDisponible ?? null;
  return lignes.filter((s) => s.gamme1 && memeValeur({ article: article.reference, gamme1: s.gamme1, gamme2: s.gamme2 }, { ...e, article: article.reference }))
    .reduce((t, s) => t + dispo(s), 0);
}

// ---------- Affichage en liste ou en boutons ----------

const dessins = { articles: () => dessinerArticles(), clients: () => dessinerClients(), commandes: () => dessinerCommandes() };

function changerAffichage(liste, mode) {
  ecrireAffichage(liste, mode);
  majBascules();
  dessins[liste]();
}

function majBascules() {
  for (const b of document.querySelectorAll("[data-liste]")) {
    const actif = lireAffichage(b.dataset.liste) === b.dataset.mode;
    b.classList.toggle("actif", actif);
    b.setAttribute("aria-pressed", String(actif));
  }
}

/** Tableau dont chaque ligne se touche : colonnes [{ titre, classe }], lignes [{ cellules, onclick, classe, attributs }]. */
function tableau(colonnes, lignes) {
  return element("table", { class: "table-liste" },
    element("thead", {}, element("tr", {}, ...colonnes.map((c) => element("th", { class: c.classe }, c.titre)))),
    element("tbody", {}, ...lignes.map((l) => element("tr", { class: l.classe, onclick: l.onclick, ...(l.attributs || {}) },
      ...l.cellules.map((v, i) => element("td", { class: colonnes[i].classe }, v))))));
}

/** Teinte stable par famille d'articles : un repère de couleur sur les boutons. */
const teinte = (texte) => [...(texte || "")].reduce((h, c) => (h * 31 + c.charCodeAt(0)) % 360, 200);
const quantiteTexte = (q) => String(q).replace(".", ",");

// ---------- 1. Client ----------

function dessinerClients() {
  const q = $("#recherche-client").value.trim().toLowerCase();
  const zone = $("#liste-clients");
  const enListe = lireAffichage("clients") === "liste";
  zone.className = enListe ? "liste-clients en-liste" : "liste-clients en-boutons";
  if (!catalogue) {
    zone.replaceChildren(element("p", { class: "vide" }, "Catalogue absent : connectez la borne au serveur puis ouvrez les réglages."));
    return;
  }
  const clients = catalogue.clients
    .filter((c) => !q || c.numero.toLowerCase().includes(q) || (c.intitule || "").toLowerCase().includes(q))
    .slice(0, enListe ? 200 : 60);
  if (clients.length === 0) return zone.replaceChildren(element("p", { class: "vide" }, "Aucun client trouvé."));
  if (enListe) {
    zone.replaceChildren(tableau(
      [{ titre: "Code", classe: "code" }, { titre: "Nom" }, { titre: "Ville", classe: "secondaire" }, { titre: "Téléphone", classe: "secondaire" }],
      clients.map((c) => ({ cellules: [c.numero, c.intitule || "", c.ville || "", c.telephone || ""], onclick: () => demarrerVente(c) }))));
  } else {
    zone.replaceChildren(...clients.map((c) => element("button", { type: "button", class: "carte-client", onclick: () => demarrerVente(c) },
      element("span", { class: "code" }, c.numero),
      element("strong", {}, c.intitule || c.numero),
      element("span", { class: "discret" }, [c.ville, c.telephone].filter(Boolean).join(" · ")))));
  }
}

function demarrerVente(client) {
  const garder = changementClient && vente && !vente.validee && !vente.existante;
  changementClient = false;
  dernierClient = client;
  if (garder) {
    vente.client = client;
  } else {
    vente = { id: null, numero: null, client, lignes: new Map(), paiements: [], validee: false };
    famille = null;
    ligneChoisie = null;
    saisie = "";
    $("#recherche-article").value = "";
  }
  afficher("vente");
}

/** Change le client du ticket en cours sans perdre ses lignes. */
function changerClient() {
  changementClient = true;
  afficher("client");
}

const clientDefaut = () => {
  const defaut = lireReglages().clientDefaut;
  return (defaut && catalogue?.clients.find((c) => c.numero === defaut)) || null;
};

function clientDePassage() {
  const c = clientDefaut();
  if (c) { changementClient = true; demarrerVente(c); }
}

// ---------- 2. Articles et panier ----------

function dessinerFamilles() {
  const zone = $("#familles");
  zone.replaceChildren();
  const familles = [...new Set(catalogue.articles.map((a) => a.famille).filter(Boolean))].sort();
  const puce = (nom, valeur) => element("button", {
    type: "button", class: famille === valeur ? "puce active" : "puce",
    onclick: () => { famille = valeur; dessinerFamilles(); dessinerArticles(); },
  }, nom);
  zone.append(puce("Toutes", null), ...familles.map((f) => puce(f, f)));
}

function dessinerArticles() {
  const q = $("#recherche-article").value.trim().toLowerCase();
  const zone = $("#articles");
  const enListe = lireAffichage("articles") === "liste";
  zone.className = enListe ? "articles en-liste" : "articles en-boutons";
  const articles = catalogue.articles
    .filter((a) => !famille || a.famille === famille)
    .filter((a) => !q || a.reference.toLowerCase().includes(q) || (a.designation || "").toLowerCase().includes(q) || a.codeBarre === q)
    .slice(0, enListe ? 300 : 120);
  if (articles.length === 0) return zone.replaceChildren(element("p", { class: "vide" }, "Aucun article."));
  const stock = (a) => {
    if (a.suiviStock === false) return null;
    const d = stockArticle(a);
    return element("span", { class: d > 0 ? "stock" : "stock rupture" }, d > 0 ? `${quantiteTexte(d)}${a.unite ? " " + a.unite : ""}` : "Rupture");
  };
  const gamme = (a) => (aGamme(a) ? element("span", { class: "gamme" }, [a.gamme1, a.gamme2].filter(Boolean).join(" / ") || "Gamme") : null);
  const cond = (a) => (conditionnementsDe(catalogue, a).length ? element("span", { class: "cond", title: "Vendu par conditionnement" }, a.conditionnement || "Cond.") : null);
  // Prix de l'unité de vente pour le client du ticket : tarif client ou catégorie tarifaire, remise comprise.
  const prix = (a) => {
    const p = prixLigne(catalogue, vente?.client, a);
    const ht = prixNetHT(p, a);
    const avant = enHT(p.prix, p.ttc, tauxTva(a));
    // Espaces entre les morceaux : la tuile passe à la ligne entre eux, jamais au milieu d'un prix.
    return element("span", { class: "prix" }, ht < avant - 0.005 ? element("span", { class: "barre" }, euros.format(avant)) : null, " ",
      element("span", {}, `${euros.format(ht)} HT`), " ", a.unite ? element("span", { class: "unite-prix" }, `/ ${a.unite}`) : null);
  };
  const rupture = (a) => a.suiviStock !== false && stockArticle(a) <= 0;
  if (enListe) {
    zone.replaceChildren(tableau(
      [{ titre: "Code", classe: "code" }, { titre: "Désignation" }, { titre: "Famille", classe: "secondaire" },
        { titre: "Unité", classe: "secondaire" }, { titre: "PU HT", classe: "nombre" }, { titre: "Stock", classe: "nombre" }],
      articles.map((a) => ({
        classe: rupture(a) ? "article rupture" : "article",
        cellules: [a.reference, element("span", {}, a.designation || a.reference, gamme(a), cond(a)), a.famille || "", a.unite || "", prix(a), stock(a)],
        onclick: () => toucherArticle(a),
      }))));
  } else {
    zone.replaceChildren(...articles.map((a) => element("button", {
      type: "button", class: rupture(a) ? "tuile rupture" : "tuile",
      style: `--teinte: ${teinte(a.famille)}`, title: a.famille || "", onclick: () => toucherArticle(a),
    },
      element("span", { class: "haut" }, element("span", { class: "code" }, a.reference), stock(a)),
      element("span", { class: "designation" }, a.designation || a.reference),
      element("span", { class: "bas" }, gamme(a), cond(a), prix(a)))));
  }
}

// ---------- Gammes (taille, couleur...) : Sage exige la valeur pour un article à gamme ----------

const enumeres = (article) => (catalogue.gammes || []).filter((g) => g.article === article.reference);
const aGamme = (article) => !!article.gamme1 || enumeres(article).length > 0;
const libelleGamme = (e) => (e ? [e.gamme1, e.gamme2].filter(Boolean).join(" / ") : "");

/** demander : fenêtre quantité + conditionnement (réglage de la borne) ; la douchette ajoute directement. */
function toucherArticle(article, demander = lireReglages().demanderQuantite !== false) {
  // Quantité tapée au pavé avant de toucher l'article (comme sur une caisse : 3 puis l'article).
  const q = prendreSaisie() ?? 1;
  if (demander) return demanderQuantite(article, q);
  const conds = conditionnementsDe(catalogue, article);
  if (conds.length) return choisirConditionnement(article, conds, q);
  if (!aGamme(article)) return ajouterArticle(article, q);
  if (stockControle(article) && quantiteAuPanier(article) + q > stockArticle(article)) return ajouterArticle(article, q); // affiche le refus
  const valeurs = enumeres(article);
  if (valeurs.length === 0) {
    bandeau(`${article.designation || article.reference} est géré en gamme : rechargez le catalogue (Réglages) pour voir ses valeurs.`, "erreur");
    return;
  }
  const d = $("#choix-gamme");
  $("#gamme-titre").textContent = `${article.designation || article.reference} : ${[article.gamme1, article.gamme2].filter(Boolean).join(" / ") || "choisir"}`;
  $("#gamme-valeurs").className = "valeurs";
  // Stock de chaque valeur (taille, couleur...) : une valeur épuisée reste visible mais ne s'ajoute pas.
  $("#gamme-valeurs").replaceChildren(...valeurs.map((e) => {
    const s = stockValeur(article, e);
    const reste = s == null ? null : s - quantiteValeurAuPanier(e);
    const epuise = stockControle(article) && reste != null && reste <= 0;
    const p = prixLigne(catalogue, vente.client, article, { enumere: e, quantite: q });
    return element("button", { type: "button", class: epuise ? "valeur epuise" : "valeur", onclick: () => { d.close(); ajouterArticle(article, q, e); } },
      element("span", {}, libelleGamme(e)),
      element("span", { class: "prix-valeur" }, `${euros.format(prixNetHT(p))} HT`),
      article.suiviStock === false || reste == null ? null
        : element("span", { class: reste > 0 ? "stock" : "stock rupture" }, reste > 0 ? `Stock ${quantiteTexte(reste)}` : "Rupture"));
  }));
  d.showModal();
}

/** Article vendu par conditionnement : choix du carton, du pack... avec son contenu, son prix et le nombre disponible. */
function choisirConditionnement(article, conds, q) {
  const d = $("#choix-gamme");
  $("#gamme-titre").textContent = `${article.designation || article.reference} : conditionnement`;
  $("#gamme-valeurs").className = "valeurs conditionnements";
  const libre = stockControle(article) ? stockArticle(article) - quantiteAuPanier(article) : null;
  $("#gamme-valeurs").replaceChildren(...conds.map((c) => {
    const nb = libre == null ? null : Math.floor(libre / contenu(c) + 1e-9);
    const p = prixLigne(catalogue, vente.client, article, { conditionnement: c, quantite: q });
    return element("button", { type: "button", class: nb != null && nb <= 0 ? "valeur epuise" : "valeur", onclick: () => { d.close(); ajouterArticle(article, q, null, c); } },
      element("span", {}, c.enumere),
      element("span", { class: "contenu" }, `${quantiteTexte(c.quantite)} ${uniteDe(article)}`.trim()),
      element("span", { class: "prix-valeur" }, `${euros.format(prixNetHT(p))} HT`),
      nb == null ? null : element("span", { class: nb > 0 ? "stock" : "stock rupture" }, nb > 0 ? `${nb} disponible(s)` : "Rupture"));
  }));
  d.showModal();
}

// ---------- Fenêtre quantité : quantité, puis conditionnement (carton...) ou valeur de gamme, puis OK ----------

let choixQte = null; // { article, conds, valeurs, choix, remplacer }

function demanderQuantite(article, q) {
  const conds = conditionnementsDe(catalogue, article);
  const valeurs = !conds.length && aGamme(article) ? enumeres(article) : [];
  if (!conds.length && aGamme(article) && valeurs.length === 0) {
    bandeau(`${article.designation || article.reference} est géré en gamme : rechargez le catalogue (Réglages) pour voir ses valeurs.`, "erreur");
    return;
  }
  // Le plus petit conditionnement est proposé d'office ; une valeur de gamme (taille...) se choisit toujours.
  choixQte = { article, conds, valeurs, choix: conds[0] || null, remplacer: true };
  $("#qte-titre").textContent = `${article.reference} · ${article.designation || ""}`.replace(/ · $/, "");
  $("#qte-valeur").value = quantiteTexte(q);
  dessinerQuantite();
  $("#choix-quantite").showModal();
  $("#qte-valeur").focus();
  $("#qte-valeur").select();
}

const lireQte = () => Number($("#qte-valeur").value.replace(",", ".").trim());

function dessinerQuantite(message = "") {
  const { article, conds, valeurs, choix } = choixQte;
  const q = lireQte() > 0 ? lireQte() : 1;
  const libre = stockControle(article) ? stockArticle(article) - quantiteAuPanier(article) : null;
  const option = (o, actif, ...contenu) => element("button", {
    type: "button", class: ["valeur", actif ? "choisi" : "", o.epuise ? "epuise" : ""].filter(Boolean).join(" "),
    "aria-pressed": String(actif), onclick: () => { choixQte.choix = o.valeur; dessinerQuantite(); },
  }, ...contenu);
  $("#qte-choix-titre").textContent = conds.length ? "Conditionnement" : valeurs.length ? [article.gamme1, article.gamme2].filter(Boolean).join(" / ") || "Gamme" : "";
  $("#qte-choix").className = conds.length ? "valeurs conditionnements" : "valeurs";
  $("#qte-choix").replaceChildren(...conds.map((c) => {
    const nb = libre == null ? null : Math.floor(libre / contenu(c) + 1e-9);
    const p = prixLigne(catalogue, vente.client, article, { conditionnement: c, quantite: q });
    return option({ valeur: c, epuise: nb != null && nb <= 0 }, choix === c,
      element("span", {}, c.enumere),
      element("span", { class: "contenu" }, `${quantiteTexte(c.quantite)} ${uniteDe(article)}`.trim()),
      element("span", { class: "prix-valeur" }, `${euros.format(prixNetHT(p, article))} HT`),
      nb == null ? null : element("span", { class: nb > 0 ? "stock" : "stock rupture" }, nb > 0 ? `${nb} dispo.` : "Rupture"));
  }), ...valeurs.map((e) => {
    const s = stockValeur(article, e);
    const reste = s == null ? null : s - quantiteValeurAuPanier(e);
    const p = prixLigne(catalogue, vente.client, article, { enumere: e, quantite: q });
    return option({ valeur: e, epuise: stockControle(article) && reste != null && reste <= 0 }, choix === e,
      element("span", {}, libelleGamme(e)),
      element("span", { class: "prix-valeur" }, `${euros.format(prixNetHT(p, article))} HT`),
      article.suiviStock === false || reste == null ? null
        : element("span", { class: reste > 0 ? "stock" : "stock rupture" }, reste > 0 ? `Stock ${quantiteTexte(reste)}` : "Rupture"));
  }));
  // Montant de la ligne et stock restant, recalculés à chaque chiffre.
  const p = prixLigne(catalogue, vente.client, article, conds.length ? { conditionnement: choix, quantite: q } : { enumere: choix, quantite: q });
  const pu = prixNetHT(p, article);
  const unite = choix && conds.length ? choix.enumere : uniteDe(article);
  const info = $("#qte-info");
  info.className = message ? "erreur-qte" : "discret";
  info.textContent = message || [
    `${euros.format(pu)} HT${unite ? " / " + unite : ""} · ligne ${euros.format(arrondi(pu * q))} HT`,
    libre == null ? null : `stock ${quantiteTexte(Math.max(0, arrondi(libre)))}${uniteDe(article) ? " " + uniteDe(article) : ""}`,
  ].filter(Boolean).join(" · ");
}

function toucherMiniPave(touche) {
  const champ = $("#qte-valeur");
  let v = choixQte.remplacer ? "" : champ.value;
  choixQte.remplacer = false;
  if (touche === "suppr") v = v.slice(0, -1);
  else if (touche === ",") { if (!v.includes(",")) v = (v || "0") + ","; }
  else v = (v === "0" ? "" : v) + touche;
  champ.value = v.slice(0, 8);
  dessinerQuantite();
}

function pasQuantite(pas) {
  const q = lireQte() > 0 ? lireQte() : 0;
  const n = arrondi(q + pas);
  $("#qte-valeur").value = quantiteTexte(n > 0 ? n : q || 1);
  choixQte.remplacer = false;
  dessinerQuantite();
}

function validerQuantite(ev) {
  ev.preventDefault();
  if (!choixQte) return;
  const { article, conds, valeurs, choix } = choixQte;
  const q = lireQte();
  if (!(q > 0)) return dessinerQuantite("Tapez une quantité.");
  if (valeurs.length && !choix) return dessinerQuantite(`Choisissez : ${$("#qte-choix-titre").textContent}.`);
  $("#choix-quantite").close();
  choixQte = null;
  if (conds.length) ajouterArticle(article, arrondi(q), null, choix);
  else if (valeurs.length) ajouterArticle(article, arrondi(q), choix);
  else ajouterArticle(article, arrondi(q));
}

// ---------- Pavé numérique masquable : sans lui, les articles prennent sa place ----------

function appliquerPave() {
  const visible = !!lireReglages().pave;
  $("#ecran-vente").classList.toggle("sans-pave", !visible);
  $("#btn-r-pave").setAttribute("aria-pressed", String(visible));
  $("#btn-r-pave").classList.toggle("actif", visible);
}

function basculerPave() {
  try { ecrireReglages({ ...lireReglages(), pave: !lireReglages().pave }); } catch { /* stockage indisponible */ }
  appliquerPave();
}

// Même règle que la fenêtre « Indisponibilité en stock » de Sage : l'API refuse aussi la commande, la borne prévient avant.
const stockControle = (article) => !!catalogue.controleStock && article.suiviStock !== false;
/** Quantité de l'article déjà au ticket, en unités de vente (les cartons comptent leur contenu). */
const quantiteAuPanier = (article) =>
  [...vente.lignes.values()].filter((l) => l.article.reference === article.reference).reduce((s, l) => s + l.quantite * contenu(l.cond), 0);
const memeValeur = (a, b) => a.article === b.article && a.gamme1 === b.gamme1 && (a.gamme2 || "") === (b.gamme2 || "");
const quantiteValeurAuPanier = (e) =>
  [...vente.lignes.values()].filter((l) => l.enumere && memeValeur(l.enumere, e)).reduce((s, l) => s + l.quantite, 0);
/** Vrai si cette valeur de gamme dépasse son propre stock (catalogue récent : stock connu par valeur). */
function valeurEnManque(article, e, ajout = 0) {
  if (!e || article.suiviStock === false) return false;
  const s = stockValeur(article, e);
  return s != null && quantiteValeurAuPanier(e) + ajout > s;
}

const cleLigne = (article, enumere, cond) =>
  enumere ? `${article.reference}|${enumere.gamme1}|${enumere.gamme2 || ""}` : cond ? `${article.reference}#${cond.numero}` : article.reference;
const ou = () => (parametres().depot == null ? "" : ` dans ${nomDepot(parametres().depot)}`);

function ajouterArticle(article, delta = 1, enumere = null, cond = null) {
  const unites = delta * contenu(cond);
  if (delta > 0 && stockControle(article) && quantiteAuPanier(article) + unites > stockArticle(article)) {
    const d = Math.max(0, stockArticle(article));
    bandeau(`Stock insuffisant pour ${article.designation || article.reference} : ${quantiteTexte(d)}${uniteDe(article) ? " " + uniteDe(article) : ""} disponible(s)${ou()}.`, "erreur");
    return;
  }
  if (delta > 0 && stockControle(article) && valeurEnManque(article, enumere, delta)) {
    bandeau(`Stock insuffisant pour ${article.designation || article.reference} ${libelleGamme(enumere)} : ${Math.max(0, stockValeur(article, enumere))} disponible(s)${ou()}.`, "erreur");
    return;
  }
  const cle = cleLigne(article, enumere, cond);
  const l = vente.lignes.get(cle) || { article, enumere, cond, quantite: 0 };
  l.quantite = arrondi(l.quantite + delta);
  if (l.quantite <= 0) vente.lignes.delete(cle);
  else vente.lignes.set(cle, l);
  if (l.quantite > 0 && delta > 0) ligneChoisie = cle;
  if (!vente.lignes.has(ligneChoisie)) ligneChoisie = [...vente.lignes.keys()].pop() ?? null;
  dessinerPanier();
}

// ---------- Pavé numérique ----------
// Les chiffres tapés servent de quantité : pour l'article touché ensuite, ou pour la ligne choisie (Quantité / Entrée).

/** Quantité tapée au pavé (vidée une fois lue), ou null si rien n'est tapé. */
function prendreSaisie() {
  const v = Number(saisie.replace(",", "."));
  saisie = "";
  majSaisie();
  return v > 0 ? v : null;
}

function majSaisie() {
  $("#saisie").textContent = saisie || "0";
}

function toucherPave(touche) {
  if (!vente || vente.validee) return;
  const l = vente.lignes.get(ligneChoisie);
  if (/^[0-9]$/.test(touche)) saisie = (saisie === "0" ? "" : saisie) + touche;
  else if (touche === ",") { if (!saisie.includes(",")) saisie = (saisie || "0") + ","; }
  else if (touche === "suppr") saisie = saisie.slice(0, -1);
  else if (touche === "plus" || touche === "moins") {
    if (!l) return bandeau("Choisissez d'abord une ligne du ticket.", "info");
    const n = prendreSaisie() ?? 1;
    return ajouterArticle(l.article, touche === "plus" ? n : -n, l.enumere, l.cond);
  } else if (touche === "quantite" || touche === "entree") {
    if (!l) return bandeau("Choisissez d'abord une ligne du ticket.", "info");
    const n = prendreSaisie();
    if (n == null) return bandeau("Tapez la quantité puis appuyez sur Quantité.", "info");
    return ajouterArticle(l.article, arrondi(n - l.quantite), l.enumere, l.cond);
  }
  if (saisie.length > 8) saisie = saisie.slice(0, 8);
  majSaisie();
}

function supprimerLigne() {
  if (!vente || vente.validee || !vente.lignes.has(ligneChoisie)) return;
  vente.lignes.delete(ligneChoisie);
  ligneChoisie = [...vente.lignes.keys()].pop() ?? null;
  dessinerPanier();
}

/**
 * Totaux du ticket. Pièce créée dans Sage (vente.sage) : montants de Sage, remises client et famille, TVA et escompte compris ;
 * le reste à payer part du net à payer de la pièce. Sinon (hors ligne), estimation sur la borne avec la TVA de chaque article.
 */
function totaux() {
  if (vente.existante) {
    const paye = arrondi(vente.dejaRegle + vente.paiements.reduce((s, p) => s + p.montant, 0));
    return { ht: 0, tva: 0, ttc: vente.totalTTC, paye, reste: Math.max(0, arrondi(vente.totalTTC - paye)), sage: true };
  }
  const paye = arrondi(vente.paiements.reduce((s, p) => s + p.montant, 0));
  if (vente.sage) {
    const s = vente.sage;
    // Escompte du client : Sage le déduit du total TTC pour donner le net à payer.
    const escompte = Math.max(0, arrondi(s.totalTTC - s.netAPayer));
    return { ht: s.totalHT, escompte, tauxEscompte: vente.client?.escompte || 0, tva: arrondi(s.totalTTC - s.totalHT), ttc: s.netAPayer, paye,
      reste: Math.max(0, arrondi(s.netAPayer - paye)), sage: true };
  }
  // Estimation comme Sage : escompte du client (CT_Taux02) déduit du HT, TVA calculée sur le montant escompté.
  const taux = (vente.client?.escompte || 0) / 100;
  const ht = arrondi([...vente.lignes.values()].reduce((s, l) => s + montantLigne(l), 0));
  const escompte = arrondi(ht * taux);
  const tva = arrondi([...vente.lignes.values()].reduce((s, l) => s + montantLigne(l) * (1 - taux) * tauxTva(l.article) / 100, 0));
  const ttc = arrondi(ht - escompte + tva);
  return { ht, escompte, tauxEscompte: vente.client?.escompte || 0, tva, ttc, paye, reste: Math.max(0, arrondi(ttc - paye)), sage: false };
}

function dessinerPanier() {
  const ul = $("#lignes");
  ul.replaceChildren();
  for (const [cle, l] of vente.lignes) {
    const alerte = stockControle(l.article) && (quantiteAuPanier(l.article) > stockArticle(l.article) || valeurEnManque(l.article, l.enumere));
    const classes = [alerte ? "alerte" : "", cle === ligneChoisie ? "choisie" : ""].filter(Boolean).join(" ");
    const p = tarif(l);
    const remises = texteRemises(p.remises);
    // Comme un ticket de caisse : code et désignation, puis quantité, unité (ou conditionnement), prix unitaire et montant.
    ul.append(element("li", { class: classes, onclick: () => { ligneChoisie = cle; dessinerPanier(); } },
      element("div", { class: "libelle" },
        element("span", { class: "code" }, l.article.reference),
        element("strong", {}, l.article.designation || l.article.reference, l.enumere ? ` · ${libelleGamme(l.enumere)}` : ""),
        alerte ? element("span", { class: "alerte-stock" }, "Stock insuffisant") : null),
      element("div", { class: "detail" },
        element("div", { class: "quantite" },
          element("button", { type: "button", "aria-label": "Retirer un", onclick: (e) => { e.stopPropagation(); ajouterArticle(l.article, -1, l.enumere, l.cond); } }, "−"),
          element("span", {}, quantiteTexte(l.quantite)),
          element("button", { type: "button", "aria-label": "Ajouter un", onclick: (e) => { e.stopPropagation(); ajouterArticle(l.article, 1, l.enumere, l.cond); } }, "+")),
        element("span", { class: "unite" }, libelleUnite(l),
          l.cond && contenu(l.cond) !== 1 ? element("span", { class: "contenu" }, ` (${quantiteTexte(arrondi(l.quantite * contenu(l.cond)))}${uniteDe(l.article) ? " " + uniteDe(l.article) : ""})`) : null),
        element("span", { class: "pu" }, `× ${euros.format(enHT(p.prix, p.ttc, tauxTva(l.article)))} HT`, remises ? " " : null, remises ? element("span", { class: "remise" }, remises) : null),
        element("span", { class: "montant" }, euros.format(montantLigne(l))))));
  }
  if (vente.lignes.size === 0) ul.append(element("li", { class: "vide" }, "Touchez un article pour l'ajouter."));
  const t = totaux();
  $("#total-ht").textContent = euros.format(t.ht);
  $("#total-tva").textContent = euros.format(t.tva);
  $("#ligne-escompte").hidden = !(t.escompte > 0);
  $("#lbl-escompte").textContent = t.tauxEscompte ? `Escompte ${quantiteTexte(t.tauxEscompte)} %` : "Escompte";
  $("#total-escompte").textContent = `-${euros.format(t.escompte || 0)}`;
  $("#total-ttc").textContent = euros.format(t.ttc);
  const vide = vente.lignes.size === 0;
  // Un vendeur sans la case Caissier enregistre la commande ; le caissier choisit directement le mode de règlement.
  $("#btn-valider").textContent = peutEncaisser() ? "Régler" : TYPES[parametres().typeDocument].valider;
  $("#btn-valider").disabled = vide;
  $("#modes-rapides").replaceChildren(...(peutEncaisser() ? catalogue.modesReglement : []).map((m) =>
    element("button", { type: "button", class: "mode-rapide", disabled: vide, onclick: () => reglerAvec(m.intitule) }, m.intitule)));
  $("#btn-supprimer-ligne").disabled = !vente.lignes.has(ligneChoisie);
  $("#btn-attente").disabled = vide;
  const articles = [...vente.lignes.values()].reduce((s, l) => s + l.quantite, 0);
  $("#onglet-total").textContent = vide ? "" : `(${String(arrondi(articles)).replace(".", ",")}) ${euros.format(t.ttc)}`;
  majEntete();
  majSaisie();
}

function majEntete() {
  $("#t-date").textContent = new Date().toLocaleString("fr-FR", { dateStyle: "short", timeStyle: "short" });
  $("#t-numero").textContent = vente?.numero || "nouveau";
  $("#t-vendeur").textContent = nomUtilisateur(utilisateur) || "—";
  $("#t-client").textContent = vente?.client ? `${vente.client.intitule || vente.client.numero} (${vente.client.numero})` : "";
  // Pièce créée dans Sage : rouge pâle quand le stock bouge à l'enregistrement (bon de livraison, facture).
  const pr = parametres();
  $("#t-document").textContent = `${TYPES[pr.typeDocument].libelle} · ${nomSouche(pr.souche)}`;
  $("#t-depot").textContent = nomDepot(pr.depot);
  $("#btn-document-ticket").classList.toggle("mouvement", pr.typeDocument !== "commande");
  const cat = vente?.client ? categorieDe(catalogue, vente.client) : null;
  $("#ligne-tarif").hidden = !cat;
  $("#t-tarif").textContent = cat?.intitule || "";
}

function majRaccourcis() {
  $("#btn-r-passage").hidden = !clientDefaut();
  $("#btn-r-x").hidden = !peutEncaisser();
  const n = lireAttente().length;
  $("#nb-attente").textContent = n ? String(n) : "";
  $("#nb-attente").hidden = !n;
}

/** Bouton de mode sous le ticket : enregistre la commande puis ouvre l'encaissement sur ce mode. */
async function reglerAvec(mode) {
  if (!vente || vente.lignes.size === 0 || vente.validee) return;
  const v = vente;
  await validerCommande();
  if (vente === v && !v.refus && !$("#ecran-paiement").hidden) choisirMode(mode);
}

function basculerOnglet(vue) {
  const e = $("#ecran-vente");
  e.classList.toggle("vue-articles", vue === "articles");
  e.classList.toggle("vue-ticket", vue === "ticket");
  $("#onglet-articles").classList.toggle("actif", vue === "articles");
  $("#onglet-ticket").classList.toggle("actif", vue === "ticket");
}

async function validerCommande() {
  // Un double appui ne doit pas créer deux commandes.
  if (!vente || vente.validee || vente.lignes.size === 0) return;
  vente.validee = true;
  $("#btn-valider").disabled = true;
  const r = lireReglages();
  const pr = parametres();
  vente.id = nouvelIdVente(r.borne);
  vente.numero ||= prochainNumeroVente(); // ticket rouvert par « Modifier le ticket » : même numéro
  vente.typeDocument = pr.typeDocument;
  const t = totaux();
  await ajouterOperation({
    cle: `commande:${vente.id}`,
    type: "commande",
    idExterne: vente.id,
    corps: {
      idExterne: vente.id,
      client: vente.client.numero,
      reference: vente.numero,
      // Pièce à créer dans Sage, souche et dépôt choisis dans les paramètres de saisie. Les prix sont recalculés par l'API.
      typeDocument: pr.typeDocument,
      ...(pr.souche != null ? { souche: pr.souche } : {}),
      ...(pr.depot != null ? { depot: pr.depot } : {}),
      lignes: [...vente.lignes.values()].map((l) => ({
        article: l.article.reference,
        quantite: l.quantite,
        ...(l.enumere ? { gamme1: l.enumere.gamme1, gamme2: l.enumere.gamme2 || null } : {}),
        ...(l.cond ? { conditionnement: l.cond.enumere, quantiteConditionnement: l.cond.quantite } : {}),
      })),
    },
    vente: { numero: vente.numero, client: vente.client.intitule || vente.client.numero, totalTtcEstime: t.ttc, typeDocument: pr.typeDocument },
    ...auteur(),
  });
  // En ligne, la pièce est créée dans Sage avant l'encaissement : on encaisse le net à payer calculé par Sage
  // (remises du client et de la famille, TVA, escompte), pas l'estimation de la borne.
  // L'écran d'encaissement s'ouvre tout de suite ; seule cette pièce part, sans attendre le reste de la file ni un test de connexion.
  const v = vente;
  v.estimation = t.ttc;
  modeChoisi = null;
  $("#saisie-paiement").hidden = true;
  bandeau("");
  v.envoiEnCours = connexion !== "hors-ligne";
  afficher("paiement");
  if (v.envoiEnCours) {
    try {
      const bilan = await envoyerMaintenant(`commande:${v.id}`);
      if (bilan.cleRefusee) bandeau("La clé d'API est refusée par le serveur. Corrigez-la dans les réglages.", "erreur");
      else if (bilan.reconnexions.length)
        bandeau(`Connexion expirée pour ${bilan.reconnexions.join(", ")} : reconnectez-vous pour envoyer vos ventes à Sage.`, "erreur");
    } catch { /* la pièce reste dans la file */ }
    v.envoiEnCours = false;
  }
  await reprendreMontantsSage(v);
  if (vente === v && !$("#ecran-paiement").hidden) dessinerPaiement();
  majEtat();
}

/** Pièce Sage de la vente une fois envoyée : numéro, montants de Sage et lignes ; ou le refus de Sage. */
async function reprendreMontantsSage(v) {
  const op = (await lireFile()).find((o) => o.cle === `commande:${v.id}`);
  if (op?.statut === "erreur") {
    v.refus = op.message || "Refusé par Sage.";
    bandeau(`Sage a refusé la pièce : ${v.refus}`, "erreur");
    return;
  }
  if (op?.statut !== "ok" || !op.resultat?.piece) {
    bandeau(connexion === "hors-ligne" ? "" : "Pièce pas encore créée dans Sage : montant estimé par la borne.", connexion === "hors-ligne" ? "info" : "erreur");
    return;
  }
  v.piece = op.resultat.piece;
  // Le net à payer suffit pour encaisser : le détail de la pièce (HT, lignes du ticket imprimé) arrive ensuite.
  v.sage = {
    netAPayer: op.resultat.netAPayer, totalTTC: op.resultat.netAPayer, lignes: null,
    totalHT: arrondi(op.resultat.netAPayer - arrondi([...v.lignes.values()].reduce((x, l) => x + montantLigne(l) * tauxTva(l.article) / 100, 0))),
  };
  bandeau("");
  detailCommande(v.piece).then((d) => {
    if (!d) return;
    Object.assign(v.sage, { totalHT: d.totalHT, totalTTC: d.entete.totalTTC, lignes: d.lignes });
  }).catch(() => {});
}

// ---------- 3. Encaissement ----------

function dessinerPaiement() {
  const t = totaux();
  $("#paiement-numero").textContent = [vente.typeDocument && !vente.existante ? TYPES[vente.typeDocument].libelle : null, vente.numero].filter(Boolean).join(" ");
  $("#p-total-libelle").textContent = vente.existante ? "Total TTC" : t.sage ? "Net à payer (Sage)" : "Total TTC estimé";
  // Pièce en cours de création dans Sage : les modes s'affichent mais attendent le net à payer de Sage.
  $("#paiement-envoi").hidden = !vente.envoiEnCours;
  // Montant de Sage différent de l'estimation de la borne : on le dit, plutôt que de changer le montant sans explication.
  const ecart = t.sage && vente.estimation != null && Math.abs(vente.estimation - t.ttc) > 0.01;
  $("#paiement-ecart").hidden = !ecart;
  $("#paiement-ecart").textContent = ecart
    ? `Estimation de la borne : ${euros.format(vente.estimation)}. Sage a calculé ${euros.format(t.ttc)} avec ses remises et son escompte : c'est ce montant qui est encaissé.`
    : "";
  // Retour au ticket tant que rien n'est encaissé (la pièce Sage est supprimée puis recréée corrigée).
  $("#btn-modifier-ticket").hidden = !!vente.existante || vente.paiements.length > 0;
  $("#btn-modifier-ticket").disabled = !!vente.envoiEnCours || !!vente.suppressionEnCours;
  $("#p-total").textContent = euros.format(t.ttc);
  $("#p-paye").textContent = euros.format(t.paye);
  $("#p-reste").textContent = euros.format(t.reste);

  const zone = $("#modes");
  zone.replaceChildren();
  // Même règle que l'API : sans la case Caissier sur sa fiche collaborateur Sage, l'utilisateur n'encaisse pas.
  const pasCaissier = !!catalogue.exigerCaissier && !!utilisateur && !utilisateur.peutEncaisser;
  // Pièce refusée par Sage : rien à encaisser tant qu'elle n'est pas corrigée (file d'envoi, Réessayer ou Abandonner).
  const interdit = pasCaissier || !!vente.refus;
  $("#encaissement-interdit").hidden = !interdit;
  $("#encaissement-interdit").textContent = vente.refus
    ? `Sage a refusé la pièce : ${vente.refus} Rien n'est encaissé. Corrigez puis renvoyez-la depuis la file d'envoi (Réglages).`
    : pasCaissier
      ? `${nomUtilisateur(utilisateur)} n'est pas caissier dans Sage : la commande est enregistrée, l'encaissement sera fait par un caissier dans Sage.`
      : "";
  for (const m of interdit ? [] : catalogue.modesReglement) {
    zone.append(element("button", {
      type: "button", class: modeChoisi === m.intitule ? "mode actif" : "mode", disabled: !!vente.envoiEnCours,
      onclick: () => choisirMode(m.intitule),
    }, m.intitule));
  }

  const ul = $("#paiements");
  ul.replaceChildren();
  for (const p of vente.paiements) {
    ul.append(element("li", {}, element("span", {}, `${p.mode}${p.reference ? " · " + p.reference : ""}`), element("strong", {}, euros.format(p.montant))));
  }
  $("#btn-terminer").textContent = t.reste > 0 ? (t.paye > 0 ? "Terminer (paiement partiel)" : "Terminer sans encaisser") : "Terminer";
  $("#btn-terminer").disabled = !!vente.envoiEnCours;
}

function choisirMode(mode) {
  if (!vente || vente.envoiEnCours) return;
  modeChoisi = mode;
  const especes = MODES_ESPECES.test(mode);
  $("#saisie-paiement").hidden = false;
  $("#lbl-reference").hidden = especes;
  $("#p-reference").value = "";
  $("#p-montant").value = totaux().reste.toFixed(2);
  majRendu();
  dessinerPaiement();
  $("#p-montant").focus();
  $("#p-montant").select();
}

function majRendu() {
  const recu = Number($("#p-montant").value) || 0;
  const rendu = arrondi(recu - totaux().reste);
  $("#p-rendu").textContent = MODES_ESPECES.test(modeChoisi || "") && rendu > 0 ? `Monnaie à rendre : ${euros.format(rendu)}` : "";
}

let encaissementEnCours = false;

async function encaisser() {
  if (encaissementEnCours) return;
  encaissementEnCours = true;
  try { await enregistrerEncaissement(); } finally { encaissementEnCours = false; }
}

async function enregistrerEncaissement() {
  const recu = arrondi(Number($("#p-montant").value) || 0);
  const t = totaux();
  if (recu <= 0) return bandeau("Saisissez un montant.", "erreur");
  // En espèces, on n'enregistre que ce qui reste dû : le surplus est rendu au client.
  const montant = MODES_ESPECES.test(modeChoisi) ? Math.min(recu, t.reste || recu) : recu;
  const reference = $("#p-reference").value.trim() || null;
  const n = vente.paiements.length + 1;
  const id = `${vente.id}-P${n}`;
  await ajouterOperation({
    cle: `encaissement:${id}`,
    type: "encaissement",
    idExterne: id,
    // Commande de cette vente, commande encore dans la file, ou pièce Sage existante.
    ...(vente.piece ? { piece: vente.piece } : { idCommande: vente.idCommande || vente.id }),
    corps: { idExterne: id, mode: modeChoisi, montant, referencePaiement: reference },
    vente: { numero: vente.numero, client: vente.client.intitule || vente.client.numero },
    ...auteur(),
  });
  vente.paiements.push({ mode: modeChoisi, montant, reference });
  modeChoisi = null;
  $("#saisie-paiement").hidden = true;
  bandeau("");
  synchroniserPuisAfficher();
  // Tout est réglé : la caisse passe directement au ticket suivant.
  if (totaux().reste <= 0.005) return terminer();
  dessinerPaiement();
}

/**
 * Le client change d'avis sur l'écran d'encaissement : retour au ticket pour le corriger.
 * Pièce déjà créée dans Sage : elle est supprimée (rien n'y est encaissé), puis recréée au prochain « Régler ».
 * Pièce encore en file (hors ligne) ou refusée par Sage : elle est simplement retirée de la file.
 */
async function modifierTicket() {
  const v = vente;
  if (!v || v.existante || v.paiements.length > 0 || v.envoiEnCours || v.suppressionEnCours) return;
  const cle = `commande:${v.id}`;
  v.suppressionEnCours = true;
  dessinerPaiement();
  try {
    await attendreFinEnvoi(); // la pièce ne doit pas partir pendant qu'on la retire
    const op = (await lireFile()).find((o) => o.cle === cle);
    const piece = op?.statut === "ok" ? op.resultat?.piece : null;
    if (piece) {
      if (!confirm(`${piece} est déjà dans Sage. Elle sera supprimée, puis recréée corrigée quand vous réglerez. Continuer ?`)) return;
      bandeau(`Suppression de ${piece} dans Sage…`);
      let r;
      try { r = await appeler("DELETE", `/commandes/${encodeURIComponent(v.id)}`, null, 90000, utilisateur?.jeton || null); }
      catch { return bandeau(`Serveur injoignable : ${piece} reste dans Sage. Réessayez, ou terminez la vente.`, "erreur"); }
      if (r.statut !== 200) {
        const msg = r.donnees?.message || (r.donnees?.erreurs || []).join(" ") || `code ${r.statut}`;
        return bandeau(`Sage n'a pas supprimé ${piece} : ${msg}`, "erreur");
      }
    }
    if (op) await supprimerOperation(cle);
    Object.assign(v, { validee: false, id: null, piece: null, sage: null, refus: null, estimation: null });
    modeChoisi = null;
    $("#saisie-paiement").hidden = true;
    afficher("vente");
    bandeau(piece ? `${piece} supprimée de Sage : corrigez le ticket puis réglez.` : "Ticket rouvert : corrigez-le puis réglez.", "ok");
    majEtat();
  } finally {
    v.suppressionEnCours = false;
    if (vente === v && !$("#ecran-paiement").hidden) dessinerPaiement();
  }
}

/**
 * Fin de la vente : la caisse ouvre aussitôt un ticket vide pour le même client (pas d'écran « Nouvelle vente »).
 * Le ticket de la vente terminée reste imprimable (bouton 🖨) tant que le nouveau ticket est vide.
 */
function terminer() {
  if (!vente || vente.envoiEnCours || vente.suppressionEnCours) return;
  const t = totaux();
  dernierTicket = ticketAImprimer();
  const fini = vente;
  // Une pièce existante (À encaisser) ne change pas le client des ventes de la caisse.
  const client = fini.existante ? dernierClient : fini.client;
  vente = null;
  modeChoisi = null;
  $("#saisie-paiement").hidden = true;
  nouvelleVente(client);
  const etat = t.reste > 0.005 ? `reste ${euros.format(t.reste)}` : "réglée";
  bandeau(`${[fini.piece, fini.numero].filter(Boolean).join(" · ")} ${etat}${connexion === "sage" ? "" : " · envoi à Sage au retour du serveur"}. Ticket suivant : ${client?.intitule || client?.numero || "choisissez le client"}.`, "ok");
}

function nouvelleVente(client = null) {
  if (connexionExigee() && !utilisateur) return afficher("connexion");
  // Première vente sur cette tablette : choisir d'abord la pièce, la souche et le dépôt.
  if (!reglagesSaisie().saisieReglee) return afficher("saisie");
  changementClient = false;
  // Le client n'est demandé qu'à la première vente : ensuite, le ticket suivant garde le dernier client.
  const c = (client?.numero ? client : null) || dernierClient || clientDefaut();
  if (c) demarrerVente(c);
  else afficher("client");
}

// ---------- Tickets en attente ----------

function mettreEnAttente() {
  if (!vente || vente.validee || vente.lignes.size === 0) return;
  ecrireAttente([...lireAttente(), {
    id: nouvelIdVente(lireReglages().borne),
    client: vente.client.numero,
    intitule: vente.client.intitule || vente.client.numero,
    lignes: [...vente.lignes.values()].map((l) => ({
      reference: l.article.reference, quantite: l.quantite,
      ...(l.enumere ? { gamme1: l.enumere.gamme1, gamme2: l.enumere.gamme2 || null } : {}),
      ...(l.cond ? { conditionnement: l.cond.numero } : {}),
    })),
    totalTtcEstime: totaux().ttc,
    utilisateur: utilisateur?.utilisateur || null,
    creeLe: Date.now(),
  }]);
  bandeau("Ticket mis en attente.", "ok");
  vente = null;
  nouvelleVente();
}

function ouvrirAttente() {
  const tickets = lireAttente();
  const contenu = tickets.length === 0
    ? [element("p", { class: "vide" }, "Aucun ticket en attente.")]
    : [element("ul", { class: "liste-clients liste-attente" }, ...tickets.slice().reverse().map((t) => element("li", {},
      element("button", { type: "button", onclick: () => rappelerTicket(t.id) },
        element("strong", {}, `${t.intitule} · ${euros.format(t.totalTtcEstime)}`),
        element("span", { class: "discret" },
          ` ${new Date(t.creeLe).toLocaleString("fr-FR", { dateStyle: "short", timeStyle: "short" })} · ${t.lignes.length} ligne(s)${t.utilisateur ? " · " + t.utilisateur : ""}`)),
      element("button", { type: "button", class: "danger petit", "aria-label": "Supprimer ce ticket", onclick: () => supprimerAttente(t.id) }, "✕"))))];
  ouvrirListe("Tickets en attente", contenu, false);
}

function supprimerAttente(id) {
  if (!confirm("Supprimer ce ticket en attente ?")) return;
  ecrireAttente(lireAttente().filter((t) => t.id !== id));
  majRaccourcis();
  ouvrirAttente();
}

function rappelerTicket(id) {
  const t = lireAttente().find((x) => x.id === id);
  if (!t) return;
  if (vente && !vente.validee && vente.lignes.size > 0 && !confirm("Le ticket en cours sera remplacé. Continuer ?")) return;
  const client = catalogue.clients.find((c) => c.numero === t.client) || { numero: t.client, intitule: t.intitule };
  vente = { id: null, numero: null, client, lignes: new Map(), paiements: [], validee: false };
  ligneChoisie = null;
  const manquants = [];
  for (const l of t.lignes) {
    const article = catalogue.articles.find((a) => a.reference === l.reference);
    const enumere = l.gamme1 ? enumeres(article || {}).find((e) => memeValeur(e, { article: l.reference, gamme1: l.gamme1, gamme2: l.gamme2 })) : null;
    const cond = l.conditionnement != null && article ? conditionnementsDe(catalogue, article).find((c) => c.numero === l.conditionnement) : null;
    if (!article || (l.gamme1 && !enumere) || (l.conditionnement != null && !cond)) { manquants.push(l.reference); continue; }
    const cle = cleLigne(article, enumere, cond);
    vente.lignes.set(cle, { article, enumere, cond, quantite: l.quantite });
    ligneChoisie = cle;
  }
  ecrireAttente(lireAttente().filter((x) => x.id !== id));
  $("#dialogue-liste").close();
  bandeau(manquants.length ? `Articles absents du catalogue, non repris : ${manquants.join(", ")}.` : "", "erreur");
  afficher("vente");
}

// ---------- X de caisse : ce que cette borne a enregistré aujourd'hui ----------

async function ouvrirX() {
  const debut = new Date();
  debut.setHours(0, 0, 0, 0);
  const ops = (await lireFile()).filter((o) => o.creeLe >= debut.getTime() && o.statut !== "erreur");
  const commandes = ops.filter((o) => o.type === "commande");
  const encaissements = ops.filter((o) => o.type === "encaissement");
  const parMode = new Map();
  for (const o of encaissements) {
    const m = parMode.get(o.corps.mode) || { nombre: 0, montant: 0 };
    m.nombre++;
    m.montant = arrondi(m.montant + o.corps.montant);
    parMode.set(o.corps.mode, m);
  }
  const totalEncaisse = arrondi(encaissements.reduce((s, o) => s + o.corps.montant, 0));
  const totalCommandes = arrondi(commandes.reduce((s, o) => s + (o.vente?.totalTtcEstime || 0), 0));
  const enAttente = ops.filter((o) => o.statut === "attente").length;
  const ligne = (a, b, c) => element("tr", {}, element("td", {}, a), element("td", {}, b), element("td", { class: "montant" }, c));
  const contenu = [
    element("p", { class: "discret" }, `Borne ${lireReglages().borne} · ${new Date().toLocaleString("fr-FR", { dateStyle: "full", timeStyle: "short" })}`),
    element("table", { class: "tableau" },
      element("thead", {}, element("tr", {}, element("th", {}, ""), element("th", {}, "Nombre"), element("th", { class: "montant" }, "Montant"))),
      element("tbody", {},
        ligne("Ventes enregistrées (TTC estimé)", String(commandes.length), euros.format(totalCommandes)),
        ...[...parMode].map(([mode, m]) => ligne(mode, String(m.nombre), euros.format(m.montant))),
        ligne(element("strong", {}, "Total encaissé"), String(encaissements.length), element("strong", {}, euros.format(totalEncaisse))))),
    element("p", { class: "discret" }, enAttente
      ? `${enAttente} opération(s) pas encore envoyée(s) à Sage. Les montants exacts sont ceux de Sage.`
      : "Tout est envoyé à Sage. Les montants exacts sont ceux de Sage."),
  ];
  ouvrirListe("X de caisse", contenu, true);
}

function ouvrirListe(titre, contenu, imprimable) {
  $("#liste-titre").textContent = titre;
  $("#liste-contenu").replaceChildren(...contenu);
  $("#btn-liste-imprimer").hidden = !imprimable;
  const d = $("#dialogue-liste");
  if (!d.open) d.showModal();
}

// ---------- Impression (imprimante ticket du navigateur ou PDF) ----------

function ticketAImprimer() {
  if (!vente) return null;
  const t = totaux();
  // Pièce dans Sage : ses lignes et ses montants ; sinon les prix de la borne (estimation).
  const lignesSage = vente.sage?.lignes?.filter((l) => l.article || l.designation);
  return {
    numero: [vente.piece, vente.numero].filter(Boolean).join(" · ") || "Ticket en cours", client: vente.client.intitule || vente.client.numero,
    vendeur: nomUtilisateur(utilisateur), date: new Date(), existante: !!vente.existante, sage: t.sage,
    document: vente.existante ? null : TYPES[vente.typeDocument || parametres().typeDocument].libelle,
    lignes: lignesSage
      ? lignesSage.map((l) => ({
        libelle: [l.designation || l.article, l.gamme1, l.gamme2].filter(Boolean).join(" "), quantite: l.quantite, unite: "",
        prix: l.quantite ? arrondi(l.montantHT / l.quantite) : l.prixUnitaireHT, remise: "", montant: l.montantHT,
      }))
      : [...vente.lignes.values()].map((l) => {
        const p = tarif(l);
        return {
          libelle: `${l.article.designation || l.article.reference}${l.enumere ? " " + libelleGamme(l.enumere) : ""}`,
          quantite: l.quantite, unite: libelleUnite(l), prix: arrondi(enHT(p.prix, p.ttc, tauxTva(l.article))), remise: texteRemises(p.remises),
          montant: montantLigne(l),
        };
      }),
    ...t, paiements: vente.paiements.slice(),
  };
}

function imprimer(ticket) {
  if (!ticket) return;
  const ligne = (a, b) => element("div", { class: "ligne" }, element("span", {}, a), element("span", {}, b));
  $("#impression").replaceChildren(
    element("h3", {}, `Borne ${lireReglages().borne}`),
    element("p", {}, ticket.date.toLocaleString("fr-FR")),
    element("p", {}, [ticket.document, ticket.numero].filter(Boolean).join(" ")),
    element("p", {}, `Client : ${ticket.client}`),
    ticket.vendeur ? element("p", {}, `Vendeur : ${ticket.vendeur}`) : null,
    element("hr"),
    ...ticket.lignes.map((l) => element("div", {},
      element("div", {}, l.libelle),
      ligne(`  ${String(l.quantite).replace(".", ",")}${l.unite ? " " + l.unite : ""} x ${euros.format(l.prix)}${l.remise ? " " + l.remise : ""}`, euros.format(l.montant)))),
    element("hr"),
    ticket.existante ? null : ligne("Total HT", euros.format(ticket.ht)),
    ticket.existante || !(ticket.escompte > 0) ? null : ligne("Escompte", `-${euros.format(ticket.escompte)}`),
    ticket.existante ? null : ligne(ticket.sage ? "TVA" : "TVA estimée", euros.format(ticket.tva)),
    ligne(ticket.existante ? "TOTAL TTC" : ticket.sage ? "NET À PAYER" : "TOTAL TTC estimé", euros.format(ticket.ttc)),
    ...ticket.paiements.map((p) => ligne(p.mode, euros.format(p.montant))),
    ticket.paiements.length ? ligne("Reste à payer", euros.format(ticket.reste)) : null,
    element("hr"),
    element("p", {}, "Document non fiscal. La facture est établie dans Sage."));
  window.print();
}

function imprimerListe() {
  $("#impression").replaceChildren(element("h3", {}, $("#liste-titre").textContent), $("#liste-contenu").cloneNode(true));
  window.print();
}

// ---------- Verrouillage : l'écran revient à la connexion, le ticket en cours est gardé pour la personne qui se reconnecte ----------

function verrouiller() {
  if (!connexionExigee() || !utilisateur) return;
  const login = utilisateur.utilisateur;
  utilisateur = null;
  ecrireSession(null);
  majUtilisateur();
  bandeau(`Caisse verrouillée : ${login} doit se reconnecter.`, "info");
  $("#form-connexion").elements.utilisateur.value = login;
  afficher("connexion");
}

// ---------- Pièces déjà enregistrées, à encaisser ----------
// Bons de commande et de livraison, factures et factures comptabilisées de Sage (saisis dans Sage, ou pris sur une borne)
// avec un reste à payer, et ventes de cette borne pas encore
// envoyées. Hors ligne : la liste du dernier catalogue. Les encaissements encore en file sont déduits du reste.

/** Type de pièce Sage (DO_Type) en abrégé, pour la liste « À encaisser ». */
const TYPES_PIECE = { 1: "BC", 3: "BL", 6: "Facture", 7: "Facture compta." };
const TYPE_LOCAL = { commande: "BC", livraison: "BL", facture: "Facture" };

const peutEncaisser = () => !catalogue?.exigerCaissier || !!utilisateur?.peutEncaisser;

function majBoutonCommandes() {
  $("#btn-commandes").hidden = !catalogue || !peutEncaisser() || (connexionExigee() && !utilisateur)
    || !$("#ecran-commandes").hidden || !$("#ecran-paiement").hidden;
}

let rechercheCommandes = 0;

async function dessinerCommandes() {
  const numero = ++rechercheCommandes; // une frappe plus récente remplace la recherche en cours
  const q = $("#recherche-commande").value.trim();
  let sage = null;
  if (connexion !== "hors-ligne") {
    try { sage = await commandesOuvertes(q); } catch { /* liste du catalogue */ }
  }
  const depuisCatalogue = sage == null;
  if (depuisCatalogue) {
    const m = q.toLowerCase();
    sage = (catalogue.commandesOuvertes || []).filter((c) => !m
      || [c.piece, c.client, c.intitule, c.reference].some((v) => (v || "").toLowerCase().includes(m)));
  }
  const ops = await lireFile();
  if (numero !== rechercheCommandes) return;

  const enFile = ops.filter((o) => o.type === "encaissement" && o.statut !== "ok");
  const enAttente = (piece, idCommande) => arrondi(enFile
    .filter((o) => (piece && o.piece === piece) || (idCommande && o.idCommande === idCommande))
    .reduce((s, o) => s + o.corps.montant, 0));
  const locales = ops.filter((o) => o.type === "commande" && o.statut === "attente").map((o) => ({
    idCommande: o.idExterne, numero: o.vente?.numero || o.idExterne, client: o.corps.client, intitule: o.vente?.client,
    reference: o.vente?.numero, date: o.creeLe, totalTTC: o.vente?.totalTtcEstime || 0, netAPayer: o.vente?.totalTtcEstime || 0,
    type: TYPE_LOCAL[o.vente?.typeDocument || o.corps.typeDocument] || "BC",
    dejaRegle: enAttente(null, o.idExterne), local: true, operation: o,
  }));
  const deSage = sage.map((c) => ({
    piece: c.piece, numero: c.piece, client: c.client, intitule: c.intitule, reference: c.reference, date: c.date,
    type: TYPES_PIECE[c.typePiece ?? 1] || "BC",
    totalTTC: c.totalTTC, netAPayer: c.netAPayer ?? c.totalTTC, dejaRegle: arrondi(c.dejaRegle + enAttente(c.piece, c.idExterne)),
  }));
  const m = q.toLowerCase();
  const commandes = [...locales.filter((c) => !m || [c.numero, c.client, c.intitule].some((v) => (v || "").toLowerCase().includes(m))), ...deSage]
    .filter((c) => c.totalTTC - c.dejaRegle > 0.005);

  $("#info-commandes").textContent = depuisCatalogue
    ? `Hors ligne : commandes connues au ${new Date(catalogue.genereLe).toLocaleString("fr-FR")}.`
    : "";
  const zone = $("#liste-commandes");
  const enListe = lireAffichage("commandes") === "liste";
  zone.className = enListe ? "liste-commandes en-liste" : "liste-commandes en-boutons";
  if (commandes.length === 0) return zone.replaceChildren(element("p", { class: "vide" }, "Aucune pièce à encaisser."));
  const date = (c) => (c.date ? new Date(c.date).toLocaleDateString("fr-FR") : "");
  const reste = (c) => euros.format(arrondi(c.totalTTC - c.dejaRegle));
  const loupe = (c) => element("button", {
    type: "button", class: "loupe", title: "Voir le contenu de la pièce", "aria-label": `Voir le contenu de ${c.numero}`,
    onclick: (e) => { e.stopPropagation(); consulterCommande(c); },
  }, "🔍");
  const liste = commandes.slice(0, 200);
  if (enListe) {
    zone.replaceChildren(tableau(
      [{ titre: "Date", classe: "date" }, { titre: "Type", classe: "type" }, { titre: "N° pièce", classe: "code" }, { titre: "Référence", classe: "secondaire" },
        { titre: "Code client", classe: "code" }, { titre: "Client", classe: "secondaire" }, { titre: "Net à payer", classe: "nombre" },
        { titre: "Reste", classe: "nombre reste" }, { titre: "", classe: "action" }],
      liste.map((c) => ({
        classe: c.local ? "commande locale" : "commande", attributs: { "data-commande": c.numero },
        cellules: [date(c), element("span", { class: "type-piece" }, c.type), element("span", {}, c.numero, c.local ? element("span", { class: "etiquette" }, "pas encore dans Sage") : null),
          c.reference && c.reference !== c.numero ? c.reference : "", c.client, c.intitule || "", euros.format(c.netAPayer), reste(c), loupe(c)],
        onclick: () => encaisserCommande(c),
      }))));
  } else {
    zone.replaceChildren(...liste.map((c) => element("div", { class: c.local ? "carte-commande locale" : "carte-commande", "data-commande": c.numero },
      element("button", { type: "button", class: "corps", onclick: () => encaisserCommande(c) },
        element("span", { class: "haut" }, element("strong", {}, element("span", { class: "type-piece" }, c.type), " ", c.numero), element("span", { class: "discret" }, date(c))),
        element("span", {}, `${c.client} · ${c.intitule || ""}`),
        element("span", { class: "discret" }, [c.local ? "pas encore dans Sage" : null, c.reference && c.reference !== c.numero ? `Réf. ${c.reference}` : null].filter(Boolean).join(" · ")),
        element("span", { class: "montants-commande" },
          element("span", {}, `Net à payer ${euros.format(c.netAPayer)}`), element("span", { class: "reste" }, `Reste ${reste(c)}`))),
      loupe(c))));
  }
}

/** Loupe : contenu de la pièce. Bon Sage : lu dans Sage (serveur joignable) ; commande de la borne : depuis la file. */
async function consulterCommande(c) {
  const titre = `${c.numero} · ${c.intitule || c.client}`;
  const encaisser = element("button", { type: "button", class: "principal", onclick: () => { $("#dialogue-liste").close(); encaisserCommande(c); } },
    `Encaisser (reste ${euros.format(arrondi(c.totalTTC - c.dejaRegle))})`);
  const entete = (date, reference) => element("p", { class: "discret" },
    [date ? new Date(date).toLocaleDateString("fr-FR") : null, `Client ${c.client}`, reference ? `Réf. ${reference}` : null].filter(Boolean).join(" · "));
  const tableauLignes = (lignes) => element("table", { class: "tableau" },
    element("thead", {}, element("tr", {}, element("th", {}, "Article"), element("th", {}, "Désignation"),
      element("th", { class: "nombre" }, "Qté"), element("th", { class: "nombre" }, "PU HT"), element("th", { class: "nombre" }, "Montant HT"))),
    element("tbody", {}, ...lignes.map((l) => element("tr", {},
      element("td", {}, l.article || ""), element("td", {}, l.designation || "", l.gamme ? ` · ${l.gamme}` : ""),
      element("td", { class: "nombre" }, l.article ? quantiteTexte(l.quantite) : ""),
      element("td", { class: "nombre" }, l.article ? euros.format(l.prix) : ""),
      element("td", { class: "nombre" }, l.article ? euros.format(l.montant) : "")))));
  const totaux = (lignes) => element("div", { class: "totaux-piece" }, ...lignes.map(([libelle, valeur, fort]) =>
    element("div", { class: fort ? "fort" : "" }, element("span", {}, libelle), element("span", {}, valeur))));

  if (c.local) {
    const client = catalogue.clients.find((x) => x.numero === c.client) || { numero: c.client };
    const lignes = c.operation.corps.lignes.map((l) => {
      const a = catalogue.articles.find((x) => x.reference === l.article);
      const cond = l.conditionnement && a ? conditionnementsDe(catalogue, a).find((x) => x.enumere === l.conditionnement && x.quantite === l.quantiteConditionnement) : null;
      const p = a ? prixLigne(catalogue, client, a, { enumere: l.gamme1 ? { gamme1: l.gamme1, gamme2: l.gamme2 } : null, conditionnement: cond, quantite: l.quantite }) : null;
      const prix = p ? arrondi(prixNetHT(p)) : 0;
      return { article: l.article, designation: [a?.designation || l.article, l.conditionnement].filter(Boolean).join(" · "),
        gamme: [l.gamme1, l.gamme2].filter(Boolean).join(" / "), quantite: l.quantite, prix, montant: arrondi(prix * l.quantite) };
    });
    return ouvrirListe(titre, [entete(c.date, null), element("p", { class: "info" }, "Pas encore envoyée à Sage : prix du tarif du client sur la borne, TTC estimé."),
      tableauLignes(lignes),
      totaux([["Total TTC estimé", euros.format(c.totalTTC)], ["Déjà encaissé", euros.format(c.dejaRegle)], ["Reste à payer", euros.format(arrondi(c.totalTTC - c.dejaRegle)), true]]),
      encaisser], false);
  }

  ouvrirListe(titre, [element("p", { class: "discret" }, "Lecture de la pièce dans Sage…")], false);
  let d;
  try {
    d = connexion === "hors-ligne" ? undefined : await detailCommande(c.piece);
  } catch { d = undefined; }
  if (!$("#dialogue-liste").open || $("#liste-titre").textContent !== titre) return; // fermée entre-temps
  if (d === undefined) {
    return ouvrirListe(titre, [entete(c.date, c.reference), element("p", { class: "info" }, "Le contenu de la pièce se lit dans Sage : il s'affichera quand le serveur répondra."),
      totaux([["Net à payer", euros.format(c.netAPayer)], ["Reste à payer", euros.format(arrondi(c.totalTTC - c.dejaRegle)), true]]), encaisser], false);
  }
  if (d === null) return ouvrirListe(titre, [element("p", { class: "info" }, "Cette pièce n'est plus un bon de commande dans Sage (livrée ou supprimée).")], false);
  const e = d.entete;
  ouvrirListe(titre, [entete(e.date, e.reference),
    tableauLignes(d.lignes.map((l) => ({ article: l.article, designation: l.designation, gamme: [l.gamme1, l.gamme2].filter(Boolean).join(" / "),
      quantite: l.quantite, prix: l.prixUnitaireHT, montant: l.montantHT }))),
    totaux([["Total HT", euros.format(d.totalHT)], ["Total TTC", euros.format(e.totalTTC)], ["Net à payer", euros.format(e.netAPayer ?? e.totalTTC)],
      ["Acomptes déjà réglés", euros.format(c.dejaRegle)], ["Reste à payer", euros.format(arrondi(c.totalTTC - c.dejaRegle)), true]]),
    encaisser], false);
}

function ouvrirCommandes() {
  if (vente && !vente.existante && (vente.lignes.size > 0 && !vente.validee) && !confirm("Abandonner la commande en cours ?")) return;
  vente = null;
  bandeau("");
  afficher("commandes");
}

function encaisserCommande(c) {
  const client = catalogue.clients.find((x) => x.numero === c.client) || { numero: c.client, intitule: c.intitule || c.client };
  vente = {
    id: nouvelIdVente(lireReglages().borne), numero: c.numero, client, lignes: new Map(), paiements: [], validee: true,
    existante: true, piece: c.piece || null, idCommande: c.idCommande || null, totalTTC: c.totalTTC, dejaRegle: c.dejaRegle,
  };
  modeChoisi = null;
  $("#saisie-paiement").hidden = true;
  afficher("paiement");
}

// ---------- Connexion des utilisateurs (login Sage) ----------

// Avec plusieurs sociétés, on se connecte toujours : c'est la connexion qui choisit la société.
const connexionExigee = () => !!catalogue?.authentification || plusieursSocietes();
const nomUtilisateur = (u) =>
  u?.collaborateur ? [u.collaborateur.prenom, u.collaborateur.nom].filter(Boolean).join(" ") : u?.utilisateur || "";
/** Utilisateur à joindre à chaque opération de la file : elle partira avec sa connexion, même plus tard. */
const auteur = () => (utilisateur ? { utilisateur: utilisateur.utilisateur, jeton: utilisateur.jeton } : {});

function majUtilisateur() {
  const b = $("#utilisateur");
  b.hidden = !connexionExigee() || !utilisateur;
  b.textContent = utilisateur ? `👤 ${nomUtilisateur(utilisateur)}` : "";
}

/** Nom de la borne en tête d'écran, suivi de la société quand le serveur en sert plusieurs. */
function majNomBorne() {
  $("#nom-borne").textContent = [lireReglages().borne, plusieursSocietes() && lireDossier() ? intituleDossier() : null].filter(Boolean).join(" · ");
}

function dessinerConnexion() {
  const f = $("#form-connexion");
  f.elements.motDePasse.value = "";
  // Société : liste affichée seulement quand le serveur en sert plusieurs ; la dernière choisie est proposée.
  const dossiers = lireDossiers();
  $("#choix-dossier").hidden = dossiers.length < 2;
  const choisi = f.elements.dossier.value || lireDossier() || dossiers[0]?.code || "";
  f.elements.dossier.replaceChildren(...dossiers.map((d) => element("option", { value: d.code }, d.intitule || d.code)));
  f.elements.dossier.value = choisi;
  dessinerConnus();
  (f.elements.utilisateur.value ? f.elements.motDePasse : f.elements.utilisateur).focus();
}

function dessinerConnus() {
  const f = $("#form-connexion");
  // Les utilisateurs déjà connectés sur cette borne (dans la société choisie) : un appui remplit le nom.
  const connus = loginsConnus(plusieursSocietes() ? f.elements.dossier.value : "");
  $("#utilisateurs-connus").replaceChildren(...connus.map((login) => element("button", {
    type: "button", class: "puce",
    onclick: () => { f.elements.utilisateur.value = login; f.elements.motDePasse.focus(); },
  }, login)));
}

async function connecter(ev) {
  ev.preventDefault();
  const f = ev.target.elements;
  const bouton = $("#btn-connexion");
  bouton.disabled = true;
  bouton.textContent = "Connexion…";
  const dossier = plusieursSocietes() ? f.dossier.value : "";
  const autreSociete = dossier !== lireDossier();
  if (autreSociete && vente && (vente.lignes.size > 0 || vente.validee)
      && !confirm("Changer de société ? Le ticket en cours sera abandonné sur cette borne.")) {
    bouton.disabled = false;
    bouton.textContent = "Se connecter";
    return;
  }
  try {
    const profil = await seConnecter(f.utilisateur.value, f.motDePasse.value, dossier);
    if (autreSociete) {
      // Nouvelle société : son catalogue, ses paramètres de saisie, ses tickets ; rien de l'ancienne n'est repris.
      ecrireDossier(dossier);
      vente = null;
      catalogue = null;
      dernierClient = null;
      dernierTicket = null;
      majNomBorne();
    }
    utilisateur = profil;
    ecrireSession(utilisateur.utilisateur);
    f.motDePasse.value = "";
    majUtilisateur();
    bandeau(utilisateur.horsLigne ? "Connecté hors ligne : les ventes partiront vers Sage au retour du serveur." : "", "info");
    if (autreSociete) {
      await chargerCatalogue(!utilisateur.horsLigne);
      if (!catalogue) {
        bandeau("Aucun catalogue de cette société sur la borne : elle doit joindre le serveur une première fois.", "erreur");
        afficher("reglages");
        return;
      }
    }
    // Après un verrouillage, on retrouve le ticket ou l'encaissement en cours.
    revenirALaVente();
    synchroniserPuisAfficher();
  } catch (e) {
    bandeau(e.message, "erreur");
    f.motDePasse.select();
  } finally {
    bouton.disabled = false;
    bouton.textContent = "Se connecter";
  }
}

function deconnecter() {
  if (vente && (vente.lignes.size > 0 || vente.validee)
      && !confirm(vente.validee ? "Changer d'utilisateur ? La commande est enregistrée ; son encaissement s'arrête ici." : "Changer d'utilisateur ? La commande en cours sera perdue.")) return;
  vente = null;
  utilisateur = null;
  ecrireSession(null);
  majUtilisateur();
  bandeau("");
  afficher("connexion");
}

// ---------- 4. Réglages et file ----------

function dessinerReglages() {
  const r = lireReglages();
  const f = $("#form-reglages");
  for (const nom of ["borne", "cle", "clientDefaut"]) f.elements[nom].value = r[nom] ?? "";
  f.elements.demanderQuantite.checked = r.demanderQuantite !== false;
  $("#info-catalogue").textContent = catalogue
    ? `Catalogue du ${new Date(catalogue.genereLe).toLocaleString("fr-FR")} : ${catalogue.clients.length} clients, ${catalogue.articles.length} articles.`
    : "Aucun catalogue sur la borne.";
  dessinerFile();
}

async function dessinerFile() {
  const ul = $("#file");
  ul.replaceChildren();
  const ops = (await lireFile()).reverse();
  for (const op of ops) {
    const societe = plusieursSocietes() && op.dossier ? `[${intituleDossier(op.dossier)}] ` : "";
    const titre = societe + (op.type === "commande"
      ? `${TYPES[op.corps.typeDocument]?.libelle || "Commande"} ${op.vente?.numero || op.idExterne} · ${op.vente?.client || op.corps.client}`
      : `Encaissement ${op.corps.mode} ${euros.format(op.corps.montant)} · ${op.vente?.numero || op.idCommande}`);
    const etat = op.statut === "ok"
      ? (op.type === "commande" ? `Dans Sage : ${op.resultat?.piece}, net à payer ${euros.format(op.resultat?.netAPayer ?? 0)}` : "Dans Sage")
      : op.statut === "erreur" ? `Refusé : ${op.message}` : `En attente${op.message ? " · " + op.message : ""}`;
    ul.append(element("li", { class: `op ${op.statut}` },
      element("div", {}, element("strong", {}, titre), element("span", { class: "discret" }, etat)),
      op.statut === "erreur" ? element("div", { class: "boutons" },
        element("button", { type: "button", class: "secondaire", onclick: () => reessayer(op) }, "Réessayer"),
        element("button", { type: "button", class: "danger", onclick: () => abandonner(op) }, "Abandonner")) : null));
  }
  if (ops.length === 0) ul.append(element("li", { class: "vide" }, "Rien à envoyer."));
}

async function reessayer(op) {
  op.statut = "attente";
  op.message = null;
  await majOperation(op);
  await synchroniserPuisAfficher();
  dessinerFile();
}

async function abandonner(op) {
  const lies = op.type === "commande" ? (await lireFile()).filter((o) => o.idCommande === op.idExterne && o.statut !== "ok") : [];
  const msg = lies.length
    ? `Abandonner cette commande et ses ${lies.length} encaissement(s) ? L'argent déjà reçu devra être rendu ou saisi à la main dans Sage.`
    : "Abandonner cette opération ? Elle ne sera jamais envoyée à Sage.";
  if (!confirm(msg)) return;
  for (const o of [op, ...lies]) await supprimerOperation(o.cle);
  await majEtat();
  dessinerFile();
}

async function enregistrerReglages(ev) {
  ev.preventDefault();
  const f = ev.target.elements;
  ecrireReglages({
    ...lireReglages(),
    borne: f.borne.value.trim().toUpperCase(),
    cle: f.cle.value.trim(),
    clientDefaut: f.clientDefaut.value.trim().toUpperCase(),
    demanderQuantite: f.demanderQuantite.checked,
  });
  bandeau("Réglages enregistrés.", "ok");
  await chargerDossiers();
  majNomBorne();
  if (plusieursSocietes() && !lireDossier()) return afficher("connexion");
  await chargerCatalogue(true);
  dessinerReglages();
}

// ---------- Connexion, catalogue et synchronisation ----------

async function chargerCatalogue(forcer = false) {
  // Plusieurs sociétés et aucune choisie : le catalogue viendra après la connexion.
  if (plusieursSocietes() && !lireDossier()) return;
  try {
    if (forcer || connexion !== "hors-ligne") {
      catalogue = await rechargerCatalogue();
      // Partie illisible côté serveur (tarifs, souches, dépôts...) : la borne marche, mais sans elle.
      if (catalogue.avertissements?.length) bandeau(`Catalogue incomplet : ${catalogue.avertissements.join(" ; ")}`, "erreur");
    }
  } catch (e) {
    // Sans ce message, la borne travaillerait sans le dire avec l'ancien catalogue (anciens prix, sans tarifs).
    if (forcer || connexion !== "hors-ligne") bandeau(`${e.message} La borne utilise le dernier catalogue reçu.`, "erreur");
  }
  catalogue ??= await lireCatalogue();
}

async function majEtat() {
  connexion = await etatConnexion();
  const ops = await lireFile();
  const attente = ops.filter((o) => o.statut === "attente").length;
  const erreurs = ops.filter((o) => o.statut === "erreur").length;
  const libelle = { sage: "Connecté à Sage", serveur: "Sage indisponible", "hors-ligne": "Hors ligne" }[connexion];
  const b = $("#etat");
  // Sur téléphone, seul le voyant et les compteurs restent visibles (le libellé complet est dans l'infobulle).
  const complet = libelle + (attente ? ` · ${attente} en attente` : "") + (erreurs ? ` · ${erreurs} refusée(s)` : "");
  b.title = complet;
  b.replaceChildren(element("span", { class: "libelle" }, complet),
    element("span", { class: "court" }, [attente ? `${attente} ⏳` : "", erreurs ? `${erreurs} ✕` : ""].filter(Boolean).join(" ")));
  b.className = `etat ${erreurs ? "erreur" : connexion}`;
}

async function synchroniserPuisAfficher() {
  await majEtat();
  if (connexion === "hors-ligne") return;
  const bilan = await synchroniser();
  if (bilan.cleRefusee) bandeau("La clé d'API est refusée par le serveur. Corrigez-la dans les réglages.", "erreur");
  else if (bilan.reconnexions.length)
    bandeau(`Connexion expirée pour ${bilan.reconnexions.join(", ")} : reconnectez-vous pour envoyer vos ventes à Sage.`, "erreur");
  await majEtat();
  if (!$("#ecran-reglages").hidden) dessinerFile();
}

// ---------- Démarrage ----------

function brancher() {
  $("#recherche-client").addEventListener("input", dessinerClients);
  $("#recherche-article").addEventListener("input", dessinerArticles);
  $("#recherche-article").addEventListener("keydown", (e) => {
    // Une douchette code-barres tape le code puis Entrée.
    if (e.key !== "Enter") return;
    const code = e.target.value.trim();
    // Le code-barres d'une valeur de gamme désigne directement l'article et sa valeur.
    const g = (catalogue.gammes || []).find((x) => x.codeBarre && x.codeBarre === code);
    // Le code-barres d'un conditionnement (carton...) désigne l'article et ce conditionnement.
    const c = g ? null : (catalogue.tarifs?.conditionnements || []).find((x) => (x.codeBarre && x.codeBarre === code) || (x.reference && x.reference === code.toUpperCase()));
    const a = catalogue.articles.find((x) => (g ? x.reference === g.article : c ? x.reference === c.article : x.codeBarre === code || x.reference === code.toUpperCase()));
    if (!a) return;
    if (g) ajouterArticle(a, 1, g);
    else if (c) ajouterArticle(a, prendreSaisie() ?? 1, null, c);
    else toucherArticle(a, false);
    e.target.value = "";
    dessinerArticles();
  });
  $("#btn-valider").addEventListener("click", () => validerCommande());
  $("#btn-fermer-gamme").addEventListener("click", () => $("#choix-gamme").close());
  $("#btn-annuler-vente").addEventListener("click", () => { if (vente.lignes.size === 0 || confirm("Annuler ce ticket ?")) { vente = null; nouvelleVente(); } });
  $("#btn-r-pave").addEventListener("click", () => basculerPave());
  appliquerPave();
  $("#form-quantite").addEventListener("submit", validerQuantite);
  $("#btn-qte-annuler").addEventListener("click", () => { choixQte = null; $("#choix-quantite").close(); });
  $("#qte-valeur").addEventListener("input", () => { if (choixQte) { choixQte.remplacer = false; dessinerQuantite(); } });
  for (const b of document.querySelectorAll("[data-chiffre]")) b.addEventListener("click", () => toucherMiniPave(b.dataset.chiffre));
  for (const b of document.querySelectorAll("[data-pas]")) b.addEventListener("click", () => pasQuantite(Number(b.dataset.pas)));
  $("#btn-supprimer-ligne").addEventListener("click", supprimerLigne);
  for (const b of document.querySelectorAll("[data-liste]")) b.addEventListener("click", () => changerAffichage(b.dataset.liste, b.dataset.mode));
  majBascules();
  $("#btn-retour-ticket").addEventListener("click", () => afficher("vente"));
  $("#btn-attente").addEventListener("click", mettreEnAttente);
  $("#btn-client-ticket").addEventListener("click", changerClient);
  $("#btn-r-client").addEventListener("click", changerClient);
  $("#btn-r-passage").addEventListener("click", clientDePassage);
  $("#btn-r-rappel").addEventListener("click", ouvrirAttente);
  $("#btn-r-x").addEventListener("click", ouvrirX);
  $("#btn-fermer-liste").addEventListener("click", () => $("#dialogue-liste").close());
  $("#btn-liste-imprimer").addEventListener("click", imprimerListe);
  // Ticket vide : on réimprime la vente qui vient de se terminer.
  $("#btn-imprimer").addEventListener("click", () => imprimer(vente && vente.lignes.size ? ticketAImprimer() : dernierTicket));
  $("#btn-verrouiller").addEventListener("click", verrouiller);
  $("#onglet-articles").addEventListener("click", () => basculerOnglet("articles"));
  $("#onglet-ticket").addEventListener("click", () => basculerOnglet("ticket"));
  for (const b of document.querySelectorAll("[data-touche]")) b.addEventListener("click", () => toucherPave(b.dataset.touche));
  $("#p-montant").addEventListener("input", majRendu);
  $("#btn-encaisser").addEventListener("click", encaisser);
  $("#btn-terminer").addEventListener("click", terminer);
  $("#btn-modifier-ticket").addEventListener("click", modifierTicket);
  $("#btn-reglages").addEventListener("click", () => afficher("reglages"));
  $("#btn-saisie").addEventListener("click", ouvrirSaisie);
  $("#btn-document-ticket").addEventListener("click", ouvrirSaisie);
  $("#form-saisie").addEventListener("submit", enregistrerSaisie);
  $("#btn-annuler-saisie").addEventListener("click", () => { choixSaisie = null; revenirALaVente(); });
  $("#etat").addEventListener("click", () => afficher("reglages"));
  $("#btn-fermer-reglages").addEventListener("click", revenirALaVente);
  $("#form-connexion").addEventListener("submit", connecter);
  $("#form-connexion").elements.dossier.addEventListener("change", dessinerConnus);
  $("#btn-commandes").addEventListener("click", ouvrirCommandes);
  $("#btn-fermer-commandes").addEventListener("click", () => nouvelleVente());
  $("#recherche-commande").addEventListener("input", dessinerCommandes);
  $("#utilisateur").addEventListener("click", deconnecter);
  $("#form-reglages").addEventListener("submit", enregistrerReglages);
  $("#btn-catalogue").addEventListener("click", async () => { await chargerCatalogue(true); dessinerReglages(); });
  $("#btn-synchroniser").addEventListener("click", synchroniserPuisAfficher);
  window.addEventListener("online", synchroniserPuisAfficher);
}

async function demarrer() {
  if ("serviceWorker" in navigator) navigator.serviceWorker.register("sw.js").catch(() => {});
  brancher();
  const r = lireReglages();
  await purgerFile();
  await majEtat();
  if (r.cle && connexion !== "hors-ligne") await chargerDossiers();
  majNomBorne();
  await chargerCatalogue();
  // La page peut être rechargée : l'utilisateur connecté le reste.
  const session = lireSession() ? utilisateurMemorise(lireSession())?.profil : null;
  utilisateur = session && new Date(session.expiration) > new Date() ? session : null;
  majUtilisateur();
  if (r.cle && plusieursSocietes() && !lireDossier()) {
    // Serveur à plusieurs sociétés : on choisit d'abord la société en se connectant.
    afficher("connexion");
  } else if (!r.cle || !catalogue) {
    bandeau(r.cle ? "Aucun catalogue : la borne doit joindre le serveur une première fois." : "Première utilisation : saisissez le nom de la borne et la clé d'API.", "info");
    afficher("reglages");
  } else {
    nouvelleVente();
  }
  synchroniserPuisAfficher();
  setInterval(synchroniserPuisAfficher, 20000);
  setInterval(() => !$("#ecran-vente").hidden && majEntete(), 30000);
  setInterval(() => connexion !== "hors-ligne" && chargerCatalogue(), 10 * 60 * 1000);
}

demarrer();
