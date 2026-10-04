using Sage100Api.Lectures;

namespace Sage100Api.Extensions;

/// <summary>
/// Balance âgée d'un client. Montants positifs = dû par le client, ventilé selon le retard de chaque échéance ;
/// les crédits non lettrés (avoirs, règlements pas encore affectés) sont à part et viennent en déduction du total.
/// </summary>
public sealed record BalanceClient(string Client, string? Intitule, decimal Total, decimal NonEchu, decimal Retard1a30, decimal Retard31a60,
    decimal Retard61a90, decimal RetardPlus90, decimal Credits, int RetardMaxJours, DateTime? DerniereRelance);

public static class Recouvrement
{
    /// <summary>Jours de retard de chaque échéance due (0 si non échue, sans date d'échéance ou au crédit).</summary>
    public static void CalculerRetards(IEnumerable<Echeance> echeances, DateTime aujourdhui)
    {
        foreach (var e in echeances)
            e.JoursRetard = e.Montant > 0 && e.DateEcheance is { } d ? Math.Max(0, (aujourdhui.Date - d.Date).Days) : 0;
    }

    public static IReadOnlyList<BalanceClient> BalanceAgee(IEnumerable<Echeance> echeances, DateTime aujourdhui)
    {
        var liste = echeances.ToList();
        CalculerRetards(liste, aujourdhui);
        return liste.GroupBy(e => e.Client)
            .Select(g =>
            {
                decimal Somme(Func<Echeance, bool> filtre) => g.Where(e => e.Montant > 0 && filtre(e)).Sum(e => e.Montant);
                return new BalanceClient(g.Key, g.First().Intitule, g.Sum(e => e.Montant),
                    Somme(e => e.JoursRetard == 0),
                    Somme(e => e.JoursRetard is >= 1 and <= 30),
                    Somme(e => e.JoursRetard is >= 31 and <= 60),
                    Somme(e => e.JoursRetard is >= 61 and <= 90),
                    Somme(e => e.JoursRetard > 90),
                    g.Where(e => e.Montant < 0).Sum(e => e.Montant),
                    g.Max(e => e.JoursRetard),
                    g.Max(e => e.DateRelance));
            })
            .Where(b => b.Total != 0)
            .OrderByDescending(b => b.RetardPlus90 + b.Retard61a90 + b.Retard31a60 + b.Retard1a30).ThenByDescending(b => b.Total)
            .ToList();
    }
}
