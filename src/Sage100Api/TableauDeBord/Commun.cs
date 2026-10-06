using System.Globalization;

namespace Sage100Api.TableauDeBord;

/// <summary>
/// Ce que l'utilisateur connecté peut voir. Profils du cahier des charges :
/// Direction (tout), Comptable (comptabilité, trésorerie, créances et dettes), Commercial (ventes, marges, stock, créances clients),
/// Vendeur (comme Commercial, limité à ses clients : ceux dont il est le représentant ou les pièces dont il est le commercial).
/// </summary>
public sealed record Perimetre(string Profil, int? Collaborateur, string? Utilisateur)
{
    public const string Direction = "Direction";
    public const string Comptable = "Comptable";
    public const string Commercial = "Commercial";
    public const string Vendeur = "Vendeur";
    public const string Aucun = "Aucun";
    public static readonly string[] Profils = [Direction, Comptable, Commercial, Vendeur];

    public bool VoitCompta => Profil is Direction or Comptable;
    public bool VoitCommercial => Profil is Direction or Commercial or Vendeur;
    public bool VoitAchats => Profil is Direction;
    public bool VoitClients => Profil is Direction or Comptable or Commercial or Vendeur;
    public bool VoitFournisseurs => Profil is Direction or Comptable;
    public bool ModifieObjectifs => Profil is Direction;
    public bool Restreint => Profil == Vendeur;

    /// <summary>Vendeur : un client ou une pièce lui appartient s'il en est le représentant ou le commercial.</summary>
    public bool Autorise(Instantane i, string? tiers, int? commercial = null) =>
        !Restreint || (Collaborateur is { } co && (commercial == co || i.Representant(tiers) == co));

    /// <summary>Profil d'un utilisateur : ligne de la configuration, sinon administrateur = Direction, vendeur = Vendeur, sinon le profil par défaut.</summary>
    public static Perimetre De(Utilisateur? u, TableauDeBordOptions o, bool authentificationActive)
    {
        if (u == null) return new(authentificationActive ? Aucun : Direction, null, null);
        var profil = o.Profils.TryGetValue(u.Login, out var p) ? p
            : u.Administrateur ? Direction
            : u.Vendeur && u.Collaborateur != null ? Vendeur
            : o.ProfilParDefaut;
        profil = Profils.FirstOrDefault(x => x.Equals(profil?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Aucun;
        return new(profil, u.Collaborateur, u.Login);
    }
}

public static class Periodes
{
    static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>Texte d'alerte avec les nombres à la française (1 234 567), quelle que soit la langue du serveur.</summary>
    public static string Texte(FormattableString f) => f.ToString(Fr);

    public static string Mois(DateTime d) => d.ToString("yyyy-MM", CultureInfo.InvariantCulture);
    public static string Trimestre(DateTime d) => $"{d.Year}-T{(d.Month - 1) / 3 + 1}";
    public static string Annee(DateTime d) => d.Year.ToString(CultureInfo.InvariantCulture);
    public static string IntituleMois(string cle) =>
        DateTime.TryParseExact(cle, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.ToString("MMM yyyy", Fr) : cle;

    /// <summary>« 2026-03 » -> 1er mars 2026 ; null ou illisible -> défaut.</summary>
    public static DateTime LireMois(string? cle, DateTime defaut) =>
        DateTime.TryParseExact(cle, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : defaut;

    public static DateTime DebutMois(DateTime d) => new(d.Year, d.Month, 1);

    public static string CleExercice(Exercice e) => e.Debut.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static string IntituleExercice(Exercice e) => e.Debut.Year == e.Fin.Year ? $"{e.Debut.Year}" : $"{e.Debut.Year}-{e.Fin.Year}";

    /// <summary>Exercice demandé (clé = date de début), sinon celui du jour.</summary>
    public static Exercice Choisir(Instantane i, string? cle, DateTime aujourdhui) =>
        i.Exercices.FirstOrDefault(e => CleExercice(e) == cle) ?? i.ExerciceDe(aujourdhui);

    /// <summary>Période analysée d'un exercice : du début à aujourd'hui pour l'exercice en cours, l'exercice entier sinon.</summary>
    public static (DateTime Du, DateTime Au) Analyse(Exercice e, DateTime aujourdhui) => (e.Debut, aujourdhui < e.Fin ? aujourdhui.Date : e.Fin);

    /// <summary>Même durée, comptée depuis le début de l'exercice précédent.</summary>
    public static (DateTime Du, DateTime Au) Comparable(Instantane i, Exercice e, DateTime au)
    {
        var p = i.Precedent(e);
        var fin = p.Debut.AddDays((au - e.Debut).Days);
        return (p.Debut, fin > p.Fin ? p.Fin : fin);
    }

    public static IReadOnlyList<string>? Liste(string? valeurs) =>
        string.IsNullOrWhiteSpace(valeurs) ? null : valeurs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Variation en % (null si la base est nulle).</summary>
    public static decimal? Variation(decimal n, decimal n1) => n1 == 0 ? null : Math.Round((n - n1) / Math.Abs(n1) * 100, 1);
}

/// <summary>Alerte affichée en tête d'écran. Niveau : critique (rouge), attention (orange), info.</summary>
public sealed record Alerte(string Niveau, string Code, string Message);
