using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Sage100Api.Contracts;
using Sage100Api.Extensions;
using Sage100Api.Lectures;
using Sage100Api.TableauDeBord;
using Sage100Api.Worker;
using Xunit;

namespace Sage100Api.Tests;

/// <summary>Plusieurs sociétés (bases Sage) servies par une même installation.</summary>
public sealed class MultiSocietesTests : IDisposable
{
    const string Cle = "cle-test";
    readonly string _dossier = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"multi-{Guid.NewGuid():N}")).FullName;
    readonly List<(string Operation, string Dossier)> _appels = [];
    readonly WebApplicationFactory<Program> _usine;

    public MultiSocietesTests()
    {
        _usine = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Sage:ChaineSql", "Server=SRV;Database=BIJOU;Integrated Security=true");
            b.UseSetting("Sage:CheminJournal", Path.Combine(_dossier, "journal.db"));
            b.UseSetting("Sage:ClesApi:borne-test", Cle);
            b.UseSetting("Sage:Dossiers:0:Base", "BIJOU");
            b.UseSetting("Sage:Dossiers:0:Intitule", "Bijouterie");
            b.UseSetting("Sage:Dossiers:1:Base", "MODE");
            b.UseSetting("Authentification:Active", "true");
            b.UseSetting("Authentification:CleSignature", Convert.ToBase64String(new byte[32]));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<IWorkerClient>();
                s.AddSingleton<IWorkerClient>(sp => new WorkerEspion(sp.GetRequiredService<Dossiers>(), _appels));
                s.RemoveAll<ILecturesSage>();
                s.AddSingleton<ILecturesSage>(new ApiTests.FaussesLectures());
                s.RemoveAll<ILecturesErp>();
                s.AddSingleton<ILecturesErp>(new ApiTests.FaussesLecturesErp());
                s.RemoveAll<ILecturesTarifs>();
                s.AddSingleton<ILecturesTarifs>(new ApiTests.FaussesLecturesTarifs());
                s.RemoveAll<ILecturesTableauDeBord>();
                s.AddSingleton<ILecturesTableauDeBord>(new FaussesLecturesTableauDeBord());
            });
        });
    }

    public void Dispose()
    {
        _usine.Dispose();
        try { Directory.Delete(_dossier, true); } catch (IOException) { }
    }

    HttpClient Client(string? jeton = null, string? dossier = null)
    {
        var http = _usine.CreateClient();
        http.DefaultRequestHeaders.Add("X-Api-Key", Cle);
        if (jeton != null) http.DefaultRequestHeaders.Authorization = new("Bearer", jeton);
        if (dossier != null) http.DefaultRequestHeaders.Add(Dossiers.Entete, dossier);
        return http;
    }

    async Task<string> Jeton(string dossier)
    {
        var r = await Client().PostAsJsonAsync("/api/v1/connexion", new ConnexionRequest { Utilisateur = "MARIE", MotDePasse = "bon", Dossier = dossier });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jeton").GetString()!;
    }

    [Fact]
    public async Task La_liste_des_societes_suit_la_configuration()
    {
        var liste = await Client().GetFromJsonAsync<JsonElement>("/api/v1/dossiers");
        Assert.Equal(2, liste.GetArrayLength());
        Assert.Equal("BIJOU", liste[0].GetProperty("code").GetString());
        Assert.Equal("Bijouterie", liste[0].GetProperty("intitule").GetString());
        Assert.True(liste[0].GetProperty("principal").GetBoolean());
        Assert.Equal("MODE", liste[1].GetProperty("code").GetString());
    }

    [Fact]
    public async Task La_connexion_exige_une_societe_et_la_verifie_dans_sa_base()
    {
        var sans = await Client().PostAsJsonAsync("/api/v1/connexion", new ConnexionRequest { Utilisateur = "MARIE", MotDePasse = "bon" });
        Assert.Equal(HttpStatusCode.BadRequest, sans.StatusCode);
        var inconnue = await Client().PostAsJsonAsync("/api/v1/connexion", new ConnexionRequest { Utilisateur = "MARIE", MotDePasse = "bon", Dossier = "XX" });
        Assert.Equal(HttpStatusCode.BadRequest, inconnue.StatusCode);

        await Jeton("mode");
        Assert.Contains((Operations.VerifierUtilisateur, "MODE"), _appels);

        // La réponse dit la société de la connexion (les applications l'affichent et la renvoient en X-Dossier).
        var r = await Client().PostAsJsonAsync("/api/v1/connexion", new ConnexionRequest { Utilisateur = "MARIE", MotDePasse = "bon", Dossier = "mode" });
        Assert.Equal("MODE", (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("dossier").GetString());
    }

    [Fact]
    public async Task Une_requete_sans_societe_est_refusee_et_le_jeton_fixe_la_societe()
    {
        var sans = await Client().GetAsync("/api/v1/clients");
        Assert.Equal(HttpStatusCode.BadRequest, sans.StatusCode);
        Assert.Contains(SocieteDeLaRequete.CodeRequis, await sans.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, (await Client(dossier: "MODE").GetAsync("/api/v1/clients")).StatusCode);

        var jeton = await Jeton("MODE");
        Assert.Equal(HttpStatusCode.OK, (await Client(jeton).GetAsync("/api/v1/clients")).StatusCode);
        // Un jeton de MODE ne sert pas pour BIJOU : il faut se reconnecter.
        var autre = await Client(jeton, "BIJOU").GetAsync("/api/v1/clients");
        Assert.Equal(HttpStatusCode.Conflict, autre.StatusCode);
    }

    [Fact]
    public async Task Les_ecritures_partent_vers_le_worker_avec_la_societe_du_jeton()
    {
        var jeton = await Jeton("MODE");
        var r = await Client(jeton).PostAsJsonAsync("/api/v1/commandes", new CommandeRequest
        {
            IdExterne = "BORNE1-000001",
            Client = "CISEL",
            Lignes = [new LigneCommande { Article = "BAOR01", Quantite = 1 }],
        });
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        Assert.Contains((Operations.CreerCommande, "MODE"), _appels);
    }

    [Fact]
    public void Chaque_societe_a_ses_propres_donnees_locales()
    {
        var o = new SageOptions
        {
            ChaineSql = "Server=SRV;Database=BIJOU;Integrated Security=true",
            CheminJournal = Path.Combine(_dossier, "local", "journal.db"),
            Dossiers = [new() { Base = "BIJOU" }, new() { Base = "MODE" }],
        };
        var dossiers = new Dossiers(new OptionsFixes<SageOptions>(o));
        var crm = new Crm(Options.Create(o), dossiers);
        using (Dossiers.Activer("MODE"))
            crm.Enregistrer("A1", new ActiviteRequest("CISEL", "visite", "Chez MODE", null, null, null, null, null, null, null, null, null), null, null);

        Assert.True(File.Exists(Path.Combine(_dossier, "local", "dossiers", "MODE", "sage100api-extensions.db")));
        Assert.Empty(crm.Lister(new FiltreActivites("CISEL", null, null, null, null, null, null, 1, 50)));
        using (Dossiers.Activer("MODE"))
            Assert.Single(crm.Lister(new FiltreActivites("CISEL", null, null, null, null, null, null, 1, 50)));
        // La société principale garde les fichiers d'avant le multi-société, à côté du journal.
        Assert.True(File.Exists(Path.Combine(_dossier, "local", "sage100api-extensions.db")));
    }

    [Fact]
    public void Une_seule_societe_sans_configuration_reste_comme_avant()
    {
        var o = new SageOptions { ChaineSql = "Server=SRV;Database=Bijou;Integrated Security=true" };
        var dossiers = new Dossiers(new OptionsFixes<SageOptions>(o));
        var d = Assert.Single(dossiers.Liste);
        Assert.Equal("Bijou", d.Code);
        Assert.True(d.Principal);
        Assert.False(dossiers.Multiple);
        Assert.Equal(o.ChaineSql, dossiers.ChaineSql);
    }

    [Fact]
    public void La_chaine_de_chaque_societe_vise_sa_base()
    {
        var o = new SageOptions
        {
            ChaineSql = "Server=SRV;Database=BIJOU;Integrated Security=true",
            Dossiers = [new() { Base = "BIJOU" }, new() { Base = "MODE" }],
        };
        var dossiers = new Dossiers(new OptionsFixes<SageOptions>(o));
        using (Dossiers.Activer("MODE"))
        {
            Assert.Contains("Initial Catalog=MODE", dossiers.ChaineSql);
            Assert.Contains("Initial Catalog=MODE", dossiers.Adapter("Server=SRV;Database=BIJOU;User ID=lecture;Password=x"));
        }
        Assert.Contains("Initial Catalog=BIJOU", dossiers.ChaineSql);
    }

    /// <summary>Faux worker qui note la société en cours de chaque appel.</summary>
    sealed class WorkerEspion(Dossiers dossiers, List<(string, string)> appels) : IWorkerClient
    {
        public Task<WorkerResponse> Envoyer(string operation, object? donnees, CancellationToken ct = default)
        {
            lock (appels) appels.Add((operation, dossiers.Code));
            object resultat = donnees switch
            {
                ConnexionRequest l => new UtilisateurVerifie { Utilisateur = l.Utilisateur },
                CommandeWorkerRequest c => new CommandeResult { IdExterne = c.Commande.IdExterne, Piece = "BC00001", NetAPayer = 10 },
                _ => new { sage = true },
            };
            return Task.FromResult(new WorkerResponse { Ok = true, Resultat = JsonSerializer.SerializeToElement(resultat, WorkerProtocol.Json) });
        }
    }
}
