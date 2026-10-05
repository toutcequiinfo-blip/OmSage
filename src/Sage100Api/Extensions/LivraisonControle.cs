using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using Sage100Api.Lectures;

namespace Sage100Api.Extensions;

/// <summary>
/// Ligne d'article d'un arrêt, recopiée de la pièce Sage à la préparation de la tournée.
/// QuantiteChargee : contrôlée au dépôt (null tant que le chargement n'est pas validé). QuantiteLivree : constatée chez le client.
/// </summary>
public sealed record ArticleArret
{
    public int Ligne { get; init; }
    public string? Article { get; init; }
    public string? Designation { get; init; }
    public string? Gamme1 { get; init; }
    public string? Gamme2 { get; init; }
    /// <summary>Quantité en unité de vente de l'article (Unite).</summary>
    public decimal Quantite { get; init; }
    /// <summary>Unité de vente de l'article (par exemple « Pièce »).</summary>
    public string? Unite { get; init; }
    /// <summary>Conditionnement saisi dans Sage (par exemple « Carton de 12 ») et quantité dans ce conditionnement, s'il diffère de l'unité.</summary>
    public string? Conditionnement { get; init; }
    public decimal? QuantiteConditionnement { get; init; }
    public decimal? QuantiteChargee { get; init; }
    public decimal? QuantiteLivree { get; init; }
    /// <summary>Raison de la non-livraison ou du retour, quand la quantité livrée est inférieure à la quantité chargée.</summary>
    public string? Motif { get; init; }
}

/// <summary>Contrôle du chargement au dépôt : responsable qui remet la marchandise, signature, heure, lignes en écart.</summary>
public sealed record Chargement
{
    public string? Responsable { get; init; }
    public bool Signe { get; init; }
    public DateTime Heure { get; init; }
    public string? Utilisateur { get; init; }
    /// <summary>Lignes dont la quantité chargée diffère de la quantité commandée.</summary>
    public int Ecarts { get; init; }
}

public sealed record LigneChargement(string Piece, int Ligne, decimal Quantite);

public sealed record ChargementRequest(IReadOnlyList<LigneChargement> Lignes, string? Responsable, string? Signature);

/// <summary>
/// Autre course de la tournée, hors pièce Sage : colis ou document à livrer, matériel ou chèque à récupérer...
/// Statut : a-faire, en-cours, fait, reporte, annule.
/// </summary>
public sealed record Course
{
    public string Id { get; init; } = "";
    public string Tournee { get; init; } = "";
    public int Ordre { get; init; }
    /// <summary>livrer, recuperer ou autre.</summary>
    public string Type { get; init; } = "autre";
    public string Description { get; init; } = "";
    public string? Client { get; init; }
    public string? Adresse { get; init; }
    public string? Contact { get; init; }
    public string? Telephone { get; init; }
    public string Statut { get; init; } = "a-faire";
    public string? Commentaire { get; init; }
    public DateTime? Heure { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public string? Utilisateur { get; init; }
}

public sealed record CourseRequest(string Type, string Description, string? Client, string? Adresse, string? Contact, string? Telephone,
    int? Ordre, string? Statut, string? Commentaire, double? Latitude, double? Longitude);

public sealed partial class Livraison
{
    public static readonly string[] TypesCourse = ["livrer", "recuperer", "autre"];
    public static readonly string[] StatutsCourse = ["a-faire", "en-cours", "fait", "reporte", "annule"];

    static void CreerTablesControle(SqliteConnection c)
    {
        CreerTablesControleInitiales(c);
        // Unité et conditionnement des lignes, ajoutés après la première version : tournées plus anciennes sans unité.
        var colonnes = c.Query<string>("SELECT name FROM pragma_table_info('arret_articles')").ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (nom, type) in new[] { ("unite", "TEXT"), ("conditionnement", "TEXT"), ("qte_conditionnement", "REAL") })
            if (!colonnes.Contains(nom)) c.Execute($"ALTER TABLE arret_articles ADD COLUMN {nom} {type} NULL");
    }

    static void CreerTablesControleInitiales(SqliteConnection c) => c.Execute("""
        CREATE TABLE IF NOT EXISTS arret_articles (
          tournee TEXT NOT NULL,
          piece TEXT NOT NULL,
          ligne INTEGER NOT NULL,
          article TEXT NULL,
          designation TEXT NULL,
          gamme1 TEXT NULL,
          gamme2 TEXT NULL,
          quantite REAL NOT NULL,
          qte_chargee REAL NULL,
          qte_livree REAL NULL,
          motif TEXT NULL,
          PRIMARY KEY (tournee, piece, ligne)
        );
        CREATE TABLE IF NOT EXISTS chargements (
          tournee TEXT PRIMARY KEY,
          responsable TEXT NULL,
          signature TEXT NULL,
          heure TEXT NOT NULL,
          utilisateur TEXT NULL
        );
        CREATE TABLE IF NOT EXISTS courses (
          id TEXT PRIMARY KEY,
          tournee TEXT NOT NULL,
          ordre INTEGER NOT NULL,
          type TEXT NOT NULL,
          description TEXT NOT NULL,
          client TEXT NULL,
          adresse TEXT NULL,
          contact TEXT NULL,
          telephone TEXT NULL,
          statut TEXT NOT NULL,
          commentaire TEXT NULL,
          heure TEXT NULL,
          latitude REAL NULL,
          longitude REAL NULL,
          utilisateur TEXT NULL,
          cree_le TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS courses_tournee ON courses (tournee, ordre);
        """);

    /// <summary>Recopie les lignes d'articles de la pièce (les lignes de commentaire, sans article, ne sont pas à contrôler).</summary>
    static void InsererArticles(SqliteConnection c, SqliteTransaction tx, string tournee, string piece, IReadOnlyList<LignePiece> lignes)
    {
        var n = 0;
        foreach (var l in lignes)
        {
            n++;
            if (l.Article is null) continue;
            c.Execute("""
                INSERT OR REPLACE INTO arret_articles (tournee, piece, ligne, article, designation, gamme1, gamme2, quantite, unite, conditionnement, qte_conditionnement)
                VALUES (@tournee, @piece, @ligne, @article, @designation, @gamme1, @gamme2, @quantite, @unite, @conditionnement, @qteConditionnement);
                """, new
            {
                tournee, piece, ligne = n, article = l.Article, designation = l.Designation, gamme1 = l.Gamme1, gamme2 = l.Gamme2, quantite = l.Quantite,
                unite = l.Unite, conditionnement = l.Conditionnement, qteConditionnement = l.QuantiteConditionnement,
            }, tx);
        }
    }

    static Dictionary<string, IReadOnlyList<ArticleArret>> Articles(SqliteConnection c, string tournee) =>
        c.Query<LigneArticle>("""
            SELECT piece AS Piece, ligne AS Ligne, article AS Article, designation AS Designation, gamme1 AS Gamme1, gamme2 AS Gamme2,
              quantite AS Quantite, unite AS Unite, conditionnement AS Conditionnement, qte_conditionnement AS QuantiteConditionnement, qte_chargee AS QuantiteChargee, qte_livree AS QuantiteLivree, motif AS Motif
            FROM arret_articles WHERE tournee = @tournee ORDER BY piece, ligne
            """, new { tournee })
        .GroupBy(l => l.Piece)
        .ToDictionary(g => g.Key, g => (IReadOnlyList<ArticleArret>)g.Select(l => l.EnArticle()).ToList());

    /// <summary>Quantités livrées par ligne ; renvoie le statut de l'arrêt. Une ligne absente de la requête est livrée en entier.</summary>
    /// <exception cref="ArgumentException">Ligne inconnue, quantité supérieure à la quantité chargée, ou manque sans motif.</exception>
    static string EnregistrerLignesLivrees(SqliteConnection c, string tournee, string piece, IReadOnlyList<LigneLivree> lignes)
    {
        var articles = Articles(c, tournee).GetValueOrDefault(piece) ?? [];
        var erreurs = new List<string>();
        foreach (var l in lignes.Where(l => articles.All(a => a.Ligne != l.Ligne))) erreurs.Add($"Ligne {l.Ligne} inconnue sur {piece}.");
        var livrees = articles.Select(a =>
        {
            var reference = a.QuantiteChargee ?? a.Quantite;
            var l = lignes.FirstOrDefault(x => x.Ligne == a.Ligne);
            var q = l?.Quantite ?? reference;
            if (q > reference) erreurs.Add($"Ligne {a.Ligne} ({a.Article}) : {q} livré(s) pour {reference} chargé(s).");
            if (q < reference && l?.Motif is null) erreurs.Add($"Ligne {a.Ligne} ({a.Article}) : indiquez la raison de la non-livraison ou du retour.");
            return (a.Ligne, Reference: reference, Quantite: q, Motif: q < reference ? l?.Motif : null);
        }).ToList();
        if (erreurs.Count > 0) throw new ArgumentException(string.Join(" ", erreurs));
        foreach (var l in livrees)
            c.Execute("UPDATE arret_articles SET qte_livree = @q, motif = @motif WHERE tournee = @tournee AND piece = @piece AND ligne = @ligne",
                new { q = l.Quantite, motif = l.Motif, tournee, piece, ligne = l.Ligne });
        if (livrees.Count == 0 || livrees.All(l => l.Quantite >= l.Reference)) return "livre";
        return livrees.All(l => l.Quantite == 0) ? "echec" : "partiel";
    }

    // ---------- Chargement au dépôt ----------

    public static IEnumerable<string> Verifier(ChargementRequest r)
    {
        if (r.Lignes is null || r.Lignes.Count == 0) yield return "lignes : les quantités chargées sont obligatoires.";
        foreach (var l in r.Lignes ?? []) if (l.Quantite < 0) yield return $"{l.Piece} ligne {l.Ligne} : quantité négative.";
        if (r.Responsable is { Length: > 100 }) yield return "responsable : 100 caractères au plus.";
        if (r.Signature != null && (!r.Signature.StartsWith("data:image/png;base64,") || r.Signature.Length > TailleMaxSignature))
            yield return "signature : image PNG en data URL (data:image/png;base64,...), 300 Ko au plus.";
    }

    /// <summary>Enregistre les quantités chargées et la signature du responsable du dépôt. Null si la tournée n'existe pas.</summary>
    /// <exception cref="ArgumentException">Ligne inconnue.</exception>
    public Chargement? ValiderChargement(string tournee, ChargementRequest r, string? utilisateur)
    {
        using var c = Ouvrir();
        if (c.ExecuteScalar<long>("SELECT COUNT(*) FROM tournees WHERE id = @tournee", new { tournee }) == 0) return null;
        using var tx = c.BeginTransaction();
        var inconnues = new List<string>();
        foreach (var l in r.Lignes)
        {
            var n = c.Execute("UPDATE arret_articles SET qte_chargee = @q WHERE tournee = @tournee AND piece = @piece AND ligne = @ligne",
                new { q = l.Quantite, tournee, piece = l.Piece.Trim().ToUpperInvariant(), ligne = l.Ligne }, tx);
            if (n == 0) inconnues.Add($"{l.Piece} ligne {l.Ligne}");
        }
        if (inconnues.Count > 0) throw new ArgumentException($"Lignes inconnues dans la tournée : {string.Join(", ", inconnues)}.");
        c.Execute("""
            INSERT INTO chargements (tournee, responsable, signature, heure, utilisateur) VALUES (@tournee, @responsable, @signature, @heure, @utilisateur)
            ON CONFLICT (tournee) DO UPDATE SET responsable = @responsable, signature = COALESCE(@signature, signature), heure = @heure, utilisateur = @utilisateur;
            """, new { tournee, responsable = r.Responsable, signature = r.Signature, heure = Texte(DateTime.UtcNow), utilisateur }, tx);
        tx.Commit();
        return LireChargement(c, tournee);
    }

    static Chargement? LireChargement(SqliteConnection c, string tournee)
    {
        var l = c.QueryFirstOrDefault<(string? Responsable, long Signe, string Heure, string? Utilisateur)>(
            "SELECT responsable, signature IS NOT NULL, heure, utilisateur FROM chargements WHERE tournee = @tournee", new { tournee });
        if (l.Heure is null) return null;
        var ecarts = c.ExecuteScalar<long>("SELECT COUNT(*) FROM arret_articles WHERE tournee = @tournee AND qte_chargee IS NOT NULL AND qte_chargee <> quantite",
            new { tournee });
        return new Chargement { Responsable = l.Responsable, Signe = l.Signe != 0, Heure = Date(l.Heure)!.Value, Utilisateur = l.Utilisateur, Ecarts = (int)ecarts };
    }

    public string? SignatureChargement(string tournee)
    {
        using var c = Ouvrir();
        return c.QueryFirstOrDefault<string>("SELECT signature FROM chargements WHERE tournee = @tournee", new { tournee });
    }

    // ---------- Autres courses ----------

    public static IEnumerable<string> Verifier(string id, CourseRequest r)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64) yield return "id : 1 à 64 caractères.";
        if (!TypesCourse.Contains(r.Type)) yield return $"type : {string.Join(", ", TypesCourse)}.";
        if (string.IsNullOrWhiteSpace(r.Description)) yield return "description est obligatoire (ce qu'il faut livrer ou récupérer).";
        else if (r.Description.Length > 500) yield return "description : 500 caractères au plus.";
        if (r.Statut != null && !StatutsCourse.Contains(r.Statut)) yield return $"statut : {string.Join(", ", StatutsCourse)}.";
        if (r.Commentaire is { Length: > 2000 }) yield return "commentaire : 2000 caractères au plus.";
        if (r.Adresse is { Length: > 300 }) yield return "adresse : 300 caractères au plus.";
        if ((r.Latitude is null) != (r.Longitude is null)) yield return "latitude et longitude vont ensemble.";
    }

    /// <summary>Crée ou met à jour une course. Null si la tournée n'existe pas.</summary>
    public Course? EnregistrerCourse(string tournee, string id, CourseRequest r, string? utilisateur)
    {
        using var c = Ouvrir();
        if (c.ExecuteScalar<long>("SELECT COUNT(*) FROM tournees WHERE id = @tournee", new { tournee }) == 0) return null;
        var ordre = r.Ordre ?? (int)c.ExecuteScalar<long>("SELECT COALESCE(MAX(ordre), 0) + 1 FROM courses WHERE tournee = @tournee", new { tournee });
        var statut = r.Statut ?? "a-faire";
        c.Execute("""
            INSERT INTO courses (id, tournee, ordre, type, description, client, adresse, contact, telephone, statut, commentaire, heure, latitude, longitude, utilisateur, cree_le)
            VALUES (@id, @tournee, @ordre, @type, @description, @client, @adresse, @contact, @telephone, @statut, @commentaire, @heure, @lat, @lon, @utilisateur, @maintenant)
            ON CONFLICT (id) DO UPDATE SET ordre = COALESCE(@ordreFixe, ordre), type = @type, description = @description, client = @client, adresse = @adresse,
              contact = @contact, telephone = @telephone, commentaire = @commentaire, latitude = COALESCE(@lat, latitude), longitude = COALESCE(@lon, longitude),
              heure = CASE WHEN statut <> @statut THEN @heure ELSE heure END, utilisateur = CASE WHEN statut <> @statut THEN @utilisateur ELSE utilisateur END,
              statut = @statut;
            """, new
        {
            id, tournee, ordre, ordreFixe = r.Ordre, type = r.Type, description = r.Description.Trim(), client = r.Client?.Trim().ToUpperInvariant(),
            adresse = r.Adresse, contact = r.Contact, telephone = r.Telephone, statut, commentaire = r.Commentaire,
            heure = statut == "a-faire" ? null : Texte(DateTime.UtcNow), lat = r.Latitude, lon = r.Longitude, utilisateur, maintenant = Texte(DateTime.UtcNow),
        });
        c.Execute("UPDATE tournees SET maj_le = @m WHERE id = @tournee", new { m = Texte(DateTime.UtcNow), tournee });
        return Courses(c, tournee).FirstOrDefault(x => x.Id == id);
    }

    public bool SupprimerCourse(string tournee, string id)
    {
        using var c = Ouvrir();
        return c.Execute("DELETE FROM courses WHERE tournee = @tournee AND id = @id", new { tournee, id }) > 0;
    }

    static IReadOnlyList<Course> Courses(SqliteConnection c, string tournee) =>
        c.Query<LigneCourse>("""
            SELECT id AS Id, tournee AS Tournee, ordre AS Ordre, type AS Type, description AS Description, client AS Client, adresse AS Adresse,
              contact AS Contact, telephone AS Telephone, statut AS Statut, commentaire AS Commentaire, heure AS Heure, latitude AS Latitude,
              longitude AS Longitude, utilisateur AS Utilisateur
            FROM courses WHERE tournee = @tournee ORDER BY ordre, cree_le
            """, new { tournee }).Select(l => l.Course()).ToList();

    sealed class LigneArticle
    {
        public string Piece { get; set; } = "";
        public long Ligne { get; set; }
        public string? Article { get; set; }
        public string? Designation { get; set; }
        public string? Gamme1 { get; set; }
        public string? Gamme2 { get; set; }
        public double Quantite { get; set; }
        public string? Unite { get; set; }
        public string? Conditionnement { get; set; }
        public double? QuantiteConditionnement { get; set; }
        public double? QuantiteChargee { get; set; }
        public double? QuantiteLivree { get; set; }
        public string? Motif { get; set; }

        public ArticleArret EnArticle() => new()
        {
            Ligne = (int)Ligne, Article = Article, Designation = Designation, Gamme1 = Gamme1, Gamme2 = Gamme2, Quantite = (decimal)Quantite,
            Unite = Unite, Conditionnement = Conditionnement, QuantiteConditionnement = (decimal?)QuantiteConditionnement,
            QuantiteChargee = (decimal?)QuantiteChargee, QuantiteLivree = (decimal?)QuantiteLivree, Motif = Motif,
        };
    }

    sealed class LigneCourse
    {
        public string Id { get; set; } = "";
        public string Tournee { get; set; } = "";
        public long Ordre { get; set; }
        public string Type { get; set; } = "";
        public string Description { get; set; } = "";
        public string? Client { get; set; }
        public string? Adresse { get; set; }
        public string? Contact { get; set; }
        public string? Telephone { get; set; }
        public string Statut { get; set; } = "";
        public string? Commentaire { get; set; }
        public string? Heure { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public string? Utilisateur { get; set; }

        public Course Course() => new()
        {
            Id = Id, Tournee = Tournee, Ordre = (int)Ordre, Type = Type, Description = Description, Client = Client, Adresse = Adresse, Contact = Contact,
            Telephone = Telephone, Statut = Statut, Commentaire = Commentaire, Heure = Date(Heure), Latitude = Latitude, Longitude = Longitude, Utilisateur = Utilisateur,
        };
    }
}
