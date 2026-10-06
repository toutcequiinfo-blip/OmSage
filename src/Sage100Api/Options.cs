namespace Sage100Api;

public sealed class SageOptions
{
    /// <summary>Connexion SQL à la base Sage, avec un compte en LECTURE SEULE : aucune écriture SQL dans Sage (loi anti-fraude).</summary>
    public string ChaineSql { get; set; } = "";
    /// <summary>Fichier SQLite du journal des opérations de l'API (idempotence, traçabilité). Jamais dans la base Sage.</summary>
    public string CheminJournal { get; set; } = "sage100api-journal.db";
    public int DelaiWorkerSecondes { get; set; } = 60;
    /// <summary>
    /// Refus des commandes au-delà du stock disponible, comme la fenêtre « Indisponibilité en stock » de la saisie Sage
    /// (les Objets Métiers ne l'appliquent pas). « Auto » : refus si Sage n'autorise pas les stocks négatifs ;
    /// « Bloquer » : toujours ; « Aucun » : jamais.
    /// </summary>
    public string ControleStock { get; set; } = "Auto";
    /// <summary>Nom de l'application cliente -> clé d'API (en-tête X-Api-Key).</summary>
    public Dictionary<string, string> ClesApi { get; set; } = new();
    /// <summary>
    /// Sociétés servies par l'installation (bases du même serveur). Vide : une seule société, celle de <see cref="ChaineSql"/>.
    /// La première est la société principale : elle garde les fichiers locaux existants (journal, extensions).
    /// </summary>
    public List<DossierOptions> Dossiers { get; set; } = new();
}

/// <summary>Section « Authentification » : connexion des utilisateurs de la borne avec leur login Sage.</summary>
public sealed class AuthentificationOptions
{
    /// <summary>Vrai : commandes et encaissements exigent un utilisateur Sage connecté (POST /api/v1/connexion).</summary>
    public bool Active { get; set; } = true;
    /// <summary>
    /// Durée de validité d'une connexion. Elle couvre aussi les ventes faites hors ligne : passé ce délai,
    /// l'utilisateur doit se reconnecter pour que ses ventes en attente partent vers Sage.
    /// </summary>
    public int DureeHeures { get; set; } = 168;
    /// <summary>Vrai : seuls les utilisateurs dont le collaborateur Sage est « Caissier » (ou les administrateurs) encaissent.</summary>
    public bool ExigerCaissier { get; set; } = true;
    /// <summary>Clé de signature des jetons (base64). Vide : clé aléatoire gardée dans un fichier à côté du journal.</summary>
    public string CleSignature { get; set; } = "";
}
