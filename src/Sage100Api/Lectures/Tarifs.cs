using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace Sage100Api.Lectures;

// ---------- Tarification Sage : tarif client, catégorie tarifaire, gammes, conditionnements, quantités ----------
// Tables : « Structure des bases Sage 100 » (F_ARTCLIENT, F_TARIFGAM, F_TARIFCOND, F_TARIFQTE, F_CONDITION, P_CATTARIF).
// Dans ces tables, une ligne vise soit un client (CT_Num / xx_RefCF = numéro du client), soit une catégorie tarifaire
// (AC_Categorie, ou xx_RefCF = « a01 » à « a32 »). Ici : Client renseigné, ou Categorie renseignée.

/// <summary>Catégorie tarifaire (P_CATTARIF) : Numero = N_CatTarif du client (1 à 32).</summary>
public sealed record CategorieTarif(int Numero, string Intitule, bool PrixTTC);

/// <summary>
/// Tarif d'un article pour un client ou une catégorie (F_ARTCLIENT). Prix 0 : prix de l'article.
/// QteMont : 0 tarif simple, 1 remises par quantité, 2 remises par montant, 3 prix net par quantité (voir <see cref="TarifQuantite"/>).
/// HorsRemise : la remise générale ne s'applique pas (AC_TypeRem = 1).
/// </summary>
public sealed record TarifArticle(string Article, int? Categorie, string? Client, decimal Prix, bool PrixTTC, decimal Remise, int QteMont, bool HorsRemise = false);

/// <summary>Prix d'une valeur de gamme pour un client ou une catégorie (F_TARIFGAM).</summary>
public sealed record TarifGamme(string Article, int? Categorie, string? Client, string Gamme1, string? Gamme2, decimal Prix);

/// <summary>
/// Conditionnement d'un article (F_CONDITION) : « Carton de 12 » contient Quantite = 12 unités de vente.
/// Numero = CO_No, repris par <see cref="TarifConditionnement"/>.
/// </summary>
public sealed record Conditionnement(string Article, int Numero, string Enumere, decimal Quantite, string? Reference, string? CodeBarre, bool Principal);

/// <summary>Prix d'un conditionnement entier (le carton) pour un client ou une catégorie (F_TARIFCOND).</summary>
public sealed record TarifConditionnement(string Article, int? Categorie, string? Client, int Conditionnement, decimal Prix);

/// <summary>Remise Sage : Type 0 = montant par unité, 1 = pourcentage, 2 = quantité offerte (non appliquée au prix).</summary>
public sealed record RemiseTarif(int Type, decimal Valeur);

/// <summary>Tranche de tarif par quantité ou par montant (F_TARIFQTE), jusqu'à BorneSup incluse.</summary>
public sealed record TarifQuantite(string Article, int? Categorie, string? Client, decimal BorneSup, IReadOnlyList<RemiseTarif> Remises, decimal PrixNet);

/// <summary>Toutes les données de tarification, pour la borne (hors ligne) et pour le calcul des prix envoyés à Sage.</summary>
public sealed record DonneesTarifs(IReadOnlyList<CategorieTarif> Categories, IReadOnlyList<TarifArticle> Articles, IReadOnlyList<TarifGamme> Gammes,
    IReadOnlyList<Conditionnement> Conditionnements, IReadOnlyList<TarifConditionnement> TarifsConditionnement, IReadOnlyList<TarifQuantite> Quantites)
{
    public static readonly DonneesTarifs Vide = new([], [], [], [], [], []);
}

/// <summary>Souche de numérotation des documents de vente (P_SOUCHEVENTE) : Numero = DO_Souche (0 pour la première).</summary>
public sealed record Souche(int Numero, string Intitule);

/// <summary>
/// Taux de TVA (taxe 1, en %) d'un article pour une catégorie comptable de vente : celui de l'article (F_ARTCOMPTA),
/// sinon celui de sa famille (F_FAMCOMPTA), lu dans F_TAXE.
/// </summary>
public sealed record TauxTva(string Article, int Categorie, decimal Taux);

/// <summary>Stock d'un article (ou d'une valeur de gamme) dans un dépôt (F_ARTSTOCK, F_GAMSTOCK).</summary>
public sealed record StockDepot(string Article, int Depot, string? Gamme1, string? Gamme2, decimal Stock, decimal StockReserve)
{
    public decimal StockDisponible => Stock - StockReserve;
}

public interface ILecturesTarifs
{
    /// <summary>Tarifs de tous les clients et catégories, ou seulement ceux d'un client (et de sa catégorie) pour quelques articles.</summary>
    Task<DonneesTarifs> Tarifs(string? client = null, int? categorie = null, IReadOnlyCollection<string>? articles = null);
    Task<IReadOnlyList<Souche>> Souches();
    /// <summary>Stock par dépôt, de tous les articles ou d'un seul ; les valeurs de gamme ont leur propre ligne.</summary>
    Task<IReadOnlyList<StockDepot>> StocksDepots(string? article = null);
    Task<IReadOnlyList<TauxTva>> TauxTva();
}

public sealed class LecturesTarifsSql(IOptions<SageOptions> options) : ILecturesTarifs
{
    SqlConnection Cnx() => new(options.Value.ChaineSql);

    public async Task<DonneesTarifs> Tarifs(string? client = null, int? categorie = null, IReadOnlyCollection<string>? articles = null)
    {
        using var c = Cnx();
        var refs = articles?.Select(a => a.Trim().ToUpperInvariant()).Distinct().ToArray();
        // Filtre : un client (ses lignes et celles de sa catégorie) et quelques articles, ou tout pour le catalogue de la borne.
        var cle = categorie is int k ? $"a{k:00}" : null;
        var p = new DynamicParameters(new { client, categorie, cle });
        string FiltreArticle(string col)
        {
            if (refs is null) return "";
            p.Add("refs", refs);
            return $" AND {col} IN @refs";
        }

        // Types exacts (CAST) : Dapper ne remplit un record positionnel que si chaque colonne a le type du paramètre ;
        // cbIndice est un smallint dans Sage, un int dans le record.
        var categories = (await c.QueryAsync<CategorieTarif>(
            "SELECT CAST(cbIndice AS int) AS Numero, CT_Intitule AS Intitule, CAST(CASE WHEN CT_PrixTTC = 1 THEN 1 ELSE 0 END AS bit) AS PrixTTC " +
            "FROM P_CATTARIF WHERE CT_Intitule <> '' ORDER BY cbIndice")).AsList();

        // AC_Categorie : numéro de catégorie (= N_CatTarif) pour une ligne de catégorie ; une ligne de client porte CT_Num.
        var lignesArticles = await c.QueryAsync<LigneArtClient>(
            "SELECT ac.AR_Ref AS Article, CAST(ac.AC_Categorie AS int) AS Categorie, NULLIF(ac.CT_Num, '') AS Client, " +
            "CAST(ac.AC_PrixVen AS decimal(18,6)) AS Prix, CAST(CASE WHEN ac.AC_PrixTTC = 1 THEN 1 ELSE 0 END AS bit) AS PrixTTC, " +
            "CAST(ac.AC_Remise AS decimal(18,6)) AS Remise, CAST(ac.AC_QteMont AS int) AS QteMont, " +
            "CAST(CASE WHEN ac.AC_TypeRem = 1 THEN 1 ELSE 0 END AS bit) AS HorsRemise " +
            "FROM F_ARTCLIENT ac JOIN F_ARTICLE a ON a.AR_Ref = ac.AR_Ref " +
            "WHERE a.AR_Sommeil = 0 AND (ac.AC_PrixVen <> 0 OR ac.AC_Remise <> 0 OR ac.AC_QteMont <> 0) " +
            (client is null && categorie is null ? "" : "AND (ac.CT_Num = @client OR ((ac.CT_Num IS NULL OR ac.CT_Num = '') AND ac.AC_Categorie = @categorie)) ") +
            FiltreArticle("ac.AR_Ref"), p);

        var lignesGammes = await c.QueryAsync<LigneTarifGamme>(
            "SELECT t.AR_Ref AS Article, t.TG_RefCF AS RefCF, g1.EG_Enumere AS Gamme1, g2.EG_Enumere AS Gamme2, CAST(t.TG_Prix AS decimal(18,6)) AS Prix " +
            "FROM F_TARIFGAM t JOIN F_ARTGAMME g1 ON g1.AG_No = t.AG_No1 LEFT JOIN F_ARTGAMME g2 ON g2.AG_No = t.AG_No2 AND t.AG_No2 <> 0 " +
            "WHERE t.TG_Prix <> 0 AND " + FiltreRefCF("t.TG_RefCF", client, categorie) + FiltreArticle("t.AR_Ref"), p);

        var conditionnements = (await c.QueryAsync<Conditionnement>(
            "SELECT co.AR_Ref AS Article, CAST(co.CO_No AS int) AS Numero, co.EC_Enumere AS Enumere, CAST(co.EC_Quantite AS decimal(18,6)) AS Quantite, " +
            "NULLIF(co.CO_Ref, '') AS Reference, NULLIF(co.CO_CodeBarre, '') AS CodeBarre, CAST(CASE WHEN co.CO_Principal = 1 THEN 1 ELSE 0 END AS bit) AS Principal " +
            "FROM F_CONDITION co JOIN F_ARTICLE a ON a.AR_Ref = co.AR_Ref " +
            "WHERE a.AR_Sommeil = 0 AND co.EC_Quantite > 0" + FiltreArticle("co.AR_Ref") + " ORDER BY co.AR_Ref, co.EC_Quantite", p)).AsList();

        var lignesCond = await c.QueryAsync<LigneTarifCond>(
            "SELECT t.AR_Ref AS Article, t.TC_RefCF AS RefCF, t.CO_No AS Conditionnement, CAST(t.TC_Prix AS decimal(18,6)) AS Prix " +
            "FROM F_TARIFCOND t WHERE t.TC_Prix <> 0 AND " + FiltreRefCF("t.TC_RefCF", client, categorie) + FiltreArticle("t.AR_Ref"), p);

        var lignesQte = await c.QueryAsync<LigneTarifQte>(
            "SELECT t.AR_Ref AS Article, t.TQ_RefCF AS RefCF, CAST(t.TQ_BorneSup AS decimal(18,6)) AS BorneSup, " +
            "CAST(t.TQ_Remise01REM_Type AS int) AS Type1, CAST(t.TQ_Remise01REM_Valeur AS decimal(18,6)) AS Valeur1, " +
            "CAST(t.TQ_Remise02REM_Type AS int) AS Type2, CAST(t.TQ_Remise02REM_Valeur AS decimal(18,6)) AS Valeur2, " +
            "CAST(t.TQ_Remise03REM_Type AS int) AS Type3, CAST(t.TQ_Remise03REM_Valeur AS decimal(18,6)) AS Valeur3, " +
            "CAST(t.TQ_PrixNet AS decimal(18,6)) AS PrixNet " +
            "FROM F_TARIFQTE t WHERE " + FiltreRefCF("t.TQ_RefCF", client, categorie) + FiltreArticle("t.AR_Ref") +
            " ORDER BY t.AR_Ref, t.TQ_RefCF, t.TQ_BorneSup", p);

        return new DonneesTarifs(
            categories,
            lignesArticles.Select(l => new TarifArticle(l.Article, l.Client is null ? l.Categorie : null, l.Client, l.Prix, l.PrixTTC, l.Remise, l.QteMont, l.HorsRemise)).ToList(),
            lignesGammes.Select(l => Cible(l.RefCF) is { } x ? new TarifGamme(l.Article, x.Categorie, x.Client, l.Gamme1, l.Gamme2, l.Prix) : null).OfType<TarifGamme>().ToList(),
            conditionnements,
            lignesCond.Select(l => Cible(l.RefCF) is { } x ? new TarifConditionnement(l.Article, x.Categorie, x.Client, l.Conditionnement, l.Prix) : null)
                .OfType<TarifConditionnement>().ToList(),
            lignesQte.Select(l => Cible(l.RefCF) is { } x ? new TarifQuantite(l.Article, x.Categorie, x.Client, l.BorneSup, l.Remises(), l.PrixNet) : null)
                .OfType<TarifQuantite>().ToList());
    }

    /// <summary>
    /// Lignes de tarif visant les clients (pas les fournisseurs, qui partagent ces tables) et les catégories tarifaires,
    /// ou seulement un client et sa catégorie.
    /// </summary>
    static string FiltreRefCF(string col, string? client, int? categorie) =>
        client is null && categorie is null
            ? $"({col} LIKE 'a[0-9][0-9]' OR {col} IN (SELECT CT_Num FROM F_COMPTET WHERE CT_Type = 0))"
            : $"({col} = @client OR {col} = @cle)";

    /// <summary>« a01 » à « a32 » (a minuscule) : catégorie tarifaire ; sinon numéro de client.</summary>
    internal static (int? Categorie, string? Client)? Cible(string? refCF)
    {
        if (string.IsNullOrWhiteSpace(refCF)) return null;
        var r = refCF.Trim();
        if (r.Length == 3 && r[0] == 'a' && int.TryParse(r.AsSpan(1), out var k)) return (k, null);
        return (null, r);
    }

    public async Task<IReadOnlyList<Souche>> Souches()
    {
        using var c = Cnx();
        // DO_Souche commence à 0 ; cbIndice de P_SOUCHEVENTE commence à 1.
        return (await c.QueryAsync<Souche>(
            "SELECT CAST(cbIndice - 1 AS int) AS Numero, S_Intitule AS Intitule FROM P_SOUCHEVENTE WHERE S_Intitule <> '' AND S_Valide = 1 ORDER BY cbIndice")).AsList();
    }

    public async Task<IReadOnlyList<TauxTva>> TauxTva()
    {
        using var c = Cnx();
        // Ventes (type 0) ; la ligne de l'article remplace celle de sa famille pour la même catégorie comptable.
        // Plusieurs lignes possibles (facture, retour, avoir) : le plus grand taux.
        return (await c.QueryAsync<TauxTva>(
            "SELECT x.Article, CAST(x.Champ AS int) AS Categorie, CAST(MAX(t.TA_Taux) AS decimal(18,4)) AS Taux FROM (" +
            "SELECT a.AR_Ref AS Article, ac.ACP_Champ AS Champ, ac.ACP_ComptaCpt_Taxe1 AS Taxe FROM F_ARTICLE a " +
            "JOIN F_ARTCOMPTA ac ON ac.AR_Ref = a.AR_Ref AND ac.ACP_Type = 0 WHERE a.AR_Sommeil = 0 " +
            "UNION ALL " +
            "SELECT a.AR_Ref, f.FCP_Champ, f.FCP_ComptaCPT_Taxe1 FROM F_ARTICLE a " +
            "JOIN F_FAMCOMPTA f ON f.FA_CodeFamille = a.FA_CodeFamille AND f.FCP_Type = 0 WHERE a.AR_Sommeil = 0 " +
            "AND NOT EXISTS (SELECT 1 FROM F_ARTCOMPTA ac WHERE ac.AR_Ref = a.AR_Ref AND ac.ACP_Type = 0 AND ac.ACP_Champ = f.FCP_Champ)" +
            ") x JOIN F_TAXE t ON t.TA_Code = x.Taxe AND t.TA_TTaux = 0 " +
            "GROUP BY x.Article, x.Champ")).AsList();
    }

    public async Task<IReadOnlyList<StockDepot>> StocksDepots(string? article = null)
    {
        using var c = Cnx();
        var simples = await c.QueryAsync<StockDepot>(
            "SELECT s.AR_Ref AS Article, CAST(s.DE_No AS int) AS Depot, CAST(NULL AS varchar(35)) AS Gamme1, CAST(NULL AS varchar(35)) AS Gamme2, " +
            "CAST(s.AS_QteSto AS decimal(18,6)) AS Stock, CAST(s.AS_QteRes AS decimal(18,6)) AS StockReserve " +
            "FROM F_ARTSTOCK s JOIN F_ARTICLE a ON a.AR_Ref = s.AR_Ref " +
            "WHERE a.AR_Sommeil = 0 AND (s.AS_QteSto <> 0 OR s.AS_QteRes <> 0) AND (@article IS NULL OR s.AR_Ref = @article)",
            new { article });
        var gammes = await c.QueryAsync<StockDepot>(
            "SELECT gs.AR_Ref AS Article, CAST(gs.DE_No AS int) AS Depot, g1.EG_Enumere AS Gamme1, g2.EG_Enumere AS Gamme2, " +
            "CAST(gs.GS_QteSto AS decimal(18,6)) AS Stock, CAST(gs.GS_QteRes AS decimal(18,6)) AS StockReserve " +
            "FROM F_GAMSTOCK gs JOIN F_ARTICLE a ON a.AR_Ref = gs.AR_Ref " +
            "JOIN F_ARTGAMME g1 ON g1.AG_No = gs.AG_No1 LEFT JOIN F_ARTGAMME g2 ON g2.AG_No = gs.AG_No2 AND gs.AG_No2 <> 0 " +
            "WHERE a.AR_Sommeil = 0 AND (gs.GS_QteSto <> 0 OR gs.GS_QteRes <> 0) AND (@article IS NULL OR gs.AR_Ref = @article)",
            new { article });
        return simples.Concat(gammes).ToList();
    }

    sealed class LigneArtClient
    {
        public string Article { get; set; } = "";
        public int Categorie { get; set; }
        public string? Client { get; set; }
        public decimal Prix { get; set; }
        public bool PrixTTC { get; set; }
        public decimal Remise { get; set; }
        public int QteMont { get; set; }
        public bool HorsRemise { get; set; }
    }

    sealed class LigneTarifGamme
    {
        public string Article { get; set; } = "";
        public string RefCF { get; set; } = "";
        public string Gamme1 { get; set; } = "";
        public string? Gamme2 { get; set; }
        public decimal Prix { get; set; }
    }

    sealed class LigneTarifCond
    {
        public string Article { get; set; } = "";
        public string RefCF { get; set; } = "";
        public int Conditionnement { get; set; }
        public decimal Prix { get; set; }
    }

    sealed class LigneTarifQte
    {
        public string Article { get; set; } = "";
        public string RefCF { get; set; } = "";
        public decimal BorneSup { get; set; }
        public int Type1 { get; set; }
        public decimal Valeur1 { get; set; }
        public int Type2 { get; set; }
        public decimal Valeur2 { get; set; }
        public int Type3 { get; set; }
        public decimal Valeur3 { get; set; }
        public decimal PrixNet { get; set; }

        public IReadOnlyList<RemiseTarif> Remises() =>
            new[] { new RemiseTarif(Type1, Valeur1), new RemiseTarif(Type2, Valeur2), new RemiseTarif(Type3, Valeur3) }.Where(r => r.Valeur != 0).ToList();
    }
}

/// <summary>
/// Prix d'une ligne calculé comme Sage. Prix : prix de l'unité vendue (l'article, ou le conditionnement entier), avant remise.
/// PrixUnitaire : prix par unité de vente de l'article (Prix / quantité du conditionnement), le prix unitaire de la ligne Sage.
/// PrixNet : prix de l'unité vendue après remises. Origine : client, categorie ou article, d'où vient le prix.
/// </summary>
public sealed record PrixCalcule(decimal Prix, decimal PrixUnitaire, bool PrixTTC, IReadOnlyList<RemiseTarif> Remises, decimal PrixNet, string Origine);

/// <summary>
/// Règles de prix de vente de Sage, appliquées par l'API avant d'envoyer la pièce au worker (les Objets Métiers
/// ne reprennent que le tarif propre au client, pas celui de sa catégorie tarifaire : manuel OM, SetDefaultArticle).
/// La borne applique les mêmes règles (borne/tarifs.js) pour afficher les prix hors ligne.
/// </summary>
public static class Tarification
{
    /// <summary>
    /// prixArticle / prixArticleTTC : AR_PrixVen et AR_PrixTTC de l'article. quantite : quantité vendue en unités vendues
    /// (nombre de conditionnements le cas échéant).
    /// </summary>
    public static PrixCalcule Calculer(DonneesTarifs t, string article, decimal prixArticle, bool prixArticleTTC, string client, int categorie,
        string? gamme1, string? gamme2, Conditionnement? conditionnement, decimal quantite)
    {
        bool Vise(int? cat, string? cli, bool pourClient) =>
            pourClient ? cli != null && Egal(cli, client) : cli == null && cat == categorie;
        bool MemeArticle(string a) => Egal(a, article);

        var ligneClient = t.Articles.FirstOrDefault(x => MemeArticle(x.Article) && Vise(x.Categorie, x.Client, true));
        var ligneCategorie = t.Articles.FirstOrDefault(x => MemeArticle(x.Article) && Vise(x.Categorie, x.Client, false));

        // Prix de base : tarif du client, sinon de sa catégorie, sinon prix de l'article.
        decimal prix;
        bool ttc;
        string origine;
        if (ligneClient is { Prix: > 0 }) (prix, ttc, origine) = (ligneClient.Prix, ligneClient.PrixTTC, "client");
        else if (ligneCategorie is { Prix: > 0 }) (prix, ttc, origine) = (ligneCategorie.Prix, ligneCategorie.PrixTTC, "categorie");
        else (prix, ttc, origine) = (prixArticle, prixArticleTTC, "article");
        // Remises : celles de la ligne client si elle existe, sinon celles de la catégorie.
        var regle = ligneClient ?? ligneCategorie;
        var pourClient = ligneClient != null;

        // Valeur de gamme ou conditionnement : leur propre prix pour le client, sinon pour la catégorie.
        if (!string.IsNullOrEmpty(gamme1))
        {
            bool MemeValeur(TarifGamme g) => MemeArticle(g.Article) && Egal(g.Gamme1, gamme1!) && Egal(g.Gamme2 ?? "", gamme2 ?? "");
            var g = t.Gammes.FirstOrDefault(x => MemeValeur(x) && Vise(x.Categorie, x.Client, true))
                    ?? t.Gammes.FirstOrDefault(x => MemeValeur(x) && Vise(x.Categorie, x.Client, false));
            if (g != null) (prix, origine) = (g.Prix, g.Client != null ? "client" : "categorie");
        }

        var contenu = conditionnement?.Quantite > 0 ? conditionnement.Quantite : 1m;
        if (conditionnement != null)
        {
            bool MemeCond(TarifConditionnement c) => MemeArticle(c.Article) && c.Conditionnement == conditionnement.Numero;
            var c = t.TarifsConditionnement.FirstOrDefault(x => MemeCond(x) && Vise(x.Categorie, x.Client, true))
                    ?? t.TarifsConditionnement.FirstOrDefault(x => MemeCond(x) && Vise(x.Categorie, x.Client, false));
            if (c != null) (prix, origine) = (c.Prix, c.Client != null ? "client" : "categorie");
            else prix *= contenu; // sans tarif propre : le carton de 12 vaut 12 fois l'unité
        }

        IReadOnlyList<RemiseTarif> remises = [];
        if (regle != null)
        {
            if (regle.QteMont is >= 1 and <= 3)
            {
                // Tranches : quantité en unités de vente (1 et 3) ou montant HT de la ligne (2), jusqu'à la borne supérieure.
                var tranches = t.Quantites.Where(x => MemeArticle(x.Article) && Vise(x.Categorie, x.Client, pourClient)).OrderBy(x => x.BorneSup).ToList();
                var valeur = regle.QteMont == 2 ? prix * quantite : quantite * contenu;
                var tranche = tranches.FirstOrDefault(x => valeur <= x.BorneSup) ?? tranches.LastOrDefault();
                if (tranche != null)
                {
                    if (regle.QteMont == 3) { if (tranche.PrixNet > 0) prix = tranche.PrixNet * contenu; }
                    else remises = tranche.Remises;
                }
            }
            else if (!regle.HorsRemise && regle.Remise != 0)
                remises = [new RemiseTarif(1, regle.Remise)];
        }

        return new PrixCalcule(Arrondi(prix), Arrondi(prix / contenu), ttc, remises, Arrondi(Net(prix, remises, contenu)), origine);
    }

    /// <summary>Remises en cascade, comme Sage : un pourcentage s'applique sur le prix déjà remisé ; un montant est par unité de vente.</summary>
    public static decimal Net(decimal prix, IReadOnlyList<RemiseTarif> remises, decimal contenu = 1)
    {
        var net = prix;
        foreach (var r in remises)
        {
            if (r.Type == 1) net *= 1 - r.Valeur / 100m;
            else if (r.Type == 0) net -= r.Valeur * contenu;
        }
        return Math.Max(0, net);
    }

    static decimal Arrondi(decimal v) => Math.Round(v, 4, MidpointRounding.AwayFromZero);

    static bool Egal(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
