using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace Sage100Api.TableauDeBord;

/// <summary>
/// Photo des données Sage utilisées par le tableau de bord, recalculée à chaque actualisation.
/// Les écrans lisent cette photo (en mémoire) : Sage n'est sollicité que pendant l'actualisation et pour le détail d'une cellule.
/// </summary>
public sealed class Instantane
{
    public DateTime? Genere { get; init; }
    public double DureeSecondes { get; init; }
    /// <summary>Parties qui n'ont pas pu être lues à la dernière actualisation (leur version précédente est gardée).</summary>
    public IReadOnlyList<string> Erreurs { get; init; } = [];
    public DateTime Depuis { get; init; }
    public IReadOnlyList<Exercice> Exercices { get; init; } = [];
    public IReadOnlyList<FaitCompta> Compta { get; init; } = [];
    public IReadOnlyList<FaitAnalytique> Analytique { get; init; } = [];
    /// <summary>Lignes des pièces retenues par <see cref="Documents"/> (toutes celles lues dans Sage tant qu'aucun tri n'est fait).</summary>
    public IReadOnlyList<FaitLigne> Lignes { get => _lignes; init => _lignes = value; }
    public IReadOnlyList<FaitPiece> Pieces { get => _pieces; init => _pieces = value; }
    IReadOnlyList<FaitLigne> _lignes = [];
    IReadOnlyList<FaitPiece> _pieces = [];
    /// <summary>Documents comptés dans le CA par cette vue (voir <see cref="Retenir"/>).</summary>
    public DocumentsCa Documents { get; private set; } = DocumentsCa.Defaut;
    // Vues par réglage, partagées avec les vues (copies) : toujours calculées depuis l'instantané d'origine.
    readonly ConcurrentDictionary<DocumentsCa, Instantane> _vues = new();
    Instantane? _origine;
    public IReadOnlyList<FaitEnCours> EnCours { get; init; } = [];
    /// <summary>Commandes clients et leurs transformations (taux de transformation commande → livraison).</summary>
    public IReadOnlyList<FaitCommande> Commandes { get; init; } = [];
    public IReadOnlyList<LigneStock> Stock { get; init; } = [];
    /// <summary>Bons de fabrication et mouvements de sortie (tableau Production).</summary>
    public IReadOnlyList<FaitMouvement> Mouvements { get; init; } = [];
    public IReadOnlyList<LigneNomenclature> Nomenclatures { get; init; } = [];
    public IReadOnlyList<RefFournisseurArticle> FournisseursArticles { get; init; } = [];
    public IReadOnlyList<FaitReception> Receptions { get; init; } = [];
    public IReadOnlyDictionary<string, RefArticleProduction> ArticlesProduction { get; init; } = new Dictionary<string, RefArticleProduction>();
    public IReadOnlyDictionary<string, DateTime> DernieresSorties { get; init; } = new Dictionary<string, DateTime>();
    public IReadOnlyList<EcheanceTiers> Echeances { get; init; } = [];
    public IReadOnlyList<FaitReglement> Reglements { get; init; } = [];
    public IReadOnlyDictionary<string, RefCompte> Comptes { get; init; } = new Dictionary<string, RefCompte>();
    public IReadOnlyDictionary<string, RefJournal> Journaux { get; init; } = new Dictionary<string, RefJournal>();
    public IReadOnlyDictionary<string, RefTiers> Tiers { get; init; } = new Dictionary<string, RefTiers>();
    public IReadOnlyDictionary<int, string?> Plans { get; init; } = new Dictionary<int, string?>();
    public IReadOnlyDictionary<(int Plan, string Section), string?> Sections { get; init; } = new Dictionary<(int, string), string?>();
    public IReadOnlyDictionary<string, RefArticle> Articles { get; init; } = new Dictionary<string, RefArticle>();
    public IReadOnlyDictionary<string, string?> Familles { get; init; } = new Dictionary<string, string?>();
    public IReadOnlyDictionary<int, string?> Depots { get; init; } = new Dictionary<int, string?>();
    public IReadOnlyDictionary<int, string?> Collaborateurs { get; init; } = new Dictionary<int, string?>();
    public IReadOnlyDictionary<int, string?> ModesReglement { get; init; } = new Dictionary<int, string?>();
    public IReadOnlyDictionary<int, string?> Categories { get; init; } = new Dictionary<int, string?>();

    public static Instantane Vide { get; } = new();

    /// <summary>
    /// Vue de l'instantané limitée aux documents choisis dans les réglages : factures, plus bons de livraison, de retour, d'avoir financier.
    /// Calculée une fois par réglage et par actualisation.
    /// </summary>
    public Instantane Retenir(DocumentsCa documents) => _vues.GetOrAdd(documents, d =>
    {
        var origine = _origine ?? this;
        var v = (Instantane)origine.MemberwiseClone();
        v._origine = origine;
        v._lignes = origine._lignes.Where(l => d.Retient(l.Type, l.Provenance)).ToList();
        v._pieces = origine._pieces.Where(p => d.Retient(p.Type, p.Provenance)).ToList();
        v.Documents = d;
        return v;
    });

    public string? IntituleTiers(string? numero) => numero != null && Tiers.TryGetValue(numero, out var t) ? t.Intitule?.Trim() : null;
    public int? Representant(string? tiers) => tiers != null && Tiers.TryGetValue(tiers, out var t) ? t.Representant : null;
    public string? Famille(string article) => Articles.TryGetValue(article, out var a) ? a.Famille : null;

    /// <summary>Exercice contenant cette date, ou l'année civile si le dossier n'en déclare aucun.</summary>
    public Exercice ExerciceDe(DateTime date) =>
        Exercices.FirstOrDefault(e => e.Debut <= date.Date && date.Date <= e.Fin)
        ?? new Exercice(0, new DateTime(date.Year, 1, 1), new DateTime(date.Year, 12, 31));

    /// <summary>Exercice précédent : celui qui finit la veille du début, sinon la même période un an plus tôt.</summary>
    public Exercice Precedent(Exercice e) =>
        Exercices.FirstOrDefault(x => x.Fin == e.Debut.AddDays(-1)) ?? new Exercice(0, e.Debut.AddYears(-1), e.Fin.AddYears(-1));
}

/// <summary>
/// Tient l'instantané à jour : au démarrage, aux heures de <see cref="TableauDeBordOptions.Actualisations"/> et à la demande.
/// Chaque partie est lue séparément : une table illisible (colonne absente d'une version de Sage...) n'empêche pas les autres,
/// elle garde sa version précédente et l'erreur est affichée dans le tableau de bord et écrite dans le journal du service.
/// </summary>
public sealed class ServiceTableauDeBord(ILecturesTableauDeBord lectures, IOptionsMonitor<TableauDeBordOptions> options, ILogger<ServiceTableauDeBord> log,
    Dossiers? dossiers = null)
    : BackgroundService
{
    readonly SemaphoreSlim _demande = new(0, 1);
    readonly SemaphoreSlim _uneSeule = new(1, 1);
    // Un instantané par société ; clé vide sans gestion des sociétés (tests).
    readonly ConcurrentDictionary<string, Instantane> _instantanes = new(StringComparer.OrdinalIgnoreCase);

    string Code => dossiers?.Code ?? "";

    /// <summary>Instantané de la société en cours.</summary>
    public Instantane Instantane => _instantanes.TryGetValue(Code, out var i) ? i : Instantane.Vide;
    public bool EnCours { get; private set; }
    public DateTime? Prochaine { get; private set; }

    /// <summary>Demande une actualisation (sans attendre). Faux si une actualisation est déjà en cours.</summary>
    public bool Demander()
    {
        if (EnCours) return false;
        try { _demande.Release(); } catch (SemaphoreFullException) { }
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken arret)
    {
        while (!arret.IsCancellationRequested)
        {
            try
            {
                await Actualiser(arret);
            }
            catch (OperationCanceledException) when (arret.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                log.LogError(e, "Tableau de bord : actualisation impossible");
            }
            var prochaine = ProchaineHeure(DateTime.Now, Periodes.Liste(options.CurrentValue.Actualisations) ?? []);
            Prochaine = prochaine;
            var attente = prochaine - DateTime.Now;
            try
            {
                await _demande.WaitAsync(attente < TimeSpan.Zero ? TimeSpan.Zero : attente, arret);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public static DateTime ProchaineHeure(DateTime maintenant, IEnumerable<string> heures)
    {
        var prochaines = heures.Select(h => TimeSpan.TryParse(h, out var t) ? t : (TimeSpan?)null).OfType<TimeSpan>()
            .Select(t => maintenant.Date + t is var d && d > maintenant ? d : d.AddDays(1)).ToList();
        return prochaines.Count > 0 ? prochaines.Min() : maintenant.Date.AddDays(1).AddHours(7);
    }

    /// <summary>Relit toutes les sociétés, l'une après l'autre ; renvoie l'instantané de la société en cours.</summary>
    public async Task<Instantane> Actualiser(CancellationToken ct = default)
    {
        await _uneSeule.WaitAsync(ct);
        EnCours = true;
        try
        {
            var codes = dossiers?.Liste.Select(d => d.Code).ToList() ?? [""];
            foreach (var code in codes)
            {
                using var societe = code.Length == 0 ? null : Dossiers.Activer(code);
                var avant = _instantanes.TryGetValue(code, out var i) ? i : Instantane.Vide;
                _instantanes[code] = await Lire(code, avant, ct);
            }
            return Instantane;
        }
        finally
        {
            EnCours = false;
            _uneSeule.Release();
        }
    }

    async Task<Instantane> Lire(string code, Instantane avant, CancellationToken ct)
    {
        var chrono = Stopwatch.StartNew();
        var erreurs = new List<string>();
        async Task<T> Partie<T>(string nom, Func<Task<T>> lire, T precedent)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await lire();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Tableau de bord : lecture des {Partie} impossible", nom);
                erreurs.Add($"{nom} : {e.Message}");
                return precedent;
            }
        }

        Task<IReadOnlyList<T>?> Referentiel<T>(string nom, Func<Task<IReadOnlyList<T>>> lire) =>
            Partie<IReadOnlyList<T>?>(nom, async () => await lire(), null);

        var exercices = await Partie("exercices", lectures.Exercices, avant.Exercices);
        var nombre = Math.Clamp(options.CurrentValue.Exercices, 1, 5);
        var depuis = exercices.Count > 0
            ? exercices.Take(nombre).Min(e => e.Debut)
            : new DateTime(DateTime.Today.Year - nombre + 1, 1, 1);

        var comptes = await Referentiel("comptes généraux", lectures.Comptes);
        var journaux = await Referentiel("journaux", lectures.Journaux);
        var tiers = await Referentiel("tiers", lectures.Tiers);
        var plans = await Referentiel("plans analytiques", lectures.Plans);
        var sections = await Referentiel("sections analytiques", lectures.Sections);
        var articles = await Referentiel("articles", lectures.Articles);
        var familles = await Referentiel("familles", lectures.Familles);
        var depots = await Referentiel("dépôts", lectures.Depots);
        var collaborateurs = await Referentiel("collaborateurs", lectures.Collaborateurs);
        var modes = await Referentiel("modes de règlement", lectures.ModesReglement);
        var categories = await Referentiel("catégories tarifaires", lectures.CategoriesTarifaires);
        var articlesProduction = await Referentiel("articles (fabrication)", lectures.ArticlesProduction);

        var nouveau = new Instantane
        {
            Depuis = depuis,
            Exercices = exercices,
            Compta = await Partie("écritures comptables", () => lectures.Compta(depuis), avant.Compta),
            Analytique = await Partie("écritures analytiques", () => lectures.Analytique(depuis), avant.Analytique),
            Lignes = await Partie("lignes de factures", () => lectures.Lignes(depuis), avant.Lignes),
            Pieces = await Partie("factures", () => lectures.Pieces(depuis), avant.Pieces),
            EnCours = await Partie("documents en cours", lectures.EnCours, avant.EnCours),
            Commandes = await Partie("commandes clients", () => lectures.Commandes(depuis), avant.Commandes),
            Stock = await Partie("stocks", lectures.Stock, avant.Stock),
            DernieresSorties = await Partie("dernières sorties de stock", lectures.DernieresSorties, avant.DernieresSorties),
            Echeances = await Partie("échéances clients et fournisseurs", lectures.Echeances, avant.Echeances),
            Reglements = await Partie("règlements", () => lectures.Reglements(depuis), avant.Reglements),
            Mouvements = await Partie("bons de fabrication", () => lectures.Mouvements(depuis), avant.Mouvements),
            Nomenclatures = await Partie("nomenclatures", lectures.Nomenclatures, avant.Nomenclatures),
            FournisseursArticles = await Partie("fournisseurs des articles", lectures.FournisseursArticles, avant.FournisseursArticles),
            Receptions = await Partie("réceptions fournisseurs", () => lectures.Receptions(depuis), avant.Receptions),
            ArticlesProduction = articlesProduction?.GroupBy(a => a.Reference).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase) ?? avant.ArticlesProduction,
            Comptes = comptes?.GroupBy(c => c.Numero).ToDictionary(g => g.Key, g => g.First()) ?? avant.Comptes,
            Journaux = journaux?.GroupBy(j => j.Code).ToDictionary(g => g.Key, g => g.First()) ?? avant.Journaux,
            Tiers = tiers?.GroupBy(t => t.Numero).ToDictionary(g => g.Key, g => g.First()) ?? avant.Tiers,
            Plans = plans?.ToDictionary(p => p.Numero, p => p.Intitule?.Trim()) ?? avant.Plans,
            Sections = sections?.GroupBy(s => (s.Plan, s.Numero)).ToDictionary(g => g.Key, g => g.First().Intitule?.Trim()) ?? avant.Sections,
            Articles = articles?.GroupBy(a => a.Reference).ToDictionary(g => g.Key, g => g.First()) ?? avant.Articles,
            Familles = familles?.GroupBy(f => f.Code).ToDictionary(g => g.Key, g => g.First().Intitule?.Trim()) ?? avant.Familles,
            Depots = depots?.ToDictionary(d => d.Numero, d => d.Intitule?.Trim()) ?? avant.Depots,
            Collaborateurs = collaborateurs?.ToDictionary(c => c.Numero, c => c.Intitule?.Trim()) ?? avant.Collaborateurs,
            ModesReglement = modes?.ToDictionary(m => m.Numero, m => m.Intitule?.Trim()) ?? avant.ModesReglement,
            Categories = categories?.ToDictionary(c => c.Numero, c => c.Intitule?.Trim()) ?? avant.Categories,
            Erreurs = erreurs,
            Genere = DateTime.Now,
            DureeSecondes = Math.Round(chrono.Elapsed.TotalSeconds, 1),
        };
        log.LogInformation("Tableau de bord {Societe} actualisé en {Duree} s : {Ecritures} lignes comptables, {Lignes} lignes de factures, {Erreurs} erreur(s)",
            code, nouveau.DureeSecondes, nouveau.Compta.Count, nouveau.Lignes.Count, erreurs.Count);
        return nouveau;
    }
}
