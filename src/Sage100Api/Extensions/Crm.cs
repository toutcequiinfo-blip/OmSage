using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Sage100Api.Extensions;

/// <summary>
/// Activité commerciale sur un client : visite, appel, rendez-vous, e-mail, note ou tâche.
/// Statut : a-faire (planifiée), fait, annule. Id : choisi par l'application (GUID), ce qui rend l'enregistrement rejouable hors ligne.
/// </summary>
public sealed record Activite
{
    public string Id { get; init; } = "";
    public string Client { get; init; } = "";
    public string Type { get; init; } = "visite";
    public string? Sujet { get; init; }
    public string? CompteRendu { get; init; }
    public string Statut { get; init; } = "fait";
    public DateTime? DatePrevue { get; init; }
    public DateTime? DateRealisee { get; init; }
    /// <summary>Collaborateur Sage (CO_No) qui porte l'activité.</summary>
    public int? Collaborateur { get; init; }
    /// <summary>Contact du client (CT_No de F_CONTACTT).</summary>
    public int? Contact { get; init; }
    /// <summary>Pièce Sage liée (devis, commande...).</summary>
    public string? Document { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public string? Utilisateur { get; init; }
    public DateTime CreeLe { get; init; }
    public DateTime MajLe { get; init; }
}

public sealed record ActiviteRequest(string Client, string Type, string? Sujet, string? CompteRendu, string? Statut, DateTime? DatePrevue,
    DateTime? DateRealisee, int? Collaborateur, int? Contact, string? Document, double? Latitude, double? Longitude);

public sealed record FiltreActivites(string? Client, int? Collaborateur, string? Utilisateur, string? Statut, string? Type, DateTime? Du, DateTime? Au, int Page, int Taille);

/// <summary>Activités CRM, gardées par l'API dans la base des extensions (sage100api-extensions.db) : Sage n'a pas de table pour elles.</summary>
public sealed class Crm
{
    public static readonly string[] Types = ["visite", "appel", "rendez-vous", "email", "note", "tache"];
    public static readonly string[] Statuts = ["a-faire", "fait", "annule"];
    readonly string _cnx;

    public Crm(IOptions<SageOptions> options)
    {
        _cnx = Geolocalisation.ChaineExtensions(options.Value);
        using var c = Ouvrir();
        c.Execute("""
            CREATE TABLE IF NOT EXISTS activites (
              id TEXT PRIMARY KEY,
              client TEXT NOT NULL,
              type TEXT NOT NULL,
              sujet TEXT NULL,
              compte_rendu TEXT NULL,
              statut TEXT NOT NULL,
              date_prevue TEXT NULL,
              date_realisee TEXT NULL,
              collaborateur INTEGER NULL,
              contact INTEGER NULL,
              document TEXT NULL,
              latitude REAL NULL,
              longitude REAL NULL,
              utilisateur TEXT NULL,
              cree_le TEXT NOT NULL,
              maj_le TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS activites_client ON activites (client, date_realisee);
            CREATE INDEX IF NOT EXISTS activites_collaborateur ON activites (collaborateur, statut, date_prevue);
            """);
    }

    SqliteConnection Ouvrir()
    {
        var c = new SqliteConnection(_cnx);
        c.Open();
        return c;
    }

    public static IEnumerable<string> Verifier(string id, ActiviteRequest a)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64) yield return "id : 1 à 64 caractères.";
        if (string.IsNullOrWhiteSpace(a.Client)) yield return "client est obligatoire.";
        if (!Types.Contains(a.Type)) yield return $"type : {string.Join(", ", Types)}.";
        if (a.Statut != null && !Statuts.Contains(a.Statut)) yield return $"statut : {string.Join(", ", Statuts)}.";
        if (a.Statut == "a-faire" && a.DatePrevue is null) yield return "Une activité à faire demande une datePrevue.";
        if (a.Sujet is { Length: > 200 }) yield return "sujet : 200 caractères au plus.";
        if (a.CompteRendu is { Length: > 8000 }) yield return "compteRendu : 8000 caractères au plus.";
        if (a.Latitude is < -90 or > 90 || a.Longitude is < -180 or > 180) yield return "Position GPS invalide.";
        if ((a.Latitude is null) != (a.Longitude is null)) yield return "latitude et longitude vont ensemble.";
    }

    /// <summary>Crée ou remplace l'activité ; la date de création et l'auteur d'origine sont gardés.</summary>
    public Activite Enregistrer(string id, ActiviteRequest a, string? utilisateur, int? collaborateurConnecte)
    {
        var maintenant = DateTime.UtcNow;
        var statut = a.Statut ?? (a.DatePrevue > maintenant ? "a-faire" : "fait");
        var realisee = a.DateRealisee ?? (statut == "fait" ? maintenant : null);
        using var c = Ouvrir();
        c.Execute("""
            INSERT INTO activites (id, client, type, sujet, compte_rendu, statut, date_prevue, date_realisee, collaborateur, contact, document,
              latitude, longitude, utilisateur, cree_le, maj_le)
            VALUES (@id, @client, @type, @sujet, @compteRendu, @statut, @datePrevue, @dateRealisee, @collaborateur, @contact, @document,
              @latitude, @longitude, @utilisateur, @maintenant, @maintenant)
            ON CONFLICT (id) DO UPDATE SET client = @client, type = @type, sujet = @sujet, compte_rendu = @compteRendu, statut = @statut,
              date_prevue = @datePrevue, date_realisee = @dateRealisee, collaborateur = @collaborateur, contact = @contact, document = @document,
              latitude = @latitude, longitude = @longitude, maj_le = @maintenant;
            """,
            new
            {
                id, client = a.Client.Trim().ToUpperInvariant(), type = a.Type, sujet = a.Sujet, compteRendu = a.CompteRendu, statut,
                datePrevue = Texte(a.DatePrevue), dateRealisee = Texte(realisee), collaborateur = a.Collaborateur ?? collaborateurConnecte,
                contact = a.Contact, document = a.Document, latitude = a.Latitude, longitude = a.Longitude, utilisateur, maintenant = Texte(maintenant),
            });
        return Lire(id)!;
    }

    public Activite? Lire(string id)
    {
        using var c = Ouvrir();
        return c.QueryFirstOrDefault<Ligne>(Select + "WHERE id = @id", new { id })?.Activite();
    }

    public bool Supprimer(string id)
    {
        using var c = Ouvrir();
        return c.Execute("DELETE FROM activites WHERE id = @id", new { id }) > 0;
    }

    /// <summary>Activités filtrées. À faire : par date prévue croissante (agenda) ; sinon les plus récentes d'abord.</summary>
    public IReadOnlyList<Activite> Lister(FiltreActivites f)
    {
        var ordre = f.Statut == "a-faire" ? "date_prevue, cree_le" : "COALESCE(date_realisee, date_prevue, cree_le) DESC";
        using var c = Ouvrir();
        return c.Query<Ligne>(Select +
            "WHERE (@client IS NULL OR client = @client) AND (@collaborateur IS NULL OR collaborateur = @collaborateur) " +
            "AND (@utilisateur IS NULL OR utilisateur = @utilisateur) AND (@statut IS NULL OR statut = @statut) AND (@type IS NULL OR type = @type) " +
            "AND (@du IS NULL OR COALESCE(date_realisee, date_prevue) >= @du) AND (@au IS NULL OR COALESCE(date_realisee, date_prevue) <= @au) " +
            $"ORDER BY {ordre} LIMIT @taille OFFSET @saut",
            new
            {
                // Noms en minuscules : SQLite distingue la casse des paramètres.
                client = f.Client?.Trim().ToUpperInvariant(), collaborateur = f.Collaborateur, utilisateur = f.Utilisateur, statut = f.Statut,
                type = f.Type, du = Texte(f.Du), au = Texte(f.Au),
                taille = f.Taille, saut = (f.Page - 1) * f.Taille,
            }).Select(l => l.Activite()).ToList();
    }

    /// <summary>Date de la dernière activité faite par client (portefeuille du commercial).</summary>
    public IReadOnlyDictionary<string, DateTime> DernieresActivites()
    {
        using var c = Ouvrir();
        return c.Query<(string Client, string Date)>(
                "SELECT client, MAX(COALESCE(date_realisee, cree_le)) FROM activites WHERE statut = 'fait' GROUP BY client")
            .ToDictionary(x => x.Client, x => Date(x.Date)!.Value, StringComparer.OrdinalIgnoreCase);
    }

    const string Select =
        "SELECT id AS Id, client AS Client, type AS Type, sujet AS Sujet, compte_rendu AS CompteRendu, statut AS Statut, date_prevue AS DatePrevue, " +
        "date_realisee AS DateRealisee, collaborateur AS Collaborateur, contact AS Contact, document AS Document, latitude AS Latitude, " +
        "longitude AS Longitude, utilisateur AS Utilisateur, cree_le AS CreeLe, maj_le AS MajLe FROM activites ";

    // Dates en texte ISO 8601 (UTC) : triables dans SQLite, relues sans perte.
    static string? Texte(DateTime? d) => d?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    static DateTime? Date(string? s) => s is null ? null : DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    sealed class Ligne
    {
        public string Id { get; set; } = "";
        public string Client { get; set; } = "";
        public string Type { get; set; } = "";
        public string? Sujet { get; set; }
        public string? CompteRendu { get; set; }
        public string Statut { get; set; } = "";
        public string? DatePrevue { get; set; }
        public string? DateRealisee { get; set; }
        public long? Collaborateur { get; set; }
        public long? Contact { get; set; }
        public string? Document { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public string? Utilisateur { get; set; }
        public string CreeLe { get; set; } = "";
        public string MajLe { get; set; } = "";

        public Activite Activite() => new()
        {
            Id = Id, Client = Client, Type = Type, Sujet = Sujet, CompteRendu = CompteRendu, Statut = Statut, DatePrevue = Date(DatePrevue),
            DateRealisee = Date(DateRealisee), Collaborateur = (int?)Collaborateur, Contact = (int?)Contact, Document = Document,
            Latitude = Latitude, Longitude = Longitude, Utilisateur = Utilisateur, CreeLe = Date(CreeLe)!.Value, MajLe = Date(MajLe)!.Value,
        };
    }
}
