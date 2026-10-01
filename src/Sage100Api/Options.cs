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
}
