using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sage100Api.Lectures;
using Sage100Api.TableauDeBord;
using Sage100Api.Worker;
using Xunit;

namespace Sage100Api.Tests;

/// <summary>Licence : signature, serveur, sociétés couvertes, échéance ; secrets « dpapi: » laissés intacts hors Windows.</summary>
public sealed class LicenceTests : IDisposable
{
    const string Cle = "cle-de-test";
    readonly string _dossier = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"lic-{Guid.NewGuid():N}")).FullName;
    readonly RSA _editeur = RSA.Create(2048);
    string Fichier => Path.Combine(_dossier, "licence.lic");

    public void Dispose()
    {
        _editeur.Dispose();
        try { Directory.Delete(_dossier, true); } catch (IOException) { }
    }

    WebApplicationFactory<Program> Usine(string? clePublique) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Sage:CheminJournal", Path.Combine(_dossier, "journal.db"));
            b.UseSetting("Sage:ClesApi:test", Cle);
            b.UseSetting("Sage:ChaineSql", "Server=test;Database=BIJOU;Integrated Security=true");
            b.UseSetting("Authentification:Active", "false");
            b.UseSetting("Licence:Fichier", Fichier);
            b.ConfigureServices(s =>
            {
                s.RemoveAll<ClePubliqueLicence>();
                s.AddSingleton(new ClePubliqueLicence(clePublique));
                s.RemoveAll<IWorkerClient>();
                s.AddSingleton<IWorkerClient>(new ApiTests.FauxWorker());
                s.RemoveAll<ILecturesSage>();
                s.AddSingleton<ILecturesSage>(new ApiTests.FaussesLectures());
                s.RemoveAll<ILecturesTableauDeBord>();
                s.AddSingleton<ILecturesTableauDeBord>(new FaussesLecturesTableauDeBord());
            });
        });

    /// <summary>Même format que deploy\licence\generer-licence.ps1.</summary>
    void Ecrire(string serveur, string[] bases, string? expiration = null, bool falsifier = false)
    {
        var contenu = JsonSerializer.Serialize(new { version = 1, client = "VITA", serveur, nomServeur = "SRV-TEST", bases, expiration, emise = "2026-10-09" });
        var octets = Encoding.UTF8.GetBytes(contenu);
        var signature = _editeur.SignData(octets, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (falsifier) octets = Encoding.UTF8.GetBytes(contenu.Replace("\"VITA\"", "\"AUTRE\""));
        File.WriteAllText(Fichier, JsonSerializer.Serialize(new { client = "VITA", nomServeur = "SRV-TEST", contenu = Convert.ToBase64String(octets), signature = Convert.ToBase64String(signature) }));
    }

    static async Task<(HttpStatusCode Statut, string Corps)> Clients(HttpClient http)
    {
        var r = await http.GetAsync("/api/v1/clients");
        return (r.StatusCode, await r.Content.ReadAsStringAsync());
    }

    HttpClient Client(WebApplicationFactory<Program> usine)
    {
        var http = usine.CreateClient();
        http.DefaultRequestHeaders.Add("X-Api-Key", Cle);
        return http;
    }

    [Fact]
    public async Task Sans_cle_integree_la_licence_n_est_pas_controlee()
    {
        using var usine = Usine(null);
        Assert.Equal(HttpStatusCode.OK, (await Clients(Client(usine))).Statut);
    }

    [Fact]
    public async Task Sans_licence_l_api_refuse_mais_donne_l_identifiant_du_serveur()
    {
        using var usine = Usine(_editeur.ToXmlString(false));
        var (statut, corps) = await Clients(Client(usine));
        Assert.Equal(HttpStatusCode.Forbidden, statut);
        Assert.Contains("LICENCE", corps);
        // /licence et /sante restent ouverts, même sans clé d'API.
        var etat = await usine.CreateClient().GetFromJsonAsync<JsonElement>("/api/v1/licence");
        Assert.False(etat.GetProperty("valide").GetBoolean());
        Assert.Equal(ServiceLicence.IdentifiantMachine(), etat.GetProperty("identifiantServeur").GetString());
    }

    [Fact]
    public async Task Une_licence_signee_pour_ce_serveur_ouvre_l_api_et_se_lit_sans_redemarrer()
    {
        using var usine = Usine(_editeur.ToXmlString(false));
        var http = Client(usine);
        Assert.Equal(HttpStatusCode.Forbidden, (await Clients(http)).Statut);

        Ecrire(ServiceLicence.IdentifiantMachine().ToUpperInvariant(), ["bijou"], "2099-12-31");
        Assert.Equal(HttpStatusCode.OK, (await Clients(http)).Statut);
        var etat = await http.GetFromJsonAsync<JsonElement>("/api/v1/licence");
        Assert.Equal("VITA", etat.GetProperty("client").GetString());
    }

    [Theory]
    [InlineData("autre-serveur", "BIJOU", null, false, "autre serveur")]
    [InlineData(null, "MODE", null, false, "pas couverte")]
    [InlineData(null, "*", "2020-01-01", false, "expirée")]
    [InlineData(null, "*", null, true, "modifié")]
    public async Task Une_licence_d_un_autre_serveur_d_une_autre_societe_expiree_ou_modifiee_est_refusee(
        string? serveur, string bases, string? expiration, bool falsifier, string message)
    {
        using var usine = Usine(_editeur.ToXmlString(false));
        Ecrire(serveur ?? ServiceLicence.IdentifiantMachine(), [bases], expiration, falsifier);
        var (statut, corps) = await Clients(Client(usine));
        Assert.Equal(HttpStatusCode.Forbidden, statut);
        Assert.Contains(message, corps);
    }

    [Fact]
    public async Task Une_licence_signee_par_une_autre_cle_est_refusee()
    {
        using var autre = RSA.Create(2048);
        using var usine = Usine(autre.ToXmlString(false));
        Ecrire(ServiceLicence.IdentifiantMachine(), ["*"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await Clients(Client(usine))).Statut);
    }

    [Fact]
    public void Un_secret_en_clair_reste_tel_quel()
    {
        Assert.Equal("mot de passe", Secrets.Devoiler("mot de passe"));
        Assert.False(Secrets.EstProtege("mot de passe"));
        Assert.True(Secrets.EstProtege("dpapi:AAAA"));
        if (OperatingSystem.IsWindows()) Assert.Equal("secret", Secrets.Devoiler(Secrets.Proteger("secret")));
        else Assert.Throws<PlatformNotSupportedException>(() => Secrets.Devoiler("dpapi:AAAA"));
    }
}
