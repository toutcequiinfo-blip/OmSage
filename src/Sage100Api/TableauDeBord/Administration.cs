using System.Collections.Concurrent;
using System.Globalization;
using Dapper;
using Microsoft.Extensions.Options;

namespace Sage100Api.TableauDeBord;

/// <summary>Onglet du tableau de bord : clé « tableau/onglet », intitulé, et profils qui l'ont par défaut.</summary>
public sealed record Onglet(string Cle, string Tableau, string Intitule, Func<string, bool> ParDefaut);

/// <summary>
/// Les trois tableaux et leurs onglets (même liste que wwwroot/tableau-de-bord/app.js). Par défaut, un profil a un onglet
/// s'il a le tableau (Comptable, Commercial, Production) et la donnée de l'onglet (clients, fournisseurs, achats).
/// </summary>
public static class OngletsTableauDeBord
{
    static bool Compta(string p) => p is Perimetre.Direction or Perimetre.Comptable;
    static bool Commercial(string p) => p is Perimetre.Direction or Perimetre.Commercial or Perimetre.Vendeur;
    static bool Production(string p) => p is Perimetre.Direction or Perimetre.Commercial;
    static bool Fournisseurs(string p) => p is Perimetre.Direction or Perimetre.Comptable;
    static bool Achats(string p) => p is Perimetre.Direction;

    public static readonly IReadOnlyList<Onglet> Tous =
    [
        new("compta/synthese", "Comptable", "Synthèse", Compta),
        new("compta/explorateur", "Comptable", "Explorateur", Compta),
        new("compta/balance", "Comptable", "Balance", Compta),
        new("compta/charges", "Comptable", "Charges & produits", Compta),
        new("compta/tresorerie", "Comptable", "Trésorerie", Compta),
        new("compta/recouvrement", "Comptable", "Recouvrement clients", Compta),
        new("compta/fournisseurs", "Comptable", "Fournisseurs", p => Compta(p) && Fournisseurs(p)),
        new("compta/rapprochement", "Comptable", "Rapprochement", Compta),
        new("commercial/direction", "Commercial", "Direction", Commercial),
        new("commercial/ventes", "Commercial", "Explorateur", Commercial),
        new("commercial/clients", "Commercial", "Clients", Commercial),
        new("commercial/articles", "Commercial", "Articles & marges", Commercial),
        new("commercial/commandes", "Commercial", "Commandes", Commercial),
        new("commercial/transformation", "Commercial", "Satisfaction client", Commercial),
        new("commercial/stock", "Commercial", "Stock", Commercial),
        new("commercial/recouvrement", "Commercial", "Recouvrement", Commercial),
        new("commercial/achats", "Commercial", "Achats", p => Commercial(p) && Achats(p)),
        new("commercial/objectifs", "Commercial", "Objectifs", Commercial),
        new("production/production", "Production", "Production", Production),
        new("production/matieres", "Production", "Matières", Production),
        new("production/appro", "Production", "Approvisionnement", Production),
        new("production/previsions", "Production", "Prévisions", Production),
    ];

    static readonly HashSet<string> Cles = Tous.Select(o => o.Cle).ToHashSet(StringComparer.OrdinalIgnoreCase);
    static readonly ConcurrentDictionary<string, IReadOnlySet<string>> Defauts = new();

    public static IReadOnlySet<string> ParDefaut(string profil) =>
        Defauts.GetOrAdd(profil, p => Tous.Where(o => o.ParDefaut(p)).Select(o => o.Cle).ToHashSet(StringComparer.OrdinalIgnoreCase));

    /// <summary>Liste « a,b,c » -> onglets connus, sans doublon.</summary>
    public static IReadOnlySet<string> Lire(string? liste) =>
        (liste ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Cles.Contains).Select(c => Tous.First(o => o.Cle.Equals(c, StringComparison.OrdinalIgnoreCase)).Cle)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static string Ecrire(IEnumerable<string>? onglets) => string.Join(",", Lire(string.Join(",", onglets ?? [])).Order(StringComparer.Ordinal));
}

/// <summary>Réglage d'un utilisateur : profil imposé, onglets propres (sinon ceux du profil), administrateur du tableau de bord.</summary>
public sealed record DroitUtilisateur(string Login, string? Profil, IReadOnlySet<string>? Onglets, bool Administrateur);

/// <summary>
/// Droits d'accès aux onglets, propres à chaque société et gardés dans la base des extensions de l'API :
/// onglets de chaque profil, et pour un utilisateur, un profil imposé et ses propres onglets.
/// Seuls les administrateurs (administrateur Sage ou coché ici) les modifient.
/// </summary>
public sealed class DroitsTableauDeBord
{
    readonly BaseLocale _base;
    readonly Dossiers _dossiers;
    readonly ConcurrentDictionary<string, Droits> _cache = new(StringComparer.OrdinalIgnoreCase);

    sealed record Droits(Dictionary<string, IReadOnlySet<string>> Profils, Dictionary<string, DroitUtilisateur> Utilisateurs);

    public DroitsTableauDeBord(IOptions<SageOptions> options, Dossiers dossiers)
    {
        _dossiers = dossiers;
        _base = new BaseLocale(dossiers, options.Value, "sage100api-extensions.db", c =>
        {
            c.Execute("""
                CREATE TABLE IF NOT EXISTS droits_profils (
                  profil TEXT NOT NULL PRIMARY KEY,
                  onglets TEXT NOT NULL,
                  utilisateur TEXT NULL,
                  maj_le TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS droits_utilisateurs (
                  login TEXT NOT NULL PRIMARY KEY COLLATE NOCASE,
                  profil TEXT NULL,
                  onglets TEXT NULL,
                  administrateur INTEGER NOT NULL DEFAULT 0,
                  utilisateur TEXT NULL,
                  maj_le TEXT NOT NULL
                );
                """);
        });
    }

    Droits Lire() => _cache.GetOrAdd(_dossiers.Code, _ =>
    {
        using var c = _base.Ouvrir();
        var profils = c.Query<(string Profil, string Onglets)>("SELECT profil, onglets FROM droits_profils")
            .ToDictionary(x => x.Profil, x => OngletsTableauDeBord.Lire(x.Onglets), StringComparer.OrdinalIgnoreCase);
        var utilisateurs = c.Query<(string Login, string? Profil, string? Onglets, long Administrateur)>(
                "SELECT login, profil, onglets, administrateur FROM droits_utilisateurs")
            .ToDictionary(x => x.Login, x => new DroitUtilisateur(x.Login, x.Profil, x.Onglets == null ? null : OngletsTableauDeBord.Lire(x.Onglets), x.Administrateur != 0),
                StringComparer.OrdinalIgnoreCase);
        return new Droits(profils, utilisateurs);
    });

    /// <summary>Périmètre final : profil imposé, onglets de l'utilisateur ou de son profil, droit d'administrer.</summary>
    public Perimetre Appliquer(Perimetre p, Utilisateur? u, bool authentificationActive)
    {
        if (u == null) return p with { Administre = !authentificationActive };
        var d = Lire();
        var reglage = d.Utilisateurs.GetValueOrDefault(u.Login);
        var profil = Normaliser(reglage?.Profil) ?? p.Profil;
        var onglets = reglage?.Onglets ?? d.Profils.GetValueOrDefault(profil);
        if (profil == Perimetre.Aucun && reglage?.Onglets == null) onglets = null;
        return p with { Profil = profil, Permis = onglets, Administre = u.Administrateur || reglage?.Administrateur == true };
    }

    /// <summary>Profil reconnu (Direction, Comptable, Commercial, Vendeur, Aucun), sinon null (= profil automatique).</summary>
    public static string? Normaliser(string? profil) =>
        Perimetre.Profils.Append(Perimetre.Aucun).FirstOrDefault(x => x.Equals(profil?.Trim(), StringComparison.OrdinalIgnoreCase));

    public object Etat() => Etat(Lire());

    static object Etat(Droits d) => new
    {
        tableaux = OngletsTableauDeBord.Tous.GroupBy(o => o.Tableau).Select(g => new { intitule = g.Key, onglets = g.Select(o => new { cle = o.Cle, o.Intitule }) }),
        profils = Perimetre.Profils.Select(p => new
        {
            profil = p,
            onglets = (d.Profils.GetValueOrDefault(p) ?? OngletsTableauDeBord.ParDefaut(p)).Order(),
            parDefaut = OngletsTableauDeBord.ParDefaut(p).Order(),
            modifie = d.Profils.ContainsKey(p),
        }),
        utilisateurs = d.Utilisateurs.Values.OrderBy(u => u.Login, StringComparer.OrdinalIgnoreCase)
            .Select(u => new { login = u.Login, profil = u.Profil, onglets = u.Onglets?.Order(), administrateur = u.Administrateur }),
    };

    /// <summary>Onglets d'un profil ; null = revenir aux onglets par défaut.</summary>
    public void EnregistrerProfil(string profil, IEnumerable<string>? onglets, string? auteur)
    {
        using (var c = _base.Ouvrir())
        {
            if (onglets == null) c.Execute("DELETE FROM droits_profils WHERE profil = @profil", new { profil });
            else
                c.Execute("INSERT INTO droits_profils (profil, onglets, utilisateur, maj_le) VALUES (@profil, @onglets, @auteur, @maj) " +
                    "ON CONFLICT (profil) DO UPDATE SET onglets = excluded.onglets, utilisateur = excluded.utilisateur, maj_le = excluded.maj_le",
                    new { profil, onglets = OngletsTableauDeBord.Ecrire(onglets), auteur, maj = Maintenant() });
        }
        _cache.TryRemove(_dossiers.Code, out _);
    }

    /// <summary>Réglage d'un utilisateur ; sans profil, sans onglets et non administrateur, la ligne est retirée.</summary>
    public void EnregistrerUtilisateur(string login, string? profil, IEnumerable<string>? onglets, bool administrateur, string? auteur)
    {
        using (var c = _base.Ouvrir())
        {
            if (profil == null && onglets == null && !administrateur) c.Execute("DELETE FROM droits_utilisateurs WHERE login = @login", new { login });
            else
                c.Execute("INSERT INTO droits_utilisateurs (login, profil, onglets, administrateur, utilisateur, maj_le) " +
                    "VALUES (@login, @profil, @onglets, @administrateur, @auteur, @maj) ON CONFLICT (login) DO UPDATE SET profil = excluded.profil, " +
                    "onglets = excluded.onglets, administrateur = excluded.administrateur, utilisateur = excluded.utilisateur, maj_le = excluded.maj_le",
                    new { login, profil, onglets = onglets == null ? null : OngletsTableauDeBord.Ecrire(onglets), administrateur = administrateur ? 1 : 0, auteur, maj = Maintenant() });
        }
        _cache.TryRemove(_dossiers.Code, out _);
    }

    static string Maintenant() => DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
}

public sealed record DroitsProfilRequete(string Profil, List<string>? Onglets);
public sealed record DroitsUtilisateurRequete(string Login, string? Profil, List<string>? Onglets, bool Administrateur);
