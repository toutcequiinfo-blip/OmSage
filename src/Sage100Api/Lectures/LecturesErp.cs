using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

using Sage100Api.Extensions;

namespace Sage100Api.Lectures;

// Lectures pour les extensions (CRM, livraison, géolocalisation, recouvrement). SQL en lecture seule, comme LecturesSql.
// Les types sont des classes à propriétés : Dapper convertit alors smallint, int et float sans CAST colonne par colonne.

public sealed record FicheClient
{
    public string Numero { get; init; } = "";
    public string? Intitule { get; init; }
    public string? Adresse { get; init; }
    public string? Complement { get; init; }
    public string? CodePostal { get; init; }
    public string? Ville { get; init; }
    public string? Region { get; init; }
    public string? Pays { get; init; }
    public string? Telephone { get; init; }
    public string? Telecopie { get; init; }
    public string? Email { get; init; }
    public string? Site { get; init; }
    public string? Siret { get; init; }
    public string? Contact { get; init; }
    public string? Commentaire { get; init; }
    /// <summary>Représentant (collaborateur Sage) de la fiche client.</summary>
    public int? Commercial { get; init; }
    public string? CommercialNom { get; init; }
    /// <summary>Encours autorisé (CT_Encours) ; 0 = pas de plafond saisi.</summary>
    public decimal EncoursAutorise { get; init; }
    public bool Sommeil { get; init; }
    public int CategorieTarifaire { get; init; }
    public IReadOnlyList<ContactClient> Contacts { get; init; } = [];
    public IReadOnlyList<AdresseLivraison> AdressesLivraison { get; init; } = [];
    /// <summary>Position GPS enregistrée par une extension (gardée par l'API, Sage n'a pas de champ pour elle).</summary>
    public Position? Position { get; init; }
}

public sealed record ContactClient
{
    public int Numero { get; init; }
    public string? Nom { get; init; }
    public string? Prenom { get; init; }
    public string? Fonction { get; init; }
    public string? Telephone { get; init; }
    public string? Portable { get; init; }
    public string? Email { get; init; }
}

public sealed record AdresseLivraison
{
    /// <summary>Numéro interne Sage de l'adresse (LI_No), repris sur les documents.</summary>
    public int Numero { get; init; }
    public string Client { get; init; } = "";
    public string? Intitule { get; init; }
    public string? Adresse { get; init; }
    public string? Complement { get; init; }
    public string? CodePostal { get; init; }
    public string? Ville { get; init; }
    public string? Pays { get; init; }
    public string? Contact { get; init; }
    public string? Telephone { get; init; }
    public string? Email { get; init; }
    public bool Principale { get; init; }
    public string? Commentaire { get; init; }
    public Position? Position { get; init; }
}

public sealed record CollaborateurFiche
{
    public int Numero { get; init; }
    public string? Nom { get; init; }
    public string? Prenom { get; init; }
    public string? Fonction { get; init; }
    public string? Telephone { get; init; }
    public string? Portable { get; init; }
    public string? Email { get; init; }
    public bool Vendeur { get; init; }
    public bool Caissier { get; init; }
}

public sealed record Depot
{
    public int Numero { get; init; }
    public string? Intitule { get; init; }
    public string? Adresse { get; init; }
    public string? CodePostal { get; init; }
    public string? Ville { get; init; }
}

/// <summary>Entête d'un document de vente (F_DOCENTETE, domaine vente).</summary>
public sealed record EnteteDocument
{
    /// <summary>devis, commande, preparation, livraison, retour, avoir, facture, facture-comptabilisee.</summary>
    public string Type { get; init; } = "";
    public string Piece { get; init; } = "";
    public DateTime Date { get; init; }
    public string Client { get; init; } = "";
    public string? Intitule { get; init; }
    public string? Reference { get; init; }
    public string? IdExterne { get; init; }
    public decimal TotalHT { get; init; }
    public decimal TotalTTC { get; init; }
    public decimal NetAPayer { get; init; }
    public decimal MontantRegle { get; init; }
    /// <summary>Date de livraison prévue (DO_DateLivr), null si non saisie.</summary>
    public DateTime? DateLivraison { get; init; }
    /// <summary>Adresse de livraison (LI_No), voir /clients/{numero}/adresses-livraison.</summary>
    public int? AdresseLivraison { get; init; }
    public int? Commercial { get; init; }
    public int? Depot { get; init; }
    /// <summary>Statut Sage du document (DO_Statut : 0 saisi, 1 confirmé, 2 accepté / à facturer selon le type).</summary>
    public int Statut { get; init; }
    public bool Cloture { get; init; }
}

public sealed record DetailDocument(EnteteDocument Entete, IReadOnlyList<LignePiece> Lignes);

/// <summary>
/// Pièce à livrer (bon de commande, préparation, bon de livraison, facture), avec l'adresse et le téléphone utiles à une tournée.
/// Type : nom du type de document (commande, preparation, livraison, facture, facture-comptabilisee).
/// </summary>
public sealed record ALivrer
{
    public string Type { get; init; } = "";
    public string Piece { get; init; } = "";
    public DateTime Date { get; init; }
    public DateTime? DateLivraison { get; init; }
    public string Client { get; init; } = "";
    public string? Intitule { get; init; }
    public string? Reference { get; init; }
    public decimal TotalTTC { get; init; }
    public decimal NetAPayer { get; init; }
    public int? Depot { get; init; }
    public int? AdresseLivraison { get; init; }
    public string? Adresse { get; init; }
    public string? Complement { get; init; }
    public string? CodePostal { get; init; }
    public string? Ville { get; init; }
    public string? Pays { get; init; }
    public string? Contact { get; init; }
    public string? Telephone { get; init; }
    /// <summary>Position de l'adresse de livraison, sinon du client, si une extension l'a enregistrée.</summary>
    public Position? Position { get; init; }
    /// <summary>Dernière tournée où la pièce figure (null si elle n'a jamais été prévue).</summary>
    public string? Tournee { get; init; }
    public DateTime? DateTournee { get; init; }
    /// <summary>Statut de la pièce dans cette tournée : a-livrer (déjà prévue), partiel ou echec (à reprogrammer).</summary>
    public string? StatutLivraison { get; init; }
}

/// <summary>Écriture client non lettrée (F_ECRITUREC) : facture ou avoir pas encore soldé. Montant positif = dû par le client.</summary>
public sealed record Echeance
{
    public string Client { get; init; } = "";
    public string? Intitule { get; init; }
    public string? Journal { get; init; }
    public DateTime Date { get; init; }
    public string? Piece { get; init; }
    public string? Reference { get; init; }
    public string? Libelle { get; init; }
    public DateTime? DateEcheance { get; init; }
    public decimal Montant { get; init; }
    public DateTime? DateRelance { get; init; }
    public int JoursRetard { get; set; }
}

/// <summary>Tables suivies par GET /modifications : code renvoyé et table Sage.</summary>
public static class TablesSuivies
{
    public static readonly IReadOnlyDictionary<string, (string Table, string Cle)> Liste = new Dictionary<string, (string, string)>
    {
        ["clients"] = ("F_COMPTET", "CT_Num"),
        ["articles"] = ("F_ARTICLE", "AR_Ref"),
        ["documents"] = ("F_DOCENTETE", "DO_Piece"),
        ["adresses-livraison"] = ("F_LIVRAISON", "CAST(LI_No AS varchar(20))"),
        ["contacts"] = ("F_CONTACTT", "CAST(CT_No AS varchar(20))"),
        ["ecritures"] = ("F_ECRITUREC", "CAST(EC_No AS varchar(20))"),
    };
}

public sealed record Modification(string Cle, DateTime ModifieLe);

/// <summary>
/// Chiffres d'un client pour la vue 360° du CRM. CA : factures HT (comptabilisées ou non) des 12 derniers mois glissants,
/// factures de retour et d'avoir déduites (DO_Provenance 1 et 2).
/// </summary>
public sealed record IndicateursClient
{
    public decimal CaDouzeMois { get; init; }
    public decimal CaDouzeMoisPrecedents { get; init; }
    public int FacturesDouzeMois { get; init; }
    public DateTime? DerniereFacture { get; init; }
    public DateTime? DerniereCommande { get; init; }
    public int CommandesEnCours { get; init; }
    public decimal MontantCommandesEnCours { get; init; }
    public int DevisEnCours { get; init; }
    public decimal MontantDevisEnCours { get; init; }
    public IReadOnlyList<ArticleAchete> ArticlesLesPlusAchetes { get; init; } = [];
}

public sealed record ArticleAchete
{
    public string Article { get; init; } = "";
    public string? Designation { get; init; }
    public decimal Quantite { get; init; }
    public decimal MontantHT { get; init; }
}

/// <summary>Client du portefeuille d'un commercial (représentant de la fiche client).</summary>
public sealed record ClientPortefeuille
{
    public string Numero { get; init; } = "";
    public string? Intitule { get; init; }
    public string? Ville { get; init; }
    public string? Telephone { get; init; }
    public string? Email { get; init; }
    public bool Sommeil { get; init; }
    /// <summary>Dernière activité CRM faite (renseignée par l'API depuis la base des extensions).</summary>
    public DateTime? DerniereActivite { get; set; }
}

public interface ILecturesErp
{
    Task<FicheClient?> FicheClient(string numero);
    Task<IReadOnlyList<ContactClient>> Contacts(string client);
    Task<IReadOnlyList<AdresseLivraison>> AdressesLivraison(string client);
    Task<IReadOnlyList<CollaborateurFiche>> Collaborateurs();
    Task<IReadOnlyList<Depot>> Depots();
    Task<IReadOnlyList<EnteteDocument>> Documents(int? type, string? client, DateTime? du, DateTime? au, bool? cloture, int page, int taille);
    Task<DetailDocument?> Document(int type, string piece);
    /// <param name="types">Codes DO_Type livrables (1, 2, 3, 6, 7) ; null = tous.</param>
    /// <param name="depuis">Factures datées de ce jour ou après (les autres types ne sont pas filtrés) ; null = toutes.</param>
    Task<IReadOnlyList<ALivrer>> ALivrer(DateTime? jusquAu, int? depot, IReadOnlyCollection<int>? types = null, DateTime? depuis = null);
    Task<IReadOnlyList<Echeance>> Echeances(string? client);
    /// <summary>Codes modifiés depuis une date (colonne cbModification des tables Sage SQL). Null si la colonne n'existe pas.</summary>
    Task<IReadOnlyList<Modification>?> Modifications(string table, DateTime depuis, int taille);
    Task<IndicateursClient> Indicateurs(string client, DateTime aujourdhui);
    /// <summary>Clients dont le représentant (CO_No de la fiche) est ce collaborateur.</summary>
    Task<IReadOnlyList<ClientPortefeuille>> Portefeuille(int collaborateur);
}

public static class TypesDocument
{
    static readonly string[] Noms = ["devis", "commande", "preparation", "livraison", "retour", "avoir", "facture", "facture-comptabilisee"];

    public static int? Code(string? nom) => Array.IndexOf(Noms, (nom ?? "").Trim().ToLowerInvariant()) is var i and >= 0 ? i : null;
    public static string Nom(int code) => code >= 0 && code < Noms.Length ? Noms[code] : code.ToString();
    public static string Liste => string.Join(", ", Noms);
}

public sealed class LecturesErpSql(Dossiers dossiers) : ILecturesErp
{
    SqlConnection Cnx() => new(dossiers.ChaineSql);

    // Sage met 1900-01-01 dans les dates non saisies.
    static string Date(string colonne) => $"NULLIF({colonne}, '1900-01-01')";

    const string SelectEntete =
        "SELECT CAST(e.DO_Type AS int) AS TypeCode, e.DO_Piece AS Piece, e.DO_Date AS Date, e.DO_Tiers AS Client, t.CT_Intitule AS Intitule, " +
        "NULLIF(e.DO_Ref, '') AS Reference, NULLIF(e.DO_RefExterne, '') AS IdExterne, " +
        "CAST(e.DO_TotalHT AS decimal(18,2)) AS TotalHT, CAST(e.DO_TotalTTC AS decimal(18,2)) AS TotalTTC, " +
        "CAST(e.DO_NetAPayer AS decimal(18,2)) AS NetAPayer, CAST(e.DO_MontantRegle AS decimal(18,2)) AS MontantRegle, " +
        "NULLIF(e.DO_DateLivr, '1900-01-01') AS DateLivraison, NULLIF(e.LI_No, 0) AS AdresseLivraison, NULLIF(e.CO_No, 0) AS Commercial, " +
        "NULLIF(e.DE_No, 0) AS Depot, CAST(e.DO_Statut AS int) AS Statut, CAST(e.DO_Cloture AS bit) AS Cloture " +
        "FROM F_DOCENTETE e LEFT JOIN F_COMPTET t ON t.CT_Num = e.DO_Tiers ";

    sealed record LigneEntete
    {
        public int TypeCode { get; init; }
        public string Piece { get; init; } = "";
        public DateTime Date { get; init; }
        public string Client { get; init; } = "";
        public string? Intitule { get; init; }
        public string? Reference { get; init; }
        public string? IdExterne { get; init; }
        public decimal TotalHT { get; init; }
        public decimal TotalTTC { get; init; }
        public decimal NetAPayer { get; init; }
        public decimal MontantRegle { get; init; }
        public DateTime? DateLivraison { get; init; }
        public int? AdresseLivraison { get; init; }
        public int? Commercial { get; init; }
        public int? Depot { get; init; }
        public int Statut { get; init; }
        public bool Cloture { get; init; }

        public EnteteDocument Entete() => new()
        {
            Type = TypesDocument.Nom(TypeCode), Piece = Piece, Date = Date, Client = Client, Intitule = Intitule, Reference = Reference,
            IdExterne = IdExterne, TotalHT = TotalHT, TotalTTC = TotalTTC, NetAPayer = NetAPayer, MontantRegle = MontantRegle,
            DateLivraison = DateLivraison, AdresseLivraison = AdresseLivraison, Commercial = Commercial, Depot = Depot, Statut = Statut, Cloture = Cloture,
        };
    }

    public async Task<FicheClient?> FicheClient(string numero)
    {
        using var c = Cnx();
        var fiche = await c.QueryFirstOrDefaultAsync<FicheClient>(
            "SELECT t.CT_Num AS Numero, t.CT_Intitule AS Intitule, NULLIF(t.CT_Adresse, '') AS Adresse, NULLIF(t.CT_Complement, '') AS Complement, " +
            "NULLIF(t.CT_CodePostal, '') AS CodePostal, NULLIF(t.CT_Ville, '') AS Ville, NULLIF(t.CT_CodeRegion, '') AS Region, NULLIF(t.CT_Pays, '') AS Pays, " +
            "NULLIF(t.CT_Telephone, '') AS Telephone, NULLIF(t.CT_Telecopie, '') AS Telecopie, NULLIF(t.CT_EMail, '') AS Email, NULLIF(t.CT_Site, '') AS Site, " +
            "NULLIF(t.CT_Siret, '') AS Siret, NULLIF(t.CT_Contact, '') AS Contact, NULLIF(t.CT_Commentaire, '') AS Commentaire, " +
            "NULLIF(t.CO_No, 0) AS Commercial, NULLIF(LTRIM(RTRIM(ISNULL(co.CO_Prenom, '') + ' ' + ISNULL(co.CO_Nom, ''))), '') AS CommercialNom, " +
            "CAST(t.CT_Encours AS decimal(18,2)) AS EncoursAutorise, CAST(t.CT_Sommeil AS bit) AS Sommeil, CAST(t.N_CatTarif AS int) AS CategorieTarifaire " +
            "FROM F_COMPTET t LEFT JOIN F_COLLABORATEUR co ON co.CO_No = t.CO_No WHERE t.CT_Type = 0 AND t.CT_Num = @numero",
            new { numero });
        if (fiche is null) return null;
        return fiche with { Contacts = await Contacts(numero), AdressesLivraison = await AdressesLivraison(numero) };
    }

    public async Task<IReadOnlyList<ContactClient>> Contacts(string client)
    {
        using var c = Cnx();
        return (await c.QueryAsync<ContactClient>(
            "SELECT CT_No AS Numero, NULLIF(CT_Nom, '') AS Nom, NULLIF(CT_Prenom, '') AS Prenom, NULLIF(CT_Fonction, '') AS Fonction, " +
            "NULLIF(CT_Telephone, '') AS Telephone, NULLIF(CT_TelPortable, '') AS Portable, NULLIF(CT_EMail, '') AS Email " +
            "FROM F_CONTACTT WHERE CT_Num = @client ORDER BY CT_Nom, CT_Prenom", new { client })).AsList();
    }

    public async Task<IReadOnlyList<AdresseLivraison>> AdressesLivraison(string client)
    {
        using var c = Cnx();
        return (await c.QueryAsync<AdresseLivraison>(
            "SELECT LI_No AS Numero, CT_Num AS Client, NULLIF(LI_Intitule, '') AS Intitule, NULLIF(LI_Adresse, '') AS Adresse, " +
            "NULLIF(LI_Complement, '') AS Complement, NULLIF(LI_CodePostal, '') AS CodePostal, NULLIF(LI_Ville, '') AS Ville, NULLIF(LI_Pays, '') AS Pays, " +
            "NULLIF(LI_Contact, '') AS Contact, NULLIF(LI_Telephone, '') AS Telephone, NULLIF(LI_EMail, '') AS Email, " +
            "CAST(LI_Principal AS bit) AS Principale, NULLIF(LI_Commentaire, '') AS Commentaire " +
            "FROM F_LIVRAISON WHERE CT_Num = @client ORDER BY LI_Principal DESC, LI_Intitule", new { client })).AsList();
    }

    public async Task<IReadOnlyList<CollaborateurFiche>> Collaborateurs()
    {
        using var c = Cnx();
        return (await c.QueryAsync<CollaborateurFiche>(
            "SELECT CO_No AS Numero, CO_Nom AS Nom, NULLIF(CO_Prenom, '') AS Prenom, NULLIF(CO_Fonction, '') AS Fonction, " +
            "NULLIF(CO_Telephone, '') AS Telephone, NULLIF(CO_TelPortable, '') AS Portable, NULLIF(CO_EMail, '') AS Email, " +
            "CAST(CO_Vendeur AS bit) AS Vendeur, CAST(CO_Caissier AS bit) AS Caissier " +
            "FROM F_COLLABORATEUR WHERE CO_Sommeil = 0 ORDER BY CO_Nom, CO_Prenom")).AsList();
    }

    public async Task<IReadOnlyList<Depot>> Depots()
    {
        using var c = Cnx();
        return (await c.QueryAsync<Depot>(
            "SELECT DE_No AS Numero, DE_Intitule AS Intitule, NULLIF(DE_Adresse, '') AS Adresse, NULLIF(DE_CodePostal, '') AS CodePostal, " +
            "NULLIF(DE_Ville, '') AS Ville FROM F_DEPOT ORDER BY DE_No")).AsList();
    }

    public async Task<IReadOnlyList<EnteteDocument>> Documents(int? type, string? client, DateTime? du, DateTime? au, bool? cloture, int page, int taille)
    {
        using var c = Cnx();
        var r = await c.QueryAsync<LigneEntete>(
            SelectEntete +
            "WHERE e.DO_Domaine = 0 AND e.DO_Type BETWEEN 0 AND 7 AND (@type IS NULL OR e.DO_Type = @type) AND (@client IS NULL OR e.DO_Tiers = @client) " +
            "AND (@du IS NULL OR e.DO_Date >= @du) AND (@au IS NULL OR e.DO_Date <= @au) " +
            "AND (@cloture IS NULL OR e.DO_Cloture = @cloture) " +
            "ORDER BY e.DO_Date DESC, e.DO_Piece DESC OFFSET @saut ROWS FETCH NEXT @taille ROWS ONLY",
            new { type, client, du, au, cloture = cloture is null ? (int?)null : cloture.Value ? 1 : 0, saut = (page - 1) * taille, taille });
        return r.Select(x => x.Entete()).ToList();
    }

    public async Task<DetailDocument?> Document(int type, string piece)
    {
        using var c = Cnx();
        var e = await c.QueryFirstOrDefaultAsync<LigneEntete>(SelectEntete + "WHERE e.DO_Domaine = 0 AND e.DO_Type = @type AND e.DO_Piece = @piece", new { type, piece });
        if (e is null) return null;
        var lignes = await c.QueryAsync<LignePiece>(
            LecturesSql.SelectLignes + "WHERE l.DO_Domaine = 0 AND l.DO_Type = @type AND l.DO_Piece = @piece ORDER BY l.DL_Ligne",
            new { type, piece });
        return new DetailDocument(e.Entete(), lignes.AsList());
    }

    /// <summary>Types de documents qu'une tournée peut livrer : commande, préparation, bon de livraison, facture, facture comptabilisée.</summary>
    public static readonly int[] TypesLivrables = [1, 2, 3, 6, 7];

    public async Task<IReadOnlyList<ALivrer>> ALivrer(DateTime? jusquAu, int? depot, IReadOnlyCollection<int>? types = null, DateTime? depuis = null)
    {
        // Commandes, préparations et BL non clôturés ; factures hors factures d'acompte (elles sont le règlement), d'avoir et de retour.
        // Adresse de livraison du document, sinon celle de la fiche client.
        var codes = (types ?? TypesLivrables).Where(TypesLivrables.Contains).Distinct().ToArray();
        if (codes.Length == 0) return [];
        using var c = Cnx();
        return (await c.QueryAsync<ALivrer>(
            "SELECT CASE e.DO_Type WHEN 1 THEN 'commande' WHEN 2 THEN 'preparation' WHEN 3 THEN 'livraison' WHEN 6 THEN 'facture' " +
            "ELSE 'facture-comptabilisee' END AS Type, e.DO_Piece AS Piece, e.DO_Date AS Date, " + Date("e.DO_DateLivr") + " AS DateLivraison, " +
            "e.DO_Tiers AS Client, t.CT_Intitule AS Intitule, NULLIF(e.DO_Ref, '') AS Reference, " +
            "CAST(e.DO_TotalTTC AS decimal(18,2)) AS TotalTTC, CAST(e.DO_NetAPayer AS decimal(18,2)) AS NetAPayer, NULLIF(e.DE_No, 0) AS Depot, " +
            "NULLIF(e.LI_No, 0) AS AdresseLivraison, " +
            "NULLIF(COALESCE(li.LI_Adresse, t.CT_Adresse), '') AS Adresse, NULLIF(COALESCE(li.LI_Complement, t.CT_Complement), '') AS Complement, " +
            "NULLIF(COALESCE(li.LI_CodePostal, t.CT_CodePostal), '') AS CodePostal, NULLIF(COALESCE(li.LI_Ville, t.CT_Ville), '') AS Ville, " +
            "NULLIF(COALESCE(li.LI_Pays, t.CT_Pays), '') AS Pays, NULLIF(COALESCE(NULLIF(li.LI_Contact, ''), t.CT_Contact), '') AS Contact, " +
            "NULLIF(COALESCE(NULLIF(li.LI_Telephone, ''), t.CT_Telephone), '') AS Telephone " +
            "FROM F_DOCENTETE e LEFT JOIN F_COMPTET t ON t.CT_Num = e.DO_Tiers LEFT JOIN F_LIVRAISON li ON li.LI_No = e.LI_No AND e.LI_No <> 0 " +
            "WHERE e.DO_Domaine = 0 AND e.DO_Type IN @codes " +
            "AND (e.DO_Type IN (1, 2, 3) AND e.DO_Cloture = 0 OR e.DO_Type IN (6, 7) AND e.DO_Provenance NOT IN (1, 2) " +
            "  AND NOT EXISTS (SELECT 1 FROM F_DOCREGL fa WHERE fa.DO_PieceAcompte = e.DO_Piece) AND (@depuis IS NULL OR e.DO_Date >= @depuis)) " +
            "AND (@jusquAu IS NULL OR " + Date("e.DO_DateLivr") + " IS NULL OR e.DO_DateLivr <= @jusquAu) AND (@depot IS NULL OR e.DE_No = @depot) " +
            "ORDER BY " + Date("e.DO_DateLivr") + ", e.DO_Piece",
            new { jusquAu, depot, codes, depuis })).AsList();
    }

    public async Task<IReadOnlyList<Echeance>> Echeances(string? client)
    {
        // Écritures non lettrées des comptes clients : débit = facture due, crédit = avoir ou règlement non affecté.
        using var c = Cnx();
        return (await c.QueryAsync<Echeance>(
            "SELECT e.CT_Num AS Client, t.CT_Intitule AS Intitule, e.JO_Num AS Journal, e.EC_Date AS Date, NULLIF(e.EC_Piece, '') AS Piece, " +
            "NULLIF(e.EC_RefPiece, '') AS Reference, NULLIF(e.EC_Intitule, '') AS Libelle, " + Date("e.EC_Echeance") + " AS DateEcheance, " +
            "CAST(CASE WHEN e.EC_Sens = 0 THEN e.EC_Montant ELSE -e.EC_Montant END AS decimal(18,2)) AS Montant, " +
            Date("e.EC_DateRelance") + " AS DateRelance " +
            "FROM F_ECRITUREC e JOIN F_COMPTET t ON t.CT_Num = e.CT_Num AND t.CT_Type = 0 " +
            "WHERE e.EC_Lettre = 0 AND (@client IS NULL OR e.CT_Num = @client) " +
            "ORDER BY e.CT_Num, " + Date("e.EC_Echeance") + ", e.EC_Date", new { client })).AsList();
    }

    public async Task<IndicateursClient> Indicateurs(string client, DateTime aujourdhui)
    {
        var debut = aujourdhui.Date.AddYears(-1).AddDays(1);
        var debutPrecedent = debut.AddYears(-1);
        using var c = Cnx();
        // Signe : une facture de retour ou d'avoir vient en déduction du CA.
        const string Signe = "CASE WHEN e.DO_Provenance IN (1, 2) THEN -1 ELSE 1 END";
        var i = await c.QueryFirstAsync<IndicateursClient>(
            "SELECT " +
            $"CAST(ISNULL(SUM(CASE WHEN e.DO_Type IN (6, 7) AND e.DO_Date >= @debut THEN {Signe} * e.DO_TotalHT END), 0) AS decimal(18,2)) AS CaDouzeMois, " +
            $"CAST(ISNULL(SUM(CASE WHEN e.DO_Type IN (6, 7) AND e.DO_Date >= @debutPrecedent AND e.DO_Date < @debut THEN {Signe} * e.DO_TotalHT END), 0) AS decimal(18,2)) AS CaDouzeMoisPrecedents, " +
            "COUNT(CASE WHEN e.DO_Type IN (6, 7) AND e.DO_Date >= @debut AND e.DO_Provenance = 0 THEN 1 END) AS FacturesDouzeMois, " +
            "MAX(CASE WHEN e.DO_Type IN (6, 7) AND e.DO_Provenance = 0 THEN e.DO_Date END) AS DerniereFacture, " +
            "MAX(CASE WHEN e.DO_Type = 1 THEN e.DO_Date END) AS DerniereCommande, " +
            "COUNT(CASE WHEN e.DO_Type = 1 AND e.DO_Cloture = 0 THEN 1 END) AS CommandesEnCours, " +
            "CAST(ISNULL(SUM(CASE WHEN e.DO_Type = 1 AND e.DO_Cloture = 0 THEN e.DO_TotalTTC END), 0) AS decimal(18,2)) AS MontantCommandesEnCours, " +
            "COUNT(CASE WHEN e.DO_Type = 0 AND e.DO_Cloture = 0 THEN 1 END) AS DevisEnCours, " +
            "CAST(ISNULL(SUM(CASE WHEN e.DO_Type = 0 AND e.DO_Cloture = 0 THEN e.DO_TotalHT END), 0) AS decimal(18,2)) AS MontantDevisEnCours " +
            "FROM F_DOCENTETE e WHERE e.DO_Domaine = 0 AND e.DO_Tiers = @client AND e.DO_Type IN (0, 1, 6, 7)",
            new { client, debut, debutPrecedent });
        var articles = await c.QueryAsync<ArticleAchete>(
            "SELECT TOP 10 l.AR_Ref AS Article, MAX(l.DL_Design) AS Designation, " +
            $"CAST(SUM({Signe} * l.DL_Qte) AS decimal(18,3)) AS Quantite, CAST(SUM({Signe} * l.DL_MontantHT) AS decimal(18,2)) AS MontantHT " +
            "FROM F_DOCLIGNE l JOIN F_DOCENTETE e ON e.DO_Domaine = l.DO_Domaine AND e.DO_Type = l.DO_Type AND e.DO_Piece = l.DO_Piece " +
            "WHERE l.DO_Domaine = 0 AND l.DO_Type IN (6, 7) AND e.DO_Tiers = @client AND e.DO_Date >= @debut AND l.AR_Ref <> '' " +
            "GROUP BY l.AR_Ref ORDER BY SUM(" + Signe + " * l.DL_MontantHT) DESC",
            new { client, debut });
        return i with { ArticlesLesPlusAchetes = articles.AsList() };
    }

    public async Task<IReadOnlyList<ClientPortefeuille>> Portefeuille(int collaborateur)
    {
        using var c = Cnx();
        return (await c.QueryAsync<ClientPortefeuille>(
            "SELECT CT_Num AS Numero, CT_Intitule AS Intitule, NULLIF(CT_Ville, '') AS Ville, NULLIF(CT_Telephone, '') AS Telephone, " +
            "NULLIF(CT_EMail, '') AS Email, CAST(CT_Sommeil AS bit) AS Sommeil " +
            "FROM F_COMPTET WHERE CT_Type = 0 AND CO_No = @collaborateur ORDER BY CT_Intitule", new { collaborateur })).AsList();
    }

    public async Task<IReadOnlyList<Modification>?> Modifications(string table, DateTime depuis, int taille)
    {
        var (nom, cle) = TablesSuivies.Liste[table];
        using var c = Cnx();
        if (await c.ExecuteScalarAsync<int?>("SELECT COL_LENGTH(@nom, 'cbModification')", new { nom }) is null) return null;
        return (await c.QueryAsync<Modification>(
            $"SELECT TOP (@taille) {cle} AS Cle, cbModification AS ModifieLe FROM {nom} WHERE cbModification > @depuis ORDER BY cbModification",
            new { depuis, taille })).AsList();
    }
}
