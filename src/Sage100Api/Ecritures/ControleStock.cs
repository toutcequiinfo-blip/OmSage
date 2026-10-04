using Microsoft.Extensions.Options;
using Sage100Api.Contracts;
using Sage100Api.Lectures;

namespace Sage100Api.Ecritures;

/// <summary>
/// Contrôle de disponibilité avant création d'une commande. La saisie Sage l'applique par sa fenêtre
/// « Indisponibilité en stock » ; les Objets Métiers créent la pièce sans contrôle, donc l'API le fait ici.
/// </summary>
public sealed class ControleStock(ILecturesSage lectures, ILecturesTarifs tarifs, IOptions<SageOptions> options)
{
    public async Task<bool> Actif() => options.Value.ControleStock.Trim().ToLowerInvariant() switch
    {
        "bloquer" => true,
        "aucun" => false,
        _ => !await lectures.StockNegatifAutorise(),
    };

    /// <summary>Quantité en unités de vente : un carton de 12 compte 12.</summary>
    public static decimal QuantiteUnites(LigneCommande l) =>
        (decimal)l.Quantite * (string.IsNullOrEmpty(l.Conditionnement) || !(l.QuantiteConditionnement > 0) ? 1m : (decimal)l.QuantiteConditionnement!.Value);

    /// <summary>
    /// Message d'erreur si une ligne dépasse le stock disponible, sinon null. Avec un dépôt choisi, le stock de ce dépôt ;
    /// sinon le stock de tous les dépôts.
    /// </summary>
    public async Task<string?> Verifier(CommandeRequest c)
    {
        if (!await Actif()) return null;
        var manques = new List<string>();
        var dans = c.Depot is int d ? $" dans le dépôt {d}" : "";
        // Plusieurs lignes du même article (gammes, conditionnements) : le contrôle porte sur le total de l'article.
        foreach (var groupe in c.Lignes.GroupBy(l => l.Article.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            var article = await lectures.Article(groupe.Key);
            if (article is null || !article.SuiviStock) continue; // article inconnu : le worker renverra 404
            var parDepot = c.Depot is int depot ? (await tarifs.StocksDepots(article.Reference)).Where(s => s.Depot == depot).ToList() : null;
            var disponible = parDepot is null ? article.StockDisponible : parDepot.Where(s => s.Gamme1 is null).Sum(s => s.StockDisponible);
            var demande = groupe.Sum(QuantiteUnites);
            if (demande > disponible)
            {
                manques.Add($"{article.Designation} ({article.Reference}) : {Format(disponible)} disponible(s){dans}, {Format(demande)} demandé(s)");
                continue;
            }
            // Article à gamme : chaque valeur (taille, couleur...) a son propre stock.
            if (!groupe.Any(l => !string.IsNullOrEmpty(l.Gamme1))) continue;
            var enumeres = (await lectures.Gammes(article.Reference)).Select(e => parDepot is null ? e : e with
            {
                Stock = parDepot.Where(s => s.Gamme1 != null && Egal(s.Gamme1, e.Gamme1.Trim().ToUpperInvariant()) && Egal(s.Gamme2 ?? "", (e.Gamme2 ?? "").Trim().ToUpperInvariant())).Sum(s => s.Stock),
                StockReserve = parDepot.Where(s => s.Gamme1 != null && Egal(s.Gamme1, e.Gamme1.Trim().ToUpperInvariant()) && Egal(s.Gamme2 ?? "", (e.Gamme2 ?? "").Trim().ToUpperInvariant())).Sum(s => s.StockReserve),
            }).ToList();
            foreach (var valeur in groupe.Where(l => !string.IsNullOrEmpty(l.Gamme1))
                         .GroupBy(l => (G1: l.Gamme1!.Trim().ToUpperInvariant(), G2: (l.Gamme2 ?? "").Trim().ToUpperInvariant())))
            {
                var e = enumeres.FirstOrDefault(x => Egal(x.Gamme1, valeur.Key.G1) && Egal(x.Gamme2 ?? "", valeur.Key.G2));
                if (e is null) continue; // valeur inconnue : le worker renverra la liste des valeurs possibles
                var demandeValeur = (decimal)valeur.Sum(l => l.Quantite);
                if (demandeValeur > e.StockDisponible)
                    manques.Add($"{article.Designation} ({article.Reference}) {LibelleGamme(e)} : {Format(e.StockDisponible)} disponible(s){dans}, {Format(demandeValeur)} demandé(s)");
            }
        }
        return manques.Count == 0 ? null : "Stock insuffisant. " + string.Join(" ; ", manques) + ".";
    }

    static bool Egal(string a, string b) => string.Equals(a.Trim(), b, StringComparison.OrdinalIgnoreCase);

    static string LibelleGamme(EnumereGamme e) => string.IsNullOrEmpty(e.Gamme2) ? e.Gamme1 : $"{e.Gamme1} / {e.Gamme2}";

    static string Format(decimal v) => v.ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("fr-FR"));
}
