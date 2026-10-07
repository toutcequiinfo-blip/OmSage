using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Sage100Api.Extensions;

/// <summary>
/// Zone d'un champ sur la page, en fraction de la largeur et de la hauteur de la page (0 à 1) :
/// indépendante de la résolution du scan.
/// </summary>
public sealed record ZoneOcr(string Champ, double X, double Y, double Largeur, double Hauteur);

/// <summary>
/// Modèle de lecture d'un format de document (une facture d'un fournisseur, un bon de commande d'un client) :
/// le texte qui le reconnaît, sa position (ancre, pour recaler un scan décalé) et la zone de chaque champ.
/// </summary>
public sealed record ModeleOcr
{
    public string Id { get; init; } = "";
    public string Nom { get; init; } = "";
    /// <summary>facture-fournisseur ou commande-client.</summary>
    public string TypeDocument { get; init; } = "facture-fournisseur";
    /// <summary>Compte tiers Sage (CT_Num) du fournisseur ou du client.</summary>
    public string Tiers { get; init; } = "";
    /// <summary>Texte qui reconnaît le format (NIF, nom du tiers en en-tête...) ; sa zone sert d'ancre.</summary>
    public string Identification { get; init; } = "";
    public ZoneOcr? Ancre { get; init; }
    public IReadOnlyList<ZoneOcr> Zones { get; init; } = [];
    /// <summary>Valeurs par défaut de la saisie : compte de charge, taux de TVA, journal, libellé.</summary>
    public IReadOnlyDictionary<string, string> Defauts { get; init; } = new Dictionary<string, string>();
    public int Utilisations { get; init; }
    public DateTime CreeLe { get; init; }
    public DateTime MajLe { get; init; }
}

public sealed record ModeleOcrRequest(string Nom, string TypeDocument, string Tiers, string Identification, ZoneOcr? Ancre,
    IReadOnlyList<ZoneOcr>? Zones, IReadOnlyDictionary<string, string>? Defauts);

/// <summary>Document lu puis validé par l'utilisateur, en attente d'écriture dans Sage.</summary>
public sealed record DocumentOcr
{
    public string Id { get; init; } = "";
    public string? Modele { get; init; }
    public string TypeDocument { get; init; } = "";
    public string Tiers { get; init; } = "";
    public string Numero { get; init; } = "";
    public DateTime? Date { get; init; }
    public decimal? MontantHT { get; init; }
    public decimal? MontantTVA { get; init; }
    public decimal? MontantTTC { get; init; }
    public IReadOnlyDictionary<string, string> Champs { get; init; } = new Dictionary<string, string>();
    public string? Fichier { get; init; }
    public string? TypeFichier { get; init; }
    public string Empreinte { get; init; } = "";
    /// <summary>a-ecrire (validé, pas encore dans Sage), ecrit, rejete.</summary>
    public string Statut { get; init; } = "a-ecrire";
    public string? PieceSage { get; init; }
    public string? Utilisateur { get; init; }
    public DateTime CreeLe { get; init; }
    public DateTime MajLe { get; init; }
}

public sealed record DocumentOcrRequest(string? Modele, string TypeDocument, string Tiers, string Numero, DateTime? Date, decimal? MontantHT,
    decimal? MontantTVA, decimal? MontantTTC, IReadOnlyDictionary<string, string>? Champs, string? Fichier, string? TypeFichier, string? Contenu, string? Statut);

/// <summary>
/// Lecture de documents scannés par modèles (OCR local, fait dans le navigateur) : les modèles et les documents validés sont gardés
/// dans la base des extensions (sage100api-extensions.db), jamais dans Sage ; l'écriture dans Sage passe ensuite par le worker.
/// </summary>
public sealed class Ocr
{
    public static readonly string[] TypesDocument = ["facture-fournisseur", "commande-client"];
    public static readonly string[] Statuts = ["a-ecrire", "ecrit", "rejete"];
    /// <summary>Écart toléré entre HT + TVA et TTC (arrondis des factures).</summary>
    public const decimal Tolerance = 1m;
    public const int TailleMaxFichier = 15 * 1024 * 1024;
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly BaseLocale _base;

    public Ocr(IOptions<SageOptions> options, Dossiers dossiers)
    {
        _base = new BaseLocale(dossiers, options.Value, "sage100api-extensions.db", c =>
        {
            c.Execute("""
                CREATE TABLE IF NOT EXISTS ocr_modeles (
                  id TEXT PRIMARY KEY,
                  nom TEXT NOT NULL,
                  type_document TEXT NOT NULL,
                  tiers TEXT NOT NULL,
                  identification TEXT NOT NULL,
                  ancre TEXT NULL,
                  zones TEXT NOT NULL,
                  defauts TEXT NOT NULL,
                  utilisations INTEGER NOT NULL DEFAULT 0,
                  cree_le TEXT NOT NULL,
                  maj_le TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS ocr_documents (
                  id TEXT PRIMARY KEY,
                  modele TEXT NULL,
                  type_document TEXT NOT NULL,
                  tiers TEXT NOT NULL,
                  numero TEXT NOT NULL,
                  date TEXT NULL,
                  montant_ht REAL NULL,
                  montant_tva REAL NULL,
                  montant_ttc REAL NULL,
                  champs TEXT NOT NULL,
                  fichier TEXT NULL,
                  type_fichier TEXT NULL,
                  contenu BLOB NULL,
                  empreinte TEXT NOT NULL,
                  statut TEXT NOT NULL,
                  piece_sage TEXT NULL,
                  utilisateur TEXT NULL,
                  cree_le TEXT NOT NULL,
                  maj_le TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ocr_documents_tiers ON ocr_documents (tiers, numero);
                CREATE INDEX IF NOT EXISTS ocr_documents_empreinte ON ocr_documents (empreinte);
                """);
        });
    }

    SqliteConnection Ouvrir() => _base.Ouvrir();

    // ---------- Modèles ----------

    public static IEnumerable<string> Verifier(string id, ModeleOcrRequest m)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64) yield return "id : 1 à 64 caractères.";
        if (string.IsNullOrWhiteSpace(m.Nom) || m.Nom.Length > 100) yield return "nom : 1 à 100 caractères.";
        if (!TypesDocument.Contains(m.TypeDocument)) yield return $"typeDocument : {string.Join(", ", TypesDocument)}.";
        if (string.IsNullOrWhiteSpace(m.Tiers)) yield return "tiers est obligatoire (compte du fournisseur ou du client dans Sage).";
        if (string.IsNullOrWhiteSpace(m.Identification) || m.Identification.Trim().Length < 3)
            yield return "identification : au moins 3 caractères (NIF, nom du tiers...) qui reconnaissent ce format.";
        foreach (var z in (m.Zones ?? []).Append(m.Ancre).OfType<ZoneOcr>())
            if (z.X is < 0 or > 1 || z.Y is < 0 or > 1 || z.Largeur is <= 0 or > 1 || z.Hauteur is <= 0 or > 1 || z.X + z.Largeur > 1.001 || z.Y + z.Hauteur > 1.001)
                yield return $"Zone « {z.Champ} » hors de la page.";
        if ((m.Zones ?? []).GroupBy(z => z.Champ).Any(g => g.Count() > 1)) yield return "Un champ ne peut avoir qu'une zone.";
    }

    public ModeleOcr EnregistrerModele(string id, ModeleOcrRequest m)
    {
        var maintenant = Texte(DateTime.UtcNow);
        using var c = Ouvrir();
        c.Execute("""
            INSERT INTO ocr_modeles (id, nom, type_document, tiers, identification, ancre, zones, defauts, cree_le, maj_le)
            VALUES (@id, @nom, @type, @tiers, @identification, @ancre, @zones, @defauts, @maintenant, @maintenant)
            ON CONFLICT (id) DO UPDATE SET nom = @nom, type_document = @type, tiers = @tiers, identification = @identification,
              ancre = @ancre, zones = @zones, defauts = @defauts, maj_le = @maintenant;
            """,
            new
            {
                id, nom = m.Nom.Trim(), type = m.TypeDocument, tiers = m.Tiers.Trim().ToUpperInvariant(), identification = m.Identification.Trim(),
                ancre = m.Ancre == null ? null : JsonSerializer.Serialize(m.Ancre, Json), zones = JsonSerializer.Serialize(m.Zones ?? [], Json),
                defauts = JsonSerializer.Serialize(m.Defauts ?? new Dictionary<string, string>(), Json), maintenant,
            });
        return LireModele(id)!;
    }

    public ModeleOcr? LireModele(string id)
    {
        using var c = Ouvrir();
        return c.QueryFirstOrDefault<LigneModele>(SelectModele + "WHERE id = @id", new { id })?.Modele();
    }

    public IReadOnlyList<ModeleOcr> Modeles()
    {
        using var c = Ouvrir();
        return c.Query<LigneModele>(SelectModele + "ORDER BY nom").Select(l => l.Modele()).ToList();
    }

    public bool SupprimerModele(string id)
    {
        using var c = Ouvrir();
        return c.Execute("DELETE FROM ocr_modeles WHERE id = @id", new { id }) > 0;
    }

    // ---------- Documents ----------

    /// <summary>Contrôles avant enregistrement : champs obligatoires, totaux cohérents, fichier lisible.</summary>
    public static IEnumerable<string> Verifier(string id, DocumentOcrRequest d)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64) yield return "id : 1 à 64 caractères.";
        if (!TypesDocument.Contains(d.TypeDocument)) yield return $"typeDocument : {string.Join(", ", TypesDocument)}.";
        if (string.IsNullOrWhiteSpace(d.Tiers)) yield return "Le tiers est obligatoire.";
        if (string.IsNullOrWhiteSpace(d.Numero) || d.Numero.Length > 35) yield return "Le numéro du document est obligatoire (35 caractères au plus).";
        if (d.Date == null) yield return "La date du document est obligatoire.";
        if (d.Statut != null && !Statuts.Contains(d.Statut)) yield return $"statut : {string.Join(", ", Statuts)}.";
        if (d.TypeDocument == "facture-fournisseur")
        {
            if (d.MontantTTC is not > 0) yield return "Le montant TTC est obligatoire.";
            if (Ecart(d.MontantHT, d.MontantTVA, d.MontantTTC) is { } e)
                yield return $"HT + TVA ne fait pas le TTC (écart de {e.ToString("N2", CultureInfo.GetCultureInfo("fr-FR"))}).";
        }
        if (d.Contenu != null)
        {
            var taille = d.Contenu.Length / 4 * 3;
            if (taille > TailleMaxFichier) yield return "Fichier trop gros (15 Mo au plus).";
            else if (!EstBase64(d.Contenu)) yield return "contenu : fichier en base64 attendu.";
        }
    }

    /// <summary>Écart entre HT + TVA et TTC au-delà de la tolérance, null si les montants concordent ou manquent.</summary>
    public static decimal? Ecart(decimal? ht, decimal? tva, decimal? ttc) =>
        ht is { } h && ttc is { } t && Math.Abs(h + (tva ?? 0) - t) > Tolerance ? Math.Abs(h + (tva ?? 0) - t) : null;

    static bool EstBase64(string s)
    {
        var tampon = new byte[s.Length];
        return Convert.TryFromBase64String(s, tampon, out _);
    }

    /// <summary>Document déjà enregistré pour ce tiers avec ce numéro, ou avec le même fichier (autre id).</summary>
    public DocumentOcr? Doublon(string id, string tiers, string numero, string? empreinte)
    {
        using var c = Ouvrir();
        return c.QueryFirstOrDefault<LigneDocument>(SelectDocument +
                "WHERE id <> @id AND statut <> 'rejete' AND ((tiers = @tiers AND numero = @numero) OR (@empreinte IS NOT NULL AND empreinte = @empreinte)) LIMIT 1",
                new { id, tiers = tiers.Trim().ToUpperInvariant(), numero = numero.Trim(), empreinte })?.Document();
    }

    public static string Empreinte(byte[] contenu) => Convert.ToHexString(SHA256.HashData(contenu)).ToLowerInvariant();

    public DocumentOcr EnregistrerDocument(string id, DocumentOcrRequest d, string? utilisateur)
    {
        var maintenant = Texte(DateTime.UtcNow);
        var contenu = d.Contenu == null ? null : Convert.FromBase64String(d.Contenu);
        using var c = Ouvrir();
        using var t = c.BeginTransaction();
        var existant = c.QueryFirstOrDefault<string?>("SELECT empreinte FROM ocr_documents WHERE id = @id", new { id }, t);
        c.Execute("""
            INSERT INTO ocr_documents (id, modele, type_document, tiers, numero, date, montant_ht, montant_tva, montant_ttc, champs, fichier, type_fichier,
              contenu, empreinte, statut, utilisateur, cree_le, maj_le)
            VALUES (@id, @modele, @type, @tiers, @numero, @date, @ht, @tva, @ttc, @champs, @fichier, @typeFichier, @contenu, @empreinte, @statut,
              @utilisateur, @maintenant, @maintenant)
            ON CONFLICT (id) DO UPDATE SET modele = @modele, type_document = @type, tiers = @tiers, numero = @numero, date = @date, montant_ht = @ht,
              montant_tva = @tva, montant_ttc = @ttc, champs = @champs, statut = @statut, maj_le = @maintenant,
              fichier = COALESCE(@fichier, fichier), type_fichier = COALESCE(@typeFichier, type_fichier), contenu = COALESCE(@contenu, contenu),
              empreinte = CASE WHEN @contenu IS NULL THEN empreinte ELSE @empreinte END;
            """,
            new
            {
                id, modele = d.Modele, type = d.TypeDocument, tiers = d.Tiers.Trim().ToUpperInvariant(), numero = d.Numero.Trim(),
                date = d.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ht = d.MontantHT, tva = d.MontantTVA, ttc = d.MontantTTC,
                champs = JsonSerializer.Serialize(d.Champs ?? new Dictionary<string, string>(), Json), fichier = d.Fichier, typeFichier = d.TypeFichier,
                contenu, empreinte = contenu == null ? existant ?? "" : Empreinte(contenu), statut = d.Statut ?? "a-ecrire", utilisateur, maintenant,
            }, t);
        // Un modèle utilisé une fois de plus : les plus utilisés sont essayés en premier.
        if (existant == null && d.Modele != null)
            c.Execute("UPDATE ocr_modeles SET utilisations = utilisations + 1 WHERE id = @modele", new { modele = d.Modele }, t);
        t.Commit();
        return LireDocument(id)!;
    }

    public DocumentOcr? LireDocument(string id)
    {
        using var c = Ouvrir();
        return c.QueryFirstOrDefault<LigneDocument>(SelectDocument + "WHERE id = @id", new { id })?.Document();
    }

    public (byte[] Contenu, string? Type, string? Nom)? Fichier(string id)
    {
        using var c = Ouvrir();
        var r = c.QueryFirstOrDefault<(byte[]? Contenu, string? Type, string? Nom)>(
            "SELECT contenu, type_fichier, fichier FROM ocr_documents WHERE id = @id", new { id });
        return r.Contenu == null ? null : (r.Contenu, r.Type, r.Nom);
    }

    public IReadOnlyList<DocumentOcr> Documents(string? statut, string? tiers, int taille)
    {
        using var c = Ouvrir();
        return c.Query<LigneDocument>(SelectDocument +
                "WHERE (@statut IS NULL OR statut = @statut) AND (@tiers IS NULL OR tiers = @tiers) ORDER BY cree_le DESC LIMIT @taille",
                new { statut, tiers = tiers?.Trim().ToUpperInvariant(), taille })
            .Select(l => l.Document()).ToList();
    }

    public bool SupprimerDocument(string id)
    {
        using var c = Ouvrir();
        return c.Execute("DELETE FROM ocr_documents WHERE id = @id AND statut <> 'ecrit'", new { id }) > 0;
    }

    const string SelectModele =
        "SELECT id AS Id, nom AS Nom, type_document AS TypeDocument, tiers AS Tiers, identification AS Identification, ancre AS Ancre, zones AS Zones, " +
        "defauts AS Defauts, utilisations AS Utilisations, cree_le AS CreeLe, maj_le AS MajLe FROM ocr_modeles ";

    const string SelectDocument =
        "SELECT id AS Id, modele AS Modele, type_document AS TypeDocument, tiers AS Tiers, numero AS Numero, date AS Date, montant_ht AS MontantHT, " +
        "montant_tva AS MontantTVA, montant_ttc AS MontantTTC, champs AS Champs, fichier AS Fichier, type_fichier AS TypeFichier, empreinte AS Empreinte, " +
        "statut AS Statut, piece_sage AS PieceSage, utilisateur AS Utilisateur, cree_le AS CreeLe, maj_le AS MajLe FROM ocr_documents ";

    static string Texte(DateTime d) => d.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    static DateTime Date(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
    static T Lire<T>(string? json, T defaut) => string.IsNullOrEmpty(json) ? defaut : JsonSerializer.Deserialize<T>(json, Json) ?? defaut;

    sealed class LigneModele
    {
        public string Id { get; set; } = "";
        public string Nom { get; set; } = "";
        public string TypeDocument { get; set; } = "";
        public string Tiers { get; set; } = "";
        public string Identification { get; set; } = "";
        public string? Ancre { get; set; }
        public string Zones { get; set; } = "[]";
        public string Defauts { get; set; } = "{}";
        public long Utilisations { get; set; }
        public string CreeLe { get; set; } = "";
        public string MajLe { get; set; } = "";

        public ModeleOcr Modele() => new()
        {
            Id = Id, Nom = Nom, TypeDocument = TypeDocument, Tiers = Tiers, Identification = Identification,
            Ancre = Lire<ZoneOcr?>(Ancre, null), Zones = Lire<List<ZoneOcr>>(Zones, []), Defauts = Lire<Dictionary<string, string>>(Defauts, []),
            Utilisations = (int)Utilisations, CreeLe = Date(CreeLe), MajLe = Date(MajLe),
        };
    }

    sealed class LigneDocument
    {
        public string Id { get; set; } = "";
        public string? Modele { get; set; }
        public string TypeDocument { get; set; } = "";
        public string Tiers { get; set; } = "";
        public string Numero { get; set; } = "";
        public string? Date { get; set; }
        public double? MontantHT { get; set; }
        public double? MontantTVA { get; set; }
        public double? MontantTTC { get; set; }
        public string Champs { get; set; } = "{}";
        public string? Fichier { get; set; }
        public string? TypeFichier { get; set; }
        public string Empreinte { get; set; } = "";
        public string Statut { get; set; } = "";
        public string? PieceSage { get; set; }
        public string? Utilisateur { get; set; }
        public string CreeLe { get; set; } = "";
        public string MajLe { get; set; } = "";

        public DocumentOcr Document() => new()
        {
            Id = Id, Modele = Modele, TypeDocument = TypeDocument, Tiers = Tiers, Numero = Numero,
            Date = Date == null ? null : DateTime.ParseExact(Date, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            MontantHT = (decimal?)MontantHT, MontantTVA = (decimal?)MontantTVA, MontantTTC = (decimal?)MontantTTC,
            Champs = Lire<Dictionary<string, string>>(Champs, []), Fichier = Fichier, TypeFichier = TypeFichier, Empreinte = Empreinte,
            Statut = Statut, PieceSage = PieceSage, Utilisateur = Utilisateur, CreeLe = Ocr.Date(CreeLe), MajLe = Ocr.Date(MajLe),
        };
    }
}
