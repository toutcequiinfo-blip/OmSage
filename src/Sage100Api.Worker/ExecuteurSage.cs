using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Objets100cLib;
using Sage100Api.Contracts;

namespace Sage100Api.Worker
{
    /// <summary>
    /// Détient la connexion Objets Métiers et exécute toutes les opérations sur un unique thread STA :
    /// les objets COM Sage ne doivent pas être partagés entre threads, et sérialiser les écritures évite
    /// les verrous entre nos propres opérations.
    /// </summary>
    internal sealed class ExecuteurSage : IDisposable
    {
        readonly WorkerConfig _config;
        readonly BlockingCollection<(WorkerRequest Requete, TaskCompletionSource<WorkerResponse> Tcs)> _file =
            new BlockingCollection<(WorkerRequest, TaskCompletionSource<WorkerResponse>)>();
        readonly Thread _thread;
        BSCIALApplication100c? _cial;
        // Référence gardée côté .NET : sinon le ramasse-miettes peut libérer l'objet compta alors que la session
        // Gescom s'en sert encore, et l'appel suivant à CptaApplication plante (0xC0000005, « Accès refusé »).
        BSCPTAApplication100c? _cpta;

        public ExecuteurSage(WorkerConfig config)
        {
            _config = config;
            _thread = new Thread(Boucle) { IsBackground = true, Name = "Sage-COM" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        public Task<WorkerResponse> Soumettre(WorkerRequest requete)
        {
            var tcs = new TaskCompletionSource<WorkerResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            _file.Add((requete, tcs));
            return tcs.Task;
        }

        void Boucle()
        {
            foreach (var (requete, tcs) in _file.GetConsumingEnumerable())
                tcs.SetResult(Traiter(requete));
            Fermer();
        }

        const int ViolationAcces = unchecked((int)0xC0000005);

        WorkerResponse Traiter(WorkerRequest requete)
        {
            var reponse = TraiterUneFois(requete, out var violation);
            if (!violation) return reponse;
            // Session COM abîmée : elle vient d'être fermée, on rejoue une fois sur une session neuve.
            // Sans risque de doublon : commande et encaissement vérifient d'abord s'ils existent déjà dans Sage.
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{requete.Operation}] nouvel essai sur une session Sage neuve.");
            return TraiterUneFois(requete, out _);
        }

        WorkerResponse TraiterUneFois(WorkerRequest requete, out bool violation)
        {
            violation = false;
            try
            {
                object resultat;
                switch (requete.Operation)
                {
                    case Operations.Ping:
                        resultat = new { sage = Session().IsOpen };
                        break;
                    case Operations.CreerCommande:
                        resultat = CreerCommande(Lire<CommandeWorkerRequest>(requete));
                        break;
                    case Operations.VerifierUtilisateur:
                        resultat = VerifierUtilisateur(Lire<ConnexionRequest>(requete));
                        break;
                    case Operations.CreerEncaissement:
                        resultat = CreerEncaissement(Lire<EncaissementCommandeRequest>(requete));
                        break;
                    default:
                        return Erreur(CodesErreur.Technique, $"Opération inconnue : {requete.Operation}");
                }
                return new WorkerResponse { Ok = true, Resultat = JsonSerializer.SerializeToElement(resultat, WorkerProtocol.Json) };
            }
            catch (ErreurMetier ex)
            {
                return Erreur(ex.Code, ex.Message);
            }
            catch (SqlException ex)
            {
                return Erreur(CodesErreur.Technique, "SQL : " + ex.Message);
            }
            catch (Exception ex)
            {
                // Les exceptions levées par les Objets Métiers sont en général des refus métier
                // (champ invalide, stock insuffisant, période clôturée...). On garde la connexion.
                // Code HRESULT et pile complets dans la console pour le diagnostic ; le code seul est renvoyé à la borne.
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{requete.Operation}] {ex.GetType().Name} 0x{ex.HResult:X8} : {ex.Message}{Environment.NewLine}{ex.StackTrace}");
                // Une erreur inattendue peut laisser la session COM dans un état instable (0xC0000005 = violation
                // d'accès dans la DLL) : on la ferme, la prochaine requête rouvre une session propre.
                Fermer();
                violation = ex.HResult == ViolationAcces;
                return Erreur(CodesErreur.SageMetier, $"{ex.Message} (0x{ex.HResult:X8})");
            }
        }

        // ---------- Connexion (manuel OM p.26-33) ----------

        BSCIALApplication100c Session()
        {
            if (_cial != null && _cial.IsOpen) return _cial;

            var cpta = new BSCPTAApplication100c();
            cpta.CompanyServer = _config.Serveur;
            cpta.CompanyDatabaseName = _config.BaseCpta;
            cpta.Loggable.UserName = _config.Utilisateur;
            cpta.Loggable.UserPwd = _config.MotDePasse;

            var cial = new BSCIALApplication100c();
            cial.CompanyServer = _config.Serveur;
            cial.CompanyDatabaseName = _config.BaseCial;
            cial.Loggable.UserName = _config.Utilisateur;
            cial.Loggable.UserPwd = _config.MotDePasse;
            cial.CptaApplication = cpta;
            try
            {
                cial.Open();
            }
            catch (Exception ex)
            {
                throw new ErreurMetier(CodesErreur.Technique, "Connexion Sage impossible : " + ex.Message);
            }
            _cial = cial;
            _cpta = cpta;
            return cial;
        }

        /// <summary>
        /// Vérifie un login Sage en ouvrant une session séparée avec ce nom et ce mot de passe, refermée aussitôt :
        /// c'est Sage qui contrôle le mot de passe. La session du worker (compte de worker.json) n'est pas touchée.
        /// </summary>
        UtilisateurVerifie VerifierUtilisateur(ConnexionRequest r)
        {
            var cpta = new BSCPTAApplication100c();
            cpta.CompanyServer = _config.Serveur;
            cpta.CompanyDatabaseName = _config.BaseCpta;
            cpta.Loggable.UserName = r.Utilisateur;
            cpta.Loggable.UserPwd = r.MotDePasse;

            var cial = new BSCIALApplication100c();
            cial.CompanyServer = _config.Serveur;
            cial.CompanyDatabaseName = _config.BaseCial;
            cial.Loggable.UserName = r.Utilisateur;
            cial.Loggable.UserPwd = r.MotDePasse;
            cial.CptaApplication = cpta;
            try
            {
                cial.Open();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Connexion refusée pour {r.Utilisateur} (0x{ex.HResult:X8}) : {ex.Message}");
                throw new ErreurMetier(CodesErreur.AccesRefuse, $"Connexion refusée par Sage : {ex.Message}");
            }
            try
            {
                if (!cial.IsOpen)
                    throw new ErreurMetier(CodesErreur.AccesRefuse, "Connexion refusée par Sage.");
                var resultat = new UtilisateurVerifie { Utilisateur = r.Utilisateur, Administrateur = cial.Loggable.IsAdministrator };
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Connexion de {r.Utilisateur}{(resultat.Administrateur ? " (administrateur)" : "")}");
                return resultat;
            }
            finally
            {
                try { if (cial.IsOpen) cial.Close(); } catch { /* session de contrôle seulement */ }
                GC.KeepAlive(cpta);
            }
        }

        void Fermer()
        {
            try { if (_cial != null && _cial.IsOpen) _cial.Close(); } catch { /* arrêt */ }
            _cial = null;
            _cpta = null;
        }

        // ---------- Commande (processus IPMDocument, manuel OM p.110 + annexe) ----------

        /// <summary>
        /// Un article à gamme doit passer par AddArticleMonoGamme / AddArticleDoubleGamme avec ses énumérés ; un conditionnement
        /// (« Carton de 12 ») par AddArticleConditionnement, qui met DL_Qte = quantité x contenu du conditionnement (manuel OM p.393).
        /// </summary>
        static IBODocumentLigne3 AjouterLigne(IPMDocument pm, IBOArticle3 article, LigneCommande l)
        {
            if (!string.IsNullOrEmpty(l.Conditionnement))
            {
                var conds = (IBOArticleCondFactory)article.FactoryArticleCond;
                var contenu = l.QuantiteConditionnement ?? 0;
                if (!conds.ExistEnumere(l.Conditionnement, contenu))
                    throw new ErreurMetier(CodesErreur.SageMetier,
                        $"Conditionnement « {l.Conditionnement} » ({contenu}) inconnu pour l'article {l.Article} ({Conditionnements(conds)}).");
                return pm.AddArticleConditionnement(conds.ReadEnumere(l.Conditionnement, contenu), l.Quantite);
            }

            var gamme1 = (IBOArticleGammeEnumFactory)article.FactoryArticleGammeEnum1;
            if (string.IsNullOrEmpty(l.Gamme1))
            {
                if (gamme1.List.Count > 0)
                    throw new ErreurMetier(CodesErreur.SageMetier,
                        $"L'article {l.Article} est géré en gamme : précisez la valeur ({Enumeres(gamme1)}).");
                return pm.AddArticle(article, l.Quantite);
            }

            var e1 = LireEnumere(gamme1, l.Article, l.Gamme1!);
            if (string.IsNullOrEmpty(l.Gamme2))
                return pm.AddArticleMonoGamme(e1, l.Quantite);
            var e2 = LireEnumere((IBOArticleGammeEnumFactory)article.FactoryArticleGammeEnum2, l.Article, l.Gamme2!);
            return pm.AddArticleDoubleGamme(e1, e2, l.Quantite);
        }

        static bool SuiviParLot(IBOArticle3 article) =>
            article.AR_SuiviStock == SuiviStockType.SuiviStockTypeLot || article.AR_SuiviStock == SuiviStockType.SuiviStockTypeSerie;

        /// <summary>
        /// Bon de livraison ou facture d'un article suivi par lot ou par numéro de série : Sage exige le lot sur la ligne qui
        /// sort le stock (sinon « L'état du stock ne permet pas de créer la ligne », code 2881). Les lots non épuisés du dépôt
        /// de la ligne sont pris du plus ancien (péremption, sinon ordre de Sage) au plus récent, une ligne par lot ;
        /// un numéro de série vaut une unité. SetDefaultLot reprend l'article : le tarif est appliqué ensuite.
        /// </summary>
        static List<IBODocumentVenteLigne3> AffecterLots(IPMDocument pm, IBODocumentVenteLigne3 ligne, IBOArticle3 article, LigneCommande l, string idExterne)
        {
            if (!string.IsNullOrEmpty(l.Gamme1) || !string.IsNullOrEmpty(l.Conditionnement))
                throw new ErreurMetier(CodesErreur.SageMetier,
                    $"L'article {l.Article} est suivi par lot ou série : sur un bon de livraison ou une facture, la borne ne gère pas encore sa gamme ou son conditionnement. Saisissez-le en bon de commande.");
            var depot = ligne.Depot;
            if (depot == null)
                throw new ErreurMetier(CodesErreur.SageMetier, $"Article {l.Article} suivi par lot : aucun dépôt sur la ligne.");

            var lots = new List<(IBOArticleDepotLot Lot, double Dispo)>();
            var depots = (IBOArticleDepotFactory)article.FactoryArticleDepot;
            if (depots.ExistDepot(depot))
            {
                var artDepot = depots.ReadDepot(depot);
                foreach (IBOArticleDepotLot lot in ((IBOArticleDepotLotFactory)artDepot.FactoryArticleDepotLot).QueryNonEpuise())
                {
                    var dispo = lot.StockReel() - lot.QteReserved;
                    if (dispo > 0.000001) lots.Add((lot, dispo));
                }
            }
            // Ordre de Sage conservé à péremption égale (tri stable) ; un lot sans péremption passe après ceux qui en ont une.
            var ordonnes = lots.Select((x, i) => (x, i))
                .OrderBy(t => t.x.Lot.DatePeremption.Year < 1900 ? DateTime.MaxValue : t.x.Lot.DatePeremption).ThenBy(t => t.i)
                .Select(t => t.x).ToList();

            var serie = article.AR_SuiviStock == SuiviStockType.SuiviStockTypeSerie;
            var reste = ligne.DL_Qte;
            var total = 0d;
            foreach (var x in ordonnes) total += serie ? 1 : x.Dispo;
            if (total + 0.000001 < reste)
                throw new ErreurMetier(CodesErreur.SageMetier,
                    $"Stock insuffisant en {(serie ? "numéros de série" : "lots")} pour {l.Article} dans le dépôt {depot.DE_Intitule} : {total} disponible(s), {reste} demandé(s).");

            var resultat = new List<IBODocumentVenteLigne3>();
            foreach (var x in ordonnes)
            {
                if (reste <= 0.000001) break;
                var qte = serie ? 1 : Math.Min(reste, x.Dispo);
                var cible = resultat.Count == 0 ? ligne : (IBODocumentVenteLigne3)pm.AddArticle(article, qte);
                cible.SetDefaultLot(x.Lot, qte);
                resultat.Add(cible);
                reste -= qte;
                Journal(idExterne, $"{l.Article} : {qte} pris dans le {(serie ? "numéro de série" : "lot")} {x.Lot.NoSerie} (dépôt {depot.DE_Intitule}).");
            }
            return resultat;
        }

        static string Conditionnements(IBOArticleCondFactory f)
        {
            var valeurs = new List<string>();
            foreach (IBOArticleCond3 c in f.List) valeurs.Add($"{c.EC_Enumere} = {c.EC_Quantite}");
            return valeurs.Count == 0 ? "aucun" : string.Join(", ", valeurs);
        }

        /// <summary>
        /// Prix et remises calculés par l'API selon le tarif du client. Les Objets Métiers ne reprennent que le tarif propre au
        /// client (pas sa catégorie tarifaire) et ne posent pas les remises sans SetDefaultRemise : on corrige la ligne si besoin.
        /// Une erreur ici ne bloque pas la vente (la ligne garde le prix calculé par Sage) : elle est écrite dans la console.
        /// </summary>
        static void AppliquerTarif(IBODocumentVenteLigne3 ligne, LigneCommande l, string idExterne)
        {
            try { ligne.SetDefaultRemise(); }
            catch (Exception ex) { Journal(idExterne, $"{l.Article} : remise par défaut non appliquée (0x{ex.HResult:X8}) : {ex.Message}"); }

            if (l.PrixUnitaire is double prix && prix > 0)
            {
                try
                {
                    if (l.PrixTTC)
                    {
                        if (!ligne.TTC) ligne.TTC = true;
                        Ecrire(ligne, "DL_PUTTC", prix);
                    }
                    else
                    {
                        if (ligne.TTC) ligne.TTC = false;
                        if (Math.Abs(ligne.DL_PrixUnitaire - prix) > 0.00005)
                        {
                            Journal(idExterne, $"{l.Article} : prix Sage {ligne.DL_PrixUnitaire} remplacé par le tarif du client {prix}.");
                            ligne.DL_PrixUnitaire = prix;
                        }
                    }
                }
                catch (Exception ex) { Journal(idExterne, $"{l.Article} : prix {prix} non appliqué (0x{ex.HResult:X8}) : {ex.Message}"); }
            }

            if (l.Remises == null || l.Remises.Count == 0) return;
            try
            {
                // IRemise2.Remise(1..3) : propriété indexée COM, lue par IDispatch (même écriture qu'en VB : .Remise.Remise(1).REM_Valeur).
                var remise = Lire(ligne, "Remise")!;
                for (int i = 0; i < 3; i++)
                {
                    var r = Lire(remise, "Remise", i + 1)!;
                    Ecrire(r, "REM_Type", i < l.Remises.Count ? l.Remises[i].Type : 0);
                    Ecrire(r, "REM_Valeur", i < l.Remises.Count ? l.Remises[i].Valeur : 0d);
                }
            }
            catch (Exception ex) { Journal(idExterne, $"{l.Article} : remises non appliquées (0x{ex.HResult:X8}) : {ex.Message}"); }
        }

        static IBOArticleGammeEnum3 LireEnumere(IBOArticleGammeEnumFactory f, string article, string valeur)
        {
            if (!f.ExistEnumere(valeur))
                throw new ErreurMetier(CodesErreur.SageMetier, $"Gamme « {valeur} » inconnue pour l'article {article} ({Enumeres(f)}).");
            return f.ReadEnumere(valeur);
        }

        static string Enumeres(IBOArticleGammeEnumFactory f)
        {
            var valeurs = new List<string>();
            foreach (IBOArticleGammeEnum3 e in f.List) valeurs.Add(e.EG_Enumere);
            return string.Join(", ", valeurs);
        }

        CommandeResult CreerCommande(CommandeWorkerRequest demande)
        {
            var c = demande.Commande;
            var type = TypesPiece.Normaliser(c.TypeDocument) ?? TypesPiece.Commande;
            var existante = PieceParRefExterne(c.IdExterne);
            var cial = Session();
            if (existante != null)
            {
                var doc = cial.FactoryDocumentVente.ReadPiece(TypeOm(existante.Value.Type), existante.Value.Piece);
                return new CommandeResult
                {
                    IdExterne = c.IdExterne, Piece = existante.Value.Piece, NetAPayer = doc.DO_NetAPayer, DejaExistante = true,
                    TypeDocument = NomType(existante.Value.Type),
                };
            }

            var cpta = _cpta!;
            if (!cpta.FactoryClient.ExistNumero(c.Client))
                throw new ErreurMetier(CodesErreur.Introuvable, $"Client inconnu : {c.Client}");
            foreach (var l in c.Lignes)
                if (!cial.FactoryArticle.ExistReference(l.Article))
                    throw new ErreurMetier(CodesErreur.Introuvable, $"Article inconnu : {l.Article}");

            // Bon de livraison et facture font sortir le stock dès leur création (manuel OM p.395).
            IPMDocument pm = cial.CreateProcess_Document(TypeOm(TypesPiece.DoType(type)));
            var entete = (IBODocumentVente3)pm.Document;
            entete.SetDefaultClient(cpta.FactoryClient.ReadNumero(c.Client));
            AffecterSouche(entete, c.Souche);
            AffecterDepot(entete, c.Depot);
            entete.DO_RefExterne = c.IdExterne;
            entete.DO_Ref = Tronquer(string.IsNullOrEmpty(c.Reference) ? c.IdExterne : c.Reference!, Validation.LongueurReference);
            AffecterCollaborateur(entete, demande.Auteur, c.IdExterne);

            // Lignes du processus dans l'ordre (un article suivi en lot peut en occuper plusieurs) : pour nommer l'article
            // dans les erreurs de Sage, qui ne donnent que l'indice de la ligne.
            var libelles = new List<string>();
            var sortieStock = type != TypesPiece.Commande;
            for (int i = 0; i < c.Lignes.Count; i++)
            {
                var l = c.Lignes[i];
                var article = cial.FactoryArticle.ReadReference(l.Article);
                var ligne = (IBODocumentVenteLigne3)AjouterLigne(pm, article, l);
                var lignes = sortieStock && SuiviParLot(article) ? AffecterLots(pm, ligne, article, l, c.IdExterne) : new List<IBODocumentVenteLigne3> { ligne };
                foreach (var x in lignes)
                {
                    x.DL_RefExterne = Tronquer($"{c.IdExterne}-{i + 1}", Validation.LongueurIdExterne);
                    AppliquerTarif(x, l, c.IdExterne);
                    libelles.Add($"{l.Article}{(x.Depot != null ? ", dépôt " + x.Depot.DE_Intitule : "")}{(string.IsNullOrEmpty(x.LS_NoSerie) ? "" : ", lot " + x.LS_NoSerie)}");
                }
            }

            if (!pm.CanProcess)
                throw new ErreurMetier(CodesErreur.SageMetier, Erreurs(pm.Errors, libelles));
            pm.Process();

            var piece = (IBODocumentVente3)pm.DocumentResult;
            Console.WriteLine($"{TypesPiece.Libelle(type)} {c.IdExterne} -> {piece.DO_Piece}{(demande.Auteur != null ? " par " + demande.Auteur.Utilisateur : "")}");
            return new CommandeResult { IdExterne = c.IdExterne, Piece = piece.DO_Piece, NetAPayer = piece.DO_NetAPayer, TypeDocument = type };
        }

        /// <summary>Souche choisie sur la borne (DO_Souche, 0 = première) : numérotation de cette souche.</summary>
        void AffecterSouche(IBODocumentVente3 entete, int? souche)
        {
            if (souche == null) return;
            var intitule = LireSql("SELECT TOP 1 S_Intitule FROM P_SOUCHEVENTE WHERE cbIndice = @n AND S_Intitule <> ''", souche.Value + 1);
            if (intitule == null || !_cial!.FactorySoucheVente.ExistIntitule(intitule))
                throw new ErreurMetier(CodesErreur.Introuvable, $"Souche {souche} inconnue dans Sage (Paramètres société / Documents).");
            entete.Souche = _cial.FactorySoucheVente.ReadIntitule(intitule);
            entete.SetDefaultDO_Piece();
        }

        /// <summary>Dépôt choisi sur la borne : les lignes le reprennent (SetDefaultArticle : Depot = Entete.DepotStockage).</summary>
        void AffecterDepot(IBODocumentVente3 entete, int? depot)
        {
            if (depot == null) return;
            var intitule = LireSql("SELECT TOP 1 DE_Intitule FROM F_DEPOT WHERE DE_No = @n", depot.Value);
            if (intitule == null || !_cial!.FactoryDepot.ExistIntitule(intitule))
                throw new ErreurMetier(CodesErreur.Introuvable, $"Dépôt {depot} inconnu dans Sage.");
            var d = _cial.FactoryDepot.ReadIntitule(intitule);
            // Le manuel nomme la propriété DO_DepotStockage dans la liste et DepotStockage dans ses exemples : on essaie les deux.
            try { Ecrire(entete, "DepotStockage", d); }
            catch (Exception) { Ecrire(entete, "DO_DepotStockage", d); }
        }

        /// <summary>DO_Type de F_DOCENTETE -> type de document Objets Métiers.</summary>
        static DocumentType TypeOm(int doType)
        {
            switch (doType)
            {
                case 3: return DocumentType.DocumentTypeVenteLivraison;
                case 6: return DocumentType.DocumentTypeVenteFacture;
                case 7: return DocumentType.DocumentTypeVenteFactureCpta;
                default: return DocumentType.DocumentTypeVenteCommande;
            }
        }

        static string NomType(int doType) => doType == 3 ? TypesPiece.Livraison : doType >= 6 ? TypesPiece.Facture : TypesPiece.Commande;

        /// <summary>Collaborateur de l'utilisateur connecté sur le bon de commande, pour savoir dans Sage qui a fait la saisie.</summary>
        void AffecterCollaborateur(IBODocumentVente3 entete, Auteur? auteur, string idExterne)
        {
            if (auteur == null || string.IsNullOrEmpty(auteur.CollaborateurNom)) return;
            try
            {
                entete.Collaborateur = _cpta!.FactoryCollaborateur.ReadNomPrenom(auteur.CollaborateurNom, auteur.CollaborateurPrenom ?? "");
            }
            catch (Exception ex)
            {
                // La vente passe quand même : le collaborateur est une information, pas une condition.
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Commande {idExterne} : collaborateur {auteur.CollaborateurNom} {auteur.CollaborateurPrenom} non affecté (0x{ex.HResult:X8}) : {ex.Message}");
            }
        }

        /// <summary>Pièce déjà créée avec cet identifiant externe (bon de commande, de livraison ou facture), ou null.</summary>
        (string Piece, int Type)? PieceParRefExterne(string idExterne) =>
            Piece("SELECT TOP 1 DO_Piece, DO_Type FROM F_DOCENTETE WHERE DO_Domaine = 0 AND DO_Type IN (1, 3, 6, 7) AND DO_RefExterne = @r ORDER BY DO_Type", idExterne);

        /// <summary>Type d'une pièce de vente par son numéro (BC, BL ou facture), ou null.</summary>
        (string Piece, int Type)? PieceParNumero(string piece) =>
            Piece("SELECT TOP 1 DO_Piece, DO_Type FROM F_DOCENTETE WHERE DO_Domaine = 0 AND DO_Type IN (1, 3, 6, 7) AND DO_Piece = @r ORDER BY DO_Type", piece);

        (string Piece, int Type)? Piece(string sql, string valeur)
        {
            using (var cnx = new SqlConnection(_config.ConnexionSql()))
            using (var cmd = new SqlCommand(sql, cnx))
            {
                cmd.Parameters.AddWithValue("@r", valeur);
                cnx.Open();
                using (var r = cmd.ExecuteReader())
                    return r.Read() ? (r.GetString(0), Convert.ToInt32(r.GetValue(1))) : ((string, int)?)null;
            }
        }

        string? LireSql(string sql, int n)
        {
            using (var cnx = new SqlConnection(_config.ConnexionSql()))
            using (var cmd = new SqlCommand(sql, cnx))
            {
                cmd.Parameters.AddWithValue("@n", n);
                cnx.Open();
                return cmd.ExecuteScalar() as string;
            }
        }

        // ---------- Encaissement = acompte sur la pièce (validé par le POC sur bon de commande, loi anti-fraude activée) ----------
        // Bon de livraison : acompte sur la pièce, comme un bon de commande. Facture : règlement client (ReglerFacture).

        EncaissementResult CreerEncaissement(EncaissementCommandeRequest r)
        {
            var e = r.Encaissement;
            var cial = Session();
            var trouvee = PieceParNumero(r.PieceCommande);
            if (trouvee == null || !cial.FactoryDocumentVente.ExistPiece(TypeOm(trouvee.Value.Type), r.PieceCommande))
                throw new ErreurMetier(CodesErreur.Introuvable, $"Pièce introuvable : {r.PieceCommande}");
            var bc = cial.FactoryDocumentVente.ReadPiece(TypeOm(trouvee.Value.Type), r.PieceCommande);
            bc.Refresh(); // le cache OM peut être périmé si Sage a modifié la pièce (manuel OM p.41)
            if (trouvee.Value.Type >= 6) return ReglerFacture(bc, r);

            // Seconde barrière anti-doublon (la première est le journal de l'API) : le libellé de l'acompte porte
            // une empreinte courte et stable de l'identifiant externe.
            var marque = Marque(e.IdExterne);
            foreach (IBODocumentAcompte3 existant in bc.FactoryDocumentAcompte.List)
            {
                if (existant.DR_Libelle != null && existant.DR_Libelle.StartsWith(marque, StringComparison.Ordinal))
                    return new EncaissementResult { IdExterne = e.IdExterne, PieceCommande = r.PieceCommande, Montant = existant.DR_Montant, DejaExistant = true };
            }

            if (!_cpta!.FactoryReglement.ExistIntitule(e.Mode))
                throw new ErreurMetier(CodesErreur.Introuvable, $"Mode de règlement inconnu dans Sage : {e.Mode}");
            // Contrôlé avant de créer l'acompte : une erreur de réglage ne doit pas laisser un acompte à moitié traité.
            var journal = JournalDuMode(e.Mode);

            var ac = (IBODocumentAcompte3)bc.FactoryDocumentAcompte.Create();
            ac.DR_Date = DateTime.Today;
            ac.DR_Libelle = Tronquer($"{marque} {e.ReferencePaiement}".TrimEnd(), 35);
            ac.DR_Montant = e.Montant;
            ac.Reglement = _cpta!.FactoryReglement.ReadIntitule(e.Mode);
            ac.WriteDefault();
            if (journal != null) ChangerJournal(ac, journal, e.IdExterne);

            Console.WriteLine($"Encaissement {e.IdExterne} ({e.Mode} {e.Montant}) -> acompte sur {r.PieceCommande}{(r.Auteur != null ? " par " + r.Auteur.Utilisateur : "")}");
            return new EncaissementResult { IdExterne = e.IdExterne, PieceCommande = r.PieceCommande, Montant = e.Montant, Impute = true };
        }

        // ---------- Facture : règlement client (pas d'acompte), imputé sur l'échéance quand Sage l'accepte ----------

        /// <summary>
        /// Une facture se règle par un règlement client, comme dans Sage (Règlements clients), pas par un acompte.
        /// Le règlement est créé puis imputé sur les échéances de la facture (IPMReglerEcheances, manuel OM p.102).
        /// Loi anti-fraude activée : Sage n'impute qu'une facture validée (manuel OM p.445) ; sinon le règlement reste
        /// à lettrer dans Sage, la vente n'est pas bloquée.
        /// </summary>
        EncaissementResult ReglerFacture(IBODocumentVente3 facture, EncaissementCommandeRequest r)
        {
            var e = r.Encaissement;
            var cial = _cial!;
            var client = facture.TiersPayeur;
            var marque = Marque(e.IdExterne);
            // Seconde barrière anti-doublon : un règlement de ce client portant l'empreinte de l'identifiant externe.
            if (MontantRegle(client.CT_Num, marque) is double deja)
                return new EncaissementResult { IdExterne = e.IdExterne, PieceCommande = r.PieceCommande, Montant = deja, DejaExistant = true, Nature = "reglement" };

            if (!_cpta!.FactoryReglement.ExistIntitule(e.Mode))
                throw new ErreurMetier(CodesErreur.Introuvable, $"Mode de règlement inconnu dans Sage : {e.Mode}");
            var journal = JournalDuMode(e.Mode) ?? JournalHabituel(e.Mode)
                ?? throw new ErreurMetier(CodesErreur.SageMetier,
                    $"Aucun journal de trésorerie connu pour le mode {e.Mode} : indiquez-le dans worker.json (journauxParMode).");

            var rg = (IBODocumentReglement)cial.FactoryDocumentReglement.Create();
            rg.TiersPayeur = client;
            rg.RG_Date = DateTime.Today;
            rg.RG_Reference = Tronquer(r.PieceCommande, 8);
            rg.RG_Libelle = Tronquer($"{marque} {e.ReferencePaiement}".TrimEnd(), 35);
            rg.RG_Montant = e.Montant;
            Ecrire(rg, "Reglement", _cpta.FactoryReglement.ReadIntitule(e.Mode)); // IPBReglement : affecté par IDispatch comme sur l'acompte
            rg.Journal = journal;
            rg.CompteG = client.CompteGPrinc;
            rg.WriteDefault();

            var impute = false;
            try
            {
                var regler = cial.CreateProcess_ReglerEcheances();
                regler.Reglement = rg;
                foreach (IBODocumentEcheance3 ech in facture.FactoryDocumentEcheance.List) regler.AddDocumentEcheance(ech);
                if (regler.CanProcess) { regler.Process(); impute = true; }
                else Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Encaissement {e.IdExterne} : règlement à lettrer dans Sage ({Erreurs(regler.Errors)}).");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Encaissement {e.IdExterne} : règlement à lettrer dans Sage (0x{ex.HResult:X8}) : {ex.Message}");
            }

            Console.WriteLine($"Encaissement {e.IdExterne} ({e.Mode} {e.Montant}) -> règlement client {(impute ? "imputé sur" : "à lettrer avec")} {r.PieceCommande}" +
                $"{(r.Auteur != null ? " par " + r.Auteur.Utilisateur : "")}");
            return new EncaissementResult { IdExterne = e.IdExterne, PieceCommande = r.PieceCommande, Montant = e.Montant, Nature = "reglement", Impute = impute };
        }

        /// <summary>Montant d'un règlement client déjà créé avec cette empreinte, ou null.</summary>
        double? MontantRegle(string client, string marque)
        {
            using (var cnx = new SqlConnection(_config.ConnexionSql()))
            using (var cmd = new SqlCommand("SELECT TOP 1 RG_Montant FROM F_CREGLEMENT WHERE RG_Type = 0 AND CT_NumPayeur = @c AND RG_Libelle LIKE @m", cnx))
            {
                cmd.Parameters.AddWithValue("@c", client);
                cmd.Parameters.AddWithValue("@m", marque + "%");
                cnx.Open();
                var v = cmd.ExecuteScalar();
                return v == null || v is DBNull ? (double?)null : Convert.ToDouble(v);
            }
        }

        /// <summary>Journal du dernier règlement client saisi avec ce mode (celui que Sage propose d'habitude), ou null.</summary>
        IBOJournal3? JournalHabituel(string mode)
        {
            string? code;
            using (var cnx = new SqlConnection(_config.ConnexionSql()))
            using (var cmd = new SqlCommand(
                "SELECT TOP 1 r.JO_Num FROM F_CREGLEMENT r JOIN P_REGLEMENT p ON p.cbIndice = r.N_Reglement " +
                "WHERE r.RG_Type = 0 AND p.R_Intitule = @m AND r.JO_Num <> '' ORDER BY r.RG_No DESC", cnx))
            {
                cmd.Parameters.AddWithValue("@m", mode);
                cnx.Open();
                code = (cmd.ExecuteScalar() as string)?.Trim();
            }
            return !string.IsNullOrEmpty(code) && _cpta!.FactoryJournal.ExistNumero(code) ? _cpta.FactoryJournal.ReadNumero(code) : null;
        }

        // ---------- Journal par mode de règlement (worker.json : journauxParMode) ----------

        /// <summary>Journal de trésorerie demandé pour ce mode, ou null pour garder celui que Sage choisit.</summary>
        IBOJournal3? JournalDuMode(string mode)
        {
            string? code = null;
            foreach (var paire in _config.JournauxParMode)
                if (string.Equals(paire.Key.Trim(), mode.Trim(), StringComparison.OrdinalIgnoreCase)) code = paire.Value?.Trim();
            if (string.IsNullOrEmpty(code)) return null;
            if (!_cpta!.FactoryJournal.ExistNumero(code))
                throw new ErreurMetier(CodesErreur.SageMetier, $"Journal {code} (réglé pour le mode {mode} dans worker.json) inconnu dans Sage.");
            return _cpta.FactoryJournal.ReadNumero(code);
        }

        /// <summary>
        /// L'acompte n'a pas de journal : Sage le donne au règlement qu'il crée (journal par défaut du mode).
        /// On corrige ce règlement, modifiable tant qu'il n'est pas comptabilisé (manuel OM, IBODocumentReglement).
        /// Le compte général du règlement reste celui du client (manuel OM p.102) : seul le journal change.
        /// </summary>
        static void ChangerJournal(IBODocumentAcompte3 ac, IBOJournal3 journal, string idExterne)
        {
            try
            {
                if (!ac.HasDocumentReglement)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Encaissement {idExterne} : pas de règlement lié à l'acompte, journal inchangé.");
                    return;
                }
                var rg = ac.DocumentReglement;
                if (rg.Journal != null && rg.Journal.JO_Num == journal.JO_Num) return;
                rg.Journal = journal;
                rg.Write();
            }
            catch (Exception ex)
            {
                // L'acompte est créé : on ne le fait pas échouer (un renvoi le retrouverait sans corriger le journal).
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Encaissement {idExterne} : journal {journal.JO_Num} non appliqué (0x{ex.HResult:X8}) : {ex.Message}");
            }
        }

        /// <summary>« BRN » + 12 caractères hexadécimaux du SHA-1 de l'identifiant externe (15 caractères, stable).</summary>
        internal static string Marque(string idExterne)
        {
            using (var sha = SHA1.Create())
            {
                var h = sha.ComputeHash(Encoding.UTF8.GetBytes(idExterne));
                var sb = new StringBuilder("BRN");
                for (int i = 0; i < 6; i++) sb.Append(h[i].ToString("X2"));
                return sb.ToString();
            }
        }

        // ---------- Utilitaires ----------

        static T Lire<T>(WorkerRequest r) =>
            r.Donnees.HasValue
                ? r.Donnees.Value.Deserialize<T>(WorkerProtocol.Json) ?? throw new ErreurMetier(CodesErreur.Technique, "Données vides.")
                : throw new ErreurMetier(CodesErreur.Technique, "Données manquantes.");

        /// <summary>Erreurs d'un processus ; libelles (lignes du document dans l'ordre) nomme l'article de la ligne en faute.</summary>
        static string Erreurs(IFailInfoCol erreurs, List<string>? libelles = null)
        {
            var messages = new List<string>();
            for (int i = 1; i <= erreurs.Count; i++)
            {
                IFailInfo f = erreurs[i];
                var ligne = libelles != null && f.Indice >= 1 && f.Indice <= libelles.Count ? $"Ligne {f.Indice} ({libelles[f.Indice - 1]}) : " : "";
                messages.Add($"{ligne}{f.Text} (code {f.ErrorCode}, indice {f.Indice})");
            }
            return messages.Count == 0 ? "Sage a refusé le document sans préciser d'erreur." : string.Join(" ; ", messages);
        }

        static string Tronquer(string s, int max) => s.Length <= max ? s : s.Substring(0, max);

        static void Journal(string idExterne, string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Pièce {idExterne} : {message}");

        // Propriétés lues et écrites par IDispatch : celles dont le nom varie selon les versions des Objets Métiers,
        // et les propriétés COM indexées (Remise(1)), que C# n'appelle pas directement.
        static object? Lire(object o, string nom, params object[] index) =>
            o.GetType().InvokeMember(nom, BindingFlags.GetProperty, null, o, index.Length == 0 ? null : index);

        static void Ecrire(object o, string nom, object valeur) =>
            o.GetType().InvokeMember(nom, BindingFlags.SetProperty, null, o, new[] { valeur });

        static WorkerResponse Erreur(string code, string message) =>
            new WorkerResponse { Ok = false, CodeErreur = code, MessageErreur = message };

        public void Dispose()
        {
            _file.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(10));
        }
    }

    internal sealed class ErreurMetier : Exception
    {
        public string Code { get; }
        public ErreurMetier(string code, string message) : base(message) => Code = code;
    }
}
