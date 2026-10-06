using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Sage100Api.Extensions;

namespace Sage100Api.TableauDeBord;

/// <summary>
/// Stock par article et dépôt (F_ARTSTOCK) : valeur tenue par Sage, ruptures, minimum, surstock, dormants et couverture.
/// Couverture = stock / ventes moyennes par jour des 12 derniers mois.
/// </summary>
public static class Stocks
{
    static readonly string[] OrdreStatuts = ["rupture", "sousMini", "surstock", "dormant", "ok"];

    public sealed record TotauxStock(decimal Valeur, int Articles, int Ruptures, int SousMini, int Surstocks, int Dormants, decimal ValeurDormante);

    public sealed record LigneStockAnalyse(string Article, string? Designation, string? Famille, int Depot, string? IntituleDepot, decimal Quantite,
        decimal Disponible, decimal Valeur, decimal Mini, decimal Maxi, decimal Commande, DateTime? DerniereSortie, decimal VentesDouzeMois,
        int? CouvertureJours, string Statut);

    static string Statut(LigneStock s, DateTime? derniere, DateTime aujourdhui, SeuilsAlertes seuils) =>
        s.Quantite <= 0 && (s.Mini > 0 || s.Reserve > 0) ? "rupture"
        : s.Mini > 0 && s.Quantite < s.Mini ? "sousMini"
        : s.Maxi > 0 && s.Quantite > s.Maxi ? "surstock"
        : s.Quantite > 0 && (derniere == null || (aujourdhui.Date - derniere.Value.Date).Days >= seuils.JoursDormant) ? "dormant"
        : "ok";

    public static IReadOnlyList<LigneStockAnalyse> Lignes(Instantane i, int? depot, string? famille, DateTime aujourdhui, SeuilsAlertes seuils)
    {
        var douzeMois = Periodes.DebutMois(aujourdhui).AddMonths(-11);
        var ventes = i.Lignes.Where(l => l.Domaine == 0 && l.Mois >= douzeMois).GroupBy(l => (l.Article, l.Depot ?? 0))
            .ToDictionary(g => g.Key, g => g.Sum(l => l.Quantite));
        return i.Stock.Where(s => (depot == null || s.Depot == depot) && (famille == null || i.Famille(s.Article) == famille)
                && (s.Quantite != 0 || s.Valeur != 0 || s.Mini > 0 || s.Reserve != 0))
            .Select(s =>
            {
                i.Articles.TryGetValue(s.Article, out var a);
                DateTime? derniere = i.DernieresSorties.TryGetValue(s.Article, out var d) ? d : null;
                var vendu = ventes.GetValueOrDefault((s.Article, s.Depot));
                int? couverture = vendu > 0 && s.Quantite > 0 ? (int)Math.Min(9999, Math.Round(s.Quantite / (vendu / 365m))) : null;
                return new LigneStockAnalyse(s.Article, a?.Designation?.Trim(), a?.Famille, s.Depot, i.Depots.GetValueOrDefault(s.Depot), s.Quantite,
                    s.Quantite - s.Reserve, s.Valeur, s.Mini, s.Maxi, s.Commande, derniere, vendu, couverture, Statut(s, derniere, aujourdhui, seuils));
            }).ToList();
    }

    public static TotauxStock Totaux(Instantane i, int? depot, string? famille, DateTime aujourdhui, SeuilsAlertes seuils)
    {
        var l = Lignes(i, depot, famille, aujourdhui, seuils);
        return new TotauxStock(l.Sum(x => x.Valeur), l.Select(x => x.Article).Distinct().Count(), l.Count(x => x.Statut == "rupture"),
            l.Count(x => x.Statut == "sousMini"), l.Count(x => x.Statut == "surstock"), l.Count(x => x.Statut == "dormant"),
            l.Where(x => x.Statut == "dormant").Sum(x => x.Valeur));
    }

    public static object Analyse(Instantane i, int? depot, string? famille, string? statut, DateTime aujourdhui, SeuilsAlertes seuils)
    {
        var lignes = Lignes(i, depot, famille, aujourdhui, seuils);
        var totaux = Totaux(i, depot, famille, aujourdhui, seuils);
        // Rotation annuelle : coût des ventes 12 mois / valeur du stock.
        var douzeMois = Periodes.DebutMois(aujourdhui).AddMonths(-11);
        var coutVentes = i.Lignes.Where(l => l.Domaine == 0 && l.Mois >= douzeMois && (depot == null || l.Depot == depot)
            && (famille == null || i.Famille(l.Article) == famille)).Sum(l => l.Cout);
        return new
        {
            totaux,
            rotation = totaux.Valeur > 0 ? Math.Round(coutVentes / totaux.Valeur, 2) : (decimal?)null,
            couvertureJours = coutVentes > 0 ? (int?)Math.Round(totaux.Valeur / (coutVentes / 365m)) : null,
            parDepot = lignes.GroupBy(x => x.Depot).Select(g => new { cle = g.Key.ToString(), intitule = g.First().IntituleDepot, valeur = g.Sum(x => x.Valeur) })
                .OrderByDescending(x => x.valeur).ToList(),
            parFamille = lignes.GroupBy(x => x.Famille ?? "").Select(g => new
            {
                cle = g.Key, intitule = g.Key == "" ? "(sans famille)" : i.Familles.GetValueOrDefault(g.Key) ?? g.Key, valeur = g.Sum(x => x.Valeur),
            }).OrderByDescending(x => x.valeur).ToList(),
            lignes = lignes.Where(x => statut == null || x.Statut == statut).OrderBy(x => Array.IndexOf(OrdreStatuts, x.Statut)).ThenByDescending(x => x.Valeur).Take(1000).ToList(),
        };
    }
}

/// <summary>
/// Objectifs de CA mensuels, globaux (commercial 0) ou par commercial. Sage n'a pas de table pour eux :
/// ils sont gardés dans la base des extensions de l'API (sage100api-extensions.db), comme les activités CRM.
/// </summary>
public sealed class Objectifs
{
    readonly BaseLocale _base;

    public Objectifs(IOptions<SageOptions> options, Dossiers dossiers)
    {
        _base = new BaseLocale(dossiers, options.Value, "sage100api-extensions.db", c =>
        {
            c.Execute("""
                CREATE TABLE IF NOT EXISTS objectifs (
                  mois TEXT NOT NULL,
                  commercial INTEGER NOT NULL,
                  montant REAL NOT NULL,
                  utilisateur TEXT NULL,
                  maj_le TEXT NOT NULL,
                  PRIMARY KEY (mois, commercial)
                );
                """);
        });
    }

    SqliteConnection Ouvrir() => _base.Ouvrir();

    public sealed record Objectif(string Mois, int Commercial, decimal Montant);

    public IReadOnlyList<Objectif> Lire(string? du = null, string? au = null)
    {
        using var c = Ouvrir();
        return c.Query<(string Mois, long Commercial, double Montant)>(
                "SELECT mois, commercial, montant FROM objectifs WHERE (@du IS NULL OR mois >= @du) AND (@au IS NULL OR mois <= @au) ORDER BY mois, commercial",
                new { du, au })
            .Select(x => new Objectif(x.Mois, (int)x.Commercial, (decimal)x.Montant)).ToList();
    }

    /// <summary>Objectif par mois (aaaa-mm) : celui du commercial demandé, ou la somme des objectifs globaux (commercial 0).</summary>
    public IReadOnlyDictionary<string, decimal> ParMois(int? commercial) =>
        Lire().Where(o => o.Commercial == (commercial ?? 0)).ToDictionary(o => o.Mois, o => o.Montant);

    /// <summary>Remplace les objectifs donnés ; montant 0 = suppression.</summary>
    public void Enregistrer(IEnumerable<Objectif> objectifs, string? utilisateur)
    {
        using var c = Ouvrir();
        using var t = c.BeginTransaction();
        foreach (var o in objectifs)
        {
            if (o.Montant <= 0)
                c.Execute("DELETE FROM objectifs WHERE mois = @Mois AND commercial = @Commercial", o, t);
            else
                c.Execute("INSERT INTO objectifs (mois, commercial, montant, utilisateur, maj_le) VALUES (@Mois, @Commercial, @montant, @utilisateur, @maj) " +
                    "ON CONFLICT (mois, commercial) DO UPDATE SET montant = excluded.montant, utilisateur = excluded.utilisateur, maj_le = excluded.maj_le",
                    new { o.Mois, o.Commercial, montant = (double)o.Montant, utilisateur, maj = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) }, t);
        }
        t.Commit();
    }
}
