using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace Sage100Api.Lectures;

public sealed record Client(string Numero, string Intitule, string? Ville, string? Telephone, string? Email);

/// <summary>
/// Gamme1 / Gamme2 : intitulés des gammes (par exemple « Taille »), null si l'article n'est pas à gamme.
/// SuiviStock : faux pour un article sans suivi de stock (AR_SuiviStock = 0), jamais bloqué par le contrôle de stock.
/// </summary>
public sealed record Article(string Reference, string Designation, string? Famille, string? CodeBarre, decimal PrixVenteHT, decimal Stock, decimal StockReserve,
    string? Gamme1 = null, string? Gamme2 = null, bool SuiviStock = true)
{
    /// <summary>Stock réel moins les quantités réservées par les commandes clients (les commandes fournisseurs ne comptent pas).</summary>
    public decimal StockDisponible => Stock - StockReserve;
}

public sealed record ModeReglement(string Intitule, string? Code);

/// <summary>Valeur (ou couple de valeurs) de gamme vendable pour un article : à renvoyer dans gamme1 / gamme2 de la ligne de commande.</summary>
public sealed record EnumereGamme(string Article, string Gamme1, string? Gamme2, string? CodeBarre);

/// <summary>ControleStock : vrai si une commande dépassant le stock disponible est refusée (voir Sage:ControleStock).</summary>
public sealed record Catalogue(DateTime GenereLe, IReadOnlyList<Client> Clients, IReadOnlyList<Article> Articles, IReadOnlyList<ModeReglement> ModesReglement,
    IReadOnlyList<EnumereGamme> Gammes, bool ControleStock);

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
    Task<string?> PieceCommande(string idExterne);
}

public sealed class LecturesSql(IOptions<SageOptions> options) : ILecturesSage
{
    // CT_Type = 0 : client ; CT_Sommeil / AR_Sommeil = 1 : mis en sommeil.
    const string SelectClient =
        "SELECT CT_Num AS Numero, CT_Intitule AS Intitule, CT_Ville AS Ville, CT_Telephone AS Telephone, CT_EMail AS Email FROM F_COMPTET";

    const string SelectArticle =
        "SELECT a.AR_Ref AS Reference, a.AR_Design AS Designation, a.FA_CodeFamille AS Famille, a.AR_CodeBarre AS CodeBarre, " +
        "CAST(a.AR_PrixVen AS decimal(18,6)) AS PrixVenteHT, " +
        "CAST(ISNULL(SUM(s.AS_QteSto), 0) AS decimal(18,6)) AS Stock, CAST(ISNULL(SUM(s.AS_QteRes), 0) AS decimal(18,6)) AS StockReserve, " +
        // AR_Gamme1 / AR_Gamme2 : indice dans P_GAMME (0 = pas de gamme).
        "(SELECT TOP 1 G_Intitule FROM P_GAMME WHERE cbIndice = a.AR_Gamme1 AND a.AR_Gamme1 > 0) AS Gamme1, " +
        "(SELECT TOP 1 G_Intitule FROM P_GAMME WHERE cbIndice = a.AR_Gamme2 AND a.AR_Gamme2 > 0) AS Gamme2, " +
        "CAST(CASE WHEN a.AR_SuiviStock <> 0 THEN 1 ELSE 0 END AS bit) AS SuiviStock " +
        "FROM F_ARTICLE a LEFT JOIN F_ARTSTOCK s ON s.AR_Ref = a.AR_Ref";

    const string GroupArticle = " GROUP BY a.AR_Ref, a.AR_Design, a.FA_CodeFamille, a.AR_CodeBarre, a.AR_PrixVen, a.AR_Gamme1, a.AR_Gamme2, a.AR_SuiviStock";

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
            "SELECT e.AR_Ref AS Article, g1.EG_Enumere AS Gamme1, g2.EG_Enumere AS Gamme2, NULLIF(e.AE_CodeBarre, '') AS CodeBarre " +
            "FROM F_ARTENUMREF e JOIN F_ARTICLE a ON a.AR_Ref = e.AR_Ref " +
            "JOIN F_ARTGAMME g1 ON g1.AG_No = e.AG_No1 " +
            "LEFT JOIN F_ARTGAMME g2 ON g2.AG_No = e.AG_No2 AND e.AG_No2 <> 0 " +
            "WHERE a.AR_Sommeil = 0 AND e.AE_Sommeil = 0 AND (@article IS NULL OR e.AR_Ref = @article) " +
            "ORDER BY e.AR_Ref, g1.AG_No, g2.AG_No",
            new { article });
        return r.AsList();
    }

    public async Task<string?> PieceCommande(string idExterne)
    {
        using var c = Cnx();
        return await c.QueryFirstOrDefaultAsync<string>(
            "SELECT TOP 1 DO_Piece FROM F_DOCENTETE WHERE DO_Domaine = 0 AND DO_Type = 1 AND DO_RefExterne = @idExterne", new { idExterne });
    }

    static string? Motif(string? recherche) => string.IsNullOrWhiteSpace(recherche) ? null : "%" + recherche.Trim() + "%";
}
