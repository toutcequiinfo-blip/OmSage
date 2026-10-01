// Borne de prise de commande et d'encaissement (Sage 100).
// Parcours : client -> articles et panier -> encaissement -> fin. Fonctionne hors ligne grâce à la file d'envoi.

import {
  lireReglages, ecrireReglages, prochainNumeroVente, nouvelIdVente,
  lireCatalogue, ajouterOperation, majOperation, supprimerOperation, lireFile, purgerFile,
} from "./stockage.js";
import { etatConnexion, rechargerCatalogue, synchroniser } from "./synchro.js";

const $ = (s) => document.querySelector(s);
const euros = new Intl.NumberFormat("fr-FR", { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const arrondi = (v) => Math.round(v * 100) / 100;
const MODES_ESPECES = /esp[eè]ce/i;

let catalogue = null;
let connexion = "hors-ligne";
let vente = null; // { id, numero, client, lignes: Map(cle -> {article, enumere, quantite}), paiements: [], validee }
let famille = null;
let modeChoisi = null;

// ---------- Écrans ----------

function afficher(ecran) {
  for (const e of document.querySelectorAll(".ecran")) e.hidden = e.id !== `ecran-${ecran}`;
  $("#client-actuel").textContent = vente?.client ? `${vente.client.numero} · ${vente.client.intitule}` : "";
  if (ecran === "client") { $("#recherche-client").value = ""; dessinerClients(); $("#recherche-client").focus(); }
  if (ecran === "vente") { dessinerFamilles(); dessinerArticles(); dessinerPanier(); }
  if (ecran === "paiement") dessinerPaiement();
  if (ecran === "reglages") dessinerReglages();
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

// ---------- 1. Client ----------

function dessinerClients() {
  const q = $("#recherche-client").value.trim().toLowerCase();
  const liste = $("#liste-clients");
  liste.replaceChildren();
  if (!catalogue) {
    liste.append(element("li", { class: "vide" }, "Catalogue absent : connectez la borne au serveur puis ouvrez les réglages."));
    return;
  }
  const clients = catalogue.clients
    .filter((c) => !q || c.numero.toLowerCase().includes(q) || (c.intitule || "").toLowerCase().includes(q))
    .slice(0, 60);
  for (const c of clients) {
    liste.append(element("li", {},
      element("button", { type: "button", onclick: () => demarrerVente(c) },
        element("strong", {}, c.intitule || c.numero),
        element("span", { class: "discret" }, ` ${c.numero}${c.ville ? " · " + c.ville : ""}`))));
  }
  if (clients.length === 0) liste.append(element("li", { class: "vide" }, "Aucun client trouvé."));
}

function demarrerVente(client) {
  vente = { id: null, numero: null, client, lignes: new Map(), paiements: [], validee: false };
  famille = null;
  $("#recherche-article").value = "";
  afficher("vente");
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
  zone.replaceChildren();
  const articles = catalogue.articles
    .filter((a) => !famille || a.famille === famille)
    .filter((a) => !q || a.reference.toLowerCase().includes(q) || (a.designation || "").toLowerCase().includes(q) || a.codeBarre === q)
    .slice(0, 120);
  for (const a of articles) {
    const dispo = a.stockDisponible;
    zone.append(element("button", { type: "button", class: "tuile", onclick: () => toucherArticle(a) },
      element("span", { class: "designation" }, a.designation || a.reference),
      aGamme(a) ? element("span", { class: "discret" }, `Choix : ${[a.gamme1, a.gamme2].filter(Boolean).join(" / ") || "gamme"}`) : null,
      element("span", { class: "discret" }, a.reference),
      element("span", { class: "prix" }, `${euros.format(a.prixVenteHT)} HT`),
      a.suiviStock === false ? null
        : element("span", { class: dispo > 0 ? "stock" : "stock rupture" }, dispo > 0 ? `Stock ${dispo}` : "Rupture")));
  }
  if (articles.length === 0) zone.append(element("p", { class: "vide" }, "Aucun article."));
}

// ---------- Gammes (taille, couleur...) : Sage exige la valeur pour un article à gamme ----------

const enumeres = (article) => (catalogue.gammes || []).filter((g) => g.article === article.reference);
const aGamme = (article) => !!article.gamme1 || enumeres(article).length > 0;
const libelleGamme = (e) => (e ? [e.gamme1, e.gamme2].filter(Boolean).join(" / ") : "");

function toucherArticle(article) {
  if (!aGamme(article)) return ajouterArticle(article);
  if (stockControle(article) && quantiteAuPanier(article) + 1 > article.stockDisponible) return ajouterArticle(article, 1); // affiche le refus
  const valeurs = enumeres(article);
  if (valeurs.length === 0) {
    bandeau(`${article.designation || article.reference} est géré en gamme : rechargez le catalogue (Réglages) pour voir ses valeurs.`, "erreur");
    return;
  }
  const d = $("#choix-gamme");
  $("#gamme-titre").textContent = `${article.designation || article.reference} : ${[article.gamme1, article.gamme2].filter(Boolean).join(" / ") || "choisir"}`;
  $("#gamme-valeurs").replaceChildren(...valeurs.map((e) =>
    element("button", { type: "button", class: "valeur", onclick: () => { d.close(); ajouterArticle(article, 1, e); } }, libelleGamme(e))));
  d.showModal();
}

// Même règle que la fenêtre « Indisponibilité en stock » de Sage : l'API refuse aussi la commande, la borne prévient avant.
const stockControle = (article) => !!catalogue.controleStock && article.suiviStock !== false;
const quantiteAuPanier = (article) =>
  [...vente.lignes.values()].filter((l) => l.article.reference === article.reference).reduce((s, l) => s + l.quantite, 0);

function ajouterArticle(article, delta = 1, enumere = null) {
  if (delta > 0 && stockControle(article) && quantiteAuPanier(article) + delta > article.stockDisponible) {
    bandeau(`Stock insuffisant pour ${article.designation || article.reference} : ${Math.max(0, article.stockDisponible)} disponible(s).`, "erreur");
    return;
  }
  const cle = enumere ? `${article.reference}|${enumere.gamme1}|${enumere.gamme2 || ""}` : article.reference;
  const l = vente.lignes.get(cle) || { article, enumere, quantite: 0 };
  l.quantite += delta;
  if (l.quantite <= 0) vente.lignes.delete(cle);
  else vente.lignes.set(cle, l);
  dessinerPanier();
}

function totaux() {
  const ht = arrondi([...vente.lignes.values()].reduce((s, l) => s + l.article.prixVenteHT * l.quantite, 0));
  const tva = arrondi(ht * lireReglages().tauxTva / 100);
  const ttc = arrondi(ht + tva);
  const paye = arrondi(vente.paiements.reduce((s, p) => s + p.montant, 0));
  return { ht, tva, ttc, paye, reste: Math.max(0, arrondi(ttc - paye)) };
}

function dessinerPanier() {
  const ul = $("#lignes");
  ul.replaceChildren();
  for (const l of vente.lignes.values()) {
    const alerte = l.article.suiviStock !== false && quantiteAuPanier(l.article) > l.article.stockDisponible;
    ul.append(element("li", { class: alerte ? "alerte" : "" },
      element("div", { class: "libelle" },
        element("strong", {}, l.article.designation || l.article.reference, l.enumere ? ` · ${libelleGamme(l.enumere)}` : ""),
        element("span", { class: "discret" }, `${euros.format(l.article.prixVenteHT)} HT${alerte ? " · stock insuffisant" : ""}`)),
      element("div", { class: "quantite" },
        element("button", { type: "button", "aria-label": "Retirer un", onclick: () => ajouterArticle(l.article, -1, l.enumere) }, "−"),
        element("span", {}, String(l.quantite)),
        element("button", { type: "button", "aria-label": "Ajouter un", onclick: () => ajouterArticle(l.article, 1, l.enumere) }, "+")),
      element("span", { class: "montant" }, euros.format(l.article.prixVenteHT * l.quantite))));
  }
  if (vente.lignes.size === 0) ul.append(element("li", { class: "vide" }, "Touchez un article pour l'ajouter."));
  const t = totaux();
  $("#total-ht").textContent = euros.format(t.ht);
  $("#total-tva").textContent = euros.format(t.tva);
  $("#total-ttc").textContent = euros.format(t.ttc);
  $("#taux-tva").textContent = lireReglages().tauxTva;
  $("#btn-valider").disabled = vente.lignes.size === 0;
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
  });
  synchroniserPuisAfficher();
  afficher("paiement");
}

// ---------- 3. Encaissement ----------

function dessinerPaiement() {
  const t = totaux();
  $("#paiement-numero").textContent = vente.numero;
  $("#p-total").textContent = euros.format(t.ttc);
  $("#p-paye").textContent = euros.format(t.paye);
  $("#p-reste").textContent = euros.format(t.reste);

  const zone = $("#modes");
  zone.replaceChildren();
  for (const m of catalogue.modesReglement) {
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
    idCommande: vente.id,
    corps: { idExterne: id, mode: modeChoisi, montant, referencePaiement: reference },
    vente: { numero: vente.numero, client: vente.client.intitule || vente.client.numero },
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
  const recap = $("#recap");
  recap.replaceChildren(
    element("p", {}, element("strong", {}, vente.numero), ` · ${vente.client.intitule || vente.client.numero}`),
    element("ul", {}, ...[...vente.lignes.values()].map((l) => element("li", {}, `${l.quantite} × ${l.article.designation || l.article.reference}${l.enumere ? ` (${libelleGamme(l.enumere)})` : ""}`))),
    element("p", {}, `Total TTC estimé : ${euros.format(t.ttc)} · Encaissé : ${euros.format(t.paye)}`),
    element("p", { class: "discret" }, connexion === "sage"
      ? "La vente est envoyée à Sage."
      : "La vente est enregistrée sur la borne et sera envoyée à Sage dès le retour du serveur."));
  vente = null;
  afficher("fin");
}

function nouvelleVente() {
  const defaut = lireReglages().clientDefaut;
  const client = defaut && catalogue?.clients.find((c) => c.numero === defaut);
  if (client) demarrerVente(client);
  else afficher("client");
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
  $("#btn-annuler-vente").addEventListener("click", () => { if (vente.lignes.size === 0 || confirm("Annuler cette commande ?")) { vente = null; afficher("client"); } });
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
  if (!r.cle || !catalogue) {
    bandeau(r.cle ? "Aucun catalogue : la borne doit joindre le serveur une première fois." : "Première utilisation : saisissez le nom de la borne et la clé d'API.", "info");
    afficher("reglages");
  } else {
    nouvelleVente();
  }
  synchroniserPuisAfficher();
  setInterval(synchroniserPuisAfficher, 20000);
  setInterval(() => connexion !== "hors-ligne" && chargerCatalogue(), 10 * 60 * 1000);
}

demarrer();
