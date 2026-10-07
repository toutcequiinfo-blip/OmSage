namespace Sage100Api.TableauDeBord;

/// <summary>Paramètres du tableau comptable croisé (query string). Mois au format aaaa-mm.</summary>
public sealed record RequeteCompta(string? Source, string? Lignes, string? Colonnes, string? Du, string? Au, string? Comptes, string? Journaux,
    int? TypeJournal, string? Tiers, bool? ANouveaux, int? Plan, string? Sections, string? Tri, int? Limite);

public static class Comptabilite
{
    public static readonly IReadOnlyDictionary<string, string> Classes = new Dictionary<string, string>
    {
        ["1"] = "Capitaux", ["2"] = "Immobilisations", ["3"] = "Stocks", ["4"] = "Tiers", ["5"] = "Financier",
        ["6"] = "Charges", ["7"] = "Produits", ["8"] = "Comptes spéciaux", ["9"] = "Analytique",
    };
    public static readonly string[] TypesJournal = ["Achats", "Ventes", "Trésorerie", "Général", "Situation"];
    public static readonly string[] TypesTiers = ["Client", "Fournisseur", "Salarié", "Autre"];
    public static readonly string[] Mesures = ["debit", "credit", "solde", "soldeCrediteur", "nombre"];
    public static readonly string[] MesuresAnalytiques = ["solde", "soldeCrediteur"];

    /// <summary>Axes du tableau croisé des écritures générales. Code -> axe.</summary>
    public static IReadOnlyDictionary<string, Axe<FaitCompta>> Axes(Instantane i) => new Axe<FaitCompta>[]
    {
        new("exercice", "Exercice", f => Periodes.CleExercice(i.ExerciceDe(f.Mois)), k => Exercice(i, k), true),
        new("annee", "Année", f => Periodes.Annee(f.Mois), null, true),
        new("trimestre", "Trimestre", f => Periodes.Trimestre(f.Mois), null, true),
        new("mois", "Mois", f => Periodes.Mois(f.Mois), Periodes.IntituleMois, true),
        new("semaine", "Semaine", f => Periodes.Semaine(f.Date), Periodes.IntituleSemaine, true),
        new("classe", "Classe", f => f.Compte[..Math.Min(1, f.Compte.Length)], k => Classes.TryGetValue(k, out var c) ? $"{k} {c}" : k, true),
        new("radical", "Compte à 2 chiffres", f => f.Compte[..Math.Min(2, f.Compte.Length)], k => Compte(i, k), true),
        new("compte", "Compte", f => f.Compte, k => Compte(i, k), true),
        new("journal", "Journal", f => f.Journal, k => i.Journaux.TryGetValue(k, out var j) ? $"{k} {j.Intitule?.Trim()}" : k, true),
        new("typeJournal", "Type de journal", f => i.Journaux.TryGetValue(f.Journal, out var j) ? j.Type.ToString() : null, TypeJournal, true),
        new("tiers", "Tiers", f => f.Tiers, k => i.IntituleTiers(k) is { } t ? $"{k} {t}" : k),
        new("typeTiers", "Type de tiers", f => f.Tiers != null && i.Tiers.TryGetValue(f.Tiers, out var t) ? t.Type.ToString() : null, TypeTiers, true),
    }.ToDictionary(a => a.Code);

    /// <summary>Axes du tableau croisé analytique (un plan à la fois : une écriture est ventilée sur chaque plan).</summary>
    public static IReadOnlyDictionary<string, Axe<FaitAnalytique>> AxesAnalytiques(Instantane i, int plan) => new Axe<FaitAnalytique>[]
    {
        new("exercice", "Exercice", f => Periodes.CleExercice(i.ExerciceDe(f.Mois)), k => Exercice(i, k), true),
        new("annee", "Année", f => Periodes.Annee(f.Mois), null, true),
        new("trimestre", "Trimestre", f => Periodes.Trimestre(f.Mois), null, true),
        new("mois", "Mois", f => Periodes.Mois(f.Mois), Periodes.IntituleMois, true),
        new("semaine", "Semaine", f => Periodes.Semaine(f.Date), Periodes.IntituleSemaine, true),
        new("section", "Section analytique", f => f.Section, k => i.Sections.TryGetValue((plan, k), out var s) ? $"{k} {s}" : k, true),
        new("classe", "Classe", f => f.Compte[..Math.Min(1, f.Compte.Length)], k => Classes.TryGetValue(k, out var c) ? $"{k} {c}" : k, true),
        new("radical", "Compte à 2 chiffres", f => f.Compte[..Math.Min(2, f.Compte.Length)], k => Compte(i, k), true),
        new("compte", "Compte", f => f.Compte, k => Compte(i, k), true),
        new("journal", "Journal", f => f.Journal, k => i.Journaux.TryGetValue(k, out var j) ? $"{k} {j.Intitule?.Trim()}" : k, true),
    }.ToDictionary(a => a.Code);

    static string Compte(Instantane i, string k) => i.Comptes.TryGetValue(k, out var c) && !string.IsNullOrWhiteSpace(c.Intitule) ? $"{k} {c.Intitule.Trim()}" : k;
    static string? TypeJournal(string k) => int.TryParse(k, out var t) && t >= 0 && t < TypesJournal.Length ? TypesJournal[t] : k;
    static string? TypeTiers(string k) => int.TryParse(k, out var t) && t >= 0 && t < TypesTiers.Length ? TypesTiers[t] : k;
    static string Exercice(Instantane i, string k) => i.Exercices.FirstOrDefault(e => Periodes.CleExercice(e) == k) is { } e ? Periodes.IntituleExercice(e) : k;

    static decimal Mesure(decimal[] s, string m) => m switch
    {
        "debit" => s[0],
        "credit" => s[1],
        "soldeCrediteur" => s[1] - s[0],
        "nombre" => s[2],
        _ => s[0] - s[1],
    };

    /// <summary>
    /// Écritures générales retenues par les filtres : période, comptes par préfixe, journaux, tiers. Comptes, journaux et tiers
    /// sont des choix multiples (<see cref="Selection"/>) : « 6,7 » garde, « !65 » exclut.
    /// </summary>
    public static IEnumerable<FaitCompta> Filtrer(Instantane i, RequeteCompta r, DateTime aujourdhui)
    {
        var (du, au) = Bornes(i, r.Du, r.Au, aujourdhui);
        var comptes = Selection.Lire(r.Comptes);
        var journaux = Selection.Lire(r.Journaux);
        var tiers = Selection.Lire(r.Tiers);
        var aNouveaux = r.ANouveaux ?? false;
        return i.Compta.Where(f => f.Date >= du && f.Date <= au
            && (aNouveaux || !f.ANouveau)
            && (comptes?.RetientPrefixe(f.Compte) ?? true)
            && (journaux?.Retient(f.Journal) ?? true)
            && (r.TypeJournal == null || (i.Journaux.TryGetValue(f.Journal, out var j) && j.Type == r.TypeJournal))
            && (tiers?.Retient(f.Tiers) ?? true));
    }

    /// <summary>Premier et dernier jour inclus (« 2026-03-15 », ou un mois entier « 2026-03 ») ; par défaut l'exercice du jour.</summary>
    public static (DateTime Du, DateTime Au) Bornes(Instantane i, string? du, string? au, DateTime aujourdhui)
    {
        var e = i.ExerciceDe(aujourdhui);
        return (Periodes.LireJour(du, e.Debut), Periodes.LireJour(au, e.Fin, fin: true));
    }

    public static ResultatCube Croiser(Instantane i, RequeteCompta r, DateTime aujourdhui)
    {
        var limite = Math.Clamp(r.Limite ?? 300, 10, 2000);
        if (r.Source == "analytique")
        {
            var plan = r.Plan ?? i.Plans.Keys.DefaultIfEmpty(1).Min();
            var axes = AxesAnalytiques(i, plan);
            var (du, au) = Bornes(i, r.Du, r.Au, aujourdhui);
            var comptes = Selection.Lire(r.Comptes);
            var sections = Selection.Lire(r.Sections);
            var journaux = Selection.Lire(r.Journaux);
            var faits = i.Analytique.Where(f => f.Plan == plan && f.Date >= du && f.Date <= au
                && (comptes?.RetientPrefixe(f.Compte) ?? true)
                && (sections?.Retient(f.Section) ?? true)
                && (journaux?.Retient(f.Journal) ?? true));
            return Cube.Pivoter(faits, Axe(axes, r.Lignes, "section"), Axe(axes, r.Colonnes, null), 1, f => [f.Montant],
                MesuresAnalytiques, (s, m) => m == "soldeCrediteur" ? -s[0] : s[0], r.Tri ?? "solde", limite);
        }
        var axesG = Axes(i);
        return Cube.Pivoter(Filtrer(i, r, aujourdhui), Axe(axesG, r.Lignes, "classe"), Axe(axesG, r.Colonnes, null), 3,
            f => [f.Debit, f.Credit, f.Nombre], Mesures, Mesure, r.Tri ?? "solde", limite);
    }

    static Axe<T>? Axe<T>(IReadOnlyDictionary<string, Axe<T>> axes, string? code, string? defaut) =>
        code is { Length: > 0 } && axes.TryGetValue(code, out var a) ? a : defaut != null && code == null ? axes[defaut] : null;

    /// <summary>
    /// Filtres SQL du détail d'une cellule : la requête du tableau, restreinte à la valeur de la ligne et de la colonne cliquées.
    /// Null si l'axe ne se traduit pas en filtre d'écritures (type de tiers, « Autres »).
    /// </summary>
    public static FiltreDetailCompta? Detail(Instantane i, RequeteCompta r, string? cleLigne, string? cleColonne, DateTime aujourdhui)
    {
        var (du, au) = Bornes(i, r.Du, r.Au, aujourdhui);
        var selComptes = Selection.Lire(r.Comptes);
        var selJournaux = Selection.Lire(r.Journaux);
        var selTiers = Selection.Lire(r.Tiers);
        var comptes = selComptes is { Exclure: false } ? selComptes.Valeurs.Where(v => v != "").ToList() : null;
        var journaux = selJournaux is { Exclure: false } ? selJournaux.Valeurs.ToList() : null;
        if (r.TypeJournal is { } tj) journaux = Intersection(journaux, i.Journaux.Values.Where(j => j.Type == tj).Select(j => j.Code));
        string? tiers = null;
        List<string>? tiersListe = selTiers is { Exclure: false } ? selTiers.Valeurs.ToList() : null;
        foreach (var (axe, cle) in new[] { (r.Lignes ?? "classe", cleLigne), (r.Colonnes, cleColonne) })
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
                case "classe" or "radical" or "compte":
                    comptes = comptes == null ? [cle] : comptes.Where(c => cle.StartsWith(c) || c.StartsWith(cle)).Select(c => c.Length > cle.Length ? c : cle).ToList();
                    break;
                case "journal":
                    journaux = Intersection(journaux, [cle]);
                    break;
                case "typeJournal" when int.TryParse(cle, out var type):
                    journaux = Intersection(journaux, i.Journaux.Values.Where(j => j.Type == type).Select(j => j.Code));
                    break;
                case "tiers":
                    if (cle == Cube.CleAucun) return null;
                    tiers = cle;
                    if (tiersListe != null && !tiersListe.Contains(cle, StringComparer.OrdinalIgnoreCase)) return null;
                    break;
                default:
                    return null;
            }
        }
        if (comptes is { Count: 0 } || journaux is { Count: 0 } || du > au) return null;
        if (tiersListe is { Count: 1 } && tiers == null) (tiers, tiersListe) = (tiersListe[0], null);
        return new FiltreDetailCompta(du, au, comptes, journaux, tiers, r.ANouveaux ?? false, 500)
        {
            TiersListe = tiers == null ? tiersListe : null,
            ComptesExclus = selComptes is { Exclure: true } ? selComptes.Valeurs.Where(v => v != "").ToList() : null,
            JournauxExclus = selJournaux is { Exclure: true } ? selJournaux.Valeurs.ToList() : null,
            TiersExclus = selTiers is { Exclure: true } ? selTiers.Valeurs.ToList() : null,
        };
    }

    static List<string> Intersection(List<string>? actuels, IEnumerable<string> autres) =>
        actuels == null ? autres.ToList() : actuels.Intersect(autres, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Solde des comptes commençant par ce préfixe à la fin d'un mois (débit - crédit). Si l'exercice a ses à-nouveaux,
    /// le solde part d'eux ; sinon (exercice précédent pas encore clôturé) il cumule tous les mouvements chargés.
    /// </summary>
    public static decimal Solde(Instantane i, string prefixe, DateTime finMois, string? exclure = null)
    {
        var e = i.ExerciceDe(finMois);
        bool Retenu(FaitCompta f) => f.Compte.StartsWith(prefixe) && (exclure == null || !f.Compte.StartsWith(exclure)) && f.Mois <= finMois;
        var avecAN = i.Compta.Any(f => f.ANouveau && f.Mois >= Periodes.DebutMois(e.Debut) && f.Mois <= Periodes.DebutMois(e.Fin) && f.Compte.StartsWith(prefixe));
        return avecAN
            ? i.Compta.Where(f => Retenu(f) && f.Mois >= Periodes.DebutMois(e.Debut)).Sum(f => f.Debit - f.Credit)
            : i.Compta.Where(f => Retenu(f) && !f.ANouveau).Sum(f => f.Debit - f.Credit);
    }

    public sealed record PointMensuel(string Mois, decimal Produits, decimal Charges, decimal Resultat, decimal ResultatCumule, decimal Tresorerie,
        decimal? ProduitsN1, decimal? ChargesN1, decimal? ResultatCumuleN1, decimal? TresorerieN1);
    public sealed record Repartition(string Cle, string? Intitule, decimal Montant);
    public sealed record SoldeCompte(string Compte, string? Intitule, decimal Solde);
    public sealed record TotalTiers(decimal Total, decimal Echu, decimal NonEchu, decimal Plus90);

    /// <summary>Écran Synthèse du tableau comptable, pour un exercice (en cours : du début à aujourd'hui).</summary>
    public static object Synthese(Instantane i, Exercice e, DateTime aujourdhui, SeuilsAlertes seuils, Perimetre p)
    {
        var (du, au) = Periodes.Analyse(e, aujourdhui);
        var (duN1, auN1) = Periodes.Comparable(i, e, au);
        IEnumerable<FaitCompta> Mouvements(DateTime d, DateTime a) =>
            i.Compta.Where(f => !f.ANouveau && f.Mois >= Periodes.DebutMois(d) && f.Mois <= Periodes.DebutMois(a));
        decimal Produits(IEnumerable<FaitCompta> m) => m.Where(f => f.Compte.StartsWith('7')).Sum(f => f.Credit - f.Debit);
        decimal Charges(IEnumerable<FaitCompta> m) => m.Where(f => f.Compte.StartsWith('6')).Sum(f => f.Debit - f.Credit);

        var n = Mouvements(du, au).ToList();
        var n1 = Mouvements(duN1, auN1).ToList();
        var produits = Produits(n);
        var charges = Charges(n);
        var produitsN1 = Produits(n1);
        var chargesN1 = Charges(n1);
        var tresorerie = Solde(i, "5", Periodes.DebutMois(au), "58");
        var tresorerieN1 = Solde(i, "5", Periodes.DebutMois(auN1), "58");
        var tresoreriePresente = i.Compta.Any(f => f.Compte.StartsWith('5'));

        // Mois de l'exercice, mis en regard des mêmes mois de l'exercice précédent.
        var precedent = i.Precedent(e);
        var points = new List<PointMensuel>();
        decimal cumul = 0, cumulN1 = 0;
        for (var m = Periodes.DebutMois(e.Debut); m <= Periodes.DebutMois(e.Fin); m = m.AddMonths(1))
        {
            var mN1 = Periodes.DebutMois(precedent.Debut).AddMonths(points.Count);
            var futur = m > Periodes.DebutMois(au);
            var mois = futur ? [] : n.Where(f => f.Mois == m).ToList();
            var moisN1 = i.Compta.Where(f => !f.ANouveau && f.Mois == mN1).ToList();
            var pr = Produits(mois);
            var ch = Charges(mois);
            cumul += pr - ch;
            cumulN1 += Produits(moisN1) - Charges(moisN1);
            var aDesN1 = mN1 >= Periodes.DebutMois(i.Depuis);
            points.Add(new PointMensuel(Periodes.Mois(m), pr, ch, pr - ch, futur ? 0 : cumul, futur ? 0 : Solde(i, "5", m, "58"),
                aDesN1 ? Produits(moisN1) : null, aDesN1 ? Charges(moisN1) : null, aDesN1 ? cumulN1 : null, aDesN1 ? Solde(i, "5", mN1, "58") : null));
        }

        IEnumerable<Repartition> ParRadical(char classe, bool credit) => n.Where(f => f.Compte.StartsWith(classe))
            .GroupBy(f => f.Compte[..Math.Min(2, f.Compte.Length)])
            .Select(g => new Repartition(g.Key, Compte(i, g.Key), credit ? g.Sum(f => f.Credit - f.Debit) : g.Sum(f => f.Debit - f.Credit)))
            .Where(r => r.Montant != 0).OrderByDescending(r => r.Montant);

        var comptesTresorerie = i.Compta.Where(f => f.Compte.StartsWith('5') && !f.Compte.StartsWith("58")).Select(f => f.Compte).Distinct()
            .Select(c => new SoldeCompte(c, i.Comptes.TryGetValue(c, out var r) ? r.Intitule?.Trim() : null, Solde(i, c, Periodes.DebutMois(au))))
            .Where(s => s.Solde != 0).OrderByDescending(s => s.Solde).ToList();

        var flux = n.Where(f => f.Compte.StartsWith('5') && !f.Compte.StartsWith("58")).ToList();
        var clients = Tiers(i, 0, aujourdhui, p);
        var fournisseurs = p.VoitFournisseurs ? Tiers(i, 1, aujourdhui, p) : null;

        var alertes = new List<Alerte>();
        if (tresoreriePresente && tresorerie < seuils.Tresorerie)
            alertes.Add(new("critique", "tresorerie", Periodes.Texte($"Trésorerie sous le seuil : {tresorerie:N2} (seuil {seuils.Tresorerie:N2}).")));
        if (produits - charges < 0)
            alertes.Add(new("attention", "resultat", Periodes.Texte($"Résultat négatif sur la période : {produits - charges:N2}.")));
        if (clients.Plus90 > 0)
            alertes.Add(new("attention", "creances90", Periodes.Texte($"Créances clients de plus de 90 jours : {clients.Plus90:N2}.")));
        if (fournisseurs is { Echu: > 0 })
            alertes.Add(new("info", "dettesEchues", Periodes.Texte($"Dettes fournisseurs échues : {fournisseurs.Echu:N2}.")));

        return new
        {
            exercice = Periodes.CleExercice(e),
            du, au, duN1, auN1,
            produits, charges, resultat = produits - charges,
            produitsN1, chargesN1, resultatN1 = produitsN1 - chargesN1,
            variationProduits = Periodes.Variation(produits, produitsN1),
            variationCharges = Periodes.Variation(charges, chargesN1),
            tresorerie, tresorerieN1, variationTresorerie = Periodes.Variation(tresorerie, tresorerieN1),
            encaissements = flux.Sum(f => f.Debit),
            decaissements = flux.Sum(f => f.Credit),
            creancesClients = clients,
            dettesFournisseurs = fournisseurs,
            mois = points,
            charges2 = ParRadical('6', false).ToList(),
            produits2 = ParRadical('7', true).ToList(),
            comptesTresorerie,
            alertes,
        };
    }

    /// <summary>Total dû par les clients (type 0) ou aux fournisseurs (type 1), échu, non échu, plus de 90 jours.</summary>
    public static TotalTiers Tiers(Instantane i, int type, DateTime aujourdhui, Perimetre p)
    {
        var e = i.Echeances.Where(x => x.Type == type && (type != 0 || p.Autorise(i, x.Tiers))).ToList();
        var echu = e.Where(x => x.Montant > 0 && x.DateEcheance is { } d && d.Date < aujourdhui.Date).ToList();
        var total = e.Sum(x => x.Montant);
        return new TotalTiers(total, echu.Sum(x => x.Montant), total - echu.Sum(x => x.Montant),
            echu.Where(x => (aujourdhui.Date - x.DateEcheance!.Value.Date).Days > 90).Sum(x => x.Montant));
    }

    public sealed record LigneRapprochement(string Mois, decimal CaGestion, decimal CaComptable, decimal EcartCa, decimal? EcartCaPourcent,
        decimal ReglementsGestion, decimal ReglementsComptables, decimal EcartReglements, decimal AchatsGestion, decimal AchatsComptables, decimal EcartAchats);

    /// <summary>
    /// Contrôle Commercial / Comptabilité par mois : CA HT des factures de vente contre comptes 70 ; règlements clients de la gestion
    /// commerciale contre crédits des comptes clients (41) dans les journaux de trésorerie ; factures d'achat contre comptes 60.
    /// Un écart vient en général de factures pas encore comptabilisées ou d'écritures saisies directement en comptabilité.
    /// </summary>
    public static IReadOnlyList<LigneRapprochement> Rapprochement(Instantane i, Exercice e, DateTime aujourdhui)
    {
        var (du, au) = Periodes.Analyse(e, aujourdhui);
        var liste = new List<LigneRapprochement>();
        for (var m = Periodes.DebutMois(du); m <= Periodes.DebutMois(au); m = m.AddMonths(1))
        {
            var fin = m.AddMonths(1);
            var mois = i.Compta.Where(f => !f.ANouveau && f.Mois == m).ToList();
            var caG = i.Pieces.Where(x => x.Domaine == 0 && x.Facture && x.Date >= m && x.Date < fin).Sum(x => x.MontantHT);
            var caC = mois.Where(f => f.Compte.StartsWith("70")).Sum(f => f.Credit - f.Debit);
            var rgG = i.Reglements.Where(x => x.Type == 0 && x.Mois == m).Sum(x => x.Montant);
            var rgC = mois.Where(f => f.Compte.StartsWith("41") && i.Journaux.TryGetValue(f.Journal, out var j) && j.Type == 2).Sum(f => f.Credit - f.Debit);
            var acG = i.Pieces.Where(x => x.Domaine == 1 && x.Facture && x.Date >= m && x.Date < fin).Sum(x => x.MontantHT);
            var acC = mois.Where(f => f.Compte.StartsWith("60")).Sum(f => f.Debit - f.Credit);
            liste.Add(new LigneRapprochement(Periodes.Mois(m), caG, caC, caG - caC, Periodes.Variation(caG, caC), rgG, rgC, rgG - rgC, acG, acC, acG - acC));
        }
        return liste;
    }
}
