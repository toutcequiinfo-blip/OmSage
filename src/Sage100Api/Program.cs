using System.Text.Json;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using Sage100Api;
using Sage100Api.Contracts;
using Sage100Api.Ecritures;
using Sage100Api.Extensions;
using Sage100Api.Journal;
using Sage100Api.Lectures;
using Sage100Api.Worker;

// En service Windows, le dossier courant est C:\Windows\System32 : la configuration et le journal se lisent à côté de l'exécutable.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null,
});
builder.Host.UseWindowsService(o => o.ServiceName = "Sage100Api");
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
// Écrit par deploy/creer-certificats.ps1 : point d'écoute HTTPS (Kestrel) et certificat du serveur.
builder.Configuration.AddJsonFile("appsettings.Https.json", optional: true, reloadOnChange: false);

builder.Services.PostConfigure<SageOptions>(o =>
{
    if (!Path.IsPathRooted(o.CheminJournal))
        o.CheminJournal = Path.Combine(builder.Environment.ContentRootPath, o.CheminJournal);
});

builder.Services.Configure<SageOptions>(builder.Configuration.GetSection("Sage"));
builder.Services.Configure<AuthentificationOptions>(builder.Configuration.GetSection("Authentification"));
builder.Services.AddSingleton<ILecturesSage, LecturesSql>();
builder.Services.AddSingleton<ILecturesErp, LecturesErpSql>();
builder.Services.AddSingleton<ILecturesTarifs, LecturesTarifsSql>();
builder.Services.AddSingleton<Geolocalisation>();
builder.Services.AddSingleton<Crm>();
builder.Services.AddSingleton<Livraison>();
builder.Services.AddSingleton<IWorkerClient, WorkerClient>();
builder.Services.AddSingleton<JournalOperations>();
builder.Services.AddSingleton<ControleStock>();
builder.Services.AddSingleton<ServiceEcritures>();
builder.Services.AddSingleton<ServiceAuthentification>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new() { Title = "API Sage 100", Version = "v1", Description = "Liaison entre Sage 100 (Objets Métiers) et les applications externes." });
    o.AddSecurityDefinition("ApiKey", new() { Name = CleApi.Entete, In = Microsoft.OpenApi.Models.ParameterLocation.Header, Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey });
    // Jeton renvoyé par POST /api/v1/connexion : à coller dans « Authorize » pour créer commandes et encaissements.
    o.AddSecurityDefinition("Utilisateur", new() { Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http, Scheme = "bearer", Description = "Jeton de POST /api/v1/connexion" });
    o.AddSecurityRequirement(new()
    {
        [new() { Reference = new() { Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme, Id = "ApiKey" } }] = Array.Empty<string>(),
        [new() { Reference = new() { Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme, Id = "Utilisateur" } }] = Array.Empty<string>(),
    });
});
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

var app = builder.Build();
app.UseSwagger();
app.UseSwaggerUI();
// Application tablette de la borne (wwwroot/borne). Toujours revalidée : c'est son service worker qui la garde hors ligne.
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = f => f.Context.Response.Headers.CacheControl = "no-cache" });
app.MapGet("/", () => Results.Redirect("/borne/")).ExcludeFromDescription();
app.UseMiddleware<CleApi>();

var v1 = app.MapGroup("/api/v1");

// ---------- Santé (sans clé) ----------
v1.MapGet("/sante", async (IWorkerClient worker, CancellationToken ct) =>
{
    // Réponse rapide même si le worker est occupé ou ouvre encore Sage : la borne s'en sert pour savoir si elle est en ligne.
    using var delai = CancellationTokenSource.CreateLinkedTokenSource(ct);
    delai.CancelAfter(TimeSpan.FromSeconds(5));
    var ping = await worker.Envoyer(Operations.Ping, null, delai.Token);
    return Results.Ok(new { api = "ok", worker = ping.Ok ? (object?)ping.Resultat : new { erreur = ping.MessageErreur } });
}).WithTags("Santé");

// ---------- Connexion des utilisateurs (login et mot de passe Sage, vérifiés par Sage via le worker) ----------
v1.MapPost("/connexion", async (ConnexionRequest demande, ServiceAuthentification auth, CancellationToken ct) =>
{
    var r = await auth.Connecter(demande, ct);
    if (r.Utilisateur is not { } u) return Reponses.Connexion(r.CodeErreur!, r.Message!);
    return Results.Ok(new
    {
        jeton = r.Jeton,
        expiration = u.Expiration,
        utilisateur = u.Login,
        administrateur = u.Administrateur,
        collaborateur = u.Collaborateur is { } no ? new { numero = no, nom = u.Nom, prenom = u.Prenom } : null,
        vendeur = u.Vendeur,
        caissier = u.Caissier,
        peutEncaisser = u.PeutEncaisser || !auth.Options.ExigerCaissier,
    });
}).WithTags("Connexion");

// ---------- Lectures (SQL, lecture seule) ----------
var lectures = v1.MapGroup("").WithTags("Lectures");

lectures.MapGet("/clients", (ILecturesSage l, string? recherche, int page = 1, int taille = 50) =>
    l.Clients(recherche, Math.Max(page, 1), Math.Clamp(taille, 1, 500)));

lectures.MapGet("/clients/{numero}", async (ILecturesSage l, string numero) =>
    await l.Client(numero) is { } c ? Results.Ok(c) : Results.NotFound());

lectures.MapGet("/articles", (ILecturesSage l, string? recherche, string? famille, int page = 1, int taille = 50) =>
    l.Articles(recherche, famille, Math.Max(page, 1), Math.Clamp(taille, 1, 500)));

lectures.MapGet("/articles/{reference}", async (ILecturesSage l, string reference) =>
    await l.Article(reference) is { } a ? Results.Ok(a) : Results.NotFound());

lectures.MapGet("/articles/{reference}/gammes", (ILecturesSage l, string reference) => l.Gammes(reference));

lectures.MapGet("/modes-reglement", (ILecturesSage l) => l.ModesReglement());

lectures.MapGet("/commandes-ouvertes", (ILecturesSage l, string? recherche, int taille = 200) =>
    l.CommandesOuvertes(recherche, Math.Clamp(taille, 1, 1000)));

lectures.MapGet("/commandes-ouvertes/{piece}", async (ILecturesSage l, string piece) =>
    await l.DetailCommande(piece) is { } d ? Results.Ok(d) : Results.NotFound());

// Instantané complet pour le mode hors ligne de la borne (clients, articles avec prix et stock, modes de règlement, valeurs de gamme,
// commandes à encaisser, tarifs par client et catégorie, conditionnements, souches, dépôts et stock par dépôt).
// Une partie annexe illisible (tarifs, souches, dépôts...) ne bloque pas le catalogue : elle est vide et signalée dans Avertissements.
lectures.MapGet("/catalogue", async (ILecturesSage l, ILecturesTarifs t, ILecturesErp erp, ControleStock stock, ServiceAuthentification auth,
    ILoggerFactory journaux) =>
{
    var log = journaux.CreateLogger("Catalogue");
    var avertissements = new List<string>();
    async Task<T?> Annexe<T>(string nom, Func<Task<T>> lire) where T : class
    {
        try { return await lire(); }
        catch (Exception e)
        {
            log.LogError(e, "Catalogue : lecture des {Partie} impossible", nom);
            avertissements.Add($"{nom} : {e.Message}");
            return null;
        }
    }
    var commandes = await Annexe("commandes à encaisser", () => l.CommandesOuvertes(null, 500));
    var tarifs = await Annexe("tarifs", () => t.Tarifs());
    var souches = await Annexe("souches", () => t.Souches());
    var depots = await Annexe("dépôts", () => erp.Depots());
    var stocksDepots = await Annexe("stocks par dépôt", () => t.StocksDepots());
    return new Catalogue(DateTime.UtcNow, await l.Clients(null, 1, 100_000), await l.Articles(null, null, 1, 100_000), await l.ModesReglement(),
        await l.Gammes(), await stock.Actif(), auth.Options.Active, auth.Options.Active && auth.Options.ExigerCaissier, commandes,
        tarifs, souches, depots, stocksDepots, avertissements.Count > 0 ? avertissements : null);
});

lectures.MapGet("/souches", (ILecturesTarifs t) => t.Souches()).WithSummary("Souches de numérotation des documents de vente (numero = DO_Souche)");

// Prix qu'aura une ligne pour ce client : pour vérifier dans Swagger que la catégorie tarifaire est bien suivie.
lectures.MapGet("/tarifs", async (ILecturesSage l, ILecturesTarifs t, string client, string article, string? gamme1, string? gamme2,
    string? conditionnement, decimal? quantiteConditionnement, decimal quantite = 1) =>
{
    var c = await l.Client(client.Trim());
    var a = await l.Article(article.Trim());
    if (c is null || a is null) return Results.NotFound(new { message = c is null ? $"Client inconnu : {client}" : $"Article inconnu : {article}" });
    var donnees = await t.Tarifs(c.Numero, c.CategorieTarif, [a.Reference]);
    Conditionnement? cond = null;
    if (!string.IsNullOrWhiteSpace(conditionnement))
    {
        cond = donnees.Conditionnements.FirstOrDefault(x => string.Equals(x.Enumere.Trim(), conditionnement.Trim(), StringComparison.OrdinalIgnoreCase)
            && (quantiteConditionnement is null || x.Quantite == quantiteConditionnement));
        if (cond is null) return Results.NotFound(new { message = $"Conditionnement inconnu pour {a.Reference} : {conditionnement}" });
    }
    var prix = Tarification.Calculer(donnees, a.Reference, a.PrixVenteHT, a.PrixTTC, c.Numero, c.CategorieTarif, gamme1, gamme2, cond, quantite);
    return Results.Ok(new
    {
        client = c.Numero, categorieTarif = c.CategorieTarif,
        intituleCategorie = donnees.Categories.FirstOrDefault(x => x.Numero == c.CategorieTarif)?.Intitule,
        article = a.Reference, unite = a.Unite, prixFiche = a.PrixVenteHT, conditionnement = cond, prix,
    });
}).WithSummary("Prix d'un article pour un client : tarif client, sinon catégorie tarifaire, gamme, conditionnement et remises");

// ---------- Lectures pour les extensions : CRM, livraison, géolocalisation, recouvrement ----------
static int Taille(int taille) => Math.Clamp(taille, 1, 1000);
static IResult TypeInconnu(string? type) =>
    Results.BadRequest(new { code = "TYPE_INCONNU", message = $"Type de document inconnu « {type} » : {TypesDocument.Liste}." });

var crm = v1.MapGroup("").WithTags("CRM");

crm.MapGet("/clients/{numero}/fiche", async (ILecturesErp l, Geolocalisation geo, string numero) =>
    await l.FicheClient(numero) is { } f
        ? Results.Ok(f with
        {
            Position = geo.Lire("client", f.Numero),
            AdressesLivraison = f.AdressesLivraison.Select(a => a with { Position = geo.Lire("adresse-livraison", a.Numero.ToString()) }).ToList(),
        })
        : Results.NotFound())
    .WithSummary("Fiche client complète : coordonnées, commercial, encours autorisé, contacts, adresses de livraison");

crm.MapGet("/clients/{numero}/contacts", (ILecturesErp l, string numero) => l.Contacts(numero));
crm.MapGet("/clients/{numero}/adresses-livraison", (ILecturesErp l, string numero) => l.AdressesLivraison(numero));
crm.MapGet("/clients/{numero}/documents", async (ILecturesErp l, string numero, string? type, DateTime? du, DateTime? au, int page = 1, int taille = 50) =>
    type != null && TypesDocument.Code(type) is null
        ? TypeInconnu(type)
        : Results.Ok(await l.Documents(TypesDocument.Code(type), numero, du, au, null, Math.Max(page, 1), Taille(taille))))
    .WithSummary("Historique des documents de vente d'un client (tous types, ou un type)");
crm.MapGet("/clients/{numero}/echeances", async (ILecturesErp l, string numero) =>
{
    var e = await l.Echeances(numero);
    Recouvrement.CalculerRetards(e, DateTime.Today);
    return e;
}).WithSummary("Écritures non lettrées du client (factures dues, avoirs) avec leurs jours de retard");
crm.MapGet("/collaborateurs", (ILecturesErp l) => l.Collaborateurs()).WithSummary("Collaborateurs actifs (commerciaux, caissiers, livreurs...)");
crm.MapGet("/depots", (ILecturesErp l) => l.Depots());

var documents = v1.MapGroup("/documents").WithTags("Documents de vente");
documents.MapGet("", async (ILecturesErp l, string? type, string? client, DateTime? du, DateTime? au, bool? cloture, int page = 1, int taille = 50) =>
    type != null && TypesDocument.Code(type) is null
        ? TypeInconnu(type)
        : Results.Ok(await l.Documents(TypesDocument.Code(type), client, du, au, cloture, Math.Max(page, 1), Taille(taille))))
    .WithSummary("Documents de vente, les plus récents d'abord. type : devis, commande, preparation, livraison, retour, avoir, facture, facture-comptabilisee");
documents.MapGet("/{type}/{piece}", async (ILecturesErp l, string type, string piece) =>
    TypesDocument.Code(type) is not { } code ? TypeInconnu(type)
    : await l.Document(code, piece) is { } d ? Results.Ok(d) : Results.NotFound())
    .WithSummary("Un document de vente et ses lignes");

var livraisons = v1.MapGroup("/livraisons").WithTags("Livraison");

livraisons.MapGet("/a-livrer", async (ILecturesErp l, Geolocalisation geo, DateTime? jusquau, int? depot) =>
    AvecPositions(await l.ALivrer(jusquau, depot), geo))
  .WithSummary("Bons de commande et préparations non clôturés à livrer (jusqu'à une date), avec adresse, contact, téléphone et position GPS");

livraisons.MapPost("/optimiser", async (ILecturesErp l, Geolocalisation geo, OptimisationRequest o) =>
{
    var aLivrer = AvecPositions(await l.ALivrer(null, null), geo).ToDictionary(a => a.Piece, StringComparer.OrdinalIgnoreCase);
    var avec = new List<Itineraire.Point>();
    var sans = new List<string>();
    foreach (var piece in o.Pieces.Select(p => p.Trim().ToUpperInvariant()))
        if (aLivrer.GetValueOrDefault(piece)?.Position is { } pos) avec.Add(new(piece, pos.Latitude, pos.Longitude));
        else sans.Add(piece);
    var depart = o.Latitude is { } lat && o.Longitude is { } lon ? (lat, lon)
        : o.Depot is { } d && geo.Lire("depot", d.ToString()) is { } pd ? (pd.Latitude, pd.Longitude) : ((double, double)?)null;
    return Results.Ok(Itineraire.Ordonner(avec, depart, o.Retour ?? depart != null, sans));
}).WithSummary("Ordre de passage conseillé (plus proche voisin puis 2-opt, à vol d'oiseau) depuis un dépôt ou la position du livreur. Les pièces sans position GPS vont à la fin");

livraisons.MapGet("/tournees", (Livraison liv, ServiceAuthentification auth, HttpContext http, DateTime? du, DateTime? au, int? livreur, bool? miennes, int taille = 100) =>
    liv.Lister(du, au, miennes == true ? auth.Lire(http)?.Collaborateur ?? -1 : livreur, Math.Clamp(taille, 1, 1000)))
  .WithSummary("Tournées (sans le détail), les plus récentes d'abord. miennes=true : celles du livreur connecté");
livraisons.MapGet("/tournees/{id}", (Livraison liv, string id) => liv.Lire(id) is { } t ? Results.Ok(t) : Results.NotFound())
  .WithSummary("Une tournée et ses arrêts dans l'ordre de passage");
livraisons.MapPut("/tournees/{id}", async (ILecturesErp l, Geolocalisation geo, Livraison liv, ServiceAuthentification auth, HttpContext http, string id, TourneeRequest t) =>
{
    var u = auth.Lire(http);
    if (u == null && auth.Options.Active) return Reponses.ConnexionRequise();
    var erreurs = Livraison.Verifier(id, t).ToList();
    if (erreurs.Count > 0) return Reponses.Invalide(erreurs);
    var aLivrer = AvecPositions(await l.ALivrer(null, null), geo).ToDictionary(a => a.Piece, StringComparer.OrdinalIgnoreCase);
    // Lignes d'articles des pièces, pour le contrôle au chargement et chez le client.
    var lignes = new Dictionary<string, IReadOnlyList<LignePiece>>(StringComparer.OrdinalIgnoreCase);
    foreach (var piece in t.Pieces.Select(p => p.Trim().ToUpperInvariant()).Distinct())
        if (aLivrer.TryGetValue(piece, out var a) && TypesDocument.Code(a.Type) is { } code)
            lignes[piece] = (await l.Document(code, piece))?.Lignes ?? [];
    var (tournee, refus) = liv.Enregistrer(id, t, aLivrer, a => a.Position, p => lignes.GetValueOrDefault(p) ?? [], u?.Login);
    return tournee is null ? Reponses.Invalide(refus) : Results.Ok(tournee);
}).WithSummary("Crée ou met à jour une tournée : date, livreur (collaborateur), dépôt, pièces dans l'ordre de passage. L'adresse et le contact sont recopiés pour le livreur");
livraisons.MapDelete("/tournees/{id}", (Livraison liv, ServiceAuthentification auth, HttpContext http, string id) =>
{
    if (auth.Lire(http) == null && auth.Options.Active) return Reponses.ConnexionRequise();
    return liv.Supprimer(id) switch
    {
        null => Results.NotFound(),
        false => Results.Conflict(new { code = "TOURNEE_COMMENCEE", message = "Des arrêts sont déjà traités : la tournée ne peut plus être supprimée." }),
        true => Results.NoContent(),
    };
});
livraisons.MapPut("/tournees/{id}/arrets/{piece}", (Livraison liv, Geolocalisation geo, ServiceAuthentification auth, HttpContext http, string id, string piece,
    CompteRenduArret r) =>
{
    var u = auth.Lire(http);
    if (u == null && auth.Options.Active) return Reponses.ConnexionRequise();
    var erreurs = Livraison.Verifier(r).ToList();
    if (erreurs.Count > 0) return Reponses.Invalide(erreurs);
    Arret? a;
    try { a = liv.CompteRendu(id, piece, r, u?.Login); }
    catch (ArgumentException e) { return Reponses.Invalide([e.Message]); }
    if (a is null) return Results.NotFound();
    // Livré sur place et destination sans position : la position du livreur devient celle de l'adresse (ou du client).
    if (a.Statut is "livre" or "partiel" && a.Latitude is null && r.Latitude is { } lat && r.Longitude is { } lon)
    {
        var (cible, cle) = a.AdresseLivraison is { } li ? ("adresse-livraison", li.ToString()) : ("client", a.Client);
        if (geo.Lire(cible, cle) is null) geo.Enregistrer(cible, cle, new PositionRequest(lat, lon, null, "livraison"), u?.Login);
    }
    return Results.Ok(a);
}).WithSummary("Compte rendu du livreur sur un arrêt : livre, partiel, echec (motif), réceptionnaire, signature PNG, position");
livraisons.MapGet("/tournees/{id}/arrets/{piece}/signature", (Livraison liv, string id, string piece) =>
    liv.Signature(id, piece) is { } s ? Results.Bytes(Convert.FromBase64String(s[(s.IndexOf(',') + 1)..]), "image/png") : Results.NotFound())
  .WithSummary("Signature du réceptionnaire (image PNG)");
livraisons.MapPut("/tournees/{id}/chargement", (Livraison liv, ServiceAuthentification auth, HttpContext http, string id, ChargementRequest r) =>
{
    var u = auth.Lire(http);
    if (u == null && auth.Options.Active) return Reponses.ConnexionRequise();
    var erreurs = Livraison.Verifier(r).ToList();
    if (erreurs.Count > 0) return Reponses.Invalide(erreurs);
    try { return liv.ValiderChargement(id, r, u?.Login) is { } c ? Results.Ok(c) : Results.NotFound(); }
    catch (ArgumentException e) { return Reponses.Invalide([e.Message]); }
}).WithSummary("Contrôle du chargement au dépôt : quantité reçue par ligne d'article (pièce + ligne), nom et signature du responsable du dépôt");
livraisons.MapGet("/tournees/{id}/chargement/signature", (Livraison liv, string id) =>
    liv.SignatureChargement(id) is { } s ? Results.Bytes(Convert.FromBase64String(s[(s.IndexOf(',') + 1)..]), "image/png") : Results.NotFound())
  .WithSummary("Signature du responsable du dépôt au chargement (image PNG)");
livraisons.MapPut("/tournees/{id}/courses/{course}", (Livraison liv, ServiceAuthentification auth, HttpContext http, string id, string course, CourseRequest r) =>
{
    var u = auth.Lire(http);
    if (u == null && auth.Options.Active) return Reponses.ConnexionRequise();
    var erreurs = Livraison.Verifier(course, r).ToList();
    if (erreurs.Count > 0) return Reponses.Invalide(erreurs);
    return liv.EnregistrerCourse(id, course, r, u?.Login) is { } k ? Results.Ok(k) : Results.NotFound();
}).WithSummary("Autre course de la tournée (livrer ou récupérer quelque chose hors pièce Sage) : description, adresse, statut a-faire, en-cours, fait, reporte, annule");
livraisons.MapDelete("/tournees/{id}/courses/{course}", (Livraison liv, ServiceAuthentification auth, HttpContext http, string id, string course) =>
{
    if (auth.Lire(http) == null && auth.Options.Active) return Reponses.ConnexionRequise();
    return liv.SupprimerCourse(id, course) ? Results.NoContent() : Results.NotFound();
});
livraisons.MapGet("/tableau-de-bord", (Livraison liv, DateTime? du, DateTime? au, int? livreur, int? depot, string? client, string? statut) =>
    liv.Tableau(new FiltreTableau(du, au, livreur, depot, client, statut)))
  .WithSummary("Tableau de bord des livraisons : indicateurs, par jour, par livreur, motifs d'échec, courses, et liste des arrêts, sur les mêmes filtres");
livraisons.MapGet("/suivi/{piece}", (Livraison liv, string piece) => liv.Suivi(piece))
  .WithSummary("Passages d'une pièce en tournée (livrée, échec, motif, heure, réceptionnaire), le plus récent d'abord");

static IEnumerable<ALivrer> AvecPositions(IEnumerable<ALivrer> liste, Geolocalisation geo)
{
    var clients = geo.Toutes("client");
    var adresses = geo.Toutes("adresse-livraison");
    return liste.Select(x => x with
    {
        Position = (x.AdresseLivraison is { } li ? adresses.GetValueOrDefault(li.ToString()) : null) ?? clients.GetValueOrDefault(x.Client),
    }).ToList();
}

var recouvrement = v1.MapGroup("/recouvrement").WithTags("Recouvrement");
recouvrement.MapGet("/echeances", async (ILecturesErp l, string? client, bool echuesSeulement = false) =>
{
    var e = await l.Echeances(client);
    Recouvrement.CalculerRetards(e, DateTime.Today);
    return echuesSeulement ? e.Where(x => x.JoursRetard > 0).ToList() : e;
}).WithSummary("Écritures clients non lettrées, avec jours de retard. echuesSeulement=true : seulement les échéances dépassées");
recouvrement.MapGet("/balance-agee", async (ILecturesErp l) => Recouvrement.BalanceAgee(await l.Echeances(null), DateTime.Today))
    .WithSummary("Balance âgée par client : non échu, 1-30, 31-60, 61-90, plus de 90 jours, crédits non affectés");

var positions = v1.MapGroup("/geolocalisation").WithTags("Géolocalisation");
positions.MapGet("/{cible}", (Geolocalisation geo, string cible) =>
    Geolocalisation.Cibles.Contains(cible) ? Results.Ok(geo.Toutes(cible)) : Results.NotFound())
    .WithSummary("Toutes les positions d'une cible : client, adresse-livraison ou depot");
positions.MapGet("/{cible}/{cle}", (Geolocalisation geo, string cible, string cle) =>
    Geolocalisation.Cibles.Contains(cible) && geo.Lire(cible, cle) is { } p ? Results.Ok(p) : Results.NotFound());
positions.MapPut("/{cible}/{cle}", (Geolocalisation geo, ServiceAuthentification auth, HttpContext http, string cible, string cle, PositionRequest p) =>
{
    var u = auth.Lire(http);
    if (u == null && auth.Options.Active) return Reponses.ConnexionRequise();
    var erreurs = Geolocalisation.Verifier(cible, p).ToList();
    return erreurs.Count > 0 ? Reponses.Invalide(erreurs) : Results.Ok(geo.Enregistrer(cible, cle.Trim(), p, u?.Login));
}).WithSummary("Enregistre la position GPS d'un client, d'une adresse de livraison (numéro LI_No) ou d'un dépôt. Gardée par l'API, pas dans Sage");
positions.MapDelete("/{cible}/{cle}", (Geolocalisation geo, ServiceAuthentification auth, HttpContext http, string cible, string cle) =>
{
    if (auth.Lire(http) == null && auth.Options.Active) return Reponses.ConnexionRequise();
    return geo.Supprimer(cible, cle) ? Results.NoContent() : Results.NotFound();
});

v1.MapGet("/modifications", async (ILecturesErp l, string table, DateTime depuis, int taille = 1000) =>
{
    if (!TablesSuivies.Liste.ContainsKey(table))
        return Results.BadRequest(new { code = "TABLE_INCONNUE", message = $"Tables suivies : {string.Join(", ", TablesSuivies.Liste.Keys)}." });
    return await l.Modifications(table, depuis, Math.Clamp(taille, 1, 10_000)) is { } m
        ? Results.Ok(m)
        : Results.Problem("Cette base Sage n'a pas de colonne cbModification : synchronisation incrémentale impossible.", statusCode: 501);
}).WithTags("Synchronisation")
  .WithSummary("Codes modifiés dans Sage depuis une date (clients, articles, documents, adresses-livraison, contacts, ecritures), pour qu'une extension ne relise que ce qui a changé");

// ---------- CRM : vue 360° des clients et activités des commerciaux (visites, appels, rendez-vous...) ----------
var crmApp = v1.MapGroup("/crm").WithTags("CRM");

crmApp.MapGet("/clients/{numero}/synthese", async (ILecturesErp l, Geolocalisation geo, Crm crm, string numero) =>
{
    if (await l.FicheClient(numero) is not { } f) return Results.NotFound();
    var echeances = await l.Echeances(f.Numero);
    Recouvrement.CalculerRetards(echeances, DateTime.Today);
    return Results.Ok(new
    {
        fiche = f with
        {
            Position = geo.Lire("client", f.Numero),
            AdressesLivraison = f.AdressesLivraison.Select(a => a with { Position = geo.Lire("adresse-livraison", a.Numero.ToString()) }).ToList(),
        },
        indicateurs = await l.Indicateurs(f.Numero, DateTime.Today),
        recouvrement = new
        {
            solde = echeances.Sum(e => e.Montant),
            echu = echeances.Where(e => e.JoursRetard > 0).Sum(e => e.Montant),
            retardMaxJours = echeances.Select(e => e.JoursRetard).DefaultIfEmpty(0).Max(),
            derniereRelance = echeances.Max(e => e.DateRelance),
        },
        prochainesActions = crm.Lister(new FiltreActivites(f.Numero, null, null, "a-faire", null, null, null, 1, 20)),
        activitesRecentes = crm.Lister(new FiltreActivites(f.Numero, null, null, "fait", null, null, null, 1, 10)),
    });
}).WithSummary("Vue 360° d'un client : fiche, CA 12 mois, articles les plus achetés, commandes et devis en cours, impayés, activités");

crmApp.MapGet("/portefeuille", async (ILecturesErp l, Crm crm, ServiceAuthentification auth, HttpContext http, int? collaborateur) =>
{
    var co = collaborateur ?? auth.Lire(http)?.Collaborateur;
    if (co is null) return Results.BadRequest(new { code = "COLLABORATEUR_REQUIS", message = "Indiquez collaborateur, ou connectez un utilisateur rattaché à un collaborateur Sage." });
    var dernieres = crm.DernieresActivites();
    var clients = await l.Portefeuille(co.Value);
    foreach (var c in clients) c.DerniereActivite = dernieres.TryGetValue(c.Numero, out var d) ? d : null;
    return Results.Ok(clients);
}).WithSummary("Clients dont le collaborateur est le représentant, avec la date de la dernière activité. Par défaut : l'utilisateur connecté");

crmApp.MapGet("/activites", (Crm crm, string? client, int? collaborateur, string? utilisateur, string? statut, string? type, DateTime? du, DateTime? au,
    int page = 1, int taille = 100) =>
    crm.Lister(new FiltreActivites(client, collaborateur, utilisateur, statut, type, du, au, Math.Max(page, 1), Math.Clamp(taille, 1, 1000))))
    .WithSummary("Activités filtrées. statut=a-faire : agenda (par date prévue) ; sinon les plus récentes d'abord");
crmApp.MapGet("/activites/{id}", (Crm crm, string id) => crm.Lire(id) is { } a ? Results.Ok(a) : Results.NotFound());
crmApp.MapPut("/activites/{id}", (Crm crm, ServiceAuthentification auth, HttpContext http, string id, ActiviteRequest a) =>
{
    var u = auth.Lire(http);
    if (u == null && auth.Options.Active) return Reponses.ConnexionRequise();
    var erreurs = Crm.Verifier(id, a).ToList();
    return erreurs.Count > 0 ? Reponses.Invalide(erreurs) : Results.Ok(crm.Enregistrer(id, a, u?.Login, u?.Collaborateur));
}).WithSummary("Crée ou met à jour une activité (id choisi par l'application, par exemple un GUID : renvoyer la même requête ne crée pas de doublon)");
crmApp.MapDelete("/activites/{id}", (Crm crm, ServiceAuthentification auth, HttpContext http, string id) =>
{
    if (auth.Lire(http) == null && auth.Options.Active) return Reponses.ConnexionRequise();
    return crm.Supprimer(id) ? Results.NoContent() : Results.NotFound();
});

// ---------- Écritures (worker Objets Métiers, idempotentes) ----------
var ecritures = v1.MapGroup("/commandes").WithTags("Commandes et encaissements");

ecritures.MapPost("", async (CommandeRequest c, ServiceEcritures s, ServiceAuthentification auth, HttpContext http, CancellationToken ct) =>
{
    var u = auth.Lire(http);
    if (u == null && auth.Options.Active) return Reponses.ConnexionRequise();
    var erreurs = Validation.Verifier(c);
    if (erreurs.Count > 0) return Reponses.Invalide(erreurs);
    var r = await s.CreerCommande(c, CleApi.Application(http), u, ct);
    return Reponses.Depuis(r, v => v.DejaExistante ? Results.Ok(v) : Results.Created($"/api/v1/commandes/{v.IdExterne}", v));
});

ecritures.MapGet("/{idExterne}", async (string idExterne, ServiceEcritures s) =>
    await s.PieceCommande(idExterne) is { } piece ? Results.Ok(new { idExterne, piece }) : Results.NotFound());

ecritures.MapPost("/{idExterne}/encaissements", async (string idExterne, EncaissementRequest e, ServiceEcritures s, ServiceAuthentification auth,
    HttpContext http, CancellationToken ct) =>
{
    if (RefusEncaissement(auth, http, e, out var u) is { } refus) return refus;
    var r = await s.CreerEncaissement(idExterne, e, CleApi.Application(http), u, ct);
    return Reponses.Depuis(r, v => v.DejaExistant ? Results.Ok(v) : Results.Created($"/api/v1/commandes/{idExterne}/encaissements/{v.IdExterne}", v));
});

// Encaissement d'une commande désignée par sa pièce Sage (par exemple saisie dans Sage, ou prise par un vendeur sur la borne).
ecritures.MapPost("/piece/{piece}/encaissements", async (string piece, EncaissementRequest e, ServiceEcritures s, ServiceAuthentification auth,
    HttpContext http, CancellationToken ct) =>
{
    if (RefusEncaissement(auth, http, e, out var u) is { } refus) return refus;
    if (string.IsNullOrWhiteSpace(piece) || piece.Length > Validation.LongueurPiece)
        return Reponses.Invalide(new[] { $"piece : {Validation.LongueurPiece} caractères maximum." });
    var r = await s.CreerEncaissementSurPiece(piece, e, CleApi.Application(http), u, ct);
    return Reponses.Depuis(r, v => v.DejaExistant ? Results.Ok(v) : Results.Created($"/api/v1/commandes/piece/{v.PieceCommande}/encaissements/{v.IdExterne}", v));
});

app.Run();

/// <summary>Connexion, droit d'encaisser et validation communs aux deux routes d'encaissement ; null si l'encaissement peut partir.</summary>
static IResult? RefusEncaissement(ServiceAuthentification auth, HttpContext http, EncaissementRequest e, out Utilisateur? u)
{
    u = auth.Lire(http);
    if (u == null && auth.Options.Active) return Reponses.ConnexionRequise();
    if (u != null && auth.Options.Active && auth.Options.ExigerCaissier && !u.PeutEncaisser)
        return Reponses.DroitRefuse($"{u.Login} ne peut pas encaisser : cochez « Caissier » sur sa fiche collaborateur dans Sage (champ Utilisateur = {u.Login}).");
    var erreurs = Validation.Verifier(e);
    return erreurs.Count > 0 ? Reponses.Invalide(erreurs) : null;
}


public partial class Program;
