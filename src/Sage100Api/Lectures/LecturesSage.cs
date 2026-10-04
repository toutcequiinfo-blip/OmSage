using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace Sage100Api.Lectures;

/// <summary>CategorieTarif : catégorie tarifaire du client (N_CatTarif, 1 à 32), qui fixe ses prix (voir <see cref="Tarification"/>).</summary>
public sealed record Client(string Numero, string Intitule, string? Ville, string? Telephone, string? Email, int CategorieTarif = 1);

/// <summary>
/// Gamme1 / Gamme2 : intitulés des gammes (par exemple « Taille »), null si l'article n'est pas à gamme.
/// SuiviStock : faux pour un article sans suivi de stock (AR_SuiviStock = 0), jamais bloqué par le contrôle de stock.
/// PrixVenteHT : prix de vente de la fiche (AR_PrixVen), TTC si PrixTTC ; le prix d'un client suit sa catégorie tarifaire.
/// Unite : unité de vente (P_UNITE, par exemple « Pièce ») ; Conditionnement : type de conditionnement (P_CONDITIONNEMENT,
/// par exemple « Carton »), les quantités de chaque conditionnement sont dans <see cref="Lectures.Conditionnement"/>.
/// </summary>
public sealed record Article(string Reference, string Designation, string? Famille, string? CodeBarre, decimal PrixVenteHT, decimal Stock, decimal StockReserve,
    string? Gamme1 = null, string? Gamme2 = null, bool SuiviStock = true, string? Unite = null, bool PrixTTC = false, string? Conditionnement = null)
{
    /// <summary>Stock réel moins les quantités réservées par les commandes clients (les commandes fournisseurs ne comptent pas).</summary>
    public decimal StockDisponible => Stock - StockReserve;
}

public sealed record ModeReglement(string Intitule, string? Code);

/// <summary>
/// Valeur (ou couple de valeurs) de gamme vendable pour un article : à renvoyer dans gamme1 / gamme2 de la ligne de commande.
/// Stock et StockReserve : stock de cette valeur, tous dépôts confondus (F_GAMSTOCK).
/// </summary>
public sealed record EnumereGamme(string Article, string Gamme1, string? Gamme2, string? CodeBarre, decimal Stock = 0, decimal StockReserve = 0)
{
    public decimal StockDisponible => Stock - StockReserve;
}

/// <summary>
/// ControleStock : vrai si une commande dépassant le stock disponible est refusée (voir Sage:ControleStock).
/// Authentification : la borne doit connecter un utilisateur Sage ; ExigerCaissier : seuls les caissiers encaissent.
/// Tarifs : de quoi calculer hors ligne le prix de chaque client (voir <see cref="Tarification"/>).
/// Souches, Depots, StocksDepots : choix de la souche et du dépôt dans les paramètres de saisie de la borne.
/// </summary>
public sealed record Catalogue(DateTime GenereLe, IReadOnlyList<Client> Clients, IReadOnlyList<Article> Articles, IReadOnlyList<ModeReglement> ModesReglement,
    IReadOnlyList<EnumereGamme> Gammes, bool ControleStock, bool Authentification = false, bool ExigerCaissier = false,
    IReadOnlyList<CommandeOuverte>? CommandesOuvertes = null, DonneesTarifs? Tarifs = null, IReadOnlyList<Souche>? Souches = null,
    IReadOnlyList<Depot>? Depots = null, IReadOnlyList<StockDepot>? StocksDepots = null);

/// <summary>
/// Bon de commande client pas encore livré ni clôturé, avec ce qui reste à encaisser.
/// DejaRegle : total des acomptes déjà saisis sur la pièce (F_DOCREGL, DR_TypeRegl = 0).
/// NetAPayer : « Net à payer » du pied de pièce Sage (DO_NetAPayer).
/// </summary>
public sealed record CommandeOuverte(string Piece, DateTime Date, string Client, string? Intitule, string? Reference, string? IdExterne,
    decimal TotalTTC, decimal DejaRegle, decimal? NetAPayer = null)
{
    public decimal Reste => TotalTTC - DejaRegle;
}

/// <summary>Ligne d'un bon de commande (F_DOCLIGNE), avec les valeurs de gamme de l'article s'il en a.</summary>
public sealed record LignePiece(string? Article, string? Designation, string? Gamme1, string? Gamme2, decimal Quantite, decimal PrixUnitaireHT,
    decimal MontantHT, decimal MontantTTC);

/// <summary>Bon de commande et ses lignes, pour le consulter depuis la borne (loupe).</summary>
public sealed record DetailPiece(CommandeOuverte Entete, decimal TotalHT, IReadOnlyList<LignePiece> Lignes);

/// <summary>Collaborateur Sage (F_COLLABORATEUR) rattaché à un utilisateur Sage, avec ses cases Vendeur et Caissier.</summary>
public sealed record Collaborateur(int Numero, string Nom, string? Prenom, bool Vendeur, bool Caissier);

/// <summary>Lectures directes en SQL. Codes et champs : « Structure des bases Sage 100 ».</summary>
public interface ILecturesSage
{
    Task<IReadOnlyList<Client>> Clients(string? recherche, int page, int taille);
    Task<Client?> Client(string numero);
    Task<IReadOnlyList<Article>> Articles(string? recherche, string? famille, int page, int taille);
    Task<Article?> Article(string reference);
    Task<IReadOnlyList<ModeReglement>> ModesReglement();
    /// <summary>Option « Autoriser la gestion des stocks négatifs » de Sage (P_PREFERENCES.PR_StockNeg).</summary>
    Task<bool> StockNegatifAutorise();
    Task<IReadOnlyList<EnumereGamme>> Gammes(string? article = null);
    /// <summary>Pièce Sage (bon de commande, de livraison ou facture) créée avec cet identifiant externe, ou null.</summary>
    Task<string?> PieceCommande(string idExterne);
    /// <summary>Collaborateur dont le champ « Utilisateur » de la fiche désigne ce login Sage, ou null.</summary>
    Task<Collaborateur?> CollaborateurUtilisateur(string utilisateur);
    /// <summary>Bons de commande avec un reste à encaisser, les plus récents d'abord.</summary>
    Task<IReadOnlyList<CommandeOuverte>> CommandesOuvertes(string? recherche, int taille);
    /// <summary>Bon de commande client et ses lignes, ou null s'il n'existe pas (ou plus : transformé en livraison).</summary>
    Task<DetailPiece?> DetailCommande(string piece);
}

public sealed class LecturesSql(IOptions<SageOptions> options) : ILecturesSage
{
    // CT_Type = 0 : client ; CT_Sommeil / AR_Sommeil = 1 : mis en sommeil.
    const string SelectClient =
        "SELECT CT_Num AS Numero, CT_Intitule AS Intitule, CT_Ville AS Ville, CT_Telephone AS Telephone, CT_EMail AS Email, " +
    "CAST(CASE WHEN N_CatTarif > 0 THEN N_CatTarif ELSE 1 END AS int) AS CategorieTarif FROM F_COMPTET";

    const string SelectArticle =
        "SELECT a.AR_Ref AS Reference, a.AR_Design AS Designation, a.FA_CodeFamille AS Famille, a.AR_CodeBarre AS CodeBarre, " +
        "CAST(a.AR_PrixVen AS decimal(18,6)) AS PrixVenteHT, " +
        "CAST(ISNULL(SUM(s.AS_QteSto), 0) AS decimal(18,6)) AS Stock, CAST(ISNULL(SUM(s.AS_QteRes), 0) AS decimal(18,6)) AS StockReserve, " +
        // AR_Gamme1 / AR_Gamme2 : indice dans P_GAMME (0 = pas de gamme).
        "(SELECT TOP 1 G_Intitule FROM P_GAMME WHERE cbIndice = a.AR_Gamme1 AND a.AR_Gamme1 > 0) AS Gamme1, " +
        "(SELECT TOP 1 G_Intitule FROM P_GAMME WHERE cbIndice = a.AR_Gamme2 AND a.AR_Gamme2 > 0) AS Gamme2, " +
        "CAST(CASE WHEN a.AR_SuiviStock <> 0 THEN 1 ELSE 0 END AS bit) AS SuiviStock, " +
        // AR_UniteVen : indice dans P_UNITE ; AR_Condition : indice dans P_CONDITIONNEMENT (0 = article non conditionné).
        "(SELECT TOP 1 NULLIF(U_Intitule, '') FROM P_UNITE WHERE cbIndice = a.AR_UniteVen) AS Unite, " +
        "CAST(CASE WHEN a.AR_PrixTTC = 1 THEN 1 ELSE 0 END AS bit) AS PrixTTC, " +
        "(SELECT TOP 1 NULLIF(P_Conditionnement, '') FROM P_CONDITIONNEMENT WHERE cbIndice = a.AR_Condition AND a.AR_Condition > 0) AS Conditionnement " +
        "FROM F_ARTICLE a LEFT JOIN F_ARTSTOCK s ON s.AR_Ref = a.AR_Ref";

    const string GroupArticle = " GROUP BY a.AR_Ref, a.AR_Design, a.FA_CodeFamille, a.AR_CodeBarre, a.AR_PrixVen, a.AR_Gamme1, a.AR_Gamme2, a.AR_SuiviStock, " +
        "a.AR_UniteVen, a.AR_PrixTTC, a.AR_Condition";

    SqlConnection Cnx() => new(options.Value.ChaineSql);

    public async Task<IReadOnlyList<Client>> Clients(string? recherche, int page, int taille)
    {
        using var c = Cnx();
        var r = await c.QueryAsync<Client>(
            SelectClient + " WHERE CT_Type = 0 AND CT_Sommeil = 0 " +
            "AND (@q IS NULL OR CT_Num LIKE @q OR CT_Intitule LIKE @q) " +
            "ORDER BY CT_Num OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY",
            new { q = Motif(recherche), skip = (page - 1) * taille, take = taille });
        return r.AsList();
    }

    public async Task<Client?> Client(string numero)
    {
        using var c = Cnx();
        return await c.QuerySingleOrDefaultAsync<Client>(SelectClient + " WHERE CT_Type = 0 AND CT_Num = @numero", new { numero });
    }

    public async Task<IReadOnlyList<Article>> Articles(string? recherche, string? famille, int page, int taille)
    {
        using var c = Cnx();
        var r = await c.QueryAsync<Article>(
            SelectArticle + " WHERE a.AR_Sommeil = 0 " +
            "AND (@q IS NULL OR a.AR_Ref LIKE @q OR a.AR_Design LIKE @q OR a.AR_CodeBarre = @exact) " +
            "AND (@famille IS NULL OR a.FA_CodeFamille = @famille)" + GroupArticle +
            " ORDER BY a.AR_Ref OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY",
            new { q = Motif(recherche), exact = recherche, famille, skip = (page - 1) * taille, take = taille });
        return r.AsList();
    }

    public async Task<Article?> Article(string reference)
    {
        using var c = Cnx();
        return await c.QuerySingleOrDefaultAsync<Article>(SelectArticle + " WHERE a.AR_Ref = @reference" + GroupArticle, new { reference });
    }

    public async Task<IReadOnlyList<ModeReglement>> ModesReglement()
    {
        using var c = Cnx();
        var r = await c.QueryAsync<ModeReglement>(
            "SELECT R_Intitule AS Intitule, NULLIF(R_Code, '') AS Code FROM P_REGLEMENT WHERE R_Intitule <> ''");
        return r.AsList();
    }

    public async Task<bool> StockNegatifAutorise()
    {
        using var c = Cnx();
        return await c.QueryFirstOrDefaultAsync<int?>("SELECT TOP 1 PR_StockNeg FROM P_PREFERENCES") == 1;
    }

    public async Task<IReadOnlyList<EnumereGamme>> Gammes(string? article = null)
    {
        // F_ARTENUMREF : une ligne par valeur (ou couple de valeurs) ; AG_No1 / AG_No2 renvoient à F_ARTGAMME.
        using var c = Cnx();
        var r = await c.QueryAsync<EnumereGamme>(
            "SELECT e.AR_Ref AS Article, g1.EG_Enumere AS Gamme1, g2.EG_Enumere AS Gamme2, NULLIF(e.AE_CodeBarre, '') AS CodeBarre, " +
            "CAST(ISNULL(s.Sto, 0) AS decimal(18,6)) AS Stock, CAST(ISNULL(s.Res, 0) AS decimal(18,6)) AS StockReserve " +
            "FROM F_ARTENUMREF e JOIN F_ARTICLE a ON a.AR_Ref = e.AR_Ref " +
            "JOIN F_ARTGAMME g1 ON g1.AG_No = e.AG_No1 " +
            "LEFT JOIN F_ARTGAMME g2 ON g2.AG_No = e.AG_No2 AND e.AG_No2 <> 0 " +
            // F_GAMSTOCK : une ligne par dépôt et par valeur de gamme (AG_No2 = 0 pour une gamme simple).
            "OUTER APPLY (SELECT SUM(gs.GS_QteSto) AS Sto, SUM(gs.GS_QteRes) AS Res FROM F_GAMSTOCK gs " +
            "  WHERE gs.AR_Ref = e.AR_Ref AND gs.AG_No1 = e.AG_No1 AND gs.AG_No2 = e.AG_No2) s " +
            "WHERE a.AR_Sommeil = 0 AND e.AE_Sommeil = 0 AND (@article IS NULL OR e.AR_Ref = @article) " +
            "ORDER BY e.AR_Ref, g1.AG_No, g2.AG_No",
            new { article });
        return r.AsList();
    }

    public async Task<string?> PieceCommande(string idExterne)
    {
        // Bon de commande, bon de livraison ou facture (comptabilisée ou non) créé par la borne.
        using var c = Cnx();
        return await c.QueryFirstOrDefaultAsync<string>(
            "SELECT TOP 1 DO_Piece FROM F_DOCENTETE WHERE DO_Domaine = 0 AND DO_Type IN (1, 3, 6, 7) AND DO_RefExterne = @idExterne ORDER BY DO_Type",
            new { idExterne });
    }

    public async Task<Collaborateur?> CollaborateurUtilisateur(string utilisateur)
    {
        // F_COLLABORATEUR.PROT_No : utilisateur Sage choisi sur la fiche collaborateur. Les utilisateurs sont dans les
        // tables système F_PROTECTIONCIAL (Gestion commerciale) et F_PROTECTIONCPTA (Comptabilité).
        using var c = Cnx();
        return await c.QueryFirstOrDefaultAsync<Collaborateur>(
            "SELECT TOP 1 co.CO_No AS Numero, co.CO_Nom AS Nom, NULLIF(co.CO_Prenom, '') AS Prenom, " +
            "CAST(co.CO_Vendeur AS bit) AS Vendeur, CAST(co.CO_Caissier AS bit) AS Caissier " +
            "FROM F_COLLABORATEUR co WHERE co.PROT_No > 0 AND co.PROT_No IN (" +
            "SELECT PROT_No FROM F_PROTECTIONCIAL WHERE PROT_User = @utilisateur " +
            "UNION SELECT PROT_No FROM F_PROTECTIONCPTA WHERE PROT_User = @utilisateur) " +
            "ORDER BY co.CO_Caissier DESC, co.CO_No",
            new { utilisateur });
    }

    public async Task<IReadOnlyList<CommandeOuverte>> CommandesOuvertes(string? recherche, int taille)
    {
        // Un bon de commande transformé en livraison change de DO_Type : il sort de cette liste.
        using var c = Cnx();
        var r = await c.QueryAsync<CommandeOuverte>(
            "SELECT TOP (@taille) e.DO_Piece AS Piece, e.DO_Date AS Date, e.DO_Tiers AS Client, t.CT_Intitule AS Intitule, " +
            "NULLIF(e.DO_Ref, '') AS Reference, NULLIF(e.DO_RefExterne, '') AS IdExterne, " +
            "CAST(e.DO_TotalTTC AS decimal(18,2)) AS TotalTTC, CAST(ISNULL(a.Regle, 0) AS decimal(18,2)) AS DejaRegle, " +
            "CAST(e.DO_NetAPayer AS decimal(18,2)) AS NetAPayer " +
            "FROM F_DOCENTETE e LEFT JOIN F_COMPTET t ON t.CT_Num = e.DO_Tiers " +
            "OUTER APPLY (SELECT SUM(r.DR_Montant) AS Regle FROM F_DOCREGL r " +
            "  WHERE r.DO_Domaine = 0 AND r.DO_Type = 1 AND r.DO_Piece = e.DO_Piece AND r.DR_TypeRegl = 0) a " +
            "WHERE e.DO_Domaine = 0 AND e.DO_Type = 1 AND e.DO_Cloture = 0 AND e.DO_TotalTTC - ISNULL(a.Regle, 0) > 0.005 " +
            "AND (@q IS NULL OR e.DO_Piece LIKE @q OR e.DO_Tiers LIKE @q OR t.CT_Intitule LIKE @q OR e.DO_Ref LIKE @q) " +
            "ORDER BY e.DO_Date DESC, e.DO_Piece DESC",
            new { q = Motif(recherche), taille });
        return r.AsList();
    }

    public async Task<DetailPiece?> DetailCommande(string piece)
    {
        using var c = Cnx();
        var entete = await c.QueryFirstOrDefaultAsync<(string Piece, DateTime Date, string Client, string? Intitule, string? Reference, string? IdExterne,
            decimal TotalTTC, decimal DejaRegle, decimal NetAPayer, decimal TotalHT)>(
            "SELECT e.DO_Piece, e.DO_Date, e.DO_Tiers, t.CT_Intitule, NULLIF(e.DO_Ref, ''), NULLIF(e.DO_RefExterne, ''), " +
            "CAST(e.DO_TotalTTC AS decimal(18,2)), " +
            "CAST(ISNULL((SELECT SUM(r.DR_Montant) FROM F_DOCREGL r WHERE r.DO_Domaine = 0 AND r.DO_Type = 1 AND r.DO_Piece = e.DO_Piece AND r.DR_TypeRegl = 0), 0) AS decimal(18,2)), " +
            "CAST(e.DO_NetAPayer AS decimal(18,2)), CAST(e.DO_TotalHT AS decimal(18,2)) " +
            "FROM F_DOCENTETE e LEFT JOIN F_COMPTET t ON t.CT_Num = e.DO_Tiers " +
            "WHERE e.DO_Domaine = 0 AND e.DO_Type = 1 AND e.DO_Piece = @piece",
            new { piece });
        if (entete.Piece is null) return null;
        // Lignes sans article (commentaires, sous-totaux) incluses : AR_Ref vide, seule la désignation compte.
        var lignes = await c.QueryAsync<LignePiece>(
            "SELECT NULLIF(l.AR_Ref, '') AS Article, NULLIF(l.DL_Design, '') AS Designation, g1.EG_Enumere AS Gamme1, g2.EG_Enumere AS Gamme2, " +
            "CAST(l.DL_Qte AS decimal(18,6)) AS Quantite, CAST(l.DL_PrixUnitaire AS decimal(18,6)) AS PrixUnitaireHT, " +
            "CAST(l.DL_MontantHT AS decimal(18,2)) AS MontantHT, CAST(l.DL_MontantTTC AS decimal(18,2)) AS MontantTTC " +
            "FROM F_DOCLIGNE l " +
            "LEFT JOIN F_ARTGAMME g1 ON g1.AG_No = l.AG_No1 AND l.AG_No1 <> 0 " +
            "LEFT JOIN F_ARTGAMME g2 ON g2.AG_No = l.AG_No2 AND l.AG_No2 <> 0 " +
            "WHERE l.DO_Domaine = 0 AND l.DO_Type = 1 AND l.DO_Piece = @piece ORDER BY l.DL_Ligne",
            new { piece });
        var e = entete;
        return new DetailPiece(new CommandeOuverte(e.Piece, e.Date, e.Client, e.Intitule, e.Reference, e.IdExterne, e.TotalTTC, e.DejaRegle, e.NetAPayer),
            e.TotalHT, lignes.AsList());
    }

    static string? Motif(string? recherche) => string.IsNullOrWhiteSpace(recherche) ? null : "%" + recherche.Trim() + "%";
}
