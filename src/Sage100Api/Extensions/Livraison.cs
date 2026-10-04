using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Sage100Api.Lectures;

namespace Sage100Api.Extensions;

/// <summary>
/// Arrêt d'une tournée : une pièce Sage (bon de commande ou préparation) à livrer.
/// L'adresse, le contact et les montants sont recopiés à la préparation de la tournée : le livreur les garde même quand la pièce
/// a été transformée en bon de livraison dans Sage (elle sort alors de la liste « à livrer »).
/// Statut : a-livrer, livre, partiel (livré en partie, reliquat à reprogrammer), echec (absent, refus...).
/// </summary>
public sealed record Arret
{
    public string Tournee { get; init; } = "";
    public string Piece { get; init; } = "";
    public int Ordre { get; init; }
    public string TypePiece { get; init; } = "";
    public string Client { get; init; } = "";
    public string? Intitule { get; init; }
    public string? Reference { get; init; }
    public int? AdresseLivraison { get; init; }
    public string? Adresse { get; init; }
    public string? Complement { get; init; }
    public string? CodePostal { get; init; }
    public string? Ville { get; init; }
    public string? Contact { get; init; }
    public string? Telephone { get; init; }
    public decimal TotalTTC { get; init; }
    public decimal NetAPayer { get; init; }
    /// <summary>Position de destination (adresse de livraison, sinon client), si elle est connue.</summary>
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public string Statut { get; init; } = "a-livrer";
    public string? Motif { get; init; }
    public string? Receptionnaire { get; init; }
    public string? Commentaire { get; init; }
    public bool Signe { get; init; }
    /// <summary>Heure et position du livreur au moment où il a validé l'arrêt.</summary>
    public DateTime? Heure { get; init; }
    public double? LatitudeLivreur { get; init; }
    public double? LongitudeLivreur { get; init; }
    public string? Utilisateur { get; init; }
}

public sealed record Tournee
{
    public string Id { get; init; } = "";
    public DateTime Date { get; init; }
    public string? Nom { get; init; }
    /// <summary>Collaborateur Sage (CO_No) qui livre.</summary>
    public int? Livreur { get; init; }
    public int? Depot { get; init; }
    /// <summary>preparee (rien de fait), en-cours, terminee (tous les arrêts traités) : calculé depuis les arrêts.</summary>
    public string Statut { get; init; } = "preparee";
    public int NbArrets { get; init; }
    public int NbTraites { get; init; }
    public decimal TotalTTC { get; init; }
    public string? Utilisateur { get; init; }
    public DateTime CreeLe { get; init; }
    public DateTime MajLe { get; init; }
    public IReadOnlyList<Arret> Arrets { get; init; } = [];
}

public sealed record TourneeRequest(DateTime Date, string? Nom, int? Livreur, int? Depot, IReadOnlyList<string> Pieces);

/// <summary>Départ : position du livreur (latitude, longitude) ou dépôt dont la position est enregistrée. retour : revenir au départ.</summary>
public sealed record OptimisationRequest(IReadOnlyList<string> Pieces, double? Latitude, double? Longitude, int? Depot, bool? Retour);

public sealed record CompteRenduArret(string Statut, string? Motif, string? Receptionnaire, string? Commentaire, string? Signature,
    double? Latitude, double? Longitude);

/// <summary>
/// Tournées de livraison et preuves de livraison (statut, réceptionnaire, signature, heure et position), gardées dans la base des
/// extensions. Rien n'est écrit dans Sage : le bon de livraison reste fait dans Sage, à partir du bon de commande.
/// </summary>
public sealed class Livraison
{
    public static readonly string[] Statuts = ["a-livrer", "livre", "partiel", "echec"];
    public static readonly string[] Motifs = ["absent", "refus", "adresse-introuvable", "ferme", "manque-marchandise", "autre"];
    /// <summary>Signature en image PNG (data URL) : 300 Ko au plus.</summary>
    public const int TailleMaxSignature = 300_000;
    readonly string _cnx;

    public Livraison(IOptions<SageOptions> options)
    {
        _cnx = Geolocalisation.ChaineExtensions(options.Value);
        using var c = Ouvrir();
        c.Execute("""
            CREATE TABLE IF NOT EXISTS tournees (
              id TEXT PRIMARY KEY,
              date TEXT NOT NULL,
              nom TEXT NULL,
              livreur INTEGER NULL,
              depot INTEGER NULL,
              utilisateur TEXT NULL,
              cree_le TEXT NOT NULL,
              maj_le TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS tournees_date ON tournees (date, livreur);
            CREATE TABLE IF NOT EXISTS arrets (
              tournee TEXT NOT NULL,
              piece TEXT NOT NULL,
              ordre INTEGER NOT NULL,
              type_piece TEXT NOT NULL,
              client TEXT NOT NULL,
              intitule TEXT NULL,
              reference TEXT NULL,
              adresse_livraison INTEGER NULL,
              adresse TEXT NULL,
              complement TEXT NULL,
              code_postal TEXT NULL,
              ville TEXT NULL,
              contact TEXT NULL,
              telephone TEXT NULL,
              total_ttc REAL NOT NULL,
              net_a_payer REAL NOT NULL,
              latitude REAL NULL,
              longitude REAL NULL,
              statut TEXT NOT NULL,
              motif TEXT NULL,
              receptionnaire TEXT NULL,
              commentaire TEXT NULL,
              signature TEXT NULL,
              heure TEXT NULL,
              latitude_livreur REAL NULL,
              longitude_livreur REAL NULL,
              utilisateur TEXT NULL,
              PRIMARY KEY (tournee, piece)
            );
            CREATE INDEX IF NOT EXISTS arrets_piece ON arrets (piece);
            """);
    }

    SqliteConnection Ouvrir()
    {
        var c = new SqliteConnection(_cnx);
        c.Open();
        return c;
    }

    public static IEnumerable<string> Verifier(string id, TourneeRequest t)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64) yield return "id : 1 à 64 caractères.";
        if (t.Pieces is null || t.Pieces.Count == 0) yield return "pieces : au moins une pièce à livrer.";
        else if (t.Pieces.Count > 200) yield return "pieces : 200 au plus par tournée.";
        else if (t.Pieces.Select(p => p.Trim().ToUpperInvariant()).Distinct().Count() != t.Pieces.Count) yield return "pieces : une pièce figure deux fois.";
        if (t.Nom is { Length: > 100 }) yield return "nom : 100 caractères au plus.";
    }

    public static IEnumerable<string> Verifier(CompteRenduArret r)
    {
        if (!Statuts.Contains(r.Statut)) yield return $"statut : {string.Join(", ", Statuts)}.";
        if (r.Statut == "echec" && r.Motif is null) yield return $"Un échec demande un motif : {string.Join(", ", Motifs)}.";
        if (r.Motif != null && !Motifs.Contains(r.Motif)) yield return $"motif : {string.Join(", ", Motifs)}.";
        if (r.Receptionnaire is { Length: > 100 }) yield return "receptionnaire : 100 caractères au plus.";
        if (r.Commentaire is { Length: > 2000 }) yield return "commentaire : 2000 caractères au plus.";
        if (r.Signature != null && (!r.Signature.StartsWith("data:image/png;base64,") || r.Signature.Length > TailleMaxSignature))
            yield return "signature : image PNG en data URL (data:image/png;base64,...), 300 Ko au plus.";
        if (r.Latitude is < -90 or > 90 || r.Longitude is < -180 or > 180) yield return "Position GPS invalide.";
        if ((r.Latitude is null) != (r.Longitude is null)) yield return "latitude et longitude vont ensemble.";
    }

    /// <summary>
    /// Crée ou remplace une tournée. Les arrêts déjà traités sont gardés tels quels (on ne retire pas une livraison faite) ;
    /// une pièce déjà prévue dans une autre tournée non traitée est refusée, pour ne pas la livrer deux fois.
    /// </summary>
    /// <returns>La tournée, ou les erreurs.</returns>
    public (Tournee? Tournee, IReadOnlyList<string> Erreurs) Enregistrer(string id, TourneeRequest t, IReadOnlyDictionary<string, ALivrer> aLivrer,
        Func<ALivrer, Position?> position, string? utilisateur)
    {
        var pieces = t.Pieces.Select(p => p.Trim().ToUpperInvariant()).ToList();
        using var c = Ouvrir();
        using var tx = c.BeginTransaction();
        var existants = c.Query<(string Piece, string Statut)>("SELECT piece, statut FROM arrets WHERE tournee = @id", new { id }, tx)
            .ToDictionary(x => x.Piece, x => x.Statut);
        var erreurs = new List<string>();
        foreach (var (piece, statut) in existants)
            if (statut != "a-livrer" && !pieces.Contains(piece)) erreurs.Add($"{piece} est déjà traitée ({statut}) : elle ne peut pas être retirée de la tournée.");
        foreach (var p in pieces)
        {
            if (existants.ContainsKey(p)) continue;
            if (!aLivrer.ContainsKey(p)) erreurs.Add($"{p} n'est pas une pièce à livrer (bon de commande ou préparation non clôturé).");
            var ailleurs = c.QueryFirstOrDefault<string>(
                "SELECT tournee FROM arrets WHERE piece = @p AND tournee <> @id AND statut IN ('a-livrer', 'livre')", new { p, id }, tx);
            if (ailleurs != null) erreurs.Add($"{p} est déjà prévue dans la tournée {ailleurs}.");
        }
        if (erreurs.Count > 0) return (null, erreurs);

        var maintenant = Texte(DateTime.UtcNow);
        c.Execute("""
            INSERT INTO tournees (id, date, nom, livreur, depot, utilisateur, cree_le, maj_le)
            VALUES (@id, @date, @nom, @livreur, @depot, @utilisateur, @maintenant, @maintenant)
            ON CONFLICT (id) DO UPDATE SET date = @date, nom = @nom, livreur = @livreur, depot = @depot, maj_le = @maintenant;
            """, new { id, date = t.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), nom = t.Nom, livreur = t.Livreur, depot = t.Depot, utilisateur, maintenant }, tx);
        foreach (var piece in existants.Keys.Except(pieces))
            c.Execute("DELETE FROM arrets WHERE tournee = @id AND piece = @piece", new { id, piece }, tx);
        for (var i = 0; i < pieces.Count; i++)
        {
            var piece = pieces[i];
            if (existants.ContainsKey(piece))
            {
                c.Execute("UPDATE arrets SET ordre = @ordre WHERE tournee = @id AND piece = @piece", new { ordre = i + 1, id, piece }, tx);
                continue;
            }
            var a = aLivrer[piece];
            var pos = position(a);
            c.Execute("""
                INSERT INTO arrets (tournee, piece, ordre, type_piece, client, intitule, reference, adresse_livraison, adresse, complement, code_postal, ville,
                  contact, telephone, total_ttc, net_a_payer, latitude, longitude, statut)
                VALUES (@id, @piece, @ordre, @type, @client, @intitule, @reference, @li, @adresse, @complement, @cp, @ville,
                  @contact, @telephone, @ttc, @net, @lat, @lon, 'a-livrer');
                """, new
            {
                id, piece, ordre = i + 1, type = a.Type, client = a.Client, intitule = a.Intitule, reference = a.Reference, li = a.AdresseLivraison,
                adresse = a.Adresse, complement = a.Complement, cp = a.CodePostal, ville = a.Ville, contact = a.Contact, telephone = a.Telephone,
                ttc = a.TotalTTC, net = a.NetAPayer, lat = pos?.Latitude, lon = pos?.Longitude,
            }, tx);
        }
        tx.Commit();
        return (Lire(id), []);
    }

    /// <summary>Supprime une tournée dont aucun arrêt n'a été traité.</summary>
    public bool? Supprimer(string id)
    {
        using var c = Ouvrir();
        if (c.ExecuteScalar<long>("SELECT COUNT(*) FROM tournees WHERE id = @id", new { id }) == 0) return null;
        if (c.ExecuteScalar<long>("SELECT COUNT(*) FROM arrets WHERE tournee = @id AND statut <> 'a-livrer'", new { id }) > 0) return false;
        c.Execute("DELETE FROM arrets WHERE tournee = @id; DELETE FROM tournees WHERE id = @id;", new { id });
        return true;
    }

    public Tournee? Lire(string id)
    {
        using var c = Ouvrir();
        var t = c.QueryFirstOrDefault<LigneTournee>(SelectTournee + "WHERE t.id = @id GROUP BY t.id", new { id });
        if (t == null) return null;
        var arrets = c.Query<LigneArret>(SelectArret + "WHERE tournee = @id ORDER BY ordre", new { id }).Select(a => a.Arret()).ToList();
        return t.Tournee() with { Arrets = arrets };
    }

    /// <summary>Tournées (sans le détail des arrêts), les plus récentes d'abord.</summary>
    public IReadOnlyList<Tournee> Lister(DateTime? du, DateTime? au, int? livreur, int taille)
    {
        using var c = Ouvrir();
        return c.Query<LigneTournee>(SelectTournee +
                "WHERE (@du IS NULL OR t.date >= @du) AND (@au IS NULL OR t.date <= @au) AND (@livreur IS NULL OR t.livreur = @livreur) " +
                "GROUP BY t.id ORDER BY t.date DESC, t.cree_le DESC LIMIT @taille",
                new { du = du?.ToString("yyyy-MM-dd"), au = au?.ToString("yyyy-MM-dd"), livreur, taille })
            .Select(t => t.Tournee()).ToList();
    }

    /// <summary>Enregistre ce que le livreur a constaté sur un arrêt. Renvoie null si l'arrêt n'existe pas.</summary>
    public Arret? CompteRendu(string tournee, string piece, CompteRenduArret r, string? utilisateur)
    {
        piece = piece.Trim().ToUpperInvariant();
        using var c = Ouvrir();
        var n = c.Execute("""
            UPDATE arrets SET statut = @statut, motif = @motif, receptionnaire = @receptionnaire, commentaire = @commentaire,
              signature = CASE WHEN @statut IN ('a-livrer', 'echec') THEN NULL ELSE COALESCE(@signature, signature) END,
              heure = CASE WHEN @statut = 'a-livrer' THEN NULL ELSE @heure END, latitude_livreur = @lat, longitude_livreur = @lon, utilisateur = @utilisateur
            WHERE tournee = @tournee AND piece = @piece;
            """, new
        {
            statut = r.Statut, motif = r.Statut == "echec" || r.Statut == "partiel" ? r.Motif : null, receptionnaire = r.Receptionnaire,
            commentaire = r.Commentaire, signature = r.Signature, heure = Texte(DateTime.UtcNow), lat = r.Latitude, lon = r.Longitude, utilisateur, tournee, piece,
        });
        if (n == 0) return null;
        c.Execute("UPDATE tournees SET maj_le = @m WHERE id = @tournee", new { m = Texte(DateTime.UtcNow), tournee });
        return c.Query<LigneArret>(SelectArret + "WHERE tournee = @tournee AND piece = @piece", new { tournee, piece }).Single().Arret();
    }

    public string? Signature(string tournee, string piece)
    {
        using var c = Ouvrir();
        return c.QueryFirstOrDefault<string>("SELECT signature FROM arrets WHERE tournee = @tournee AND piece = @piece",
            new { tournee, piece = piece.Trim().ToUpperInvariant() });
    }

    /// <summary>Suivi d'une pièce : tous ses passages en tournée, le plus récent d'abord.</summary>
    public IReadOnlyList<Arret> Suivi(string piece)
    {
        using var c = Ouvrir();
        return c.Query<LigneArret>(SelectArret + "JOIN tournees t ON t.id = arrets.tournee WHERE piece = @piece ORDER BY t.date DESC, arrets.heure DESC",
            new { piece = piece.Trim().ToUpperInvariant() }).Select(a => a.Arret()).ToList();
    }

    const string SelectTournee =
        "SELECT t.id AS Id, t.date AS Date, t.nom AS Nom, t.livreur AS Livreur, t.depot AS Depot, t.utilisateur AS Utilisateur, t.cree_le AS CreeLe, " +
        "t.maj_le AS MajLe, COUNT(a.piece) AS NbArrets, COALESCE(SUM(CASE WHEN a.statut <> 'a-livrer' THEN 1 ELSE 0 END), 0) AS NbTraites, " +
        "COALESCE(SUM(a.total_ttc), 0) AS TotalTTC FROM tournees t LEFT JOIN arrets a ON a.tournee = t.id ";

    const string SelectArret =
        "SELECT arrets.tournee AS Tournee, piece AS Piece, ordre AS Ordre, type_piece AS TypePiece, client AS Client, intitule AS Intitule, " +
        "reference AS Reference, adresse_livraison AS AdresseLivraison, adresse AS Adresse, complement AS Complement, code_postal AS CodePostal, " +
        "ville AS Ville, contact AS Contact, telephone AS Telephone, total_ttc AS TotalTTC, net_a_payer AS NetAPayer, latitude AS Latitude, " +
        "longitude AS Longitude, statut AS Statut, motif AS Motif, receptionnaire AS Receptionnaire, commentaire AS Commentaire, " +
        "signature IS NOT NULL AS Signe, heure AS Heure, latitude_livreur AS LatitudeLivreur, longitude_livreur AS LongitudeLivreur, " +
        "arrets.utilisateur AS Utilisateur FROM arrets ";

    static string? Texte(DateTime? d) => d?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    static DateTime? Date(string? s) => s is null ? null : DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    sealed class LigneTournee
    {
        public string Id { get; set; } = "";
        public string Date { get; set; } = "";
        public string? Nom { get; set; }
        public long? Livreur { get; set; }
        public long? Depot { get; set; }
        public string? Utilisateur { get; set; }
        public string CreeLe { get; set; } = "";
        public string MajLe { get; set; } = "";
        public long NbArrets { get; set; }
        public long NbTraites { get; set; }
        public double TotalTTC { get; set; }

        public Tournee Tournee() => new()
        {
            Id = Id, Date = DateTime.ParseExact(Date, "yyyy-MM-dd", CultureInfo.InvariantCulture), Nom = Nom, Livreur = (int?)Livreur, Depot = (int?)Depot,
            Utilisateur = Utilisateur, CreeLe = Livraison.Date(CreeLe)!.Value, MajLe = Livraison.Date(MajLe)!.Value, NbArrets = (int)NbArrets, NbTraites = (int)NbTraites,
            TotalTTC = Math.Round((decimal)TotalTTC, 2),
            Statut = NbTraites == 0 ? "preparee" : NbTraites < NbArrets ? "en-cours" : "terminee",
        };
    }

    sealed class LigneArret
    {
        public string Tournee { get; set; } = "";
        public string Piece { get; set; } = "";
        public long Ordre { get; set; }
        public string TypePiece { get; set; } = "";
        public string Client { get; set; } = "";
        public string? Intitule { get; set; }
        public string? Reference { get; set; }
        public long? AdresseLivraison { get; set; }
        public string? Adresse { get; set; }
        public string? Complement { get; set; }
        public string? CodePostal { get; set; }
        public string? Ville { get; set; }
        public string? Contact { get; set; }
        public string? Telephone { get; set; }
        public double TotalTTC { get; set; }
        public double NetAPayer { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public string Statut { get; set; } = "";
        public string? Motif { get; set; }
        public string? Receptionnaire { get; set; }
        public string? Commentaire { get; set; }
        public long Signe { get; set; }
        public string? Heure { get; set; }
        public double? LatitudeLivreur { get; set; }
        public double? LongitudeLivreur { get; set; }
        public string? Utilisateur { get; set; }

        public Arret Arret() => new()
        {
            Tournee = Tournee, Piece = Piece, Ordre = (int)Ordre, TypePiece = TypePiece, Client = Client, Intitule = Intitule, Reference = Reference,
            AdresseLivraison = (int?)AdresseLivraison, Adresse = Adresse, Complement = Complement, CodePostal = CodePostal, Ville = Ville, Contact = Contact,
            Telephone = Telephone, TotalTTC = Math.Round((decimal)TotalTTC, 2), NetAPayer = Math.Round((decimal)NetAPayer, 2), Latitude = Latitude, Longitude = Longitude,
            Statut = Statut, Motif = Motif, Receptionnaire = Receptionnaire, Commentaire = Commentaire, Signe = Signe != 0, Heure = Date(Heure),
            LatitudeLivreur = LatitudeLivreur, LongitudeLivreur = LongitudeLivreur, Utilisateur = Utilisateur,
        };
    }
}

/// <summary>Ordre de passage : plus proche voisin depuis le point de départ, puis amélioration 2-opt. Distances à vol d'oiseau.</summary>
public static class Itineraire
{
    public sealed record Point(string Cle, double Latitude, double Longitude);

    public sealed record Resultat(IReadOnlyList<string> Ordre, double DistanceKm, IReadOnlyList<string> SansPosition);

    /// <summary>Distance à vol d'oiseau en kilomètres (haversine).</summary>
    public static double Km(double lat1, double lon1, double lat2, double lon2)
    {
        const double r = 6371.0;
        double Rad(double d) => d * Math.PI / 180;
        var dLat = Rad(lat2 - lat1);
        var dLon = Rad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * r * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    /// <summary>
    /// Ordonne les points. depart : dépôt ou position du livreur ; sans départ, la tournée part du premier point.
    /// retour : compter le retour au départ (tournée qui revient au dépôt). Les clés sans position vont à la fin, dans l'ordre reçu.
    /// </summary>
    public static Resultat Ordonner(IReadOnlyList<Point> points, (double Latitude, double Longitude)? depart, bool retour, IReadOnlyList<string>? sansPosition = null)
    {
        var restants = points.ToList();
        var ordre = new List<Point>();
        var courant = depart ?? (restants.Count > 0 ? (restants[0].Latitude, restants[0].Longitude) : (0, 0));
        while (restants.Count > 0)
        {
            var proche = restants.MinBy(p => Km(courant.Item1, courant.Item2, p.Latitude, p.Longitude))!;
            ordre.Add(proche);
            restants.Remove(proche);
            courant = (proche.Latitude, proche.Longitude);
        }

        // 2-opt : inverse un segment tant que cela raccourcit le trajet.
        var ameliore = true;
        for (var tour = 0; ameliore && tour < 50; tour++)
        {
            ameliore = false;
            for (var i = 0; i < ordre.Count - 1; i++)
                for (var j = i + 1; j < ordre.Count; j++)
                {
                    var avant = Longueur(ordre, depart, retour);
                    ordre.Reverse(i, j - i + 1);
                    if (Longueur(ordre, depart, retour) < avant - 1e-9) ameliore = true;
                    else ordre.Reverse(i, j - i + 1);
                }
        }
        return new Resultat(ordre.Select(p => p.Cle).Concat(sansPosition ?? []).ToList(), Math.Round(Longueur(ordre, depart, retour), 1), sansPosition ?? []);
    }

    static double Longueur(List<Point> ordre, (double Latitude, double Longitude)? depart, bool retour)
    {
        if (ordre.Count == 0) return 0;
        var total = 0.0;
        if (depart is { } d) total += Km(d.Latitude, d.Longitude, ordre[0].Latitude, ordre[0].Longitude);
        for (var i = 1; i < ordre.Count; i++) total += Km(ordre[i - 1].Latitude, ordre[i - 1].Longitude, ordre[i].Latitude, ordre[i].Longitude);
        if (retour && depart is { } r) total += Km(ordre[^1].Latitude, ordre[^1].Longitude, r.Latitude, r.Longitude);
        return total;
    }
}
