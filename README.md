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
| GET | `/api/v1/articles/{reference}` | Un article |
| GET | `/api/v1/modes-reglement` | Modes de règlement Sage |
| GET | `/api/v1/catalogue` | Instantané complet pour le mode hors ligne de la borne |
| POST | `/api/v1/commandes` | Crée un bon de commande. **Idempotent** sur `idExterne` : 201 à la création, 200 si déjà reçu. |
| GET | `/api/v1/commandes/{idExterne}` | Pièce Sage d'une commande |
| POST | `/api/v1/commandes/{idExterne}/encaissements` | Encaissement, enregistré comme acompte. Idempotent sur son propre `idExterne`. |

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
  "lignes": [ { "article": "CHORFA", "quantite": 1 } ] }
```

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

## Tests

```
dotnet test tests/Sage100Api.Tests
```

8 tests couvrent la clé d'API, la validation, les doublons de commandes et d'encaissements, et la conversion des erreurs Sage et du worker en codes HTTP. Ils tournent sans Sage.

## Reste à faire

- Exécuter l'API et le worker en **services Windows** sur le serveur Sage, avec HTTPS.
- Faire la **synchronisation incrémentale** du catalogue : aujourd'hui, `/catalogue` renvoie un instantané complet.
- Créer un **compte SQL en lecture seule** dédié à l'API.
- Développer l'**application tablette** (PWA hors ligne).
- Choisir le **journal** des acomptes par mode de règlement : aujourd'hui, Sage prend le journal par défaut du mode, par exemple BEU pour Espèces au lieu de CAIS.

## Recette

- **01/10/2026, base Bijou, PC SQ-SOFT** : lectures SQL OK. Commande BC00034 créée par l'API (CISEL / CHORFA, 1 303,20). Acompte de 500 en espèces créé sur BC00034, avec le libellé `BRN…`. Les appels Objets Métiers nouveaux par rapport au POC sont tous validés.
