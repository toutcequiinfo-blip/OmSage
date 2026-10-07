namespace Sage100Api.TableauDeBord;

/// <summary>
/// Recouvrement : balance âgée des clients (type 0) ou des fournisseurs (type 1) à partir des écritures de tiers non lettrées.
/// Tranches du cahier des charges : non échu, 1-30, 31-60, 61-90, plus de 90 jours de retard.
/// </summary>
public static class Creances
{
    public sealed record Tranches(decimal NonEchu, decimal R1a30, decimal R31a60, decimal R61a90, decimal Plus90, decimal Credits, decimal Total)
    {
        public decimal Echu => R1a30 + R31a60 + R61a90 + Plus90;
    }

    public sealed record LigneTiers(string Tiers, string? Intitule, string? Representant, string? Categorie, Tranches Tranches, int RetardMaxJours,
        DateTime? DerniereRelance, DateTime? DerniereFacture, int Pieces);

    public sealed record LigneGroupe(string Cle, string? Intitule, Tranches Tranches, int Tiers);

    public sealed record PieceDue(DateTime Date, string? Journal, string? Piece, string? Libelle, DateTime? Echeance, decimal Montant, int JoursRetard,
        DateTime? DerniereRelance);

    public static int Retard(EcheanceTiers e, DateTime aujourdhui) =>
        e.Montant > 0 && e.DateEcheance is { } d ? Math.Max(0, (aujourdhui.Date - d.Date).Days) : 0;

    public static Tranches Ventiler(IEnumerable<EcheanceTiers> echeances, DateTime aujourdhui)
    {
        decimal ne = 0, r1 = 0, r31 = 0, r61 = 0, r90 = 0, cr = 0;
        foreach (var e in echeances)
        {
            if (e.Montant <= 0) { cr += e.Montant; continue; }
            switch (Retard(e, aujourdhui))
            {
                case 0: ne += e.Montant; break;
                case <= 30: r1 += e.Montant; break;
                case <= 60: r31 += e.Montant; break;
                case <= 90: r61 += e.Montant; break;
                default: r90 += e.Montant; break;
            }
        }
        return new Tranches(ne, r1, r31, r61, r90, cr, ne + r1 + r31 + r61 + r90 + cr);
    }

    /// <summary>Échéances retenues : type de tiers, périmètre du vendeur, filtres commercial (représentant du tiers), catégorie tarifaire et qualité.</summary>
    public static IEnumerable<EcheanceTiers> Filtrer(Instantane i, int type, Perimetre p, string? commercial, string? categorie, string? qualite = null) =>
        Filtrer(i, type, p, Selection.Lire(commercial), Selection.Lire(categorie), Selection.Lire(qualite));

    static IEnumerable<EcheanceTiers> Filtrer(Instantane i, int type, Perimetre p, Selection? commercial, Selection? categorie, Selection? qualite) =>
        i.Echeances.Where(e => e.Type == type
            && (type != 0 || p.Autorise(i, e.Tiers))
            && Retient(i, e.Tiers, commercial, categorie, qualite));

    /// <summary>Tiers retenu par les choix multiples commercial (représentant), catégorie tarifaire et qualité.</summary>
    static bool Retient(Instantane i, string? tiers, Selection? commercial, Selection? categorie, Selection? qualite)
    {
        if (commercial == null && categorie == null && qualite == null) return true;
        var t = tiers != null && i.Tiers.TryGetValue(tiers, out var x) ? x : null;
        return (commercial?.Retient(t?.Representant) ?? true) && (categorie?.Retient(t?.Categorie) ?? true) && (qualite?.Retient(t?.Qualite) ?? true);
    }

    public static object Analyse(Instantane i, int type, Perimetre p, string? commercialTexte, string? categorieTexte, DateTime aujourdhui, string? qualiteTexte = null)
    {
        Selection? commercial = Selection.Lire(commercialTexte), categorie = Selection.Lire(categorieTexte), qualite = Selection.Lire(qualiteTexte);
        var echeances = Filtrer(i, type, p, commercial, categorie, qualite).ToList();
        var total = Ventiler(echeances, aujourdhui);
        var derniereFacture = i.Pieces.Where(x => x.Domaine == type && x.Facture && !x.Avoir).GroupBy(x => x.Tiers)
            .ToDictionary(g => g.Key, g => g.Max(x => x.Date), StringComparer.OrdinalIgnoreCase);

        var parTiers = echeances.GroupBy(e => e.Tiers).Select(g =>
        {
            i.Tiers.TryGetValue(g.Key, out var t);
            return new LigneTiers(g.Key, t?.Intitule?.Trim(),
                t?.Representant is { } r ? i.Collaborateurs.GetValueOrDefault(r) : null,
                t?.Categorie is { } c ? i.Categories.GetValueOrDefault(c) : null,
                Ventiler(g, aujourdhui), g.Max(e => Retard(e, aujourdhui)), g.Max(e => e.DateRelance),
                derniereFacture.TryGetValue(g.Key, out var d) ? d : null, g.Count(e => e.Montant > 0));
        }).Where(l => l.Tranches.Total != 0)
          .OrderByDescending(l => l.Tranches.Echu).ThenByDescending(l => l.Tranches.Total).ToList();

        List<LigneGroupe> Grouper(Func<EcheanceTiers, int?> cle, Func<int, string?> intitule) => echeances.GroupBy(cle)
            .Select(g => new LigneGroupe(g.Key?.ToString() ?? "", g.Key is { } k ? intitule(k) ?? k.ToString() : "(non renseigné)",
                Ventiler(g, aujourdhui), g.Select(e => e.Tiers).Distinct().Count()))
            .Where(l => l.Tranches.Total != 0).OrderByDescending(l => l.Tranches.Echu).ToList();

        var echues = echeances.Where(e => Retard(e, aujourdhui) > 0).ToList();
        var montantEchu = echues.Sum(e => e.Montant);
        var retardMoyen = montantEchu == 0 ? 0 : Math.Round(echues.Sum(e => e.Montant * Retard(e, aujourdhui)) / montantEchu, 0);

        // Encaissements (clients) ou décaissements (fournisseurs) des 12 derniers mois, par mode de règlement et par mois.
        var depuis = Periodes.DebutMois(aujourdhui).AddMonths(-11);
        var reglements = i.Reglements.Where(r => r.Type == type && r.Mois >= depuis && (type != 0 || p.Autorise(i, r.Tiers))
            && Retient(i, r.Tiers, commercial, categorie, qualite)).ToList();

        return new
        {
            type = type == 0 ? "clients" : "fournisseurs",
            total,
            echu = total.Echu,
            retardMoyenJours = retardMoyen,
            tiersEnRetard = parTiers.Count(l => l.Tranches.Echu > 0),
            tiers = parTiers.Take(1000).ToList(),
            parCommercial = type == 0 ? Grouper(e => i.Representant(e.Tiers), k => i.Collaborateurs.GetValueOrDefault(k)) : [],
            parCategorie = type == 0 ? Grouper(e => i.Tiers.TryGetValue(e.Tiers, out var t) ? t.Categorie : null, k => i.Categories.GetValueOrDefault(k)) : [],
            parQualite = type == 0 ? echeances.GroupBy(e => i.Tiers.TryGetValue(e.Tiers, out var t) ? t.Qualite ?? "" : "")
                .Select(g => new LigneGroupe(g.Key, g.Key.Length > 0 ? g.Key : "(non renseignée)", Ventiler(g, aujourdhui), g.Select(e => e.Tiers).Distinct().Count()))
                .Where(l => l.Tranches.Total != 0).OrderByDescending(l => l.Tranches.Echu).ToList() : [],
            reglementsParMode = reglements.GroupBy(r => r.Mode)
                .Select(g => new { mode = g.Key, intitule = i.ModesReglement.GetValueOrDefault(g.Key) ?? $"Mode {g.Key}", montant = g.Sum(r => r.Montant), nombre = g.Sum(r => r.Nombre) })
                .OrderByDescending(x => x.montant).ToList(),
            reglementsParMois = Enumerable.Range(0, 12).Select(k => depuis.AddMonths(k))
                .Select(m => new { mois = Periodes.Mois(m), montant = reglements.Where(r => r.Mois == m).Sum(r => r.Montant) }).ToList(),
        };
    }

    /// <summary>Pièces non soldées d'un tiers, les plus en retard d'abord.</summary>
    public static IReadOnlyList<PieceDue> Pieces(Instantane i, string tiers, DateTime aujourdhui) => i.Echeances
        .Where(e => string.Equals(e.Tiers, tiers, StringComparison.OrdinalIgnoreCase))
        .Select(e => new PieceDue(e.Date, e.Journal, e.Piece, e.Libelle, e.DateEcheance, e.Montant, Retard(e, aujourdhui), e.DateRelance))
        .OrderByDescending(e => e.JoursRetard).ThenBy(e => e.Echeance).ToList();
}
