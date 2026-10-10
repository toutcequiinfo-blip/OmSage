using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Sage100Api.TableauDeBord;

/// <summary>
/// Routes /api/v1/tableau-de-bord : lecture seule. Elles lisent l'instantané en mémoire ; seul le détail d'une cellule interroge Sage (SQL, SELECT).
/// Chaque route vérifie le profil de l'utilisateur connecté (jeton de POST /api/v1/connexion).
/// </summary>
public static class Routes
{
    // Onglets servis par les explorateurs (cube et détail d'une cellule).
    static readonly string[] OngletsCubeCompta = ["compta/explorateur", "compta/balance", "compta/charges"];
    static readonly string[] OngletsCreancesClients = ["compta/recouvrement", "commercial/recouvrement", "compta/tresorerie"];
    static readonly string[] OngletsCubeVentes = ["commercial/ventes", "commercial/articles", "commercial/direction", "commercial/clients"];

    public static void MapTableauDeBord(this RouteGroupBuilder v1)
    {
        var g = v1.MapGroup("/tableau-de-bord").WithTags("Tableau de bord");

        g.MapGet("/etat", (Contexte c) =>
        {
            if (c.Refus(p => p.Onglets.Count > 0 || p.Administre) is { } refus) return refus;
            var i = c.Instantane;
            var p = c.Perimetre;
            var aujourdhui = DateTime.Today;
            var courant = i.ExerciceDe(aujourdhui);
            return Results.Ok(new
            {
                genere = i.Genere, i.DureeSecondes, enCours = c.Service.EnCours, prochaine = c.Service.Prochaine, erreurs = i.Erreurs,
                profil = p.Profil, utilisateur = p.Utilisateur,
                onglets = p.Onglets.Order().Concat(p.Administre ? ["administration/droits", "administration/utilisateurs"] : []),
                droits = new
                {
                    compta = p.VoitCompta, commercial = p.VoitCommercial, achats = p.VoitAchats, clients = p.VoitClients,
                    fournisseurs = p.VoitFournisseurs, production = p.VoitProduction, objectifs = p.ModifieObjectifs, reglages = p.Profil == Perimetre.Direction, administration = p.Administre, actualiser = p.Profil is Perimetre.Direction or Perimetre.Comptable,
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
                documents = i.Documents,
                coutProduction = c.Reglages.CoutProduction(),
            });
        }).WithSummary("État de la dernière actualisation, droits de l'utilisateur, exercices et listes des filtres");

        g.MapGet("/reglages", (Contexte c) =>
        {
            if (c.Refus(p => p.Onglets.Count > 0 || p.Administre) is { } refus) return refus;
            return Results.Ok(new { documents = c.Reglages.Documents(), coutProduction = c.Reglages.CoutProduction() });
        }).WithSummary("Réglages de la société : documents comptés dans le CA, valorisation du tableau Production");

        g.MapPut("/reglages", (Contexte c, ReglagesRequete r) =>
        {
            if (c.Refus(p => p.Profil == Perimetre.Direction) is { } refus) return refus;
            if (r.CoutProduction != null && !MethodeCout.Toutes.Contains(r.CoutProduction.Trim().ToLowerInvariant()))
                return Reponses.Invalide([$"Méthode de coût inconnue : {r.CoutProduction} ({string.Join(", ", MethodeCout.Toutes)})."]);
            if (r.Documents != null || r.CoutProduction == null) c.Reglages.Enregistrer(r.Documents ?? DocumentsCa.Defaut, c.Perimetre.Utilisateur);
            if (r.CoutProduction != null) c.Reglages.EnregistrerCoutProduction(r.CoutProduction, c.Perimetre.Utilisateur);
            return Results.Ok(new { documents = c.Reglages.Documents(), coutProduction = c.Reglages.CoutProduction() });
        }).WithSummary("Change les réglages (Direction) : documents comptés dans le CA ; coût du tableau Production (revient, achat, dernier, cmup)");

        g.MapPost("/actualiser", (Contexte c) =>
        {
            if (c.Refus(p => p.Profil is Perimetre.Direction or Perimetre.Comptable) is { } refus) return refus;
            var lancee = c.Service.Demander();
            return Results.Accepted(value: new { enCours = true, dejaEnCours = !lancee });
        }).WithSummary("Relit Sage maintenant (Direction et Comptable). L'actualisation se fait en arrière-plan : suivre /etat");

        // ---------- Tableau comptable ----------
        g.MapGet("/compta/synthese", (Contexte c, string? exercice) =>
            c.Refus(p => p.Peut("compta/synthese", "compta/charges", "compta/tresorerie")) ?? Results.Ok(Comptabilite.Synthese(c.Instantane, Periodes.Choisir(c.Instantane, exercice, DateTime.Today),
                DateTime.Today, c.Options.Seuils, c.Perimetre)))
            .WithSummary("Produits, charges, résultat, trésorerie, créances et dettes de l'exercice, avec N-1, par mois et alertes");

        g.MapGet("/compta/cube", (Contexte c, [AsParameters] RequeteCompta r) =>
            c.Refus(p => p.Peut(OngletsCubeCompta)) ?? Results.Ok(Comptabilite.Croiser(c.Instantane, r, DateTime.Today)))
            .WithSummary("Tableau croisé des écritures : axes en lignes et colonnes (mois, classe, compte, journal, tiers, section...), mesures débit, crédit, solde");

        g.MapGet("/compta/detail", async (Contexte c, ILecturesTableauDeBord l, [AsParameters] RequeteCompta r, string? cleLigne, string? cleColonne) =>
        {
            if (c.Refus(p => p.Peut(OngletsCubeCompta)) is { } refus) return refus;
            if (r.Source == "analytique") return Results.BadRequest(new { code = "DETAIL_INDISPONIBLE", message = "Le détail est disponible sur les écritures générales." });
            var f = Comptabilite.Detail(c.Instantane, r, cleLigne, cleColonne, DateTime.Today);
            if (f == null) return Results.BadRequest(new { code = "DETAIL_INDISPONIBLE", message = "Pas de détail pour cette cellule : choisissez une ligne précise." });
            return await c.Sage(() => l.DetailCompta(f));
        }).WithSummary("Écritures d'une cellule du tableau croisé (500 au plus), lues dans Sage");

        g.MapGet("/compta/rapprochement", (Contexte c, string? exercice) =>
            c.Refus(p => p.Peut("compta/rapprochement")) ?? Results.Ok(Comptabilite.Rapprochement(c.Instantane, Periodes.Choisir(c.Instantane, exercice, DateTime.Today), DateTime.Today)))
            .WithSummary("Contrôle par mois : CA, règlements et achats de la gestion commerciale face à la comptabilité");

        g.MapGet("/valeurs/{liste}", (Contexte c, string liste, string? q, int? limite) =>
        {
            var i = c.Instantane;
            var n = Math.Clamp(limite ?? 200, 1, 1000);
            bool Trouve(string code, string? intitule) => string.IsNullOrWhiteSpace(q)
                || code.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase) || (intitule?.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase) ?? false);
            IEnumerable<(string Code, string? Intitule)> Tiers(int? type) => i.Tiers.Values
                .Where(t => (type == null || t.Type == type) && (t.Type != 0 || c.Perimetre.Autorise(i, t.Numero)))
                .Select(t => (t.Numero, t.Intitule?.Trim()));
            IResult Liste(IEnumerable<(string Code, string? Intitule)> v) =>
                Results.Ok(v.Where(x => Trouve(x.Code, x.Intitule)).OrderBy(x => x.Code, StringComparer.OrdinalIgnoreCase).Take(n)
                    .Select(x => new { valeur = x.Code, intitule = x.Intitule }));
            return liste switch
            {
                "articles" => c.Refus(p => p.VoitCommercial || p.VoitAchats) ?? Liste(i.Articles.Values.Select(a => (a.Reference, a.Designation?.Trim()))),
                "clients" => c.Refus(p => p.VoitClients) ?? Liste(Tiers(0)),
                "fournisseurs" => c.Refus(p => p.VoitFournisseurs) ?? Liste(Tiers(1)),
                "tiers" => c.Refus(p => p.VoitCompta) ?? Liste(Tiers(null)),
                _ => Results.NotFound(),
            };
        }).WithSummary("Valeurs d'un filtre à choix multiple (articles, clients, fournisseurs, tiers), cherchées par code ou intitulé");

        // ---------- Recouvrement ----------
        g.MapGet("/recouvrement/{type}", (Contexte c, string type, string? commercial, string? categorie, string? qualite) =>
        {
            var t = type == "fournisseurs" ? 1 : 0;
            return c.Refus(p => t == 0 ? p.Peut(OngletsCreancesClients) : p.VoitFournisseurs)
                ?? Results.Ok(Creances.Analyse(c.Instantane, t, c.Perimetre, commercial, categorie, DateTime.Today, qualite));
        }).WithSummary("Balance âgée (clients ou fournisseurs) : tranches, par tiers, par commercial, par catégorie, par qualité, règlements par mode");

        g.MapGet("/recouvrement/{type}/{tiers}", (Contexte c, string type, string tiers) =>
        {
            var t = type == "fournisseurs" ? 1 : 0;
            if (c.Refus(p => (t == 0 ? p.Peut(OngletsCreancesClients) : p.VoitFournisseurs) && (t == 1 || p.Autorise(c.Instantane, tiers))) is { } refus) return refus;
            return Results.Ok(Creances.Pieces(c.Instantane, tiers, DateTime.Today));
        }).WithSummary("Pièces non soldées d'un client ou d'un fournisseur, avec leur retard");

        // ---------- Tableau commercial ----------
        g.MapGet("/commercial/synthese", (Contexte c, Objectifs o, string? exercice, int? commercial) =>
            c.Refus(p => p.Peut("commercial/direction", "commercial/clients", "commercial/objectifs")) ?? Results.Ok(Commercial.Synthese(c.Instantane, Periodes.Choisir(c.Instantane, exercice, DateTime.Today),
                DateTime.Today, c.Options.Seuils, c.Perimetre, commercial ?? (c.Perimetre.Restreint ? c.Perimetre.Collaborateur : null),
                o.ParMois(commercial ?? (c.Perimetre.Restreint ? c.Perimetre.Collaborateur : null)))))
            .WithSummary("CA jour / mois / exercice et N-1, marge, panier moyen, clients, objectifs, classements, en-cours, stock et alertes");

        g.MapGet("/commercial/cube", (Contexte c, [AsParameters] RequeteVentes r) =>
            c.Refus(p => Commercial.Domaine(r.Domaine) == 1 ? p.VoitAchats : p.Peut(OngletsCubeVentes))
                ?? Results.Ok(Commercial.Croiser(c.Instantane, r, c.Perimetre, DateTime.Today)))
            .WithSummary("Tableau croisé des factures de vente (ou d'achat) : axes article, famille, client, catégorie tarifaire, qualité client, commercial, dépôt, mois... ; mesures CA, quantité, coût, marge, taux");

        g.MapGet("/commercial/detail", async (Contexte c, ILecturesTableauDeBord l, [AsParameters] RequeteVentes r, string? cleLigne, string? cleColonne) =>
        {
            if (c.Refus(p => Commercial.Domaine(r.Domaine) == 1 ? p.VoitAchats : p.Peut(OngletsCubeVentes)) is { } refus) return refus;
            var f = Commercial.Detail(c.Instantane, r, c.Perimetre, cleLigne, cleColonne, DateTime.Today);
            if (f == null) return Results.BadRequest(new { code = "DETAIL_INDISPONIBLE", message = "Pas de détail pour cette cellule : choisissez une ligne précise." });
            return await c.Sage(() => l.DetailVentes(f));
        }).WithSummary("Lignes de factures d'une cellule du tableau croisé (500 au plus), lues dans Sage");

        g.MapGet("/commercial/transformation/cube", (Contexte c, [AsParameters] RequeteVentes r) =>
            c.Refus(p => p.Peut("commercial/transformation")) ?? Results.Ok(Transformation.Croiser(c.Instantane, r, c.Perimetre, DateTime.Today)))
            .WithSummary("Taux de transformation commande → livraison : commandé, livré, en cours, non servi et taux, par article, client, mois, semaine...");

        g.MapGet("/commercial/transformation/detail", (Contexte c, [AsParameters] RequeteVentes r, string? cleLigne, string? cleColonne) =>
        {
            if (c.Refus(p => p.Peut("commercial/transformation")) is { } refus) return refus;
            var d = Transformation.Detail(c.Instantane, r, c.Perimetre, cleLigne, cleColonne, DateTime.Today);
            return d == null ? Results.BadRequest(new { code = "DETAIL_INDISPONIBLE", message = "Pas de détail pour cette cellule : choisissez une ligne précise." }) : Results.Ok(d);
        }).WithSummary("Commandes clients d'une cellule du tableau de transformation : commandé, livré, reste, taux, délai et statut (500 au plus)");

        g.MapGet("/commercial/commandes", (Contexte c, int? commercial) =>
            c.Refus(p => p.Peut("commercial/commandes")) ?? Results.Ok(Commercial.Commandes(c.Instantane, c.Perimetre, commercial, DateTime.Today)))
            .WithSummary("Devis, commandes, préparations et bons de livraison non clôturés");

        g.MapGet("/stock", (Contexte c, int? depot, string? famille, string? statut) =>
            c.Refus(p => p.Peut("commercial/stock")) ?? Results.Ok(Stocks.Analyse(c.Instantane, depot, famille, statut, DateTime.Today, c.Options.Seuils)))
            .WithSummary("Valeur du stock, ruptures, sous minimum, surstocks, dormants, rotation et couverture");

        // ---------- Tableau Production ----------
        g.MapGet("/production/synthese", (Contexte c, [AsParameters] RequeteProduction r) =>
            c.Refus(p => p.Peut("production/production")) ?? Results.Ok(Production.Synthese(c.Instantane, c.Production(r), DateTime.Today)))
            .WithSummary("Production réalisée (bons de fabrication) : valeur, matières consommées face à la nomenclature, par mois et par produit");

        g.MapGet("/production/produit/{article}", (Contexte c, string article, [AsParameters] RequeteProduction r) =>
            c.Refus(p => p.Peut("production/production", "production/previsions")) ?? Results.Ok(Production.DetailProduit(c.Instantane, article, c.Production(r), DateTime.Today)))
            .WithSummary("Matières d'un produit fabriqué : consommation réelle face à la nomenclature, et ses bons de fabrication");

        g.MapGet("/production/matieres", (Contexte c, [AsParameters] RequeteProduction r) =>
            c.Refus(p => p.Peut("production/matieres")) ?? Results.Ok(Production.Matieres(c.Instantane, c.Production(r), DateTime.Today)))
            .WithSummary("Consommation des matières : réelle, théorique (nomenclature), écarts, sorties hors fabrication");

        g.MapGet("/production/appro", (Contexte c, [AsParameters] RequeteProduction r) =>
            c.Refus(p => p.Peut("production/appro")) ?? Results.Ok(Production.Appro(c.Instantane, c.Production(r), DateTime.Today)))
            .WithSummary("Aide à l'approvisionnement : besoins des fabrications, couverture, point de commande, quantité proposée par fournisseur");

        g.MapGet("/production/previsions", (Contexte c, [AsParameters] RequeteProduction r) =>
            c.Refus(p => p.Peut("production/previsions")) ?? Results.Ok(Production.Previsions(c.Instantane, c.Production(r), DateTime.Today)))
            .WithSummary("Prévisions : ventes prévues des produits finis, quantité à produire, quantité fabricable avec le stock des composants");

        g.MapGet("/objectifs", (Contexte c, Objectifs o, string? du, string? au) =>
            c.Refus(p => p.Peut("commercial/objectifs", "commercial/direction")) ?? Results.Ok(o.Lire(du, au).Where(x => !c.Perimetre.Restreint || x.Commercial == c.Perimetre.Collaborateur)))
            .WithSummary("Objectifs de CA mensuels (commercial 0 = objectif global)");

        g.MapPut("/objectifs", (Contexte c, Objectifs o, List<Objectifs.Objectif> objectifs) =>
        {
            if (c.Refus(p => p.ModifieObjectifs) is { } refus) return refus;
            var invalides = objectifs.Where(x => Periodes.LireMois(x.Mois, DateTime.MinValue) == DateTime.MinValue || x.Commercial < 0 || x.Montant < 0).ToList();
            if (invalides.Count > 0) return Reponses.Invalide(invalides.Select(x => $"Objectif invalide : mois {x.Mois} (aaaa-mm), commercial {x.Commercial}, montant {x.Montant}."));
            o.Enregistrer(objectifs, c.Perimetre.Utilisateur);
            return Results.NoContent();
        }).WithSummary("Enregistre des objectifs (Direction). Montant 0 = suppression");
        // ---------- Administration : droits par onglet, utilisateurs actifs ----------
        g.MapGet("/administration/droits", (Contexte c, UtilisateursActifs activite, Dossiers dossiers) =>
        {
            if (c.Refus(p => p.Administre) is { } refus) return refus;
            var connus = activite.Connus(dossiers.Code).GroupBy(u => u.Login, StringComparer.OrdinalIgnoreCase).Select(x => x.First()).ToList();
            var logins = connus.Select(u => u.Login).Concat(c.Options.Profils.Keys).Distinct(StringComparer.OrdinalIgnoreCase);
            return Results.Ok(new
            {
                droits = c.Droits.Etat(),
                connus = logins.Order(StringComparer.OrdinalIgnoreCase).Select(l =>
                {
                    var u = connus.FirstOrDefault(x => x.Login.Equals(l, StringComparison.OrdinalIgnoreCase));
                    return new
                    {
                        login = l,
                        nom = u == null ? null : string.Join(" ", new[] { u.Prenom, u.Nom }.Where(x => !string.IsNullOrWhiteSpace(x))),
                        profilAuto = u == null ? DroitsTableauDeBord.Normaliser(c.Options.Profils.GetValueOrDefault(l)) : Perimetre.De(u, c.Options, true).Profil,
                        administrateurSage = u?.Administrateur ?? false,
                    };
                }),
            });
        }).WithSummary("Droits d'accès aux onglets (profils et utilisateurs) et utilisateurs connus (administrateurs)");

        g.MapPut("/administration/droits/profil", (Contexte c, DroitsProfilRequete r) =>
        {
            if (c.Refus(p => p.Administre) is { } refus) return refus;
            if (!Perimetre.Profils.Contains(r.Profil)) return Reponses.Invalide([$"Profil inconnu : {r.Profil}."]);
            c.Droits.EnregistrerProfil(r.Profil, r.Onglets, c.Perimetre.Utilisateur);
            return Results.Ok(c.Droits.Etat());
        }).WithSummary("Onglets d'un profil (administrateurs). Onglets absents = revenir aux onglets par défaut");

        g.MapPut("/administration/droits/utilisateur", (Contexte c, DroitsUtilisateurRequete r) =>
        {
            if (c.Refus(p => p.Administre) is { } refus) return refus;
            var login = r.Login?.Trim() ?? "";
            if (login.Length == 0) return Reponses.Invalide(["Saisissez le login Sage de l'utilisateur."]);
            var profil = string.IsNullOrWhiteSpace(r.Profil) ? null : DroitsTableauDeBord.Normaliser(r.Profil);
            if (!string.IsNullOrWhiteSpace(r.Profil) && profil == null) return Reponses.Invalide([$"Profil inconnu : {r.Profil}."]);
            // Un administrateur ne peut pas se retirer lui-même le droit d'administrer.
            var administrateur = r.Administrateur || (login.Equals(c.Perimetre.Utilisateur, StringComparison.OrdinalIgnoreCase) && c.Perimetre.Administre);
            c.Droits.EnregistrerUtilisateur(login, profil, r.Onglets, administrateur, c.Perimetre.Utilisateur);
            return Results.Ok(c.Droits.Etat());
        }).WithSummary("Profil imposé, onglets propres et droit d'administrer d'un utilisateur (administrateurs). Tout vide = réglage retiré");

        g.MapGet("/administration/utilisateurs", (Contexte c, UtilisateursActifs activite) =>
        {
            if (c.Refus(p => p.Administre) is { } refus) return refus;
            var maintenant = DateTime.UtcNow;
            return Results.Ok(activite.Sessions().Select(s => new
            {
                login = s.Login, nom = s.Nom, dossier = s.Dossier, adresse = s.Adresse, poste = string.IsNullOrEmpty(s.Poste) ? null : s.Poste,
                sessionWindows = s.SessionWindows, administrateur = s.Administrateur,
                applications = s.Applications.OrderByDescending(a => a.Value).Select(a => a.Key),
                debut = s.Debut, derniere = s.Derniere, inactifMinutes = (int)(maintenant - s.Derniere).TotalMinutes, requetes = s.Requetes,
            }));
        }).WithSummary("Utilisateurs connectés depuis 24 heures : société, poste, session Windows, applications, dernière activité (administrateurs)");
    }
}

/// <summary>Ce dont chaque route a besoin : l'instantané, la configuration et le périmètre de l'utilisateur connecté.</summary>
public sealed class Contexte
{
    public required ServiceTableauDeBord Service { get; init; }
    public required TableauDeBordOptions Options { get; init; }
    public required Perimetre Perimetre { get; init; }
    public required ILogger Log { get; init; }
    public required ReglagesTableauDeBord Reglages { get; init; }
    public required DroitsTableauDeBord Droits { get; init; }
    /// <summary>Instantané de la société, limité aux documents que ses réglages comptent dans le CA.</summary>
    public Instantane Instantane => Service.Instantane.Retenir(Reglages.Documents());

    public static ValueTask<Contexte?> BindAsync(HttpContext http)
    {
        var s = http.RequestServices;
        var auth = s.GetRequiredService<ServiceAuthentification>();
        var options = s.GetRequiredService<IOptionsMonitor<TableauDeBordOptions>>().CurrentValue;
        var droits = s.GetRequiredService<DroitsTableauDeBord>();
        var u = auth.Lire(http);
        return ValueTask.FromResult<Contexte?>(new Contexte
        {
            Service = s.GetRequiredService<ServiceTableauDeBord>(),
            Options = options,
            Perimetre = droits.Appliquer(Perimetre.De(u, options, auth.Options.Active), u, auth.Options.Active),
            Log = s.GetRequiredService<ILoggerFactory>().CreateLogger("TableauDeBord"),
            Reglages = s.GetRequiredService<ReglagesTableauDeBord>(),
            Droits = droits,
        });
    }

    /// <summary>Requête du tableau Production avec la méthode de coût des réglages (sauf si la requête en donne une).</summary>
    public RequeteProduction Production(RequeteProduction r) => r with { Cout = MethodeCout.Normaliser(r.Cout ?? Reglages.CoutProduction()) };

    /// <summary>401 sans connexion, 403 si le profil n'a pas ce droit, null si l'accès est permis.</summary>
    public IResult? Refus(Func<Perimetre, bool> droit)
    {
        if (Perimetre.Utilisateur == null && Perimetre.Profil == Perimetre.Aucun && !Perimetre.Administre) return Reponses.ConnexionRequise();
        if (!droit(Perimetre))
            return Reponses.DroitRefuse(Perimetre.Onglets.Count == 0 && !Perimetre.Administre
                ? $"{Perimetre.Utilisateur} n'a accès à aucun onglet du tableau de bord : un administrateur peut lui en ouvrir (Administration > Droits d'accès)."
                : $"Vous n'avez pas accès à cet écran (profil {Perimetre.Profil}).");
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

public sealed record ReglagesRequete(DocumentsCa? Documents, string? CoutProduction = null);
