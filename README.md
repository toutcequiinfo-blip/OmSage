# API Sage 100

API REST qui relie Sage 100 (Gestion commerciale et Comptabilité V12, SQL Server) aux applications externes. La première application prévue est la borne tactile de prise de commande et d'encaissement.

## Architecture

```
Borne / applications ──HTTPS + X-Api-Key──► Sage100Api (.NET 8, 64 bits)
                                              ├─ lectures ──► SQL Server (compte en lecture seule)
                                              ├─ journal  ──► SQLite (anti-doublon, traçabilité)
                                              └─ écritures ─► canal nommé ─► Sage100Api.Worker (.NET 4.8, x86)
                                                                              └─ Objets Métiers V12 ─► Sage
```

| Projet | Rôle |
|---|---|
| `src/Sage100Api` | API REST, Swagger, clés d'API, lectures SQL, journal des opérations |
| `src/Sage100Api.Worker` | Seul processus qui charge les Objets Métiers (DLL 32 bits). Exécute les écritures une par une. |
| `src/Sage100Api.Contracts` | Objets échangés (commande, encaissement), validation, protocole API ↔ worker |
| `src/Sage100Api/wwwroot/borne` | Application tablette de la borne (PWA), servie par l'API sur `/borne/` |
| `src/Sage100Api/wwwroot/crm` | Application téléphone des commerciaux (CRM), servie par l'API sur `/crm/` |
| `src/Sage100Api/wwwroot/livraison` | Application téléphone des tournées de livraison, servie par l'API sur `/livraison/` |
| `tests/Sage100Api.Tests` | Tests de l'API avec un faux worker (sans Sage) |

**Règles de base :**
- On **n'écrit jamais dans Sage en SQL**. La loi anti-fraude est activée, et une écriture SQL corromprait la base (Structure des bases p.440). Toutes les écritures passent par les Objets Métiers.
- **Double barrière anti-doublon** : le journal de l'API d'abord, puis une vérification dans Sage. Pour une commande, le worker cherche `DO_RefExterne`. Pour un encaissement, il cherche l'empreinte `BRN…` dans le libellé de l'acompte.
- **Un encaissement sur un bon de commande ou de livraison est un acompte.** Ce choix a été validé par le POC du 29/09/2026, loi anti-fraude activée : Sage génère une facture d'acompte.
- **Un encaissement sur une facture est un règlement client**, imputé sur l'échéance de la facture. Loi anti-fraude activée, Sage n'impute qu'une facture validée : sinon le règlement reste à lettrer dans Sage. Le journal est celui de `journauxParMode`, sinon celui du dernier règlement client saisi avec ce mode. L'empreinte `BRN…` est dans le libellé du règlement.
- **Bon de livraison et facture d'un article suivi par lot ou série** : le worker prend les lots non épuisés du dépôt, du plus ancien (péremption) au plus récent, une ligne par lot.

## Routes (v1)

| Méthode | Route | Description |
|---|---|---|
| GET | `/api/v1/sante` | État de l'API et du worker (sans clé) |
| POST | `/api/v1/connexion` | Connexion d'un utilisateur avec son login Sage : renvoie un jeton, le collaborateur rattaché et le droit d'encaisser |
| GET | `/api/v1/clients?recherche=&page=&taille=` | Clients actifs |
| GET | `/api/v1/clients/{numero}` | Un client |
| GET | `/api/v1/articles?recherche=&famille=&page=&taille=` | Articles actifs, prix HT, stock et stock réservé |
| GET | `/api/v1/articles/{reference}` | Un article (`gamme1` / `gamme2` : intitulés des gammes, ou null) |
| GET | `/api/v1/articles/{reference}/gammes` | Valeurs de gamme vendables d'un article |
| GET | `/api/v1/modes-reglement` | Modes de règlement Sage |
| GET | `/api/v1/catalogue` | Instantané complet pour le mode hors ligne de la borne (avec tarifs, conditionnements, souches, dépôts et stock par dépôt) |
| GET | `/api/v1/souches` | Souches de numérotation des documents de vente (`numero` = DO_Souche, 0 pour la première) |
| GET | `/api/v1/tarifs?client=&article=&gamme1=&conditionnement=&quantite=` | Prix qu'aura une ligne pour ce client : tarif client, sinon catégorie tarifaire, gamme, conditionnement, remises. Pour vérifier les tarifs dans Swagger. |
| POST | `/api/v1/commandes` | Crée un bon de commande, un bon de livraison ou une facture (`typeDocument`), dans la souche et le dépôt demandés. **Idempotent** sur `idExterne` : 201 à la création, 200 si déjà reçu. |
| GET | `/api/v1/commandes/{idExterne}` | Pièce Sage d'une commande |
| POST | `/api/v1/commandes/{idExterne}/encaissements` | Encaissement : acompte sur un bon de commande ou de livraison, règlement client sur une facture. Idempotent sur son propre `idExterne`. |
| GET | `/api/v1/commandes-ouvertes?recherche=&taille=` | Bons de commande non clôturés avec un reste à payer (total TTC moins acomptes) et leur net à payer Sage, les plus récents d'abord |
| GET | `/api/v1/commandes-ouvertes/{piece}` | Un bon de commande et ses lignes (article, désignation, gamme, quantité, prix), pour la loupe de la borne |
| POST | `/api/v1/commandes/piece/{piece}/encaissements` | Encaissement d'un bon de commande désigné par sa pièce Sage (saisi dans Sage ou par un vendeur). Idempotent sur `idExterne`. |


### Routes pour les extensions (CRM, livraison, géolocalisation, recouvrement)

Lectures SQL seules, sauf les positions GPS gardées par l'API. Chaque route est décrite dans Swagger.

| Méthode | Route | Usage |
|---|---|---|
| GET | `/api/v1/clients/{numero}/fiche` | Fiche client complète : adresse, téléphone, e-mail, SIRET, commercial, encours autorisé, catégorie tarifaire, contacts, adresses de livraison, positions GPS |
| GET | `/api/v1/clients/{numero}/contacts` | Contacts du client (F_CONTACTT) |
| GET | `/api/v1/clients/{numero}/adresses-livraison` | Adresses de livraison (F_LIVRAISON), avec leur numéro `LI_No` |
| GET | `/api/v1/clients/{numero}/documents?type=&du=&au=` | Historique des documents de vente du client |
| GET | `/api/v1/clients/{numero}/echeances` | Écritures non lettrées du client avec jours de retard |
| GET | `/api/v1/collaborateurs` | Collaborateurs actifs : commerciaux, caissiers, livreurs |
| GET | `/api/v1/depots` | Dépôts |
| GET | `/api/v1/documents?type=&client=&du=&au=&cloture=&page=&taille=` | Documents de vente. `type` : devis, commande, preparation, livraison, retour, avoir, facture, facture-comptabilisee |
| GET | `/api/v1/documents/{type}/{piece}` | Un document et ses lignes |
| GET | `/api/v1/livraisons/a-livrer?jusquau=&depot=` | Bons de commande et préparations non clôturés, avec adresse de livraison (ou du client), contact, téléphone et position GPS |
| POST | `/api/v1/livraisons/optimiser` | Ordre de passage conseillé pour `{ "pieces": [...], "depot": 1 }` ou depuis `latitude`/`longitude` : plus proche voisin puis 2-opt, à vol d'oiseau. Les pièces sans position vont à la fin |
| GET | `/api/v1/livraisons/tournees?du=&au=&livreur=&miennes=` | Tournées avec avancement (arrêts traités, total TTC). `miennes=true` : celles du livreur connecté |
| GET / PUT / DELETE | `/api/v1/livraisons/tournees/{id}` | Une tournée et ses arrêts. PUT `{ "date", "nom", "livreur", "depot", "pieces": [...] }` dans l'ordre de passage : adresse, contact et montants sont recopiés. Une pièce déjà prévue ailleurs est refusée, un arrêt traité ne peut pas être retiré |
| PUT | `/api/v1/livraisons/tournees/{id}/arrets/{piece}` | Compte rendu du livreur : `statut` (livre, partiel, echec, a-livrer), `motif`, `receptionnaire`, `commentaire`, `signature` (PNG en data URL), position, et `lignes` (`ligne`, `quantite` livrée, `motif` si moins que chargé) : le statut se calcule alors tout seul |
| GET | `/api/v1/livraisons/tournees/{id}/arrets/{piece}/signature` | Signature du réceptionnaire (image PNG) |
| PUT | `/api/v1/livraisons/tournees/{id}/chargement` | Contrôle au dépôt : `{ "lignes": [{ "piece", "ligne", "quantite" }], "responsable", "signature" }`. Chaque arrêt porte ses `articles` (lignes de la pièce Sage) avec quantité commandée, chargée et livrée |
| GET | `/api/v1/livraisons/tournees/{id}/chargement/signature` | Signature du responsable du dépôt (image PNG) |
| PUT / DELETE | `/api/v1/livraisons/tournees/{id}/courses/{course}` | Autre course hors pièce Sage : `type` (livrer, recuperer, autre), `description`, client, adresse, contact, `statut` (a-faire, en-cours, fait, reporte, annule) |
| GET | `/api/v1/livraisons/tableau-de-bord?du=&au=&livreur=&depot=&client=&statut=` | Indicateurs, arrêts par jour, par livreur, raisons d'échec et de retour, courses, villes, et liste des arrêts, sur les mêmes filtres |
| GET | `/api/v1/livraisons/suivi/{piece}` | Passages d'une pièce en tournée : livrée ou non, motif, heure, réceptionnaire |
| GET | `/api/v1/recouvrement/echeances?client=&echuesSeulement=` | Écritures clients non lettrées (factures dues, avoirs), jours de retard, date de relance |
| GET | `/api/v1/recouvrement/balance-agee` | Par client : non échu, 1-30, 31-60, 61-90, plus de 90 jours, crédits non affectés |
| GET / PUT / DELETE | `/api/v1/geolocalisation/{cible}/{cle}` | Position GPS d'un `client` (code), d'une `adresse-livraison` (LI_No) ou d'un `depot`. PUT `{ "latitude", "longitude", "precision", "source" }`, avec un utilisateur connecté si la connexion est active |
| GET | `/api/v1/modifications?table=&depuis=` | Codes modifiés dans Sage depuis une date (colonne `cbModification`) : clients, articles, documents, adresses-livraison, contacts, ecritures |
| GET | `/api/v1/crm/clients/{numero}/synthese` | Vue 360° : fiche, CA HT 12 mois (et 12 mois d'avant), articles les plus achetés, commandes et devis en cours, solde dû, échu, retard, prochaines actions, dernières activités |
| GET | `/api/v1/crm/portefeuille?collaborateur=` | Clients dont le collaborateur est le représentant, avec la date de la dernière activité. Par défaut : l'utilisateur connecté |
| GET | `/api/v1/crm/activites?client=&collaborateur=&utilisateur=&statut=&type=&du=&au=` | Activités (visite, appel, rendez-vous, email, note, tache). `statut=a-faire` donne l'agenda, trié par date prévue |
| GET / PUT / DELETE | `/api/v1/crm/activites/{id}` | Une activité. L'id est choisi par l'application (GUID) : renvoyer le même PUT ne crée pas de doublon. PUT et DELETE demandent un utilisateur connecté si la connexion est active |

Sage n'a pas de champ pour une position GPS, et l'API n'écrit jamais en SQL dans Sage : les positions, les activités CRM et les tournées de livraison sont gardées dans `sage100api-extensions.db`, à côté du journal. Sauvegarde ce fichier avec le journal.

**Journal des encaissements :** un acompte n'a pas de journal. Sage le donne au règlement qu'il crée : c'est le journal par défaut du mode, par exemple BEU pour Espèces. Pour imposer un journal de trésorerie par mode, renseigne `journauxParMode` dans `worker.json`, par exemple `{ "Espèces": "CAIS", "Carte bancaire": "BQ1" }`, puis redémarre le worker. Un mode absent garde le journal de Sage. Un code de journal inconnu fait refuser l'encaissement avant qu'il soit créé.

**Connexion des utilisateurs :** avec `Authentification:Active` à `true` (par défaut), commandes et encaissements exigent un utilisateur Sage connecté, en plus de la clé d'API.
- `POST /api/v1/connexion` avec `{ "utilisateur": "MARIE", "motDePasse": "..." }`. Le worker ouvre Sage avec ce login et ce mot de passe, le temps de la vérification : c'est Sage qui les contrôle. L'API ne garde aucun mot de passe.
- La réponse contient un `jeton`, à envoyer ensuite dans l'en-tête `Authorization: Bearer <jeton>` (bouton « Authorize » dans Swagger). Il est valable `Authentification:DureeHeures` heures (168 par défaut), ce qui couvre aussi les ventes faites hors ligne.
- Le bon de commande reçoit le collaborateur Sage rattaché à l'utilisateur : c'est le champ « Utilisateur » de la fiche collaborateur. Le journal de l'API note aussi le login.
- Avec `Authentification:ExigerCaissier` à `true` (par défaut), seuls les utilisateurs dont le collaborateur a la case « Caissier » cochée, ou les administrateurs Sage, peuvent encaisser.
- Après 5 mauvais mots de passe, le login est bloqué 2 minutes.
- Les droits configurés dans Sage (menus, fonctions) ne sont pas appliqués : les écritures passent par la session du worker.

Codes d'erreur :
- 401 : clé d'API absente ou invalide, connexion absente ou expirée (`CONNEXION_REQUISE`), login refusé par Sage (`ACCES_REFUSE`).
- 403 : utilisateur sans droit d'encaisser (`DROIT_REFUSE`).
- 429 : trop de mauvais mots de passe.
- 404 : client, article, mode ou commande inconnu.
- 409 : la même opération est déjà en cours.
- 422 : données invalides, ou règle Sage refusée (stock, période clôturée…).
- 503 : worker ou Sage indisponible. La borne garde alors l'opération en file et la renvoie plus tard.

Exemple de commande :

```json
POST /api/v1/commandes
X-Api-Key: changer-cette-cle

{ "idExterne": "BORNE1-20260929-0001", "client": "CISEL",
  "lignes": [ { "article": "CHORFA", "quantite": 1 },
              { "article": "BAOR01", "quantite": 1, "gamme1": "52" } ] }
```

Champs facultatifs de la pièce :
- `typeDocument` : `commande` (par défaut), `livraison` ou `facture`. Le bon de livraison et la facture font sortir le stock dès leur création.
- `souche` : numéro de souche (`GET /souches`). Vide : souche par défaut de Sage.
- `depot` : numéro de dépôt (`DE_No`, voir `GET /depots`). Vide : dépôt du client ou dépôt principal.

Sur une ligne, `conditionnement` et `quantiteConditionnement` vendent un conditionnement : `{ "article": "ECRIN", "quantite": 2, "conditionnement": "Carton de 12", "quantiteConditionnement": 12 }` fait 24 unités dans Sage.

**Prix et catégories tarifaires :** les Objets Métiers ne prennent que le tarif propre au client, puis le prix de la fiche article ; ils ignorent la catégorie tarifaire du client et ne posent pas les remises (manuel OM, SetDefaultArticle). L'API calcule donc le prix de chaque ligne comme la saisie Sage, et le worker l'impose sur la ligne si Sage en a mis un autre :
1. tarif propre au client (F_ARTCLIENT avec son numéro), sinon celui de sa catégorie tarifaire (`N_CatTarif` de la fiche client), sinon prix de la fiche article ;
2. prix propre à une valeur de gamme (F_TARIFGAM) ou à un conditionnement (F_TARIFCOND) ; sans prix propre, un « Carton de 12 » vaut 12 fois l'unité ;
3. remise générale du tarif (sauf « hors remise »), ou tranches par quantité ou par montant, ou prix net par quantité (F_TARIFQTE).

Les prix envoyés par l'application sont ignorés. Le worker écrit dans sa console chaque prix remplacé.

**Contrôle du stock :** la fenêtre « Indisponibilité en stock » de la saisie Sage n'existe pas dans les Objets Métiers, donc c'est l'API qui contrôle. Une commande qui dépasse le stock disponible (stock réel moins réservé, dans le dépôt demandé, ou tous dépôts confondus sans dépôt) est refusée en 422, avec le détail par article. Un conditionnement compte son contenu (2 cartons de 12 = 24). Les articles sans suivi de stock ne sont pas contrôlés. Le réglage `Sage:ControleStock` vaut :
- `Auto` (par défaut) : refus si l'option « Autoriser la gestion des stocks négatifs » est décochée dans Sage ;
- `Bloquer` : refus dans tous les cas ;
- `Aucun` : aucun contrôle.

Pour un article à gamme, chaque valeur (taille, couleur…) est aussi contrôlée avec son propre stock (table F_GAMSTOCK, tous dépôts), en plus du total de l'article.

La borne applique la même règle au moment d'ajouter un article au panier. Le choix de la gamme affiche le stock de chaque valeur, et une valeur épuisée ne s'ajoute pas.

Un article géré en gamme (taille, couleur…) doit recevoir sa valeur dans `gamme1`, et `gamme2` pour une double gamme. Sans valeur, Sage refuse la ligne ; l'API renvoie alors 422 avec la liste des valeurs possibles.

Exemple d'encaissement :

```json
POST /api/v1/commandes/BORNE1-20260929-0001/encaissements

{ "idExterne": "BORNE1-20260929-0001-P1", "mode": "Mobile money",
  "montant": 500, "referencePaiement": "MM-784512" }
```

## Lancer en développement (PC Windows avec Sage V12 et les Objets Métiers V12)

1. Ouvre `Sage100Api.sln` dans Visual Studio 2022.
2. Dans le projet **Sage100Api.Worker**, fais un clic droit sur Dépendances > **Ajouter une référence COM** et coche « Objets métiers Sage 100c 12.0 Type Library », comme pour le POC.
3. Adapte `src/Sage100Api.Worker/worker.json`, avec les mêmes valeurs que le POC : `SQ-SOFT`, `Bijou`.
4. Pour l'API, crée `src/Sage100Api/appsettings.Local.json`. Ce fichier n'est pas versionné, c'est là que vont les secrets :
   ```json
   { "Sage": {
       "ChaineSql": "Server=SQ-SOFT;Database=Bijou;Integrated Security=true;TrustServerCertificate=true;ApplicationIntent=ReadOnly",
       "ClesApi": { "borne-dev": "une-cle-longue-et-secrete" } } }
   ```
5. Générer > Générer la solution.
6. Lance les deux programmes : clic droit sur la solution > **Configurer les projets de démarrage** > « Plusieurs projets de démarrage ». Mets Sage100Api.Worker et Sage100Api sur « Démarrer », puis appuie sur F5.
7. Ouvre http://localhost:5080/swagger. Clique sur **Authorize**, saisis la clé, puis essaie `GET /api/v1/sante` et `POST /api/v1/commandes`.

**Sur la copie de la base uniquement** tant que la recette n'est pas terminée.

## Application tablette (borne)

L'API sert l'application sur **http://<serveur>:5080/borne/**. Il n'y a rien à installer à part : c'est une page web installable (PWA).

**Parcours :** connexion avec le login Sage (si `Authentification:Active`), paramètres de saisie à la première vente, choix du client (ou client par défaut des réglages), puis l'écran caisse, puis encaissement (plusieurs modes possibles pour une même vente, avec la monnaie à rendre en espèces), puis fin.

**Paramètres de saisie** (icône 🧾 en haut, ou la ligne jaune en tête du ticket) : la pièce que la borne crée dans Sage (bon de commande, bon de livraison ou facture), la souche et le dépôt, puis « Enregistrer ». Les choix restent sur la tablette. Avec un bon de livraison ou une facture, le stock bouge dès l'envoi à Sage ; la ligne en tête du ticket passe alors en rose. Le stock affiché et contrôlé est celui du dépôt choisi.

**Prix :** chaque article s'affiche au prix du client du ticket (tarif client, sinon sa catégorie tarifaire, remise comprise ; le prix de la fiche est barré quand une remise s'applique). Changer de client recalcule le ticket. La catégorie du client s'affiche en tête du ticket.

**Unités et conditionnements :** les tuiles et le ticket montrent l'unité de vente (« Pièce », « Unité »…). Un article vendu par conditionnement ouvre le choix du conditionnement (Unité, Carton de 12…), avec son contenu, son prix et le nombre disponible. Sur le ticket : « 2 Carton de 12 (24 Unité) ». Le code-barres d'un conditionnement l'ajoute directement.

**Écran caisse** (présenté comme une caisse : ticket à gauche, articles au centre, pavé numérique à droite) :
- Un article à gamme ouvre le choix de sa valeur ; le code-barres d'une valeur l'ajoute directement.
- Pavé : taper 3 puis toucher un article en ajoute 3. Toucher une ligne du ticket la choisit ; taper une quantité puis « Quantité » (ou « Entrée ») la remplace, « + » et « − » l'ajustent, « Supprimer ligne » la retire.
- « Client » (ou le nom du client sur le ticket) change le client sans perdre les lignes ; « Client de passage » reprend le client par défaut des réglages.
- « Mettre en attente » met le ticket de côté sur la tablette ; « Tickets en attente » le reprend. Rien n'est envoyé à Sage tant que le ticket n'est pas validé.
- « Régler » enregistre la commande et ouvre l'encaissement ; les boutons de mode (Espèces, Carte bancaire…) le font directement sur ce mode. Un vendeur non caissier voit « Valider la commande » à la place.
- « X de caisse » (caissiers) résume la journée de la borne : nombre de commandes, encaissements par mode. Les montants exacts restent ceux de Sage.
- « Imprimer » imprime le ticket en cours (ou, depuis l'écran de fin, celui de la vente terminée) au format d'une imprimante ticket de 80 mm, via l'impression du navigateur. C'est un document non fiscal : la facture est faite dans Sage.
- « Verrouiller » revient à l'écran de connexion en gardant le ticket en cours ; « Changer d'utilisateur » l'abandonne.
- Articles et clients s'affichent au choix en boutons (▦) ou en liste (☰, tableau compact). Le choix est gardé par la tablette.
- Sur téléphone, l'écran passe en deux onglets, Articles et Ticket, sans pavé (les + et − des lignes suffisent). Sur tablette en portrait, le pavé passe sous les articles.

**Hors ligne :**
- Chaque commande et chaque encaissement est d'abord enregistré sur la tablette (IndexedDB), puis envoyé à Sage. L'envoi se fait toutes les 20 secondes, au retour du réseau, ou avec le bouton « Envoyer maintenant ».
- Les renvois ne créent jamais de doublon, grâce à l'`idExterne` et à l'anti-doublon de l'API.
- Un encaissement attend toujours que sa commande soit dans Sage avant de partir.
- Un refus de Sage (422 : stock, client bloqué…) est affiché dans **Réglages > File d'envoi**, avec les boutons Réessayer et Abandonner.
- Le catalogue (clients, articles, prix, stock, modes de règlement) est gardé sur la tablette et rafraîchi toutes les 10 minutes.
- Un utilisateur déjà connecté une fois sur la tablette peut s'y reconnecter sans serveur : la borne garde une empreinte salée de son mot de passe, jamais le mot de passe. Ses ventes partent avec sa connexion ; si elle a expiré entre-temps, la borne demande de le reconnecter pour les envoyer.

**Commandes à encaisser :** le bouton « À encaisser » en haut (caissiers seulement) liste les bons de commande Sage qui ont un reste à payer, ainsi que les commandes de la borne pas encore envoyées. Elles s'affichent en liste (date, n° de pièce, référence, code client, net à payer, reste) ou en boutons. Un appui ouvre l'encaissement de la commande choisie ; la loupe 🔍 montre le contenu de la pièce (lu dans Sage, ou dans la file pour une commande de la borne pas encore envoyée). Hors ligne, la liste vient du dernier catalogue, et les encaissements encore en file sont déduits du reste.

**Utilisateurs :** le bouton 👤 en haut affiche l'utilisateur connecté ; un appui permet de changer d'utilisateur. Sans la case « Caissier » sur sa fiche collaborateur Sage, l'utilisateur prend la commande mais la borne n'affiche pas les modes de règlement.

**Montants :** la borne affiche un TTC **estimé** avec le taux de TVA des réglages. Le net à payer réel est celui calculé par Sage, visible dans la file d'envoi une fois la commande envoyée.

**Première utilisation :** l'écran Réglages s'ouvre. Saisis le nom de la borne (par exemple `BORNE1`), la clé d'API et le taux de TVA. Le numéro de vente `BORNE1-000001` devient la référence de la pièce dans Sage.

**Sur la tablette :** le cache hors ligne (service worker) n'est activé par le navigateur qu'en **HTTPS** ou sur `localhost`.
- En `http://` depuis la tablette, la file d'envoi fonctionne quand même. En revanche, la page ne peut pas être rouverte si le serveur est coupé.
- Pour les tests, dans Chrome Android : `chrome://flags`, option « Insecure origins treated as secure », ajouter `http://SQ-SOFT:5080`.
- La solution définitive est HTTPS : voir « Reste à faire ».

## Application des commerciaux (CRM)

L'API sert l'application sur **https://<serveur>:5443/crm/** (ou http://<serveur>:5080/crm/). Elle est faite pour le téléphone et s'ouvre aussi sur tablette.
- **Connexion :** la clé d'API, reprise de la borne si elle est réglée sur le même appareil, puis le login et le mot de passe Sage. Le portefeuille est celui du collaborateur rattaché à l'utilisateur Sage (champ « Utilisateur » de la fiche collaborateur, et champ « Représentant » de la fiche client).
- **Clients :** « Mon portefeuille » avec la date de la dernière activité, ou « Tous les clients » avec recherche.
- **Fiche client :** appel, e-mail et itinéraire en un geste, CA 12 mois, impayés, commandes et devis en cours, articles les plus achetés, derniers documents, contacts et adresses de livraison.
- **Activité :** visite, appel, rendez-vous, e-mail, note ou tâche, avec compte rendu, contact et position GPS. Une « prochaine action » crée une tâche dans l'agenda.
- **Agenda :** les actions à faire, en retard, aujourd'hui et à venir, avec un bouton « Fait ».
- **« Je suis chez ce client » :** enregistre la position GPS du client (utile ensuite pour les tournées de livraison).
- Le GPS du téléphone ne fonctionne qu'en **HTTPS**. Cette première version demande une connexion au serveur.

## Application des livraisons

L'API sert l'application sur **https://<serveur>:5443/livraison/**. Au bureau, on y prépare les tournées ; sur la route, le livreur suit la sienne.
- **Préparer une tournée :** date, livreur (collaborateur Sage), dépôt de départ, puis cocher les bons de commande et préparations à livrer. « Optimiser l'ordre » propose l'ordre de passage le plus court ; les flèches permettent de le changer.
- **Le livreur** se connecte avec son login Sage et ouvre « Mes tournées ». Pour chaque arrêt : appeler, « Y aller » (GPS du téléphone), puis « Livrer » avec le nom de la personne qui reçoit et sa signature, ou « Échec » avec le motif. « Itinéraire » ouvre tout le trajet restant dans Google Maps.
- **Contrôle du chargement :** avant de partir, le livreur coche chaque article reçu du dépôt (en corrigeant la quantité si besoin), puis le responsable du dépôt signe. Les écarts sont signalés.
- **Chez le client :** chaque article est coché comme livré ; sinon, la quantité est corrigée et la raison choisie (refus, endommagé, manquant, erreur de commande, retour...). Livraison complète, partielle ou échec se déduit des quantités.
- **Autres courses :** ce qu'il faut livrer ou récupérer en route hors commandes Sage (chèque, colis, matériel...), avec le suivi À faire, En cours, Fait, Reporté, Annulé.
- **Tableau de bord** (`/livraison/tableau.html`, bouton en bas de la liste des tournées) : indicateurs, arrêts par jour, par livreur, raisons des échecs et retours, courses, villes, et détail exportable en CSV. Filtres : période, livreur, dépôt, statut, client ; cliquer un jour, un livreur ou un statut filtre tout le tableau.
- Une livraison faite sur place enregistre la position du client s'il n'en avait pas : les tournées suivantes sont mieux optimisées.
- Pour optimiser depuis le dépôt, enregistre une fois sa position : `PUT /api/v1/geolocalisation/depot/1` dans Swagger. Sinon, choisis « Ma position » comme départ.
- Rien n'est écrit dans Sage : le bon de livraison se fait toujours dans Sage, à partir du bon de commande.

## Installation sur le serveur (services Windows et HTTPS)

L'API et le worker deviennent deux services Windows qui démarrent avec le PC : **Sage100Api** et **Sage100Api.Worker**. Ils redémarrent seuls en cas de plantage. Plus besoin de Visual Studio ni de F5 pour faire tourner la borne.

Dans **PowerShell ouvert en tant qu'administrateur**, depuis le dossier du dépôt (par exemple `C:\Dev\OmSage`) :

1. Arrête l'exécution dans Visual Studio. L'API et le worker lancés par F5 occupent le port 5080 et le canal du worker.
2. Crée le certificat HTTPS :
   ```
   powershell -ExecutionPolicy Bypass -File deploy\creer-certificats.ps1
   ```
3. Installe les services :
   ```
   powershell -ExecutionPolicy Bypass -File deploy\installer.ps1
   ```
   Le script demande le compte Windows des services : prends celui qui a accès à SQL Server et à Sage, par exemple ton compte. Ce compte doit avoir un mot de passe.
4. Sur chaque tablette, installe une seule fois `C:\Sage100Api\certificats\autorite-sage100api.cer` : Paramètres > Sécurité > Chiffrement et identifiants > Installer un certificat > **Certificat CA**.
5. Ouvre la borne sur **https://SQ-SOFT:5443/borne/**, ou avec l'adresse IP du serveur. Avec le cadenas, l'application se rouvre même serveur coupé.

Ce qui est installé dans `C:\Sage100Api` :
- `api\` : l'API. Ses réglages sont dans `appsettings.Local.json`, copié depuis le dépôt à la première installation, et dans `appsettings.Https.json`, écrit par le script des certificats.
- `worker\` : le worker. Son `worker.json` est gardé lors des mises à jour, et son journal est dans `logs\`.
- `certificats\` : l'autorité pour les tablettes et le certificat du serveur.

**Mise à jour après un Git > Tirer :** relance `deploy\installer.ps1`. Il arrête les services, recompile, puis redémarre.

À savoir :
- **Passage de http:// à https:// :** pour la tablette, c'est une nouvelle adresse. Vérifie d'abord que la file d'envoi de l'ancienne adresse est vide, puis ressaisis les réglages de la borne (nom, clé) sur la nouvelle.
- **Adresse IP :** le certificat couvre le nom et les adresses IP actuelles du serveur. Réserve l'adresse IP du serveur dans la box ou le routeur. Si elle change, relance le script des certificats et réinstalle l'autorité sur les tablettes.
- **Développement :** pour retravailler dans Visual Studio, arrête d'abord les services avec `Stop-Service Sage100Api, Sage100Api.Worker`.

## Tests

```
dotnet test tests/Sage100Api.Tests
```

52 tests couvrent les règles de prix (tarif client, catégorie tarifaire, gammes, conditionnements, remises et tranches), les prix envoyés au worker, le type de pièce, la souche et le dépôt, le stock par dépôt et par conditionnement, la balance âgée, les positions GPS, les routes des documents, le détail d'un bon de commande, le stock par valeur de gamme, l'encaissement par numéro de pièce, la clé d'API, la connexion des utilisateurs (jeton, collaborateur, caissier, blocage), l'accès à l'application borne, la validation (dont les gammes), le contrôle du stock, le catalogue (une partie illisible y est signalée sans le bloquer), les doublons de commandes et d'encaissements (une opération interrompue peut être renvoyée), et la conversion des erreurs Sage et du worker en codes HTTP. Ils tournent sans Sage.

## Reste à faire

- Faire la **synchronisation incrémentale** du catalogue : aujourd'hui, `/catalogue` renvoie un instantané complet.
- Sur l'écran caisse : remises et changement de prix (droit à définir), avoirs, historique client, lignes de commentaire, ouverture du tiroir-caisse.
- Créer un **compte SQL en lecture seule** dédié à l'API.

## Recette

- **01/10/2026, base Bijou, PC SQ-SOFT** : lectures SQL OK. Commande BC00034 créée par l'API (CISEL / CHORFA, 1 303,20). Acompte de 500 en espèces créé sur BC00034, avec le libellé `BRN…`. Les appels Objets Métiers nouveaux par rapport au POC sont tous validés.
