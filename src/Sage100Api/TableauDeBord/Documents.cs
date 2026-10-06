using System.Collections.Concurrent;
using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Sage100Api.TableauDeBord;

/// <summary>
/// Documents comptés dans le CA, les quantités et les marges du tableau commercial (et dans les achats).
/// Les factures (et factures comptabilisées) le sont toujours ; les bons de livraison, bons de retour et bons d'avoir financier
/// font déjà bouger le stock et peuvent être ajoutés. Pas de double compte : en transformant un bon en facture,
/// Sage change le type de la pièce, le bon n'existe plus.
/// </summary>
public sealed record DocumentsCa(bool Livraisons = false, bool Retours = false, bool AvoirsFinanciers = false)
{
    public static DocumentsCa Factures { get; } = new();

    /// <summary>Type de pièce (DO_Type) retenu ? Ventes 3 à 7, achats 13 à 17 : même nature au chiffre des unités près.</summary>
    public bool Retient(int type) => (type % 10) switch
    {
        6 or 7 => true,
        3 => Livraisons,
        4 => Retours,
        5 => AvoirsFinanciers,
        _ => false,
    };

    /// <summary>Types de pièces à lire dans Sage pour un domaine (0 ventes, 1 achats).</summary>
    public IReadOnlyList<int> Types(int domaine) =>
        Enumerable.Range(3, 5).Where(Retient).Select(t => domaine == 1 ? t + 10 : t).ToList();

    /// <summary>Retour et avoir financier : en négatif, comme les factures de retour et d'avoir.</summary>
    public static bool Negatif(int type) => type % 10 is 4 or 5;
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
        return new DocumentsCa(v.GetValueOrDefault("livraisons"), v.GetValueOrDefault("retours"), v.GetValueOrDefault("avoirsFinanciers"));
    });

    public void Enregistrer(DocumentsCa d, string? utilisateur)
    {
        using (var c = _base.Ouvrir())
        using (var t = c.BeginTransaction())
        {
            var maj = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            foreach (var (cle, oui) in new[] { ("livraisons", d.Livraisons), ("retours", d.Retours), ("avoirsFinanciers", d.AvoirsFinanciers) })
                c.Execute("INSERT INTO reglages_tableau_de_bord (cle, valeur, utilisateur, maj_le) VALUES (@cle, @valeur, @utilisateur, @maj) " +
                    "ON CONFLICT (cle) DO UPDATE SET valeur = excluded.valeur, utilisateur = excluded.utilisateur, maj_le = excluded.maj_le",
                    new { cle, valeur = oui ? "1" : "0", utilisateur, maj }, t);
            t.Commit();
        }
        _cache[_dossiers.Code] = d;
    }
}
