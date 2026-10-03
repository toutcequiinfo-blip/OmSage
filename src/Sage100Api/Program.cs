using System.Text.Json;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using Sage100Api;
using Sage100Api.Contracts;
using Sage100Api.Ecritures;
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
// commandes à encaisser).
lectures.MapGet("/catalogue", async (ILecturesSage l, ControleStock stock, ServiceAuthentification auth) =>
    new Catalogue(DateTime.UtcNow, await l.Clients(null, 1, 100_000), await l.Articles(null, null, 1, 100_000), await l.ModesReglement(),
        await l.Gammes(), await stock.Actif(), auth.Options.Active, auth.Options.Active && auth.Options.ExigerCaissier, await l.CommandesOuvertes(null, 500)));

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
