using System.Text.Json;
using Microsoft.Extensions.Options;
using Sage100Api;
using Sage100Api.Contracts;
using Sage100Api.Ecritures;
using Sage100Api.Journal;
using Sage100Api.Lectures;
using Sage100Api.Worker;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

builder.Services.Configure<SageOptions>(builder.Configuration.GetSection("Sage"));
builder.Services.AddSingleton<ILecturesSage, LecturesSql>();
builder.Services.AddSingleton<IWorkerClient, WorkerClient>();
builder.Services.AddSingleton<JournalOperations>();
builder.Services.AddSingleton<ServiceEcritures>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new() { Title = "API Sage 100", Version = "v1", Description = "Liaison entre Sage 100 (Objets Métiers) et les applications externes." });
    o.AddSecurityDefinition("ApiKey", new() { Name = CleApi.Entete, In = Microsoft.OpenApi.Models.ParameterLocation.Header, Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey });
    o.AddSecurityRequirement(new()
    {
        [new() { Reference = new() { Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme, Id = "ApiKey" } }] = Array.Empty<string>(),
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

// Instantané complet pour le mode hors ligne de la borne (clients, articles avec prix et stock, modes de règlement, valeurs de gamme).
lectures.MapGet("/catalogue", async (ILecturesSage l) =>
    new Catalogue(DateTime.UtcNow, await l.Clients(null, 1, 100_000), await l.Articles(null, null, 1, 100_000), await l.ModesReglement(),
        await l.Gammes()));

// ---------- Écritures (worker Objets Métiers, idempotentes) ----------
var ecritures = v1.MapGroup("/commandes").WithTags("Commandes et encaissements");

ecritures.MapPost("", async (CommandeRequest c, ServiceEcritures s, HttpContext http, CancellationToken ct) =>
{
    var erreurs = Validation.Verifier(c);
    if (erreurs.Count > 0) return Reponses.Invalide(erreurs);
    var r = await s.CreerCommande(c, CleApi.Application(http), ct);
    return Reponses.Depuis(r, v => v.DejaExistante ? Results.Ok(v) : Results.Created($"/api/v1/commandes/{v.IdExterne}", v));
});

ecritures.MapGet("/{idExterne}", async (string idExterne, ServiceEcritures s) =>
    await s.PieceCommande(idExterne) is { } piece ? Results.Ok(new { idExterne, piece }) : Results.NotFound());

ecritures.MapPost("/{idExterne}/encaissements", async (string idExterne, EncaissementRequest e, ServiceEcritures s, HttpContext http, CancellationToken ct) =>
{
    var erreurs = Validation.Verifier(e);
    if (erreurs.Count > 0) return Reponses.Invalide(erreurs);
    var r = await s.CreerEncaissement(idExterne, e, CleApi.Application(http), ct);
    return Reponses.Depuis(r, v => v.DejaExistant ? Results.Ok(v) : Results.Created($"/api/v1/commandes/{idExterne}/encaissements/{v.IdExterne}", v));
});

app.Run();

public partial class Program;
