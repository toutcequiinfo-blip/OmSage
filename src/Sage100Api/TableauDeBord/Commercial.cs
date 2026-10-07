namespace Sage100Api.TableauDeBord;

/// <summary>
/// Paramètres du tableau commercial croisé. Domaine : ventes (défaut) ou achats.
/// Article, famille, tiers, commercial, dépôt, catégorie et qualité sont des choix multiples (<see cref="Selection"/>) :
/// « A,B » garde ces valeurs, « !A,B » les exclut.
/// </summary>
public sealed record RequeteVentes(string? Domaine, string? Lignes, string? Colonnes, string? Du, string? Au, string? Article, string? Famille,
    string? Tiers, string? Commercial, string? Depot, string? Categorie, string? Tri, int? Limite, string? Qualite = null);

/// <summary>Filtres à choix multiple d'une requête du tableau commercial, lus une fois.</summary>
sealed class SelectionsVentes(RequeteVentes r)
{
    public Selection? Article { get; } = Selection.Lire(r.Article);
    public Selection? Famille { get; } = Selection.Lire(r.Famille);
    public Selection? Tiers { get; } = Selection.Lire(r.Tiers);
    public Selection? Commercial { get; } = Selection.Lire(r.Commercial);
    public Selection? Depot { get; } = Selection.Lire(r.Depot);
    public Selection? Categorie { get; } = Selection.Lire(r.Categorie);
    public Selection? Qualite { get; } = Selection.Lire(r.Qualite);

    public bool RetientArticle(Instantane i, string article) =>
        (Article?.Retient(article) ?? true) && (Famille?.Retient(i.Famille(article)) ?? true);

    public bool RetientTiers(Instantane i, string? tiers)
    {
        if (!(Tiers?.Retient(tiers) ?? true)) return false;
        if (Categorie == null && Qualite == null) return true;
        var t = tiers != null && i.Tiers.TryGetValue(tiers, out var x) ? x : null;
        return (Categorie?.Retient(t?.Categorie) ?? true) && (Qualite?.Retient(t?.Qualite) ?? true);
    }

    public bool FiltreArticles => Article != null || Famille != null;
    public bool FiltreTiers => Tiers != null || Categorie != null || Qualite != null;
}

public static class Commercial
{
    public static readonly string[] Mesures = ["ca", "quantite", "cout", "marge", "taux"];

    public static int Domaine(string? d) => d == "achats" ? 1 : 0;

    public static IReadOnlyDictionary<string, Axe<FaitLigne>> Axes(Instantane i) =>
        AxesVentes<FaitLigne>(i, f => f.Date, f => f.Article, f => f.Tiers, f => f.Commercial, f => f.Depot);

    /// <summary>Axes des ventes, communs aux lignes de factures et aux commandes (taux de transformation).</summary>
    internal static IReadOnlyDictionary<string, Axe<T>> AxesVentes<T>(Instantane i, Func<T, DateTime> date, Func<T, string> article, Func<T, string> tiers,
        Func<T, int?> commercial, Func<T, int?> depot) => new Axe<T>[]
    {
        new("exercice", "Exercice", f => Periodes.CleExercice(i.ExerciceDe(date(f))),
            k => i.Exercices.FirstOrDefault(e => Periodes.CleExercice(e) == k) is { } e ? Periodes.IntituleExercice(e) : k, true),
        new("annee", "Année", f => Periodes.Annee(date(f)), null, true),
        new("trimestre", "Trimestre", f => Periodes.Trimestre(date(f)), null, true),
        new("mois", "Mois", f => Periodes.Mois(date(f)), Periodes.IntituleMois, true),
        new("semaine", "Semaine", f => Periodes.Semaine(date(f)), Periodes.IntituleSemaine, true),
        new("article", "Article", f => article(f), k => i.Articles.TryGetValue(k, out var a) ? $"{k} {a.Designation?.Trim()}" : k),
        new("famille", "Famille", f => i.Famille(article(f)), k => i.Familles.GetValueOrDefault(k) is { } x ? $"{k} {x}" : k),
        new("tiers", "Client / fournisseur", f => tiers(f), k => i.IntituleTiers(k) is { } t ? $"{k} {t}" : k),
        new("categorie", "Catégorie tarifaire", f => i.Tiers.TryGetValue(tiers(f), out var t) ? t.Categorie?.ToString() : null,
            k => int.TryParse(k, out var c) ? i.Categories.GetValueOrDefault(c) ?? k : k),
        new("qualite", "Qualité client", f => i.Tiers.TryGetValue(tiers(f), out var t) ? t.Qualite : null, null),
        new("commercial", "Commercial", f => (commercial(f) ?? i.Representant(tiers(f)))?.ToString(),
            k => int.TryParse(k, out var c) ? i.Collaborateurs.GetValueOrDefault(c) ?? k : k),
        new("depot", "Dépôt", f => depot(f)?.ToString(), k => int.TryParse(k, out var d) ? i.Depots.GetValueOrDefault(d) ?? k : k),
    }.ToDictionary(a => a.Code);

    static decimal Mesure(decimal[] s, string m) => m switch
    {
        "quantite" => s[1],
        "cout" => s[2],
        "marge" => s[0] - s[2],
        // Taux de marge du cahier des charges : marge / coût de revient × 100.
        "taux" => s[2] == 0 ? 0 : (s[0] - s[2]) / s[2] * 100,
        _ => s[0],
    };

    public static IEnumerable<FaitLigne> Filtrer(Instantane i, RequeteVentes r, Perimetre p, DateTime aujourdhui)
    {
        var domaine = Domaine(r.Domaine);
        var (du, au) = Comptabilite.Bornes(i, r.Du, r.Au, aujourdhui);
        var s = new SelectionsVentes(r);
        return i.Lignes.Where(f => f.Domaine == domaine && f.Date >= du && f.Date <= au
            && (domaine == 1 || p.Autorise(i, f.Tiers, f.Commercial))
            && (!s.FiltreArticles || s.RetientArticle(i, f.Article))
            && (!s.FiltreTiers || s.RetientTiers(i, f.Tiers))
            && (s.Commercial?.Retient(f.Commercial ?? i.Representant(f.Tiers)) ?? true)
            && (s.Depot?.Retient(f.Depot) ?? true));
    }

    public static ResultatCube Croiser(Instantane i, RequeteVentes r, Perimetre p, DateTime aujourdhui)
    {
        var axes = Axes(i);
        Axe<FaitLigne>? Axe(string? code, string? defaut) =>
            code is { Length: > 0 } && axes.TryGetValue(code, out var a) ? a : defaut != null && code == null ? axes[defaut] : null;
        return Cube.Pivoter(Filtrer(i, r, p, aujourdhui), Axe(r.Lignes, "famille"), Axe(r.Colonnes, null), 3,
            f => [f.MontantHT, f.Quantite, f.Cout], Mesures, Mesure, r.Tri ?? "ca", Math.Clamp(r.Limite ?? 300, 10, 2000));
    }

    /// <summary>Filtres SQL du détail d'une cellule (lignes de factures). Null si la cellule ne se traduit pas en filtre.</summary>
    public static FiltreDetailVentes? Detail(Instantane i, RequeteVentes r, Perimetre p, string? cleLigne, string? cleColonne, DateTime aujourdhui)
    {
        var (du, au) = Comptabilite.Bornes(i, r.Du, r.Au, aujourdhui);
        var s = new SelectionsVentes(r);
        var domaine = Domaine(r.Domaine);
        string? article = null, tiers = null;
        int? commercial = null, depot = null;
        // Choix multiples traduits en listes SQL : ce qui est gardé (IN) quand une valeur est cochée en « garder »,
        // sinon ce qui est exclu (NOT IN). Les membres d'une famille, d'une catégorie ou d'une qualité viennent de l'instantané.
        List<string>? articles = null, articlesExclus = null, tiersListe = null, tiersExclus = null;
        if (s.FiltreArticles)
        {
            var connus = i.Articles.Keys.Concat(s.Article?.Valeurs ?? Enumerable.Empty<string>()).Where(a => a != "").Distinct(StringComparer.OrdinalIgnoreCase);
            if (s.Article is { Exclure: false } || s.Famille is { Exclure: false })
                articles = (s.Article is { Exclure: false } av ? av.Valeurs.Where(v => v != "") : connus).Where(x => s.RetientArticle(i, x)).ToList();
            else articlesExclus = connus.Where(x => !s.RetientArticle(i, x)).ToList();
        }
        if (s.FiltreTiers)
        {
            var connus = i.Tiers.Values.Where(t => t.Type == domaine).Select(t => t.Numero)
                .Concat(s.Tiers?.Valeurs ?? Enumerable.Empty<string>()).Where(t => t != "").Distinct(StringComparer.OrdinalIgnoreCase);
            if (s.Tiers is { Exclure: false } || s.Categorie is { Exclure: false } || s.Qualite is { Exclure: false })
                tiersListe = (s.Tiers is { Exclure: false } tv ? tv.Valeurs.Where(v => v != "") : connus).Where(x => s.RetientTiers(i, x)).ToList();
            else tiersExclus = connus.Where(x => !s.RetientTiers(i, x)).ToList();
        }
        foreach (var (axe, cle) in new[] { (r.Lignes ?? "famille", cleLigne), (r.Colonnes, cleColonne) })
        {
            if (axe == null || cle == null) continue;
            if (cle == Cube.CleAutres) return null;
            if (axe is "exercice" or "annee" or "trimestre" or "mois" or "semaine")
            {
                if (!Periodes.Restreindre(i, axe, cle, ref du, ref au)) return null;
                continue;
            }
            switch (axe)
            {
                case "article": article = cle; break;
                case "famille":
                    var membres = i.Articles.Values.Where(a => (a.Famille ?? "") == cle).Select(a => a.Reference);
                    articles = articles == null ? membres.ToList() : articles.Intersect(membres).ToList();
                    break;
                case "tiers": tiers = cle; break;
                case "categorie":
                    var cat = int.TryParse(cle, out var c) ? c : (int?)null;
                    var dansCat = i.Tiers.Values.Where(x => x.Categorie == cat).Select(x => x.Numero);
                    tiersListe = tiersListe == null ? dansCat.ToList() : tiersListe.Intersect(dansCat).ToList();
                    break;
                case "qualite":
                    var deQ = i.Tiers.Values.Where(x => string.Equals(x.Qualite ?? "", cle, StringComparison.OrdinalIgnoreCase)).Select(x => x.Numero);
                    tiersListe = tiersListe == null ? deQ.ToList() : tiersListe.Intersect(deQ).ToList();
                    break;
                case "commercial" when int.TryParse(cle, out var co): commercial = co; break;
                case "depot" when int.TryParse(cle, out var d): depot = d; break;
                default: return null;
            }
        }
        if (p.Restreint)
        {
            // Vendeur : seulement ses clients, filtrés ici puisque la requête SQL ne connaît pas le représentant.
            var siens = i.Tiers.Values.Where(t => t.Type == 0 && t.Representant == p.Collaborateur).Select(t => t.Numero);
            tiersListe = tiersListe == null ? siens.ToList() : tiersListe.Intersect(siens).ToList();
            if (tiers != null && !tiersListe.Contains(tiers)) return null;
        }
        if (articles is { Count: 0 } || tiersListe is { Count: 0 } || du > au) return null;
        if (new[] { articles, articlesExclus, tiersListe, tiersExclus }.Any(x => x is { Count: > 2000 })) return null;
        return new FiltreDetailVentes(domaine, du, au, article, articles, tiers, tiersListe, commercial, depot, 500, i.Documents)
        {
            ArticlesExclus = articlesExclus,
            TiersExclus = tiersExclus,
            Commerciaux = s.Commercial is { Exclure: false } cg ? cg.Entiers() : null,
            CommerciauxExclus = s.Commercial is { Exclure: true } cx ? cx.Entiers() : null,
            Depots = s.Depot is { Exclure: false } de ? de.Entiers() : null,
            DepotsExclus = s.Depot is { Exclure: true } dx ? dx.Entiers() : null,
        };
    }


    public sealed record PointMensuel(string Mois, decimal Ca, decimal Marge, decimal? CaN1, decimal? MargeN1, decimal? Objectif);
    public sealed record Classement(string Cle, string? Intitule, decimal Ca, decimal Marge, decimal? Taux, decimal Quantite, decimal? CaN1);
    public sealed record ClientARelancer(string Tiers, string? Intitule, DateTime DerniereFacture, int Jours, decimal CaDouzeMois);

    /// <summary>
    /// Écran Direction / Ventes : KPI du cahier (CA jour, mois, exercice et N-1, marge, taux, panier moyen, clients actifs et nouveaux),
    /// courbes mensuelles, classements, clients à relancer, documents en cours, stock et alertes.
    /// </summary>
    public static object Synthese(Instantane i, Exercice e, DateTime aujourdhui, SeuilsAlertes seuils, Perimetre p, int? commercial,
        IReadOnlyDictionary<string, decimal> objectifs)
    {
        var (du, au) = Periodes.Analyse(e, aujourdhui);
        var (duN1, auN1) = Periodes.Comparable(i, e, au);
        bool Retenue(string tiers, int? co) => p.Autorise(i, tiers, co) && (commercial == null || (co ?? i.Representant(tiers)) == commercial);
        var pieces = i.Pieces.Where(x => x.Domaine == 0 && Retenue(x.Tiers, x.Commercial)).ToList();
        var lignes = i.Lignes.Where(x => x.Domaine == 0 && Retenue(x.Tiers, x.Commercial)).ToList();
        decimal Ca(DateTime d, DateTime a) => pieces.Where(x => x.Date.Date >= d.Date && x.Date.Date <= a.Date).Sum(x => x.MontantHT);
        IEnumerable<FaitLigne> Lignes(DateTime d, DateTime a) => lignes.Where(x => x.Mois >= Periodes.DebutMois(d) && x.Mois <= Periodes.DebutMois(a));

        var caExercice = Ca(du, au);
        var caN1 = Ca(duN1, auN1);
        var debutMois = Periodes.DebutMois(au);
        var caMois = Ca(debutMois, au);
        var caMoisN1 = Ca(debutMois.AddYears(-1), au.AddYears(-1));
        var lignesN = Lignes(du, au).ToList();
        var lignesN1 = Lignes(duN1, auN1).ToList();
        var cout = lignesN.Sum(x => x.Cout);
        var marge = lignesN.Sum(x => x.MontantHT) - cout;
        var margeN1 = lignesN1.Sum(x => x.MontantHT - x.Cout);
        decimal? taux = cout == 0 ? null : Math.Round(marge / cout * 100, 1);
        var factures = pieces.Where(x => !x.Avoir && x.Date.Date >= du && x.Date.Date <= au).ToList();
        var premiere = pieces.Where(x => !x.Avoir).GroupBy(x => x.Tiers).ToDictionary(g => g.Key, g => g.Min(x => x.Date));
        var actifs = factures.Select(x => x.Tiers).Distinct().ToList();

        var precedent = i.Precedent(e);
        var points = new List<PointMensuel>();
        for (var m = Periodes.DebutMois(e.Debut); m <= Periodes.DebutMois(e.Fin); m = m.AddMonths(1))
        {
            var mN1 = Periodes.DebutMois(precedent.Debut).AddMonths(points.Count);
            var aDesN1 = mN1 >= Periodes.DebutMois(i.Depuis);
            var duMois = lignes.Where(x => x.Mois == m).ToList();
            var duMoisN1 = lignes.Where(x => x.Mois == mN1).ToList();
            points.Add(new PointMensuel(Periodes.Mois(m), duMois.Sum(x => x.MontantHT), duMois.Sum(x => x.MontantHT - x.Cout),
                aDesN1 ? duMoisN1.Sum(x => x.MontantHT) : null, aDesN1 ? duMoisN1.Sum(x => x.MontantHT - x.Cout) : null,
                objectifs.TryGetValue(Periodes.Mois(m), out var o) ? o : null));
        }

        List<Classement> Top(Func<FaitLigne, string?> cle, Func<string, string?> intitule, int nombre)
        {
            var n1 = lignesN1.GroupBy(x => cle(x) ?? "").ToDictionary(g => g.Key, g => g.Sum(x => x.MontantHT));
            return lignesN.GroupBy(x => cle(x) ?? "")
                .Select(g => (g.Key, Ca: g.Sum(x => x.MontantHT), Cout: g.Sum(x => x.Cout), Quantite: g.Sum(x => x.Quantite)))
                .OrderByDescending(x => x.Ca).Take(nombre)
                .Select(x => new Classement(x.Key, x.Key == "" ? "(non renseigné)" : intitule(x.Key), x.Ca, x.Ca - x.Cout,
                    x.Cout == 0 ? null : Math.Round((x.Ca - x.Cout) / x.Cout * 100, 1), x.Quantite, n1.GetValueOrDefault(x.Key)))
                .ToList();
        }

        var douzeMois = aujourdhui.Date.AddYears(-1);
        var aRelancer = pieces.Where(x => !x.Avoir && x.Date >= douzeMois).GroupBy(x => x.Tiers)
            .Select(g => new ClientARelancer(g.Key, i.IntituleTiers(g.Key), g.Max(x => x.Date), (aujourdhui.Date - g.Max(x => x.Date).Date).Days, g.Sum(x => x.MontantHT)))
            .Where(c => c.Jours >= seuils.JoursSansCommande).OrderByDescending(c => c.CaDouzeMois).Take(20).ToList();

        var enCours = i.EnCours.Where(x => Retenue(x.Tiers, x.Commercial)).ToList();
        object EnCours(int type) => new { nombre = enCours.Count(x => x.Type == type), montant = enCours.Where(x => x.Type == type).Sum(x => x.MontantHT) };
        var bcEnRetard = enCours.Where(x => x.Type == 1 && x.DateLivraison is { } d && d.Date < aujourdhui.Date).ToList();

        var objectifMois = objectifs.TryGetValue(Periodes.Mois(au), out var om) ? om : (decimal?)null;
        var objectifExercice = points.Where(x => string.CompareOrdinal(x.Mois, Periodes.Mois(au)) <= 0).Sum(x => x.Objectif ?? 0);
        var stock = Stocks.Totaux(i, null, null, aujourdhui, seuils);
        var creances = Comptabilite.Tiers(i, 0, aujourdhui, p);

        var alertes = new List<Alerte>();
        if (Periodes.Variation(caMois, caMoisN1) is { } v && v < -seuils.BaisseCaPourcent)
            alertes.Add(new("critique", "baisseCa", Periodes.Texte($"CA du mois en baisse de {-v:N1} % par rapport au même mois N-1.")));
        if (taux is { } t && t < seuils.TauxMargeMini)
            alertes.Add(new("attention", "marge", Periodes.Texte($"Taux de marge de {t:N1} %, sous le seuil de {seuils.TauxMargeMini:N0} %.")));
        if (objectifMois is > 0 && caMois < objectifMois * (decimal)aujourdhui.Day / DateTime.DaysInMonth(aujourdhui.Year, aujourdhui.Month)
            && Periodes.Mois(au) == Periodes.Mois(aujourdhui))
            alertes.Add(new("attention", "objectif", Periodes.Texte($"Objectif du mois en retard : {caMois:N2} réalisés sur {objectifMois:N2}.")));
        if (aRelancer.Count > 0)
            alertes.Add(new("info", "relance", Periodes.Texte($"{aRelancer.Count} client(s) habituel(s) sans facture depuis {seuils.JoursSansCommande} jours.")));
        if (stock.Ruptures > 0)
            alertes.Add(new("attention", "rupture", Periodes.Texte($"{stock.Ruptures} article(s) en rupture, {stock.SousMini} sous le stock minimum.")));
        if (creances.Plus90 > 0)
            alertes.Add(new("attention", "creances90", Periodes.Texte($"Créances clients de plus de 90 jours : {creances.Plus90:N2}.")));
        if (bcEnRetard.Count > 0)
            alertes.Add(new("info", "bcRetard", Periodes.Texte($"{bcEnRetard.Count} commande(s) client dont la date de livraison est dépassée.")));

        return new
        {
            exercice = Periodes.CleExercice(e),
            du, au, duN1, auN1,
            caJour = Ca(aujourdhui, aujourdhui),
            caMois, caMoisN1, variationMois = Periodes.Variation(caMois, caMoisN1),
            caExercice, caN1, variation = Periodes.Variation(caExercice, caN1),
            marge, margeN1, taux,
            factures = factures.Count,
            panierMoyen = factures.Count == 0 ? 0 : Math.Round(caExercice / factures.Count, 2),
            quantites = lignesN.Sum(x => x.Quantite),
            clientsActifs = actifs.Count,
            nouveauxClients = actifs.Count(c => premiere.TryGetValue(c, out var d) && d >= du && d.Date <= au),
            objectifMois, objectifExercice = objectifExercice == 0 ? (decimal?)null : objectifExercice,
            atteinteMois = objectifMois is > 0 ? Math.Round(caMois / objectifMois.Value * 100, 1) : (decimal?)null,
            atteinteExercice = objectifExercice > 0 ? Math.Round(caExercice / objectifExercice * 100, 1) : (decimal?)null,
            mois = points,
            topClients = Top(x => x.Tiers, k => i.IntituleTiers(k), 10),
            topArticles = Top(x => x.Article, k => i.Articles.TryGetValue(k, out var a) ? a.Designation?.Trim() : null, 10),
            familles = Top(x => i.Famille(x.Article), k => i.Familles.GetValueOrDefault(k), 8),
            commerciaux = p.Restreint ? [] : Top(x => (x.Commercial ?? i.Representant(x.Tiers))?.ToString(),
                k => int.TryParse(k, out var c) ? i.Collaborateurs.GetValueOrDefault(c) : k, 10),
            aRelancer,
            devis = EnCours(0), commandes = EnCours(1), preparations = EnCours(2), livraisons = EnCours(3),
            commandesEnRetard = new { nombre = bcEnRetard.Count, montant = bcEnRetard.Sum(x => x.MontantHT) },
            transformation = Transformation.Synthese(i.Commandes.Where(x => x.DateCommande.Date >= du.Date && x.DateCommande.Date <= au.Date && Retenue(x.Tiers, x.Commercial))),
            stock,
            creancesClients = creances,
            alertes,
        };
    }

    /// <summary>Devis, commandes, préparations et bons de livraison non clôturés, avec les commandes en retard de livraison.</summary>
    public static object Commandes(Instantane i, Perimetre p, int? commercial, DateTime aujourdhui)
    {
        var liste = i.EnCours.Where(x => p.Autorise(i, x.Tiers, x.Commercial) && (commercial == null || (x.Commercial ?? i.Representant(x.Tiers)) == commercial))
            .ToList();
        string[] noms = ["Devis", "Bons de commande", "Préparations de livraison", "Bons de livraison"];
        return new
        {
            types = Enumerable.Range(0, 4).Select(t => new { type = t, intitule = noms[t], nombre = liste.Count(x => x.Type == t), montant = liste.Where(x => x.Type == t).Sum(x => x.MontantHT) }),
            pieces = liste.OrderBy(x => x.Type).ThenBy(x => x.DateLivraison ?? x.Date).Take(1000).Select(x => new
            {
                x.Type, x.Piece, x.Date, x.DateLivraison, x.Tiers, intitule = i.IntituleTiers(x.Tiers), x.MontantHT,
                commercial = (x.Commercial ?? i.Representant(x.Tiers)) is { } c ? i.Collaborateurs.GetValueOrDefault(c) : null,
                enRetard = x.Type == 1 && x.DateLivraison is { } d && d.Date < aujourdhui.Date,
                ageJours = (aujourdhui.Date - x.Date.Date).Days,
            }),
        };
    }
}
