using Microsoft.Extensions.Options;
using Sage100Api.Contracts;
using Sage100Api.Lectures;

namespace Sage100Api.Ecritures;

/// <summary>
/// Contrôle de disponibilité avant création d'une commande. La saisie Sage l'applique par sa fenêtre
/// « Indisponibilité en stock » ; les Objets Métiers créent la pièce sans contrôle, donc l'API le fait ici.
/// </summary>
public sealed class ControleStock(ILecturesSage lectures, IOptions<SageOptions> options)
{
    public async Task<bool> Actif() => options.Value.ControleStock.Trim().ToLowerInvariant() switch
    {
        "bloquer" => true,
        "aucun" => false,
        _ => !await lectures.StockNegatifAutorise(),
    };

    /// <summary>Message d'erreur si une ligne dépasse le stock disponible, sinon null.</summary>
    public async Task<string?> Verifier(CommandeRequest c)
    {
        if (!await Actif()) return null;
        var manques = new List<string>();
        // Plusieurs lignes du même article (gammes différentes) : le contrôle porte sur le total de l'article.
        foreach (var groupe in c.Lignes.GroupBy(l => l.Article.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            var article = await lectures.Article(groupe.Key);
            if (article is null || !article.SuiviStock) continue; // article inconnu : le worker renverra 404
            var demande = (decimal)groupe.Sum(l => l.Quantite);
            if (demande > article.StockDisponible)
                manques.Add($"{article.Designation} ({article.Reference}) : {Format(article.StockDisponible)} disponible(s), {Format(demande)} demandé(s)");
        }
        return manques.Count == 0 ? null : "Stock insuffisant. " + string.Join(" ; ", manques) + ".";
    }

    static string Format(decimal v) => v.ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("fr-FR"));
}
