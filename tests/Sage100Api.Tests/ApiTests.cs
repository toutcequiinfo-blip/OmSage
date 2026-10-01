using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sage100Api.Contracts;
using Sage100Api.Lectures;
using Sage100Api.Worker;
using Xunit;

namespace Sage100Api.Tests;

/// <summary>Tests de l'API sans Sage : le worker et les lectures SQL sont simulés.</summary>
public sealed class ApiTests : IDisposable
{
    const string Cle = "cle-de-test";
    readonly string _journal = Path.Combine(Path.GetTempPath(), $"journal-{Guid.NewGuid():N}.db");
    readonly FauxWorker _worker = new();
    readonly FaussesLectures _lectures = new();
    readonly WebApplicationFactory<Program> _usine;
    readonly HttpClient _http;

    public ApiTests()
    {
        _usine = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Sage:CheminJournal", _journal);
            b.UseSetting("Sage:ClesApi:borne-test", Cle);
            b.ConfigureServices(s =>
            {
                s.RemoveAll<IWorkerClient>();
                s.AddSingleton<IWorkerClient>(_worker);
                s.RemoveAll<ILecturesSage>();
                s.AddSingleton<ILecturesSage>(_lectures);
            });
        });
        _http = _usine.CreateClient();
        _http.DefaultRequestHeaders.Add("X-Api-Key", Cle);
    }

    static CommandeRequest Commande(string id = "BORNE1-000001") => new()
    {
        IdExterne = id,
        Client = "CISEL",
        Lignes = { new LigneCommande { Article = "CHORFA", Quantite = 1 } },
    };

    [Fact]
    public async Task Sans_cle_API_la_requete_est_refusee()
    {
        var anonyme = _usine.CreateClient();
        var r = await anonyme.GetAsync("/api/v1/clients");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task La_sante_est_publique()
    {
        var anonyme = _usine.CreateClient();
        var r = await anonyme.GetAsync("/api/v1/sante");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Une_commande_invalide_renvoie_422_sans_appeler_Sage()
    {
        var c = Commande();
        c.Client = "";
        c.Lignes[0].Quantite = 0;
        var r = await _http.PostAsJsonAsync("/api/v1/commandes", c);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal(0, _worker.Appels(Operations.CreerCommande));
    }

    [Fact]
    public async Task Renvoyer_la_meme_commande_ne_cree_pas_de_doublon()
    {
        var r1 = await _http.PostAsJsonAsync("/api/v1/commandes", Commande());
        var r2 = await _http.PostAsJsonAsync("/api/v1/commandes", Commande());

        Assert.Equal(HttpStatusCode.Created, r1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        var c2 = await r2.Content.ReadFromJsonAsync<CommandeResult>();
        Assert.Equal("BC00100", c2!.Piece);
        Assert.True(c2.DejaExistante);
        Assert.Equal(1, _worker.Appels(Operations.CreerCommande));
    }

    [Fact]
    public async Task Renvoyer_le_meme_encaissement_ne_cree_pas_de_doublon()
    {
        await _http.PostAsJsonAsync("/api/v1/commandes", Commande());
        var p = new EncaissementRequest { IdExterne = "BORNE1-PAY-1", Mode = "Mobile money", Montant = 500, ReferencePaiement = "MM123" };

        var r1 = await _http.PostAsJsonAsync("/api/v1/commandes/BORNE1-000001/encaissements", p);
        var r2 = await _http.PostAsJsonAsync("/api/v1/commandes/BORNE1-000001/encaissements", p);

        Assert.Equal(HttpStatusCode.Created, r1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        Assert.Equal(1, _worker.Appels(Operations.CreerEncaissement));
        Assert.Equal("BC00100", _worker.DernierePiece);
    }

    [Fact]
    public async Task Un_encaissement_sur_une_commande_inconnue_renvoie_404()
    {
        var p = new EncaissementRequest { IdExterne = "PAY-X", Mode = "Espèces", Montant = 10 };
        var r = await _http.PostAsJsonAsync("/api/v1/commandes/INCONNUE/encaissements", p);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal(0, _worker.Appels(Operations.CreerEncaissement));
    }

    [Fact]
    public async Task Un_refus_Sage_renvoie_422_et_la_commande_peut_etre_retentee()
    {
        _worker.ProchaineErreur = (CodesErreur.SageMetier, "Stock insuffisant");
        var r1 = await _http.PostAsJsonAsync("/api/v1/commandes", Commande("BORNE1-000002"));
        var r2 = await _http.PostAsJsonAsync("/api/v1/commandes", Commande("BORNE1-000002"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, r1.StatusCode);
        Assert.Equal(HttpStatusCode.Created, r2.StatusCode);
        Assert.Equal(2, _worker.Appels(Operations.CreerCommande));
    }

    [Fact]
    public async Task Worker_injoignable_renvoie_503()
    {
        _worker.ProchaineErreur = (CodesErreur.Technique, "Worker Objets Métiers injoignable");
        var r = await _http.PostAsJsonAsync("/api/v1/commandes", Commande("BORNE1-000003"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
    }

    public void Dispose()
    {
        _http.Dispose();
        _usine.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_journal)) File.Delete(_journal);
    }

    sealed class FauxWorker : IWorkerClient
    {
        readonly Dictionary<string, int> _appels = new();
        public (string Code, string Message)? ProchaineErreur;
        public string? DernierePiece;

        public int Appels(string op) => _appels.GetValueOrDefault(op);

        public Task<WorkerResponse> Envoyer(string operation, object? donnees, CancellationToken ct = default)
        {
            _appels[operation] = Appels(operation) + 1;
            if (ProchaineErreur is { } e)
            {
                ProchaineErreur = null;
                return Task.FromResult(new WorkerResponse { Ok = false, CodeErreur = e.Code, MessageErreur = e.Message });
            }
            object resultat = donnees switch
            {
                CommandeRequest c => new CommandeResult { IdExterne = c.IdExterne, Piece = "BC00100", NetAPayer = 1303.2 },
                EncaissementCommandeRequest p => Encaissement(p),
                _ => new { sage = true },
            };
            return Task.FromResult(new WorkerResponse { Ok = true, Resultat = JsonSerializer.SerializeToElement(resultat, WorkerProtocol.Json) });
        }

        EncaissementResult Encaissement(EncaissementCommandeRequest p)
        {
            DernierePiece = p.PieceCommande;
            return new EncaissementResult { IdExterne = p.Encaissement.IdExterne, PieceCommande = p.PieceCommande, Montant = p.Encaissement.Montant };
        }
    }

    sealed class FaussesLectures : ILecturesSage
    {
        public Task<IReadOnlyList<Client>> Clients(string? recherche, int page, int taille) =>
            Task.FromResult<IReadOnlyList<Client>>(new[] { new Client("CISEL", "Ciselure", null, null, null) });
        public Task<Client?> Client(string numero) => Task.FromResult<Client?>(null);
        public Task<IReadOnlyList<Article>> Articles(string? recherche, string? famille, int page, int taille) =>
            Task.FromResult<IReadOnlyList<Article>>(Array.Empty<Article>());
        public Task<Article?> Article(string reference) => Task.FromResult<Article?>(null);
        public Task<IReadOnlyList<ModeReglement>> ModesReglement() => Task.FromResult<IReadOnlyList<ModeReglement>>(Array.Empty<ModeReglement>());
        public Task<string?> PieceCommande(string idExterne) => Task.FromResult<string?>(null);
    }
}
