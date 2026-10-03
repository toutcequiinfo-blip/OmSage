// Borne de prise de commande et d'encaissement (Sage 100).
// Parcours : connexion (login Sage) -> client -> caisse (ticket, articles, pavé numérique) -> encaissement -> fin.
// Sur téléphone, la caisse passe en deux onglets : Articles et Ticket.
// Fonctionne hors ligne grâce à la file d'envoi.

import {
  lireReglages, ecrireReglages, prochainNumeroVente, nouvelIdVente,
  lireCatalogue, ajouterOperation, majOperation, supprimerOperation, lireFile, purgerFile,
  lireSession, ecrireSession, utilisateurMemorise, lireUtilisateurs, lireAttente, ecrireAttente,
  lireAffichage, ecrireAffichage,
} from "./stockage.js";
import { etatConnexion, rechargerCatalogue, synchroniser, commandesOuvertes, detailCommande } from "./synchro.js";
import { seConnecter } from "./connexion.js";

const $ = (s) => document.querySelector(s);
const euros = new Intl.NumberFormat("fr-FR", { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const arrondi = (v) => Math.round(v * 100) / 100;
const MODES_ESPECES = /esp[eè]ce/i;

let catalogue = null;
let connexion = "hors-ligne";
// { id, numero, client, lignes: Map(cle -> {article, enumere, quantite}), paiements: [], validee }
// Commande déjà enregistrée : existante, piece (Sage) ou idCommande (encore dans la file), totalTTC, dejaRegle.
let vente = null;
let famille = null;
let modeChoisi = null;
let utilisateur = null; // profil renvoyé par la connexion (voir connexion.js)
let ligneChoisie = null; // clé de la ligne du ticket sur laquelle agit le pavé numérique
let saisie = ""; // chiffres tapés sur le pavé
let changementClient = false; // l'écran client change le client du ticket en cours au lieu d'en ouvrir un nouveau
let dernierTicket = null; // ticket de la dernière vente terminée, pour l'imprimer depuis l'écran de fin

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
    const dispo = a.stockDisponible;
    return element("span", { class: dispo > 0 ? "stock" : "stock rupture" }, dispo > 0 ? quantiteTexte(dispo) : "Rupture");
  };
  const gamme = (a) => (aGamme(a) ? element("span", { class: "gamme" }, [a.gamme1, a.gamme2].filter(Boolean).join(" / ") || "Gamme") : null);
  if (enListe) {
    zone.replaceChildren(tableau(
      [{ titre: "Code", classe: "code" }, { titre: "Désignation" }, { titre: "Famille", classe: "secondaire" },
        { titre: "PV HT", classe: "nombre" }, { titre: "Stock", classe: "nombre" }],
      articles.map((a) => ({
        classe: a.suiviStock !== false && a.stockDisponible <= 0 ? "article rupture" : "article",
        cellules: [a.reference, element("span", {}, a.designation || a.reference, gamme(a)), a.famille || "", euros.format(a.prixVenteHT), stock(a)],
        onclick: () => toucherArticle(a),
      }))));
  } else {
    zone.replaceChildren(...articles.map((a) => element("button", {
      type: "button", class: a.suiviStock !== false && a.stockDisponible <= 0 ? "tuile rupture" : "tuile",
      style: `--teinte: ${teinte(a.famille)}`, title: a.famille || "", onclick: () => toucherArticle(a),
    },
      element("span", { class: "haut" }, element("span", { class: "code" }, a.reference), stock(a)),
      element("span", { class: "designation" }, a.designation || a.reference),
      element("span", { class: "bas" }, gamme(a), element("span", { class: "prix" }, `${euros.format(a.prixVenteHT)} HT`)))));
  }
}

// ---------- Gammes (taille, couleur...) : Sage exige la valeur pour un article à gamme ----------

const enumeres = (article) => (catalogue.gammes || []).filter((g) => g.article === article.reference);
const aGamme = (article) => !!article.gamme1 || enumeres(article).length > 0;
const libelleGamme = (e) => (e ? [e.gamme1, e.gamme2].filter(Boolean).join(" / ") : "");

function toucherArticle(article) {
  // Quantité tapée au pavé avant de toucher l'article (comme sur une caisse : 3 puis l'article).
  const q = prendreSaisie() ?? 1;
  if (!aGamme(article)) return ajouterArticle(article, q);
  if (stockControle(article) && quantiteAuPanier(article) + q > article.stockDisponible) return ajouterArticle(article, q); // affiche le refus
  const valeurs = enumeres(article);
  if (valeurs.length === 0) {
    bandeau(`${article.designation || article.reference} est géré en gamme : rechargez le catalogue (Réglages) pour voir ses valeurs.`, "erreur");
    return;
  }
  const d = $("#choix-gamme");
  $("#gamme-titre").textContent = `${article.designation || article.reference} : ${[article.gamme1, article.gamme2].filter(Boolean).join(" / ") || "choisir"}`;
  // Stock de chaque valeur (taille, couleur...) : une valeur épuisée reste visible mais ne s'ajoute pas.
  $("#gamme-valeurs").replaceChildren(...valeurs.map((e) => {
    const dispo = e.stockDisponible == null ? null : e.stockDisponible - quantiteValeurAuPanier(e);
    const epuise = stockControle(article) && dispo != null && dispo <= 0;
    return element("button", { type: "button", class: epuise ? "valeur epuise" : "valeur", onclick: () => { d.close(); ajouterArticle(article, q, e); } },
      element("span", {}, libelleGamme(e)),
      article.suiviStock === false || dispo == null ? null
        : element("span", { class: dispo > 0 ? "stock" : "stock rupture" }, dispo > 0 ? `Stock ${dispo}` : "Rupture"));
  }));
  d.showModal();
}

// Même règle que la fenêtre « Indisponibilité en stock » de Sage : l'API refuse aussi la commande, la borne prévient avant.
const stockControle = (article) => !!catalogue.controleStock && article.suiviStock !== false;
const quantiteAuPanier = (article) =>
  [...vente.lignes.values()].filter((l) => l.article.reference === article.reference).reduce((s, l) => s + l.quantite, 0);
const memeValeur = (a, b) => a.article === b.article && a.gamme1 === b.gamme1 && (a.gamme2 || "") === (b.gamme2 || "");
const quantiteValeurAuPanier = (e) =>
  [...vente.lignes.values()].filter((l) => l.enumere && memeValeur(l.enumere, e)).reduce((s, l) => s + l.quantite, 0);
/** Vrai si cette valeur de gamme dépasse son propre stock (catalogue récent : stock connu par valeur). */
const valeurEnManque = (article, e, ajout = 0) =>
  !!e && e.stockDisponible != null && article.suiviStock !== false && quantiteValeurAuPanier(e) + ajout > e.stockDisponible;

function ajouterArticle(article, delta = 1, enumere = null) {
  if (delta > 0 && stockControle(article) && quantiteAuPanier(article) + delta > article.stockDisponible) {
    bandeau(`Stock insuffisant pour ${article.designation || article.reference} : ${Math.max(0, article.stockDisponible)} disponible(s).`, "erreur");
    return;
  }
  if (delta > 0 && stockControle(article) && valeurEnManque(article, enumere, delta)) {
    bandeau(`Stock insuffisant pour ${article.designation || article.reference} ${libelleGamme(enumere)} : ${Math.max(0, enumere.stockDisponible)} disponible(s).`, "erreur");
    return;
  }
  const cle = enumere ? `${article.reference}|${enumere.gamme1}|${enumere.gamme2 || ""}` : article.reference;
  const l = vente.lignes.get(cle) || { article, enumere, quantite: 0 };
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
    return ajouterArticle(l.article, touche === "plus" ? n : -n, l.enumere);
  } else if (touche === "quantite" || touche === "entree") {
    if (!l) return bandeau("Choisissez d'abord une ligne du ticket.", "info");
    const n = prendreSaisie();
    if (n == null) return bandeau("Tapez la quantité puis appuyez sur Quantité.", "info");
    return ajouterArticle(l.article, arrondi(n - l.quantite), l.enumere);
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

function totaux() {
  if (vente.existante) {
    const paye = arrondi(vente.dejaRegle + vente.paiements.reduce((s, p) => s + p.montant, 0));
    return { ht: 0, tva: 0, ttc: vente.totalTTC, paye, reste: Math.max(0, arrondi(vente.totalTTC - paye)) };
  }
  const ht = arrondi([...vente.lignes.values()].reduce((s, l) => s + l.article.prixVenteHT * l.quantite, 0));
  const tva = arrondi(ht * lireReglages().tauxTva / 100);
  const ttc = arrondi(ht + tva);
  const paye = arrondi(vente.paiements.reduce((s, p) => s + p.montant, 0));
  return { ht, tva, ttc, paye, reste: Math.max(0, arrondi(ttc - paye)) };
}

function dessinerPanier() {
  const ul = $("#lignes");
  ul.replaceChildren();
  for (const [cle, l] of vente.lignes) {
    const alerte = (l.article.suiviStock !== false && quantiteAuPanier(l.article) > l.article.stockDisponible) || valeurEnManque(l.article, l.enumere);
    const classes = [alerte ? "alerte" : "", cle === ligneChoisie ? "choisie" : ""].filter(Boolean).join(" ");
    // Comme un ticket de caisse : code et désignation, puis quantité × prix unitaire et montant.
    ul.append(element("li", { class: classes, onclick: () => { ligneChoisie = cle; dessinerPanier(); } },
      element("div", { class: "libelle" },
        element("span", { class: "code" }, l.article.reference),
        element("strong", {}, l.article.designation || l.article.reference, l.enumere ? ` · ${libelleGamme(l.enumere)}` : ""),
        alerte ? element("span", { class: "alerte-stock" }, "Stock insuffisant") : null),
      element("div", { class: "detail" },
        element("div", { class: "quantite" },
          element("button", { type: "button", "aria-label": "Retirer un", onclick: (e) => { e.stopPropagation(); ajouterArticle(l.article, -1, l.enumere); } }, "−"),
          element("span", {}, quantiteTexte(l.quantite)),
          element("button", { type: "button", "aria-label": "Ajouter un", onclick: (e) => { e.stopPropagation(); ajouterArticle(l.article, 1, l.enumere); } }, "+")),
        element("span", { class: "pu" }, `× ${euros.format(l.article.prixVenteHT)} HT`),
        element("span", { class: "montant" }, euros.format(l.article.prixVenteHT * l.quantite)))));
  }
  if (vente.lignes.size === 0) ul.append(element("li", { class: "vide" }, "Touchez un article pour l'ajouter."));
  const t = totaux();
  $("#total-ht").textContent = euros.format(t.ht);
  $("#total-tva").textContent = euros.format(t.tva);
  $("#total-ttc").textContent = euros.format(t.ttc);
  $("#taux-tva").textContent = lireReglages().tauxTva;
  const vide = vente.lignes.size === 0;
  // Un vendeur sans la case Caissier enregistre la commande ; le caissier choisit directement le mode de règlement.
  $("#btn-valider").textContent = peutEncaisser() ? "Régler" : "Valider la commande";
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
  if (!vente || vente.lignes.size === 0) return;
  await validerCommande();
  if (!$("#ecran-paiement").hidden) choisirMode(mode);
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
  if (vente.validee) return;
  vente.validee = true;
  $("#btn-valider").disabled = true;
  const r = lireReglages();
  vente.id = nouvelIdVente(r.borne);
  vente.numero = prochainNumeroVente();
  const t = totaux();
  await ajouterOperation({
    cle: `commande:${vente.id}`,
    type: "commande",
    idExterne: vente.id,
    corps: {
      idExterne: vente.id,
      client: vente.client.numero,
      reference: vente.numero,
      lignes: [...vente.lignes.values()].map((l) => ({
        article: l.article.reference,
        quantite: l.quantite,
        ...(l.enumere ? { gamme1: l.enumere.gamme1, gamme2: l.enumere.gamme2 || null } : {}),
      })),
    },
    vente: { numero: vente.numero, client: vente.client.intitule || vente.client.numero, totalTtcEstime: t.ttc },
    ...auteur(),
  });
  synchroniserPuisAfficher();
  afficher("paiement");
}

// ---------- 3. Encaissement ----------

function dessinerPaiement() {
  const t = totaux();
  $("#paiement-numero").textContent = vente.numero;
  $("#p-total-libelle").textContent = vente.existante && vente.piece ? "Total TTC" : "Total TTC estimé";
  $("#p-total").textContent = euros.format(t.ttc);
  $("#p-paye").textContent = euros.format(t.paye);
  $("#p-reste").textContent = euros.format(t.reste);

  const zone = $("#modes");
  zone.replaceChildren();
  // Même règle que l'API : sans la case Caissier sur sa fiche collaborateur Sage, l'utilisateur n'encaisse pas.
  const interdit = !!catalogue.exigerCaissier && !!utilisateur && !utilisateur.peutEncaisser;
  $("#encaissement-interdit").hidden = !interdit;
  $("#encaissement-interdit").textContent = interdit
    ? `${nomUtilisateur(utilisateur)} n'est pas caissier dans Sage : la commande est enregistrée, l'encaissement sera fait par un caissier dans Sage.`
    : "";
  for (const m of interdit ? [] : catalogue.modesReglement) {
    zone.append(element("button", {
      type: "button", class: modeChoisi === m.intitule ? "mode actif" : "mode",
      onclick: () => choisirMode(m.intitule),
    }, m.intitule));
  }

  const ul = $("#paiements");
  ul.replaceChildren();
  for (const p of vente.paiements) {
    ul.append(element("li", {}, element("span", {}, `${p.mode}${p.reference ? " · " + p.reference : ""}`), element("strong", {}, euros.format(p.montant))));
  }
  $("#btn-terminer").textContent = t.reste > 0 ? (t.paye > 0 ? "Terminer (paiement partiel)" : "Terminer sans encaisser") : "Terminer";
}

function choisirMode(mode) {
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
  dessinerPaiement();
}

function terminer() {
  const t = totaux();
  dernierTicket = ticketAImprimer();
  const recap = $("#recap");
  recap.replaceChildren(
    element("p", {}, element("strong", {}, vente.numero), ` · ${vente.client.intitule || vente.client.numero}`),
    vente.existante ? null
      : element("ul", {}, ...[...vente.lignes.values()].map((l) => element("li", {}, `${l.quantite} × ${l.article.designation || l.article.reference}${l.enumere ? ` (${libelleGamme(l.enumere)})` : ""}`))),
    element("p", {}, `${vente.existante ? "Total TTC" : "Total TTC estimé"} : ${euros.format(t.ttc)} · Encaissé : ${euros.format(t.paye)} · Reste : ${euros.format(t.reste)}`),
    element("p", { class: "discret" }, connexion === "sage"
      ? "La vente est envoyée à Sage."
      : "La vente est enregistrée sur la borne et sera envoyée à Sage dès le retour du serveur."));
  vente = null;
  afficher("fin");
}

function nouvelleVente() {
  if (connexionExigee() && !utilisateur) return afficher("connexion");
  changementClient = false;
  const client = clientDefaut();
  if (client) demarrerVente(client);
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
    if (!article || (l.gamme1 && !enumere)) { manquants.push(l.reference); continue; }
    const cle = enumere ? `${article.reference}|${enumere.gamme1}|${enumere.gamme2 || ""}` : article.reference;
    vente.lignes.set(cle, { article, enumere, quantite: l.quantite });
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
        ligne("Commandes (TTC estimé)", String(commandes.length), euros.format(totalCommandes)),
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
  return {
    numero: vente.numero || "Ticket en cours", client: vente.client.intitule || vente.client.numero, vendeur: nomUtilisateur(utilisateur),
    date: new Date(), existante: !!vente.existante, taux: lireReglages().tauxTva,
    lignes: [...vente.lignes.values()].map((l) => ({
      libelle: `${l.article.designation || l.article.reference}${l.enumere ? " " + libelleGamme(l.enumere) : ""}`,
      quantite: l.quantite, prix: l.article.prixVenteHT, montant: arrondi(l.article.prixVenteHT * l.quantite),
    })),
    ...t, paiements: vente.paiements.slice(),
  };
}

function imprimer(ticket) {
  if (!ticket) return;
  const ligne = (a, b) => element("div", { class: "ligne" }, element("span", {}, a), element("span", {}, b));
  $("#impression").replaceChildren(
    element("h3", {}, `Borne ${lireReglages().borne}`),
    element("p", {}, ticket.date.toLocaleString("fr-FR")),
    element("p", {}, ticket.numero),
    element("p", {}, `Client : ${ticket.client}`),
    ticket.vendeur ? element("p", {}, `Vendeur : ${ticket.vendeur}`) : null,
    element("hr"),
    ...ticket.lignes.map((l) => element("div", {},
      element("div", {}, l.libelle),
      ligne(`  ${String(l.quantite).replace(".", ",")} x ${euros.format(l.prix)}`, euros.format(l.montant)))),
    element("hr"),
    ticket.existante ? null : ligne("Total HT", euros.format(ticket.ht)),
    ticket.existante ? null : ligne(`TVA estimée ${ticket.taux} %`, euros.format(ticket.tva)),
    ligne(ticket.existante ? "TOTAL TTC" : "TOTAL TTC estimé", euros.format(ticket.ttc)),
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

// ---------- Commandes déjà enregistrées, à encaisser ----------
// Bons de commande Sage (saisis dans Sage, ou pris par un vendeur sur une borne) et commandes de cette borne pas encore
// envoyées. Hors ligne : la liste du dernier catalogue. Les encaissements encore en file sont déduits du reste.

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
    dejaRegle: enAttente(null, o.idExterne), local: true, operation: o,
  }));
  const deSage = sage.map((c) => ({
    piece: c.piece, numero: c.piece, client: c.client, intitule: c.intitule, reference: c.reference, date: c.date,
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
  if (commandes.length === 0) return zone.replaceChildren(element("p", { class: "vide" }, "Aucune commande à encaisser."));
  const date = (c) => (c.date ? new Date(c.date).toLocaleDateString("fr-FR") : "");
  const reste = (c) => euros.format(arrondi(c.totalTTC - c.dejaRegle));
  const loupe = (c) => element("button", {
    type: "button", class: "loupe", title: "Voir le contenu de la pièce", "aria-label": `Voir le contenu de ${c.numero}`,
    onclick: (e) => { e.stopPropagation(); consulterCommande(c); },
  }, "🔍");
  const liste = commandes.slice(0, 200);
  if (enListe) {
    zone.replaceChildren(tableau(
      [{ titre: "Date", classe: "date" }, { titre: "N° pièce", classe: "code" }, { titre: "Référence", classe: "secondaire" },
        { titre: "Code client", classe: "code" }, { titre: "Client", classe: "secondaire" }, { titre: "Net à payer", classe: "nombre" },
        { titre: "Reste", classe: "nombre reste" }, { titre: "", classe: "action" }],
      liste.map((c) => ({
        classe: c.local ? "commande locale" : "commande", attributs: { "data-commande": c.numero },
        cellules: [date(c), element("span", {}, c.numero, c.local ? element("span", { class: "etiquette" }, "pas encore dans Sage") : null),
          c.reference && c.reference !== c.numero ? c.reference : "", c.client, c.intitule || "", euros.format(c.netAPayer), reste(c), loupe(c)],
        onclick: () => encaisserCommande(c),
      }))));
  } else {
    zone.replaceChildren(...liste.map((c) => element("div", { class: c.local ? "carte-commande locale" : "carte-commande", "data-commande": c.numero },
      element("button", { type: "button", class: "corps", onclick: () => encaisserCommande(c) },
        element("span", { class: "haut" }, element("strong", {}, c.numero), element("span", { class: "discret" }, date(c))),
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
    const lignes = c.operation.corps.lignes.map((l) => {
      const a = catalogue.articles.find((x) => x.reference === l.article);
      const prix = a?.prixVenteHT || 0;
      return { article: l.article, designation: a?.designation || l.article, gamme: [l.gamme1, l.gamme2].filter(Boolean).join(" / "),
        quantite: l.quantite, prix, montant: arrondi(prix * l.quantite) };
    });
    return ouvrirListe(titre, [entete(c.date, null), element("p", { class: "info" }, "Commande pas encore envoyée à Sage : prix du catalogue, TTC estimé."),
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

const connexionExigee = () => !!catalogue?.authentification;
const nomUtilisateur = (u) =>
  u?.collaborateur ? [u.collaborateur.prenom, u.collaborateur.nom].filter(Boolean).join(" ") : u?.utilisateur || "";
/** Utilisateur à joindre à chaque opération de la file : elle partira avec sa connexion, même plus tard. */
const auteur = () => (utilisateur ? { utilisateur: utilisateur.utilisateur, jeton: utilisateur.jeton } : {});

function majUtilisateur() {
  const b = $("#utilisateur");
  b.hidden = !connexionExigee() || !utilisateur;
  b.textContent = utilisateur ? `👤 ${nomUtilisateur(utilisateur)}` : "";
}

function dessinerConnexion() {
  const f = $("#form-connexion");
  f.elements.motDePasse.value = "";
  // Les utilisateurs déjà connectés sur cette borne : un appui remplit le nom.
  const connus = Object.values(lireUtilisateurs()).map((u) => u.profil.utilisateur).sort();
  $("#utilisateurs-connus").replaceChildren(...connus.map((login) => element("button", {
    type: "button", class: "puce",
    onclick: () => { f.elements.utilisateur.value = login; f.elements.motDePasse.focus(); },
  }, login)));
  (f.elements.utilisateur.value ? f.elements.motDePasse : f.elements.utilisateur).focus();
}

async function connecter(ev) {
  ev.preventDefault();
  const f = ev.target.elements;
  const bouton = $("#btn-connexion");
  bouton.disabled = true;
  bouton.textContent = "Connexion…";
  try {
    utilisateur = await seConnecter(f.utilisateur.value, f.motDePasse.value);
    ecrireSession(utilisateur.utilisateur);
    f.motDePasse.value = "";
    majUtilisateur();
    bandeau(utilisateur.horsLigne ? "Connecté hors ligne : les ventes partiront vers Sage au retour du serveur." : "", "info");
    // Après un verrouillage, on retrouve le ticket ou l'encaissement en cours.
    if (vente?.validee) afficher("paiement");
    else if (vente) afficher("vente");
    else nouvelleVente();
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
  for (const nom of ["borne", "cle", "tauxTva", "clientDefaut"]) f.elements[nom].value = r[nom] ?? "";
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
    const titre = op.type === "commande"
      ? `Commande ${op.vente?.numero || op.idExterne} · ${op.vente?.client || op.corps.client}`
      : `Encaissement ${op.corps.mode} ${euros.format(op.corps.montant)} · ${op.vente?.numero || op.idCommande}`;
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
    tauxTva: Number(f.tauxTva.value),
    clientDefaut: f.clientDefaut.value.trim().toUpperCase(),
  });
  $("#nom-borne").textContent = lireReglages().borne;
  bandeau("Réglages enregistrés.", "ok");
  await chargerCatalogue(true);
  dessinerReglages();
}

// ---------- Connexion, catalogue et synchronisation ----------

async function chargerCatalogue(forcer = false) {
  try {
    if (forcer || connexion !== "hors-ligne") catalogue = await rechargerCatalogue();
  } catch (e) {
    if (forcer) bandeau(e.message, "erreur");
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
  b.textContent = libelle + (attente ? ` · ${attente} en attente` : "") + (erreurs ? ` · ${erreurs} refusée(s)` : "");
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
    const a = catalogue.articles.find((x) => (g ? x.reference === g.article : x.codeBarre === code || x.reference === code.toUpperCase()));
    if (!a) return;
    if (g) ajouterArticle(a, 1, g);
    else toucherArticle(a);
    e.target.value = "";
    dessinerArticles();
  });
  $("#btn-valider").addEventListener("click", validerCommande);
  $("#btn-fermer-gamme").addEventListener("click", () => $("#choix-gamme").close());
  $("#btn-annuler-vente").addEventListener("click", () => { if (vente.lignes.size === 0 || confirm("Annuler ce ticket ?")) { vente = null; nouvelleVente(); } });
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
  $("#btn-imprimer").addEventListener("click", () => imprimer(ticketAImprimer()));
  $("#btn-imprimer-fin").addEventListener("click", () => imprimer(dernierTicket));
  $("#btn-verrouiller").addEventListener("click", verrouiller);
  $("#btn-changer-utilisateur").addEventListener("click", deconnecter);
  $("#onglet-articles").addEventListener("click", () => basculerOnglet("articles"));
  $("#onglet-ticket").addEventListener("click", () => basculerOnglet("ticket"));
  for (const b of document.querySelectorAll("[data-touche]")) b.addEventListener("click", () => toucherPave(b.dataset.touche));
  $("#p-montant").addEventListener("input", majRendu);
  $("#btn-encaisser").addEventListener("click", encaisser);
  $("#btn-terminer").addEventListener("click", terminer);
  $("#btn-nouvelle").addEventListener("click", nouvelleVente);
  $("#btn-reglages").addEventListener("click", () => afficher("reglages"));
  $("#etat").addEventListener("click", () => afficher("reglages"));
  $("#btn-fermer-reglages").addEventListener("click", () => {
    if (vente?.validee) afficher("paiement");
    else if (vente) afficher("vente");
    else nouvelleVente();
  });
  $("#form-connexion").addEventListener("submit", connecter);
  $("#btn-commandes").addEventListener("click", ouvrirCommandes);
  $("#btn-fermer-commandes").addEventListener("click", nouvelleVente);
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
  $("#nom-borne").textContent = r.borne;
  await purgerFile();
  await majEtat();
  await chargerCatalogue();
  // La page peut être rechargée : l'utilisateur connecté le reste.
  const session = lireSession() ? utilisateurMemorise(lireSession())?.profil : null;
  utilisateur = session && new Date(session.expiration) > new Date() ? session : null;
  majUtilisateur();
  if (!r.cle || !catalogue) {
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
