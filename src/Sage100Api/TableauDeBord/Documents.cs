using System.Collections.Concurrent;
using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Sage100Api.TableauDeBord;

/// <summary>
/// Documents comptés dans le CA, les quantités et les marges du tableau commercial (et dans les achats), cochés dans les réglages.
/// Par défaut : factures, factures de retour et factures d'avoir (et leurs versions comptabilisées). Les bons de livraison,
/// bons de retour et bons d'avoir financier font déjà bouger le stock et peuvent être ajoutés. Pas de double compte : en transformant
/// un bon en facture, Sage change le type de la pièce, le bon n'existe plus.
/// </summary>
public sealed record DocumentsCa(bool Livraisons = false, bool Retours = false, bool AvoirsFinanciers = false,
    bool Factures = true, bool FacturesRetour = true, bool FacturesAvoir = true)
{
    public static DocumentsCa Defaut { get; } = new();

    /// <summary>
    /// Pièce retenue ? Type (DO_Type) : ventes 3 à 7, achats 13 à 17, même nature au chiffre des unités près.
    /// Provenance (DO_Provenance) d'une facture : 1 = facture de retour, 2 = facture d'avoir, autre = facture.
    /// </summary>
    public bool Retient(int type, int provenance = 0) => (type % 10) switch
    {
        6 or 7 => provenance switch { 1 => FacturesRetour, 2 => FacturesAvoir, _ => Factures },
        3 => Livraisons,
        4 => Retours,
        5 => AvoirsFinanciers,
        _ => false,
    };

    /// <summary>Condition SQL (alias e = F_DOCENTETE) des pièces retenues pour un domaine (0 ventes, 1 achats).</summary>
    public string Condition(int domaine)
    {
        var d = domaine == 1 ? 10 : 0;
        var parties = new List<string>();
        var bons = new[] { (3, Livraisons), (4, Retours), (5, AvoirsFinanciers) }.Where(x => x.Item2).Select(x => x.Item1 + d).ToList();
        if (bons.Count > 0) parties.Add($"e.DO_Type IN ({string.Join(", ", bons)})");
        var provenances = new List<string>();
        if (Factures) provenances.Add("e.DO_Provenance NOT IN (1, 2)");
        if (FacturesRetour) provenances.Add("e.DO_Provenance = 1");
        if (FacturesAvoir) provenances.Add("e.DO_Provenance = 2");
        if (provenances.Count == 3) parties.Add($"e.DO_Type IN ({6 + d}, {7 + d})");
        else if (provenances.Count > 0) parties.Add($"(e.DO_Type IN ({6 + d}, {7 + d}) AND ({string.Join(" OR ", provenances)}))");
        return parties.Count == 0 ? "1 = 0" : $"({string.Join(" OR ", parties)})";
    }
}

/// <summary>
/// Réglages du tableau de bord propres à chaque société, gardés dans la base des extensions de l'API (Sage n'a pas de table pour eux).
/// Seule la Direction les modifie ; ils s'appliquent à tous les utilisateurs de la société.
/// </summary>
public sealed class ReglagesTableauDeBord
{
    readonly BaseLocale _base;
    readonly Dossiers _dossiers;
    readonly ConcurrentDictionary<string, DocumentsCa> _cache = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, string> _cout = new(StringComparer.OrdinalIgnoreCase);

    public ReglagesTableauDeBord(IOptions<SageOptions> options, Dossiers dossiers)
    {
        _dossiers = dossiers;
        _base = new BaseLocale(dossiers, options.Value, "sage100api-extensions.db", c =>
        {
            c.Execute("""
                CREATE TABLE IF NOT EXISTS reglages_tableau_de_bord (
                  cle TEXT NOT NULL PRIMARY KEY,
                  valeur TEXT NOT NULL,
                  utilisateur TEXT NULL,
                  maj_le TEXT NOT NULL
                );
                """);
        });
    }

    public DocumentsCa Documents() => _cache.GetOrAdd(_dossiers.Code, _ =>
    {
        using var c = _base.Ouvrir();
        var v = c.Query<(string Cle, string Valeur)>("SELECT cle, valeur FROM reglages_tableau_de_bord").ToDictionary(x => x.Cle, x => x.Valeur == "1");
        bool Lire(string cle, bool defaut) => v.TryGetValue(cle, out var oui) ? oui : defaut;
        var d = DocumentsCa.Defaut;
        return new DocumentsCa(Lire("livraisons", d.Livraisons), Lire("retours", d.Retours), Lire("avoirsFinanciers", d.AvoirsFinanciers),
            Lire("factures", d.Factures), Lire("facturesRetour", d.FacturesRetour), Lire("facturesAvoir", d.FacturesAvoir));
    });

    /// <summary>Méthode de valorisation du tableau Production (<see cref="MethodeCout"/>) : prix de revient par défaut.</summary>
    public string CoutProduction() => _cout.GetOrAdd(_dossiers.Code, _ =>
    {
        using var c = _base.Ouvrir();
        return MethodeCout.Normaliser(c.QueryFirstOrDefault<string>("SELECT valeur FROM reglages_tableau_de_bord WHERE cle = 'coutProduction'"));
    });

    public void EnregistrerCoutProduction(string methode, string? utilisateur)
    {
        var m = MethodeCout.Normaliser(methode);
        using (var c = _base.Ouvrir())
            c.Execute("INSERT INTO reglages_tableau_de_bord (cle, valeur, utilisateur, maj_le) VALUES ('coutProduction', @m, @utilisateur, @maj) " +
                "ON CONFLICT (cle) DO UPDATE SET valeur = excluded.valeur, utilisateur = excluded.utilisateur, maj_le = excluded.maj_le",
                new { m, utilisateur, maj = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) });
        _cout[_dossiers.Code] = m;
    }

    public void Enregistrer(DocumentsCa d, string? utilisateur)
    {
        using (var c = _base.Ouvrir())
        using (var t = c.BeginTransaction())
        {
            var maj = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            foreach (var (cle, oui) in new[] { ("livraisons", d.Livraisons), ("retours", d.Retours), ("avoirsFinanciers", d.AvoirsFinanciers),
                         ("factures", d.Factures), ("facturesRetour", d.FacturesRetour), ("facturesAvoir", d.FacturesAvoir) })
                c.Execute("INSERT INTO reglages_tableau_de_bord (cle, valeur, utilisateur, maj_le) VALUES (@cle, @valeur, @utilisateur, @maj) " +
                    "ON CONFLICT (cle) DO UPDATE SET valeur = excluded.valeur, utilisateur = excluded.utilisateur, maj_le = excluded.maj_le",
                    new { cle, valeur = oui ? "1" : "0", utilisateur, maj }, t);
            t.Commit();
        }
        _cache[_dossiers.Code] = d;
    }
}
