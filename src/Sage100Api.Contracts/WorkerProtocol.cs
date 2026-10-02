using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sage100Api.Contracts
{
    /// <summary>
    /// Protocole entre l'API et le worker Objets Métiers : un message JSON par ligne sur un canal nommé (named pipe).
    /// Le worker traite les requêtes une par une, sur un seul thread, pour éviter tout accès concurrent aux objets COM.
    /// </summary>
    public static class WorkerProtocol
    {
        public const string NomCanal = "Sage100Api.Worker";

        public static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
    }

    public static class Operations
    {
        public const string Ping = "ping";
        public const string CreerCommande = "creerCommande";
        public const string CreerEncaissement = "creerEncaissement";
        public const string VerifierUtilisateur = "verifierUtilisateur";
    }

    public sealed class WorkerRequest
    {
        public string Operation { get; set; } = "";
        /// <summary>Charge utile sérialisée (CommandeRequest, ou EncaissementCommandeRequest).</summary>
        public JsonElement? Donnees { get; set; }
    }

    public sealed class WorkerResponse
    {
        public bool Ok { get; set; }
        public JsonElement? Resultat { get; set; }
        /// <summary>Code d'erreur stable : SAGE_METIER (règle Sage refusée), INTROUVABLE, TECHNIQUE.</summary>
        public string? CodeErreur { get; set; }
        public string? MessageErreur { get; set; }
    }

    /// <summary>Commande adressée au worker, avec l'utilisateur connecté qui l'a saisie (null si la connexion n'est pas exigée).</summary>
    public sealed class CommandeWorkerRequest
    {
        public CommandeRequest Commande { get; set; } = new CommandeRequest();
        public Auteur? Auteur { get; set; }
    }

    /// <summary>Encaissement adressé au worker : l'API y ajoute la pièce Sage de la commande.</summary>
    public sealed class EncaissementCommandeRequest
    {
        public string PieceCommande { get; set; } = "";
        public EncaissementRequest Encaissement { get; set; } = new EncaissementRequest();
        public Auteur? Auteur { get; set; }
    }

    /// <summary>Utilisateur Sage connecté et le collaborateur Sage qui lui est rattaché (fiche collaborateur, champ Utilisateur).</summary>
    public sealed class Auteur
    {
        public string Utilisateur { get; set; } = "";
        public string? CollaborateurNom { get; set; }
        public string? CollaborateurPrenom { get; set; }
    }

    /// <summary>Résultat de la vérification d'un login Sage par le worker.</summary>
    public sealed class UtilisateurVerifie
    {
        public string Utilisateur { get; set; } = "";
        public bool Administrateur { get; set; }
    }

    public static class CodesErreur
    {
        public const string SageMetier = "SAGE_METIER";
        public const string Introuvable = "INTROUVABLE";
        public const string Technique = "TECHNIQUE";
        /// <summary>Login ou mot de passe Sage refusé.</summary>
        public const string AccesRefuse = "ACCES_REFUSE";
    }
}
