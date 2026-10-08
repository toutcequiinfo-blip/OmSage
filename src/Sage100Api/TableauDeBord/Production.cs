namespace Sage100Api.TableauDeBord;

/// <summary>
/// Ligne de document de stock regroupée par pièce, composé, article et dépôt : bons de fabrication (DO_Type 26) et mouvements de sortie (21).
/// Dans un bon de fabrication, la ligne du produit fabriqué entre en stock (DL_MvtStock 1) et porte sa propre référence en AR_RefCompose ;
/// les composants sortent du stock et portent la référence du composé.
/// </summary>
public sealed class FaitMouvement
{
    public int Type { get; init; } = 26;
    public DateTime Date { get; init; }
    public string Piece { get; init; } = "";
    /// <summary>Produit fini de la ligne (AR_RefCompose), vide hors fabrication.</summary>
    public string? Compose { get; init; }
    public string Article { get; init; } = "";
    public int? Depot { get; init; }
    /// <summary>Entrée en stock (DL_MvtStock 1 ou 2).</summary>
    public bool Entree { get; init; }
    public decimal Quantite { get; init; }
    public decimal Valeur { get; init; }

    public bool Fabrication => Type == 26;
    /// <summary>Ligne du produit fabriqué.</summary>
    public bool Produit => Fabrication && (Entree || (Compose != null && string.Equals(Compose, Article, StringComparison.OrdinalIgnoreCase)));
    /// <summary>Matière consommée par la fabrication.</summary>
    public bool Composant => Fabrication && !Produit;
    /// <summary>Mouvement de sortie hors fabrication (consommation diverse, perte...).</summary>
    public bool AutreSortie => Type == 21 && !Entree;
}

/// <summary>Composant d'une nomenclature (F_NOMENCLAT). Variable : par quantité de composition du composé ; fixe : par fabrication.</summary>
public sealed class LigneNomenclature
{
    public string Compose { get; init; } = "";
    public string Composant { get; init; } = "";
    public decimal Quantite { get; init; }
    public bool Fixe { get; init; }
    /// <summary>Quantité du composé pour laquelle la nomenclature est décrite (AR_QteComp, 1 à défaut).</summary>
    public decimal QteComposition { get; init; } = 1;
    public int Gamme1 { get; init; }
    public int Gamme2 { get; init; }
}

/// <summary>Fournisseur d'un article (F_ARTFOURNISS).</summary>
public sealed class RefFournisseurArticle
{
    public string Article { get; init; } = "";
    public string Fournisseur { get; init; } = "";
    public bool Principal { get; init; }
    /// <summary>Délai d'approvisionnement en jours (0 = non renseigné).</summary>
    public int Delai { get; init; }
    public decimal QteMini { get; init; }
    public decimal Colisage { get; init; }
    public decimal Prix { get; init; }
}

/// <summary>Réception d'une commande fournisseur (ligne de BL ou de facture d'achat issue d'un bon de commande) : sert au délai réel.</summary>
public sealed class FaitReception
{
    public string Article { get; init; } = "";
    public string Fournisseur { get; init; } = "";
    public DateTime DateCommande { get; init; }
    public DateTime DateReception { get; init; }
    public decimal Quantite { get; init; }
}

/// <summary>Données de fabrication d'un article (F_ARTICLE).</summary>
public sealed class RefArticleProduction
{
    public string Reference { get; init; } = "";
    /// <summary>AR_Nomencl : 0 aucune, 1 fabrication, 2 commerciale/composé, 3 commerciale/composant, 4 article lié.</summary>
    public int Nomenclature { get; init; }
    public decimal PrixAchat { get; init; }
    public int DelaiFabrication { get; init; }
}

public sealed record RequeteProduction(string? Du, string? Au, int? Depot, string? Famille, string? Statut, string? Vue,
    int? Jours, int? Delai, int? Securite, int? Couverture);

/// <summary>
/// Tableau de bord fabrication et approvisionnement, calculé sur l'instantané (lecture seule) :
/// production réalisée par les bons de fabrication, consommation réelle des matières face à la nomenclature,
/// besoins et propositions d'achat, prévisions de production.
/// </summary>
public static class Production
{
    static readonly StringComparer Ref = StringComparer.OrdinalIgnoreCase;
    static readonly string[] OrdreStatuts = ["rupture", "urgent", "commander", "ok"];

    /// <summary>Composant par unité de composé (variable) et par fabrication (fixe).</summary>
    public sealed record Composant(string Article, decimal ParUnite, decimal Fixe);

    /// <summary>
    /// Nomenclature de chaque composé. Un article à gammes a une nomenclature par combinaison : la première est retenue.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<Composant>> Nomenclatures(Instantane i) =>
        i.Nomenclatures.GroupBy(n => n.Compose, Ref).ToDictionary(g => g.Key, g =>
        {
            var premiere = g.OrderBy(n => n.Gamme1).ThenBy(n => n.Gamme2).First();
            return (IReadOnlyList<Composant>)g.Where(n => n.Gamme1 == premiere.Gamme1 && n.Gamme2 == premiere.Gamme2)
                .GroupBy(n => n.Composant, Ref)
                .Select(c => new Composant(c.Key,
                    c.Where(n => !n.Fixe).Sum(n => n.Quantite / (n.QteComposition > 0 ? n.QteComposition : 1)),
                    c.Where(n => n.Fixe).Sum(n => n.Quantite)))
                .ToList();
        }, Ref);

    static (DateTime Du, DateTime Au) Plage(Instantane i, RequeteProduction r, DateTime aujourdhui)
    {
        var (du, au) = Periodes.Analyse(i.ExerciceDe(aujourdhui), aujourdhui);
        return (Periodes.LireJour(r.Du, du), Periodes.LireJour(r.Au, au, fin: true));
    }

    static string? Designation(Instantane i, string article) => i.Articles.TryGetValue(article, out var a) ? a.Designation?.Trim() : null;

    /// <summary>Prix unitaire d'une matière : valeur moyenne de ses sorties sur la période, sinon prix du fournisseur principal ou prix d'achat.</summary>
    static Func<string, decimal> Prix(Instantane i, IEnumerable<FaitMouvement> sorties)
    {
        var moyens = sorties.GroupBy(m => m.Article, Ref).Where(g => g.Sum(m => m.Quantite) > 0)
            .ToDictionary(g => g.Key, g => g.Sum(m => m.Valeur) / g.Sum(m => m.Quantite), Ref);
        var fournisseurs = Fournisseurs(i);
        return a => moyens.TryGetValue(a, out var p) ? p
            : fournisseurs.TryGetValue(a, out var f) && f.Prix > 0 ? f.Prix
            : i.ArticlesProduction.TryGetValue(a, out var x) ? x.PrixAchat : 0;
    }

    /// <summary>Fournisseur retenu par article : le principal, sinon le premier.</summary>
    static Dictionary<string, RefFournisseurArticle> Fournisseurs(Instantane i) =>
        i.FournisseursArticles.GroupBy(f => f.Article, Ref).ToDictionary(g => g.Key, g => g.OrderByDescending(f => f.Principal).First(), Ref);

    /// <summary>Consommation réelle et théorique par composé et composant, pour les bons de fabrication donnés.</summary>
    public sealed record Consommation(string Compose, string Composant, decimal Reelle, decimal Theorique, decimal ValeurReelle);

    static List<Consommation> Consommations(IReadOnlyList<FaitMouvement> fabrication, IReadOnlyDictionary<string, IReadOnlyList<Composant>> nomenclatures)
    {
        var lignes = new Dictionary<(string, string), (decimal Reelle, decimal Theorique, decimal Valeur)>();
        void Ajouter(string compose, string composant, decimal reelle, decimal theorique, decimal valeur)
        {
            var cle = (compose.ToUpperInvariant(), composant.ToUpperInvariant());
            var v = lignes.GetValueOrDefault(cle);
            lignes[cle] = (v.Reelle + reelle, v.Theorique + theorique, v.Valeur + valeur);
        }
        foreach (var m in fabrication.Where(m => m.Composant))
            Ajouter(m.Compose ?? "", m.Article, m.Quantite, 0, m.Valeur);
        // Théorique : nomenclature × quantité fabriquée, plus les composants fixes une fois par bon.
        foreach (var g in fabrication.Where(m => m.Produit).GroupBy(m => (m.Piece, m.Article.ToUpperInvariant())))
        {
            if (!nomenclatures.TryGetValue(g.Key.Item2, out var composants)) continue;
            var fabrique = g.Sum(m => m.Quantite);
            foreach (var c in composants) Ajouter(g.Key.Item2, c.Article, 0, c.ParUnite * fabrique + c.Fixe, 0);
        }
        return lignes.Select(x => new Consommation(x.Key.Item1, x.Key.Item2, x.Value.Reelle, x.Value.Theorique, x.Value.Valeur)).ToList();
    }

    static List<FaitMouvement> Mouvements(Instantane i, RequeteProduction r, DateTime du, DateTime au) =>
        i.Mouvements.Where(m => m.Date.Date >= du.Date && m.Date.Date <= au.Date && (r.Depot == null || m.Depot == r.Depot)).ToList();

    static decimal? Pourcent(decimal ecart, decimal reference) => reference == 0 ? null : Math.Round(ecart / reference * 100, 1);

    // ---------- Production réalisée ----------

    public static object Synthese(Instantane i, RequeteProduction r, DateTime aujourdhui)
    {
        var (du, au) = Plage(i, r, aujourdhui);
        var nomenclatures = Nomenclatures(i);
        var mouvements = Mouvements(i, r, du, au);
        var fabrication = mouvements.Where(m => m.Fabrication && (r.Famille == null || i.Famille(m.Compose ?? m.Article) == r.Famille)).ToList();
        var prix = Prix(i, mouvements.Where(m => m.Composant || m.AutreSortie));
        var conso = Consommations(fabrication, nomenclatures);
        var parProduit = conso.GroupBy(c => c.Compose).ToDictionary(g => g.Key, g => (
            Reelle: g.Sum(c => c.ValeurReelle),
            Theorique: g.Sum(c => c.Theorique * prix(c.Composant))));

        var produits = fabrication.Where(m => m.Produit).GroupBy(m => m.Article.ToUpperInvariant()).Select(g =>
        {
            var v = parProduit.GetValueOrDefault(g.Key);
            var quantite = g.Sum(m => m.Quantite);
            return new
            {
                article = g.First().Article, designation = Designation(i, g.First().Article), famille = i.Famille(g.First().Article),
                quantite, bons = g.Select(m => m.Piece).Distinct().Count(), valeur = g.Sum(m => m.Valeur),
                matieres = v.Reelle, theorique = Math.Round(v.Theorique, 2), ecart = Math.Round(v.Reelle - v.Theorique, 2),
                ecartPourcent = Pourcent(v.Reelle - v.Theorique, v.Theorique),
                coutUnitaire = quantite > 0 ? Math.Round(v.Reelle / quantite, 4) : (decimal?)null,
                nomenclature = nomenclatures.ContainsKey(g.Key),
                derniere = g.Max(m => m.Date),
            };
        }).OrderByDescending(x => x.valeur).ToList();

        var matieres = produits.Sum(p => p.matieres);
        var theorique = produits.Sum(p => p.theorique);
        var mois = new List<object>();
        for (var m = Periodes.DebutMois(du); m <= au; m = m.AddMonths(1))
        {
            var dans = fabrication.Where(x => x.Date.Year == m.Year && x.Date.Month == m.Month).ToList();
            mois.Add(new
            {
                mois = Periodes.Mois(m), production = dans.Where(x => x.Produit).Sum(x => x.Valeur), matieres = dans.Where(x => x.Composant).Sum(x => x.Valeur),
                bons = dans.Where(x => x.Produit).Select(x => x.Piece).Distinct().Count(),
            });
        }
        var autres = mouvements.Where(m => m.AutreSortie).ToList();
        return new
        {
            du, au,
            totaux = new
            {
                bons = fabrication.Where(m => m.Produit).Select(m => m.Piece).Distinct().Count(),
                produits = produits.Count, valeur = produits.Sum(p => p.valeur), matieres, theorique,
                ecart = matieres - theorique, ecartPourcent = Pourcent(matieres - theorique, theorique),
                autresSorties = autres.Sum(m => m.Valeur),
                sansNomenclature = produits.Count(p => !p.nomenclature),
            },
            mois,
            produits = produits.Take(1000).ToList(),
        };
    }

    /// <summary>Matières d'un produit sur la période : réel face à la nomenclature.</summary>
    public static object DetailProduit(Instantane i, string article, RequeteProduction r, DateTime aujourdhui)
    {
        var (du, au) = Plage(i, r, aujourdhui);
        var mouvements = Mouvements(i, r, du, au);
        var fabrication = mouvements.Where(m => m.Fabrication && string.Equals(m.Compose ?? m.Article, article, StringComparison.OrdinalIgnoreCase)).ToList();
        var prix = Prix(i, mouvements.Where(m => m.Composant || m.AutreSortie));
        var conso = Consommations(fabrication, Nomenclatures(i));
        return new
        {
            article, designation = Designation(i, article),
            quantite = fabrication.Where(m => m.Produit).Sum(m => m.Quantite),
            bons = fabrication.Where(m => m.Produit).GroupBy(m => m.Piece)
                .Select(g => new { piece = g.Key, date = g.Min(m => m.Date), quantite = g.Sum(m => m.Quantite), valeur = g.Sum(m => m.Valeur) })
                .OrderByDescending(b => b.date).Take(100).ToList(),
            composants = conso.Select(c => new
            {
                article = c.Composant, designation = Designation(i, c.Composant), reelle = c.Reelle, theorique = Math.Round(c.Theorique, 4),
                ecart = Math.Round(c.Reelle - c.Theorique, 4), ecartPourcent = Pourcent(c.Reelle - c.Theorique, c.Theorique),
                valeur = c.ValeurReelle, ecartValeur = Math.Round((c.Reelle - c.Theorique) * prix(c.Composant), 2),
            }).OrderByDescending(c => Math.Abs(c.ecartValeur)).ToList(),
        };
    }

    // ---------- Consommation des matières ----------

    public static object Matieres(Instantane i, RequeteProduction r, DateTime aujourdhui)
    {
        var (du, au) = Plage(i, r, aujourdhui);
        var mouvements = Mouvements(i, r, du, au);
        var prix = Prix(i, mouvements.Where(m => m.Composant || m.AutreSortie));
        var conso = Consommations(mouvements.Where(m => m.Fabrication).ToList(), Nomenclatures(i));
        var autres = mouvements.Where(m => m.AutreSortie).GroupBy(m => m.Article.ToUpperInvariant())
            .ToDictionary(g => g.Key, g => (Quantite: g.Sum(m => m.Quantite), Valeur: g.Sum(m => m.Valeur)));
        var parMatiere = conso.ToLookup(c => c.Composant);
        var articles = conso.Select(c => c.Composant).Concat(autres.Keys).Distinct().Select(a =>
            {
                var c = parMatiere[a].ToList();
                var reelle = c.Sum(x => x.Reelle);
                var theorique = c.Sum(x => x.Theorique);
                var p = prix(a);
                var autre = autres.GetValueOrDefault(a);
                var reference = i.Articles.Keys.FirstOrDefault(k => Ref.Equals(k, a)) ?? a;
                return new
                {
                    article = reference, designation = Designation(i, reference), famille = i.Famille(reference),
                    reelle, theorique = Math.Round(theorique, 4), ecart = Math.Round(reelle - theorique, 4), ecartPourcent = Pourcent(reelle - theorique, theorique),
                    valeur = c.Sum(x => x.ValeurReelle), ecartValeur = Math.Round((reelle - theorique) * p, 2), prixUnitaire = Math.Round(p, 4),
                    autresSorties = autre.Quantite, valeurAutresSorties = autre.Valeur,
                    produits = c.Where(x => x.Compose != "").Select(x => x.Compose).Distinct().Count(),
                };
            }).Where(a => r.Famille == null || a.famille == r.Famille).ToList();
        return new
        {
            du, au,
            totaux = new
            {
                matieres = articles.Count, valeur = articles.Sum(a => a.valeur), ecartValeur = articles.Sum(a => a.ecartValeur),
                surconsommation = articles.Where(a => a.ecartValeur > 0).Sum(a => a.ecartValeur),
                sousConsommation = articles.Where(a => a.ecartValeur < 0).Sum(a => a.ecartValeur),
                autresSorties = articles.Sum(a => a.valeurAutresSorties),
            },
            matieres = articles.OrderByDescending(a => Math.Abs(a.ecartValeur)).ThenByDescending(a => a.valeur).Take(1000).ToList(),
        };
    }

    // ---------- Approvisionnement ----------

    public sealed record LigneAppro(string Article, string? Designation, string? Famille, decimal Stock, decimal Reserve, decimal Commande,
        decimal BesoinFabrication, decimal Disponible, decimal ConsoJour, int Delai, string SourceDelai, decimal Seuil, int? CouvertureJours,
        decimal AProposer, string? Fournisseur, string? IntituleFournisseur, decimal Prix, decimal Valeur, decimal Colisage, decimal QteMini, string Statut);

    /// <summary>
    /// Pour chaque matière : stock, réservé (commandes clients), commandé (fournisseurs), besoin des produits finis à fabriquer
    /// pour les commandes clients non couvertes par le stock (nomenclature éclatée sur tous ses niveaux), consommation moyenne,
    /// délai (fiche fournisseur, sinon délai réel des réceptions, sinon délai de fabrication, sinon défaut), point de commande et quantité proposée.
    /// </summary>
    public static IReadOnlyList<LigneAppro> Lignes(Instantane i, RequeteProduction r, DateTime aujourdhui)
    {
        var jours = Math.Clamp(r.Jours ?? 90, 7, 730);
        var delaiDefaut = Math.Clamp(r.Delai ?? 15, 0, 365);
        var securite = Math.Clamp(r.Securite ?? 7, 0, 365);
        var couverture = Math.Clamp(r.Couverture ?? 30, 0, 365);
        var nomenclatures = Nomenclatures(i);
        var fournisseurs = Fournisseurs(i);
        var depuis = aujourdhui.Date.AddDays(-jours);

        var stocks = i.Stock.Where(s => r.Depot == null || s.Depot == r.Depot).GroupBy(s => s.Article.ToUpperInvariant())
            .ToDictionary(g => g.Key, g => (Quantite: g.Sum(s => s.Quantite), Reserve: g.Sum(s => s.Reserve), Commande: g.Sum(s => s.Commande), Mini: g.Sum(s => s.Mini)));
        decimal Disponible(string a) => stocks.TryGetValue(a.ToUpperInvariant(), out var s) ? s.Quantite - s.Reserve : 0;

        // Consommation : matières des bons de fabrication, sorties diverses et ventes de la période de référence.
        var sorties = i.Mouvements.Where(m => m.Date >= depuis && (m.Composant || m.AutreSortie) && (r.Depot == null || m.Depot == r.Depot))
            .Select(m => (m.Article, m.Quantite))
            .Concat(i.Lignes.Where(l => l.Domaine == 0 && l.Date >= depuis && (r.Depot == null || l.Depot == r.Depot)).Select(l => (l.Article, l.Quantite)))
            .GroupBy(x => x.Article.ToUpperInvariant()).ToDictionary(g => g.Key, g => g.Sum(x => x.Quantite) / jours);

        // Besoin des fabrications : produits finis réservés par les clients au-delà de leur stock, éclatés niveau par niveau.
        var besoins = new Dictionary<string, decimal>(Ref);
        var restant = new Dictionary<string, decimal>(Ref);
        decimal Restant(string a) => restant.TryGetValue(a, out var v) ? v : Math.Max(0, Disponible(a));
        void Eclater(string compose, decimal quantite, int niveau)
        {
            if (quantite <= 0 || niveau > 10 || !nomenclatures.TryGetValue(compose, out var composants)) return;
            foreach (var c in composants)
            {
                var besoin = c.ParUnite * quantite + c.Fixe;
                if (besoin <= 0) continue;
                besoins[c.Article] = besoins.GetValueOrDefault(c.Article) + besoin;
                // Composant lui-même fabriqué : ce que son stock ne couvre pas est à fabriquer à son tour.
                var pris = Math.Min(Restant(c.Article), besoin);
                restant[c.Article] = Restant(c.Article) - pris;
                if (nomenclatures.ContainsKey(c.Article)) Eclater(c.Article, besoin - pris, niveau + 1);
            }
        }
        foreach (var (article, s) in stocks)
            if (nomenclatures.ContainsKey(article) && s.Reserve > s.Quantite) Eclater(article, s.Reserve - Math.Max(0, s.Quantite), 1);

        // Délai réel moyen des réceptions par article.
        var delaisReels = i.Receptions.Where(x => x.DateReception >= x.DateCommande).GroupBy(x => x.Article.ToUpperInvariant())
            .ToDictionary(g => g.Key, g => (int)Math.Round(g.Average(x => (x.DateReception - x.DateCommande).TotalDays)));

        var matieres = nomenclatures.Values.SelectMany(c => c).Select(c => c.Article.ToUpperInvariant()).ToHashSet();
        var tous = r.Vue == "tous";
        var univers = matieres.Concat(tous ? fournisseurs.Keys.Select(k => k.ToUpperInvariant()) : []).Concat(besoins.Keys.Select(k => k.ToUpperInvariant()))
            .Distinct().ToList();
        var lignes = new List<LigneAppro>();
        foreach (var a in univers)
        {
            var reference = i.Articles.Keys.FirstOrDefault(k => Ref.Equals(k, a)) ?? a;
            if (r.Famille != null && i.Famille(reference) != r.Famille) continue;
            var s = stocks.GetValueOrDefault(a);
            var besoin = Math.Round(besoins.GetValueOrDefault(a), 4);
            var conso = Math.Round(sorties.GetValueOrDefault(a), 4);
            if (s.Quantite == 0 && s.Reserve == 0 && s.Commande == 0 && besoin == 0 && conso == 0) continue;
            fournisseurs.TryGetValue(a, out var f);
            i.ArticlesProduction.TryGetValue(reference, out var ap);
            var (delai, source) = f?.Delai > 0 ? (f.Delai, "fiche")
                : delaisReels.TryGetValue(a, out var reel) ? (reel, "historique")
                : ap?.DelaiFabrication > 0 && nomenclatures.ContainsKey(a) ? (ap.DelaiFabrication, "fabrication")
                : (delaiDefaut, "defaut");
            var disponible = s.Quantite - s.Reserve - besoin;
            var projete = disponible + s.Commande;
            var seuil = Math.Max(s.Mini, conso * (delai + securite));
            var cible = Math.Max(seuil, conso * (delai + securite + couverture));
            var statut = disponible <= 0 && (conso > 0 || besoin > 0 || s.Reserve > 0) ? "rupture"
                : conso > 0 && projete < conso * delai ? "urgent"
                : projete < seuil || projete < 0 ? "commander"
                : "ok";
            var proposer = statut == "ok" ? 0 : Arrondir(cible - projete, f?.Colisage ?? 0, f?.QteMini ?? 0);
            var prix = f?.Prix > 0 ? f.Prix : ap?.PrixAchat ?? 0;
            lignes.Add(new LigneAppro(reference, Designation(i, reference), i.Famille(reference), s.Quantite, s.Reserve, s.Commande, besoin, Math.Round(disponible, 4),
                conso, delai, source, Math.Round(seuil, 4), conso > 0 ? (int)Math.Min(9999, Math.Max(0, Math.Floor(disponible / conso))) : null,
                proposer, f?.Fournisseur, f == null ? null : i.IntituleTiers(f.Fournisseur), prix, Math.Round(proposer * prix, 2),
                f?.Colisage ?? 0, f?.QteMini ?? 0, statut));
        }
        return lignes;
    }

    /// <summary>Quantité à commander : au moins la quantité minimale du fournisseur, arrondie au colisage.</summary>
    public static decimal Arrondir(decimal quantite, decimal colisage, decimal mini)
    {
        if (quantite <= 0) return 0;
        var q = Math.Max(quantite, mini);
        return colisage > 0 ? Math.Ceiling(q / colisage) * colisage : Math.Ceiling(q * 100) / 100;
    }

    public static object Appro(Instantane i, RequeteProduction r, DateTime aujourdhui)
    {
        var lignes = Lignes(i, r, aujourdhui);
        var aCommander = lignes.Where(l => l.AProposer > 0).ToList();
        return new
        {
            parametres = new { jours = Math.Clamp(r.Jours ?? 90, 7, 730), delai = Math.Clamp(r.Delai ?? 15, 0, 365), securite = Math.Clamp(r.Securite ?? 7, 0, 365), couverture = Math.Clamp(r.Couverture ?? 30, 0, 365) },
            totaux = new
            {
                articles = lignes.Count, ruptures = lignes.Count(l => l.Statut == "rupture"), urgents = lignes.Count(l => l.Statut == "urgent"),
                aCommander = aCommander.Count, valeur = aCommander.Sum(l => l.Valeur),
                sansDelai = lignes.Count(l => l.SourceDelai == "defaut"),
                besoinsFabrication = lignes.Count(l => l.BesoinFabrication > 0),
            },
            parFournisseur = aCommander.GroupBy(l => l.Fournisseur ?? "").Select(g => new
            {
                cle = g.Key, intitule = g.Key == "" ? "(sans fournisseur)" : g.First().IntituleFournisseur ?? g.Key, articles = g.Count(), valeur = g.Sum(l => l.Valeur),
            }).OrderByDescending(x => x.valeur).ToList(),
            lignes = lignes.Where(l => r.Statut == null || l.Statut == r.Statut)
                .OrderBy(l => Array.IndexOf(OrdreStatuts, l.Statut)).ThenBy(l => l.CouvertureJours ?? int.MaxValue).ThenByDescending(l => l.Valeur)
                .Take(1000).ToList(),
        };
    }

    // ---------- Prévisions ----------

    /// <summary>
    /// Pour chaque produit fini : ventes des 12 derniers mois, prévision du mois prochain (moyenne des 3 derniers mois complets
    /// corrigée de la saisonnalité de l'an dernier), quantité à produire face au stock et aux commandes, quantité fabricable avec le stock des composants.
    /// </summary>
    public static object Previsions(Instantane i, RequeteProduction r, DateTime aujourdhui)
    {
        var nomenclatures = Nomenclatures(i);
        var moisCourant = Periodes.DebutMois(aujourdhui);
        var prochain = moisCourant.AddMonths(1);
        var debut = moisCourant.AddMonths(-24);
        var produitsFabriques = i.Mouvements.Where(m => m.Produit).Select(m => m.Article.ToUpperInvariant()).ToHashSet();
        var finis = nomenclatures.Keys.Where(k => produitsFabriques.Contains(k.ToUpperInvariant())
                || (i.ArticlesProduction.TryGetValue(k, out var a) && a.Nomenclature == 1))
            .Select(k => k.ToUpperInvariant()).Concat(produitsFabriques).Distinct().ToList();

        var ventes = i.Lignes.Where(l => l.Domaine == 0 && l.Date >= debut && (r.Depot == null || l.Depot == r.Depot))
            .GroupBy(l => (l.Article.ToUpperInvariant(), Periodes.DebutMois(l.Date))).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantite));
        var production = i.Mouvements.Where(m => m.Produit && m.Date >= debut && (r.Depot == null || m.Depot == r.Depot))
            .GroupBy(m => (m.Article.ToUpperInvariant(), Periodes.DebutMois(m.Date))).ToDictionary(g => g.Key, g => g.Sum(m => m.Quantite));
        var stocks = i.Stock.Where(s => r.Depot == null || s.Depot == r.Depot).GroupBy(s => s.Article.ToUpperInvariant())
            .ToDictionary(g => g.Key, g => (Quantite: g.Sum(s => s.Quantite), Reserve: g.Sum(s => s.Reserve)));
        decimal Disponible(string a) => stocks.TryGetValue(a.ToUpperInvariant(), out var s) ? s.Quantite - s.Reserve : 0;

        var lignes = finis.Select(a =>
        {
            var reference = i.Articles.Keys.FirstOrDefault(k => Ref.Equals(k, a)) ?? a;
            var douze = Enumerable.Range(1, 12).Select(n => ventes.GetValueOrDefault((a, moisCourant.AddMonths(-13 + n)))).ToList();
            var moyenne3 = douze.Skip(9).Average();
            var anneePassee = Enumerable.Range(0, 12).Select(n => ventes.GetValueOrDefault((a, moisCourant.AddMonths(-24 + n)))).ToList();
            var moyenneN1 = anneePassee.Average();
            var memeMoisN1 = ventes.GetValueOrDefault((a, prochain.AddYears(-1)));
            var saison = moyenneN1 > 0 && memeMoisN1 > 0 ? Math.Clamp(memeMoisN1 / moyenneN1, 0.5m, 2m) : 1m;
            var prevision = Math.Round(moyenne3 * saison, 2);
            var s = stocks.GetValueOrDefault(a);
            var aProduire = Math.Max(0, Math.Round(prevision + s.Reserve - s.Quantite, 2));
            // Fabricable : composant le plus limitant (stock disponible / quantité par unité).
            decimal? fabricable = null;
            string? bloquant = null;
            if (nomenclatures.TryGetValue(a, out var composants))
                foreach (var c in composants.Where(c => c.ParUnite > 0))
                {
                    var possible = Math.Max(0, Math.Floor((Disponible(c.Article) - c.Fixe) / c.ParUnite));
                    if (fabricable == null || possible < fabricable) (fabricable, bloquant) = (possible, c.Article);
                }
            return new
            {
                article = reference, designation = Designation(i, reference), famille = i.Famille(reference),
                ventes = douze, ventes12 = douze.Sum(), moyenne3 = Math.Round(moyenne3, 2), saisonnalite = Math.Round(saison, 2), prevision,
                production3 = Math.Round(Enumerable.Range(1, 3).Sum(n => production.GetValueOrDefault((a, moisCourant.AddMonths(-n)))) / 3, 2),
                stock = s.Quantite, reserve = s.Reserve, aProduire, fabricable, bloquant,
                designationBloquant = bloquant == null ? null : Designation(i, bloquant),
                manque = fabricable != null && fabricable < aProduire,
            };
        }).Where(x => (r.Famille == null || x.famille == r.Famille) && (x.ventes12 > 0 || x.stock != 0 || x.reserve > 0 || x.production3 > 0)).ToList();

        return new
        {
            mois = Enumerable.Range(1, 12).Select(n => Periodes.Mois(moisCourant.AddMonths(-13 + n))).ToList(),
            prochain = Periodes.Mois(prochain),
            totaux = new
            {
                produits = lignes.Count, aProduire = lignes.Count(l => l.aProduire > 0), manques = lignes.Count(l => l.manque),
                sansVente = lignes.Count(l => l.ventes12 == 0),
            },
            lignes = lignes.OrderByDescending(l => l.manque).ThenByDescending(l => l.aProduire).ThenByDescending(l => l.ventes12).Take(1000).ToList(),
        };
    }
}
