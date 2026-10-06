namespace Sage100Api.TableauDeBord;

/// <summary>
/// Axe d'analyse d'un cube : la clé d'un fait sur cet axe (mois, compte, client...) et son intitulé.
/// TriParCle : lignes rangées par clé (temps, comptes) plutôt que par montant décroissant (clients, articles).
/// </summary>
public sealed record Axe<T>(string Code, string Libelle, Func<T, string?> Cle, Func<string, string?>? Intitule = null, bool TriParCle = false);

public sealed record ColonneCube(string Cle, string? Intitule);

public sealed record LigneCube(string Cle, string? Intitule, IReadOnlyDictionary<string, IReadOnlyDictionary<string, decimal>> Cellules,
    IReadOnlyDictionary<string, decimal> Total);

/// <summary>
/// Tableau croisé : une ligne par valeur de l'axe des lignes, une cellule par valeur de l'axe des colonnes, chaque cellule portant toutes les mesures.
/// LignesMasquees : lignes au-delà de la limite, regroupées dans la dernière ligne « Autres ».
/// </summary>
public sealed record ResultatCube(string? AxeLignes, string? AxeColonnes, IReadOnlyList<string> Mesures, IReadOnlyList<ColonneCube> Colonnes,
    IReadOnlyList<LigneCube> Lignes, LigneCube Total, int LignesMasquees);

public static class Cube
{
    public const string CleAutres = "*autres*";
    public const string CleAucun = "";

    /// <summary>
    /// Croise les faits selon les deux axes. Chaque fait apporte des sommes (débit, crédit... ou CA, coût...),
    /// additionnées par cellule ; les mesures (solde, marge, taux...) sont calculées à partir des sommes de la cellule,
    /// ce qui garde juste un taux de marge sur un total.
    /// </summary>
    public static ResultatCube Pivoter<T>(IEnumerable<T> faits, Axe<T>? lignes, Axe<T>? colonnes, int nombreSommes, Func<T, decimal[]> sommes,
        IReadOnlyList<string> mesures, Func<decimal[], string, decimal> mesure, string triMesure, int limiteLignes = 200, int limiteColonnes = 24)
    {
        var cellules = new Dictionary<string, Dictionary<string, decimal[]>>();
        var parLigne = new Dictionary<string, decimal[]>();
        var parColonne = new Dictionary<string, decimal[]>();
        var total = new decimal[nombreSommes];

        static void Ajouter(decimal[] cible, decimal[] valeurs)
        {
            for (var i = 0; i < valeurs.Length; i++) cible[i] += valeurs[i];
        }
        decimal[] Case<TCle>(Dictionary<TCle, decimal[]> d, TCle cle) where TCle : notnull
        {
            if (!d.TryGetValue(cle, out var v)) d[cle] = v = new decimal[nombreSommes];
            return v;
        }

        foreach (var f in faits)
        {
            var v = sommes(f);
            var l = lignes?.Cle(f) ?? CleAucun;
            var c = colonnes?.Cle(f) ?? CleAucun;
            if (!cellules.TryGetValue(l, out var ligne)) cellules[l] = ligne = new();
            Ajouter(Case(ligne, c), v);
            Ajouter(Case(parLigne, l), v);
            Ajouter(Case(parColonne, c), v);
            Ajouter(total, v);
        }

        Dictionary<string, decimal> Mesures(decimal[] s) => mesures.ToDictionary(m => m, m => Math.Round(mesure(s, m), 2));

        // Colonnes : temps et comptes dans l'ordre, sinon les plus fortes d'abord, le reste dans « Autres ».
        var clesColonnes = Ranger(parColonne, colonnes, triMesure, mesure);
        var regroupees = new HashSet<string>();
        if (colonnes != null && !colonnes.TriParCle && clesColonnes.Count > limiteColonnes)
        {
            foreach (var k in clesColonnes.Skip(limiteColonnes - 1)) regroupees.Add(k);
            clesColonnes = clesColonnes.Take(limiteColonnes - 1).Append(CleAutres).ToList();
        }
        string Colonne(string c) => regroupees.Contains(c) ? CleAutres : c;

        LigneCube Construire(string cle, string? intitule, IEnumerable<string> clesSources)
        {
            var parCellule = new Dictionary<string, decimal[]>();
            var t = new decimal[nombreSommes];
            foreach (var l in clesSources)
            {
                foreach (var (c, v) in cellules[l])
                {
                    Ajouter(Case(parCellule, Colonne(c)), v);
                    Ajouter(t, v);
                }
            }
            return new LigneCube(cle, intitule,
                colonnes == null ? new Dictionary<string, IReadOnlyDictionary<string, decimal>>()
                    : parCellule.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, decimal>)Mesures(kv.Value)),
                Mesures(t));
        }

        var clesLignes = Ranger(parLigne, lignes, triMesure, mesure);
        var masquees = Math.Max(0, clesLignes.Count - limiteLignes);
        var resultat = clesLignes.Take(masquees > 0 ? limiteLignes - 1 : limiteLignes)
            .Select(k => Construire(k, Intitule(lignes, k), [k])).ToList();
        if (masquees > 0)
        {
            var reste = clesLignes.Skip(limiteLignes - 1).ToList();
            resultat.Add(Construire(CleAutres, $"Autres ({reste.Count})", reste));
            masquees = reste.Count;
        }

        var ligneTotal = new LigneCube("*total*", "Total",
            colonnes == null ? new Dictionary<string, IReadOnlyDictionary<string, decimal>>()
                : parColonne.GroupBy(kv => Colonne(kv.Key)).ToDictionary(g => g.Key, g =>
                {
                    var s = new decimal[nombreSommes];
                    foreach (var kv in g) Ajouter(s, kv.Value);
                    return (IReadOnlyDictionary<string, decimal>)Mesures(s);
                }),
            Mesures(total));

        return new ResultatCube(lignes?.Code, colonnes?.Code, mesures,
            colonnes == null ? [] : clesColonnes.Select(k => new ColonneCube(k, k == CleAutres ? "Autres" : Intitule(colonnes, k))).ToList(),
            resultat, ligneTotal, masquees);
    }

    static List<string> Ranger<T>(Dictionary<string, decimal[]> totaux, Axe<T>? axe, string triMesure, Func<decimal[], string, decimal> mesure) =>
        axe == null || axe.TriParCle
            ? totaux.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList()
            : totaux.OrderByDescending(kv => Math.Abs(mesure(kv.Value, triMesure))).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key).ToList();

    static string? Intitule<T>(Axe<T>? axe, string cle) =>
        cle == CleAucun ? "(non renseigné)" : axe?.Intitule?.Invoke(cle) ?? cle;
}
