using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Sage100Api.TableauDeBord;

/// <summary>
/// Routes /api/v1/tableau-de-bord : lecture seule. Elles lisent l'instantané en mémoire ; seul le détail d'une cellule interroge Sage (SQL, SELECT).
/// Chaque route vérifie le profil de l'utilisateur connecté (jeton de POST /api/v1/connexion).
/// </summary>
public static class Routes
{
    public static void MapTableauDeBord(this RouteGroupBuilder v1)
    {
        var g = v1.MapGroup("/tableau-de-bord").WithTags("Tableau de bord");

        g.MapGet("/etat", (Contexte c) =>
        {
            if (c.Refus(p => p.Profil != Perimetre.Aucun) is { } refus) return refus;
            var i = c.Instantane;
            var p = c.Perimetre;
            var aujourdhui = DateTime.Today;
            var courant = i.ExerciceDe(aujourdhui);
            return Results.Ok(new
            {
                genere = i.Genere, i.DureeSecondes, enCours = c.Service.EnCours, prochaine = c.Service.Prochaine, erreurs = i.Erreurs,
                profil = p.Profil, utilisateur = p.Utilisateur,
                droits = new
                {
                    compta = p.VoitCompta, commercial = p.VoitCommercial, achats = p.VoitAchats, clients = p.VoitClients,
                    fournisseurs = p.VoitFournisseurs, objectifs = p.ModifieObjectifs, actualiser = p.Profil is Perimetre.Direction or Perimetre.Comptable,
                },
                exercices = (i.Exercices.Count > 0 ? i.Exercices : [courant]).Where(e => e.Fin >= i.Depuis)
                    .Select(e => new { cle = Periodes.CleExercice(e), intitule = Periodes.IntituleExercice(e), debut = e.Debut, fin = e.Fin }),
                exerciceCourant = Periodes.CleExercice(courant),
                journaux = i.Journaux.Values.OrderBy(j => j.Code).Select(j => new { j.Code, intitule = j.Intitule?.Trim(), j.Type }),
                plans = i.Plans.OrderBy(x => x.Key).Select(x => new { numero = x.Key, intitule = x.Value }),
                collaborateurs = i.Collaborateurs.Where(x => !p.Restreint || x.Key == p.Collaborateur).OrderBy(x => x.Value)
                    .Select(x => new { numero = x.Key, intitule = x.Value }),
                depots = i.Depots.OrderBy(x => x.Key).Select(x => new { numero = x.Key, intitule = x.Value }),
                familles = i.Familles.OrderBy(x => x.Key).Select(x => new { code = x.Key, intitule = x.Value }),
                // Catégories de P_CATTARIF, plus celles portées par des clients sans intitulé dans les paramètres.
                categories = i.Categories.Keys.Concat(i.Tiers.Values.Where(t => t.Type == 0 && t.Categorie != null).Select(t => t.Categorie!.Value))
                    .Distinct().Order().Select(k => new { numero = k, intitule = i.Categories.GetValueOrDefault(k) is { Length: > 0 } x ? x : $"Catégorie {k}" }),
                qualites = i.Tiers.Values.Where(t => t.Type == 0 && t.Qualite != null).Select(t => t.Qualite!)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase),
                axesCompta = Comptabilite.Axes(i).Values.Select(a => new { a.Code, a.Libelle }),
                axesAnalytiques = Comptabilite.AxesAnalytiques(i, 1).Values.Select(a => new { a.Code, a.Libelle }),
                axesVentes = Commercial.Axes(i).Values.Select(a => new { a.Code, a.Libelle }),
                seuils = c.Options.Seuils,
            });
        }).WithSummary("État de la dernière actualisation, droits de l'utilisateur, exercices et listes des filtres");

        g.MapPost("/actualiser", (Contexte c) =>
        {
            if (c.Refus(p => p.Profil is Perimetre.Direction or Perimetre.Comptable) is { } refus) return refus;
            var lancee = c.Service.Demander();
            return Results.Accepted(value: new { enCours = true, dejaEnCours = !lancee });
        }).WithSummary("Relit Sage maintenant (Direction et Comptable). L'actualisation se fait en arrière-plan : suivre /etat");

        // ---------- Tableau comptable ----------
        g.MapGet("/compta/synthese", (Contexte c, string? exercice) =>
            c.Refus(p => p.VoitCompta) ?? Results.Ok(Comptabilite.Synthese(c.Instantane, Periodes.Choisir(c.Instantane, exercice, DateTime.Today),
                DateTime.Today, c.Options.Seuils, c.Perimetre)))
            .WithSummary("Produits, charges, résultat, trésorerie, créances et dettes de l'exercice, avec N-1, par mois et alertes");

        g.MapGet("/compta/cube", (Contexte c, [AsParameters] RequeteCompta r) =>
            c.Refus(p => p.VoitCompta) ?? Results.Ok(Comptabilite.Croiser(c.Instantane, r, DateTime.Today)))
            .WithSummary("Tableau croisé des écritures : axes en lignes et colonnes (mois, classe, compte, journal, tiers, section...), mesures débit, crédit, solde");

        g.MapGet("/compta/detail", async (Contexte c, ILecturesTableauDeBord l, [AsParameters] RequeteCompta r, string? cleLigne, string? cleColonne) =>
        {
            if (c.Refus(p => p.VoitCompta) is { } refus) return refus;
            if (r.Source == "analytique") return Results.BadRequest(new { code = "DETAIL_INDISPONIBLE", message = "Le détail est disponible sur les écritures générales." });
            var f = Comptabilite.Detail(c.Instantane, r, cleLigne, cleColonne, DateTime.Today);
            if (f == null) return Results.BadRequest(new { code = "DETAIL_INDISPONIBLE", message = "Pas de détail pour cette cellule : choisissez une ligne précise." });
            return await c.Sage(() => l.DetailCompta(f));
        }).WithSummary("Écritures d'une cellule du tableau croisé (500 au plus), lues dans Sage");

        g.MapGet("/compta/rapprochement", (Contexte c, string? exercice) =>
            c.Refus(p => p.VoitCompta) ?? Results.Ok(Comptabilite.Rapprochement(c.Instantane, Periodes.Choisir(c.Instantane, exercice, DateTime.Today), DateTime.Today)))
            .WithSummary("Contrôle par mois : CA, règlements et achats de la gestion commerciale face à la comptabilité");

        // ---------- Recouvrement ----------
        g.MapGet("/recouvrement/{type}", (Contexte c, string type, int? commercial, int? categorie, string? qualite) =>
        {
            var t = type == "fournisseurs" ? 1 : 0;
            return c.Refus(p => t == 0 ? p.VoitClients : p.VoitFournisseurs)
                ?? Results.Ok(Creances.Analyse(c.Instantane, t, c.Perimetre, commercial, categorie, DateTime.Today, string.IsNullOrWhiteSpace(qualite) ? null : qualite));
        }).WithSummary("Balance âgée (clients ou fournisseurs) : tranches, par tiers, par commercial, par catégorie, par qualité, règlements par mode");

        g.MapGet("/recouvrement/{type}/{tiers}", (Contexte c, string type, string tiers) =>
        {
            var t = type == "fournisseurs" ? 1 : 0;
            if (c.Refus(p => (t == 0 ? p.VoitClients : p.VoitFournisseurs) && (t == 1 || p.Autorise(c.Instantane, tiers))) is { } refus) return refus;
            return Results.Ok(Creances.Pieces(c.Instantane, tiers, DateTime.Today));
        }).WithSummary("Pièces non soldées d'un client ou d'un fournisseur, avec leur retard");

        // ---------- Tableau commercial ----------
        g.MapGet("/commercial/synthese", (Contexte c, Objectifs o, string? exercice, int? commercial) =>
            c.Refus(p => p.VoitCommercial) ?? Results.Ok(Commercial.Synthese(c.Instantane, Periodes.Choisir(c.Instantane, exercice, DateTime.Today),
                DateTime.Today, c.Options.Seuils, c.Perimetre, commercial ?? (c.Perimetre.Restreint ? c.Perimetre.Collaborateur : null),
                o.ParMois(commercial ?? (c.Perimetre.Restreint ? c.Perimetre.Collaborateur : null)))))
            .WithSummary("CA jour / mois / exercice et N-1, marge, panier moyen, clients, objectifs, classements, en-cours, stock et alertes");

        g.MapGet("/commercial/cube", (Contexte c, [AsParameters] RequeteVentes r) =>
            c.Refus(p => Commercial.Domaine(r.Domaine) == 1 ? p.VoitAchats : p.VoitCommercial)
                ?? Results.Ok(Commercial.Croiser(c.Instantane, r, c.Perimetre, DateTime.Today)))
            .WithSummary("Tableau croisé des factures de vente (ou d'achat) : axes article, famille, client, catégorie tarifaire, qualité client, commercial, dépôt, mois... ; mesures CA, quantité, coût, marge, taux");

        g.MapGet("/commercial/detail", async (Contexte c, ILecturesTableauDeBord l, [AsParameters] RequeteVentes r, string? cleLigne, string? cleColonne) =>
        {
            if (c.Refus(p => Commercial.Domaine(r.Domaine) == 1 ? p.VoitAchats : p.VoitCommercial) is { } refus) return refus;
            var f = Commercial.Detail(c.Instantane, r, c.Perimetre, cleLigne, cleColonne, DateTime.Today);
            if (f == null) return Results.BadRequest(new { code = "DETAIL_INDISPONIBLE", message = "Pas de détail pour cette cellule : choisissez une ligne précise." });
            return await c.Sage(() => l.DetailVentes(f));
        }).WithSummary("Lignes de factures d'une cellule du tableau croisé (500 au plus), lues dans Sage");

        g.MapGet("/commercial/commandes", (Contexte c, int? commercial) =>
            c.Refus(p => p.VoitCommercial) ?? Results.Ok(Commercial.Commandes(c.Instantane, c.Perimetre, commercial, DateTime.Today)))
            .WithSummary("Devis, commandes, préparations et bons de livraison non clôturés");

        g.MapGet("/stock", (Contexte c, int? depot, string? famille, string? statut) =>
            c.Refus(p => p.VoitCommercial || p.VoitCompta) ?? Results.Ok(Stocks.Analyse(c.Instantane, depot, famille, statut, DateTime.Today, c.Options.Seuils)))
            .WithSummary("Valeur du stock, ruptures, sous minimum, surstocks, dormants, rotation et couverture");

        g.MapGet("/objectifs", (Contexte c, Objectifs o, string? du, string? au) =>
            c.Refus(p => p.VoitCommercial) ?? Results.Ok(o.Lire(du, au).Where(x => !c.Perimetre.Restreint || x.Commercial == c.Perimetre.Collaborateur)))
            .WithSummary("Objectifs de CA mensuels (commercial 0 = objectif global)");

        g.MapPut("/objectifs", (Contexte c, Objectifs o, List<Objectifs.Objectif> objectifs) =>
        {
            if (c.Refus(p => p.ModifieObjectifs) is { } refus) return refus;
            var invalides = objectifs.Where(x => Periodes.LireMois(x.Mois, DateTime.MinValue) == DateTime.MinValue || x.Commercial < 0 || x.Montant < 0).ToList();
            if (invalides.Count > 0) return Reponses.Invalide(invalides.Select(x => $"Objectif invalide : mois {x.Mois} (aaaa-mm), commercial {x.Commercial}, montant {x.Montant}."));
            o.Enregistrer(objectifs, c.Perimetre.Utilisateur);
            return Results.NoContent();
        }).WithSummary("Enregistre des objectifs (Direction). Montant 0 = suppression");
    }
}

/// <summary>Ce dont chaque route a besoin : l'instantané, la configuration et le périmètre de l'utilisateur connecté.</summary>
public sealed class Contexte
{
    public required ServiceTableauDeBord Service { get; init; }
    public required TableauDeBordOptions Options { get; init; }
    public required Perimetre Perimetre { get; init; }
    public required ILogger Log { get; init; }
    public Instantane Instantane => Service.Instantane;

    public static ValueTask<Contexte?> BindAsync(HttpContext http)
    {
        var s = http.RequestServices;
        var auth = s.GetRequiredService<ServiceAuthentification>();
        var options = s.GetRequiredService<IOptionsMonitor<TableauDeBordOptions>>().CurrentValue;
        return ValueTask.FromResult<Contexte?>(new Contexte
        {
            Service = s.GetRequiredService<ServiceTableauDeBord>(),
            Options = options,
            Perimetre = Perimetre.De(auth.Lire(http), options, auth.Options.Active),
            Log = s.GetRequiredService<ILoggerFactory>().CreateLogger("TableauDeBord"),
        });
    }

    /// <summary>401 sans connexion, 403 si le profil n'a pas ce droit, null si l'accès est permis.</summary>
    public IResult? Refus(Func<Perimetre, bool> droit)
    {
        if (Perimetre.Utilisateur == null && Perimetre.Profil == Perimetre.Aucun) return Reponses.ConnexionRequise();
        if (!droit(Perimetre))
            return Reponses.DroitRefuse(Perimetre.Profil == Perimetre.Aucun
                ? $"{Perimetre.Utilisateur} n'a pas de profil pour le tableau de bord (Direction, Comptable, Commercial ou Vendeur) : à déclarer dans TableauDeBord:Profils."
                : $"Le profil {Perimetre.Profil} n'a pas accès à cet écran.");
        return null;
    }

    /// <summary>Lecture directe dans Sage (détail d'une cellule) : 503 avec le message si SQL Server refuse.</summary>
    public async Task<IResult> Sage<T>(Func<Task<T>> lire)
    {
        try
        {
            return Results.Ok(await lire());
        }
        catch (Exception e)
        {
            Log.LogError(e, "Tableau de bord : lecture du détail impossible");
            return Results.Json(new { code = "SQL", message = e.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
