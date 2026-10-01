using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SqlClient;
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
                        resultat = CreerCommande(Lire<CommandeRequest>(requete));
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

        void Fermer()
        {
            try { if (_cial != null && _cial.IsOpen) _cial.Close(); } catch { /* arrêt */ }
            _cial = null;
            _cpta = null;
        }

        // ---------- Commande (processus IPMDocument, manuel OM p.110 + annexe) ----------

        /// <summary>Un article à gamme doit passer par AddArticleMonoGamme / AddArticleDoubleGamme avec ses énumérés.</summary>
        static IBODocumentLigne3 AjouterLigne(IPMDocument pm, IBOArticle3 article, LigneCommande l)
        {
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

        CommandeResult CreerCommande(CommandeRequest c)
        {
            var existante = PieceParRefExterne(c.IdExterne);
            var cial = Session();
            if (existante != null)
            {
                var doc = cial.FactoryDocumentVente.ReadPiece(DocumentType.DocumentTypeVenteCommande, existante);
                return new CommandeResult { IdExterne = c.IdExterne, Piece = existante, NetAPayer = doc.DO_NetAPayer, DejaExistante = true };
            }

            var cpta = _cpta!;
            if (!cpta.FactoryClient.ExistNumero(c.Client))
                throw new ErreurMetier(CodesErreur.Introuvable, $"Client inconnu : {c.Client}");
            foreach (var l in c.Lignes)
                if (!cial.FactoryArticle.ExistReference(l.Article))
                    throw new ErreurMetier(CodesErreur.Introuvable, $"Article inconnu : {l.Article}");

            IPMDocument pm = cial.CreateProcess_Document(DocumentType.DocumentTypeVenteCommande);
            var entete = (IBODocumentVente3)pm.Document;
            entete.SetDefaultClient(cpta.FactoryClient.ReadNumero(c.Client));
            entete.DO_RefExterne = c.IdExterne;
            entete.DO_Ref = Tronquer(string.IsNullOrEmpty(c.Reference) ? c.IdExterne : c.Reference!, Validation.LongueurReference);

            for (int i = 0; i < c.Lignes.Count; i++)
            {
                var ligne = (IBODocumentVenteLigne3)AjouterLigne(pm, cial.FactoryArticle.ReadReference(c.Lignes[i].Article), c.Lignes[i]);
                ligne.DL_RefExterne = Tronquer($"{c.IdExterne}-{i + 1}", Validation.LongueurIdExterne);
            }

            if (!pm.CanProcess)
                throw new ErreurMetier(CodesErreur.SageMetier, Erreurs(pm.Errors));
            pm.Process();

            var bc = (IBODocumentVente3)pm.DocumentResult;
            Console.WriteLine($"Commande {c.IdExterne} -> {bc.DO_Piece}");
            return new CommandeResult { IdExterne = c.IdExterne, Piece = bc.DO_Piece, NetAPayer = bc.DO_NetAPayer };
        }

        string? PieceParRefExterne(string idExterne)
        {
            using (var cnx = new SqlConnection(_config.ConnexionSql()))
            using (var cmd = new SqlCommand(
                "SELECT TOP 1 DO_Piece FROM F_DOCENTETE WHERE DO_Domaine = 0 AND DO_Type = 1 AND DO_RefExterne = @r", cnx))
            {
                cmd.Parameters.AddWithValue("@r", idExterne);
                cnx.Open();
                return cmd.ExecuteScalar() as string;
            }
        }

        // ---------- Encaissement = acompte sur le bon de commande (validé par le POC, loi anti-fraude activée) ----------

        EncaissementResult CreerEncaissement(EncaissementCommandeRequest r)
        {
            var e = r.Encaissement;
            var cial = Session();
            if (!cial.FactoryDocumentVente.ExistPiece(DocumentType.DocumentTypeVenteCommande, r.PieceCommande))
                throw new ErreurMetier(CodesErreur.Introuvable, $"Bon de commande introuvable : {r.PieceCommande}");
            var bc = cial.FactoryDocumentVente.ReadPiece(DocumentType.DocumentTypeVenteCommande, r.PieceCommande);
            bc.Refresh(); // le cache OM peut être périmé si Sage a modifié la pièce (manuel OM p.41)

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

            var ac = (IBODocumentAcompte3)bc.FactoryDocumentAcompte.Create();
            ac.DR_Date = DateTime.Today;
            ac.DR_Libelle = Tronquer($"{marque} {e.ReferencePaiement}".TrimEnd(), 35);
            ac.DR_Montant = e.Montant;
            ac.Reglement = _cpta!.FactoryReglement.ReadIntitule(e.Mode);
            ac.WriteDefault();

            Console.WriteLine($"Encaissement {e.IdExterne} ({e.Mode} {e.Montant}) -> acompte sur {r.PieceCommande}");
            return new EncaissementResult { IdExterne = e.IdExterne, PieceCommande = r.PieceCommande, Montant = e.Montant };
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

        static string Erreurs(IFailInfoCol erreurs)
        {
            var messages = new List<string>();
            for (int i = 1; i <= erreurs.Count; i++)
            {
                IFailInfo f = erreurs[i];
                messages.Add($"{f.Text} (code {f.ErrorCode}, indice {f.Indice})");
            }
            return messages.Count == 0 ? "Sage a refusé le document sans préciser d'erreur." : string.Join(" ; ", messages);
        }

        static string Tronquer(string s, int max) => s.Length <= max ? s : s.Substring(0, max);

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
