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
| `tests/Sage100Api.Tests` | Tests de l'API avec un faux worker (sans Sage) |

**Règles de base :**
- On **n'écrit jamais dans Sage en SQL**. La loi anti-fraude est activée, et une écriture SQL corromprait la base (Structure des bases p.440). Toutes les écritures passent par les Objets Métiers.
- **Double barrière anti-doublon** : le journal de l'API d'abord, puis une vérification dans Sage. Pour une commande, le worker cherche `DO_RefExterne`. Pour un encaissement, il cherche l'empreinte `BRN…` dans le libellé de l'acompte.
- **Un encaissement est un acompte sur le bon de commande.** Ce choix a été validé par le POC du 29/09/2026, loi anti-fraude activée : Sage génère une facture d'acompte.

## Routes (v1)

| Méthode | Route | Description |
|---|---|---|
| GET | `/api/v1/sante` | État de l'API et du worker (sans clé) |
| GET | `/api/v1/clients?recherche=&page=&taille=` | Clients actifs |
| GET | `/api/v1/clients/{numero}` | Un client |
| GET | `/api/v1/articles?recherche=&famille=&page=&taille=` | Articles actifs, prix HT, stock et stock réservé |
| GET | `/api/v1/articles/{reference}` | Un article (`gamme1` / `gamme2` : intitulés des gammes, ou null) |
| GET | `/api/v1/articles/{reference}/gammes` | Valeurs de gamme vendables d'un article |
| GET | `/api/v1/modes-reglement` | Modes de règlement Sage |
| GET | `/api/v1/catalogue` | Instantané complet pour le mode hors ligne de la borne |
| POST | `/api/v1/commandes` | Crée un bon de commande. **Idempotent** sur `idExterne` : 201 à la création, 200 si déjà reçu. |
| GET | `/api/v1/commandes/{idExterne}` | Pièce Sage d'une commande |
| POST | `/api/v1/commandes/{idExterne}/encaissements` | Encaissement, enregistré comme acompte. Idempotent sur son propre `idExterne`. |

**Journal des encaissements :** un acompte n'a pas de journal. Sage le donne au règlement qu'il crée : c'est le journal par défaut du mode, par exemple BEU pour Espèces. Pour imposer un journal de trésorerie par mode, renseigne `journauxParMode` dans `worker.json`, par exemple `{ "Espèces": "CAIS", "Carte bancaire": "BQ1" }`, puis redémarre le worker. Un mode absent garde le journal de Sage. Un code de journal inconnu fait refuser l'encaissement avant qu'il soit créé.

Codes d'erreur :
- 401 : clé d'API absente ou invalide.
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

**Contrôle du stock :** la fenêtre « Indisponibilité en stock » de la saisie Sage n'existe pas dans les Objets Métiers, donc c'est l'API qui contrôle. Une commande qui dépasse le stock disponible (stock réel moins réservé, tous dépôts confondus) est refusée en 422, avec le détail par article. Les articles sans suivi de stock ne sont pas contrôlés. Le réglage `Sage:ControleStock` vaut :
- `Auto` (par défaut) : refus si l'option « Autoriser la gestion des stocks négatifs » est décochée dans Sage ;
- `Bloquer` : refus dans tous les cas ;
- `Aucun` : aucun contrôle.

La borne applique la même règle au moment d'ajouter un article au panier.

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

**Parcours :** choix du client, puis articles et panier (un article à gamme ouvre le choix de sa valeur ; le code-barres d'une valeur l'ajoute directement), puis encaissement (plusieurs modes possibles pour une même vente, avec la monnaie à rendre en espèces), puis fin.

**Hors ligne :**
- Chaque commande et chaque encaissement est d'abord enregistré sur la tablette (IndexedDB), puis envoyé à Sage. L'envoi se fait toutes les 20 secondes, au retour du réseau, ou avec le bouton « Envoyer maintenant ».
- Les renvois ne créent jamais de doublon, grâce à l'`idExterne` et à l'anti-doublon de l'API.
- Un encaissement attend toujours que sa commande soit dans Sage avant de partir.
- Un refus de Sage (422 : stock, client bloqué…) est affiché dans **Réglages > File d'envoi**, avec les boutons Réessayer et Abandonner.
- Le catalogue (clients, articles, prix, stock, modes de règlement) est gardé sur la tablette et rafraîchi toutes les 10 minutes.

**Montants :** la borne affiche un TTC **estimé** avec le taux de TVA des réglages. Le net à payer réel est celui calculé par Sage, visible dans la file d'envoi une fois la commande envoyée.

**Première utilisation :** l'écran Réglages s'ouvre. Saisis le nom de la borne (par exemple `BORNE1`), la clé d'API et le taux de TVA. Le numéro de vente `BORNE1-000001` devient la référence de la pièce dans Sage.

**Sur la tablette :** le cache hors ligne (service worker) n'est activé par le navigateur qu'en **HTTPS** ou sur `localhost`.
- En `http://` depuis la tablette, la file d'envoi fonctionne quand même. En revanche, la page ne peut pas être rouverte si le serveur est coupé.
- Pour les tests, dans Chrome Android : `chrome://flags`, option « Insecure origins treated as secure », ajouter `http://SQ-SOFT:5080`.
- La solution définitive est HTTPS : voir « Reste à faire ».

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

13 tests couvrent la clé d'API, l'accès à l'application borne, la validation (dont les gammes), le contrôle du stock, le catalogue, les doublons de commandes et d'encaissements, et la conversion des erreurs Sage et du worker en codes HTTP. Ils tournent sans Sage.

## Reste à faire

- Faire la **synchronisation incrémentale** du catalogue : aujourd'hui, `/catalogue` renvoie un instantané complet.
- Afficher le **stock par valeur de gamme** (F_GAMSTOCK) ; la borne montre aujourd'hui le stock total de l'article.
- Créer un **compte SQL en lecture seule** dédié à l'API.

## Recette

- **01/10/2026, base Bijou, PC SQ-SOFT** : lectures SQL OK. Commande BC00034 créée par l'API (CISEL / CHORFA, 1 303,20). Acompte de 500 en espèces créé sur BC00034, avec le libellé `BRN…`. Les appels Objets Métiers nouveaux par rapport au POC sont tous validés.
