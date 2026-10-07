namespace Sage100Api.TableauDeBord;

/// <summary>
/// Taux de transformation commande → livraison : part des commandes clients (en montant HT et en quantité) déjà livrée.
/// Une commande est rattachée à sa date de commande : la période et les filtres portent sur les commandes passées.
/// </summary>
public static class Transformation
{
    // Sommes : commandé, livré, préparé, en attente, non servi (bon soldé), quantité commandée, quantité livrée.
    public static readonly string[] Mesures = ["commande", "livre", "encours", "nonservi", "transfo", "qteCommandee", "qteLivree", "transfoQte"];

    public static IReadOnlyDictionary<string, Axe<FaitCommande>> Axes(Instantane i) =>
        Commercial.AxesVentes<FaitCommande>(i, f => f.DateCommande, f => f.Article, f => f.Tiers, f => f.Commercial, f => f.Depot);

    static decimal[] Sommes(FaitCommande f) =>
    [
        f.MontantHT, f.Livree ? f.MontantHT : 0, f.Preparee ? f.MontantHT : 0, f.EnAttente ? f.MontantHT : 0, f.NonServie ? f.MontantHT : 0,
        f.Quantite, f.Livree ? f.Quantite : 0,
    ];

    public static decimal? Taux(decimal livre, decimal commande) => commande == 0 ? null : Math.Round(livre / commande * 100, 1);

    static decimal Mesure(decimal[] s, string m) => m switch
    {
        "livre" => s[1],
        "encours" => s[2] + s[3],
        "nonservi" => s[4],
        "transfo" => Taux(s[1], s[0]) ?? 0,
        "qteCommandee" => s[5],
        "qteLivree" => s[6],
        "transfoQte" => Taux(s[6], s[5]) ?? 0,
        _ => s[0],
    };

    public static IEnumerable<FaitCommande> Filtrer(Instantane i, RequeteVentes r, Perimetre p, DateTime aujourdhui)
    {
        var (du, au) = Comptabilite.Bornes(i, r.Du, r.Au, aujourdhui);
        var s = new SelectionsVentes(r);
        return i.Commandes.Where(f => f.DateCommande >= du && f.DateCommande <= au
            && p.Autorise(i, f.Tiers, f.Commercial)
            && (!s.FiltreArticles || s.RetientArticle(i, f.Article))
            && (!s.FiltreTiers || s.RetientTiers(i, f.Tiers))
            && (s.Commercial?.Retient(f.Commercial ?? i.Representant(f.Tiers)) ?? true)
            && (s.Depot?.Retient(f.Depot) ?? true));
    }

    static Axe<FaitCommande>? Axe(IReadOnlyDictionary<string, Axe<FaitCommande>> axes, string? code, string? defaut) =>
        code is { Length: > 0 } && axes.TryGetValue(code, out var a) ? a : defaut != null && code == null ? axes[defaut] : null;

    public static ResultatCube Croiser(Instantane i, RequeteVentes r, Perimetre p, DateTime aujourdhui)
    {
        var axes = Axes(i);
        return Cube.Pivoter(Filtrer(i, r, p, aujourdhui), Axe(axes, r.Lignes, "famille"), Axe(axes, r.Colonnes, null), 7,
            Sommes, Mesures, Mesure, r.Tri ?? "commande", Math.Clamp(r.Limite ?? 300, 10, 2000));
    }

    public sealed record LigneCommande(string Piece, DateTime Date, string Tiers, string? Intitule, string? Commercial, decimal Commande, decimal Livre,
        decimal EnCours, decimal NonServi, decimal? Taux, decimal? TauxQuantite, DateTime? DerniereLivraison, int? Delai, string Statut);

    /// <summary>
    /// Commandes d'une cellule du tableau (500 au plus, les plus récentes d'abord) : ce qui a été commandé, livré, reste à livrer ou ne sera pas servi.
    /// Lu dans l'instantané : seules les lignes de la cellule (article, famille... de la ligne) comptent dans les montants d'une commande.
    /// </summary>
    public static IReadOnlyList<LigneCommande>? Detail(Instantane i, RequeteVentes r, Perimetre p, string? cleLigne, string? cleColonne, DateTime aujourdhui)
    {
        var axes = Axes(i);
        var faits = Filtrer(i, r, p, aujourdhui);
        foreach (var (axe, cle) in new[] { (Axe(axes, r.Lignes, "famille"), cleLigne), (Axe(axes, r.Colonnes, null), cleColonne) })
        {
            if (axe == null || cle == null) continue;
            if (cle == Cube.CleAutres) return null;
            faits = faits.Where(f => (axe.Cle(f) ?? Cube.CleAucun) == cle);
        }
        return faits.GroupBy(f => f.Commande).Select(g =>
        {
            var s = new decimal[7];
            foreach (var f in g)
            {
                var v = Sommes(f);
                for (var k = 0; k < 7; k++) s[k] += v[k];
            }
            var premier = g.First();
            var livraison = g.Where(f => f.Livree).Max(f => f.DateLivraison);
            var date = g.Min(f => f.DateCommande);
            var enCours = s[2] + s[3];
            var statut = s[1] >= s[0] && s[0] != 0 ? "Livrée"
                : enCours != 0 ? (s[1] != 0 ? "Partielle" : s[2] != 0 ? "En préparation" : "En attente")
                : s[1] != 0 ? "Partielle, soldée" : "Non servie";
            var co = premier.Commercial ?? i.Representant(premier.Tiers);
            return new LigneCommande(g.Key, date, premier.Tiers, i.IntituleTiers(premier.Tiers), co is { } c ? i.Collaborateurs.GetValueOrDefault(c) : null,
                s[0], s[1], enCours, s[4], Taux(s[1], s[0]), Taux(s[6], s[5]), livraison,
                livraison is { } l ? (l.Date - date.Date).Days : null, statut);
        }).OrderByDescending(c => c.Date).ThenBy(c => c.Piece).Take(500).ToList();
    }

    /// <summary>Tuile de l'écran Direction : commandes passées sur la période et part livrée.</summary>
    public static object Synthese(IEnumerable<FaitCommande> commandes)
    {
        var parCommande = commandes.GroupBy(f => f.Commande)
            .Select(g => (Commande: g.Sum(f => f.MontantHT), Livre: g.Where(f => f.Livree).Sum(f => f.MontantHT),
                Ouverte: g.Any(f => f.EnAttente || f.Preparee),
                Delai: g.Where(f => f.Livree).Max(f => f.DateLivraison) is { } l ? (l.Date - g.Min(f => f.DateCommande).Date).Days : (int?)null))
            .ToList();
        var commande = parCommande.Sum(c => c.Commande);
        var livre = parCommande.Sum(c => c.Livre);
        var delais = parCommande.Where(c => c.Delai != null && c.Livre >= c.Commande).Select(c => c.Delai!.Value).ToList();
        return new
        {
            taux = Taux(livre, commande),
            commande, livre,
            nombre = parCommande.Count,
            completes = parCommande.Count(c => c.Commande != 0 && c.Livre >= c.Commande),
            ouvertes = parCommande.Count(c => c.Ouverte),
            delaiMoyen = delais.Count == 0 ? (double?)null : Math.Round(delais.Average(), 1),
        };
    }
}
