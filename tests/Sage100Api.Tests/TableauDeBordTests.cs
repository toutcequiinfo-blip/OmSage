using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sage100Api.Contracts;
using Sage100Api.Lectures;
using Sage100Api.TableauDeBord;
using Sage100Api.Worker;
using Xunit;

namespace Sage100Api.Tests;

/// <summary>Tableau de bord sur un jeu de données simulé : calculs, croisements, profils et routes.</summary>
public sealed class TableauDeBordTests : IDisposable
{
    const string Cle = "cle-de-test";
    static readonly DateTime Aujourdhui = DateTime.Today;
    readonly string _dossier = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"tdb-{Guid.NewGuid():N}")).FullName;
    readonly FaussesLecturesTableauDeBord _donnees = new();
    readonly WebApplicationFactory<Program> _usine;

    public TableauDeBordTests()
    {
        _usine = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Sage:CheminJournal", Path.Combine(_dossier, "journal.db"));
            b.UseSetting("Sage:ClesApi:test", Cle);
            b.UseSetting("Authentification:Active", "true");
            b.UseSetting("Authentification:CleSignature", Convert.ToBase64String(new byte[32]));
            b.UseSetting("TableauDeBord:Profils:DIR", "Direction");
            b.UseSetting("TableauDeBord:Profils:COMPTA", "Comptable");
            b.ConfigureServices(s =>
            {
                s.RemoveAll<IWorkerClient>();
                s.AddSingleton<IWorkerClient>(new ApiTests.FauxWorker());
                s.RemoveAll<ILecturesSage>();
                s.AddSingleton<ILecturesSage>(new ApiTests.FaussesLectures());
                s.RemoveAll<ILecturesErp>();
                s.AddSingleton<ILecturesErp>(new ApiTests.FaussesLecturesErp());
                s.RemoveAll<ILecturesTarifs>();
                s.AddSingleton<ILecturesTarifs>(new ApiTests.FaussesLecturesTarifs());
                s.RemoveAll<ILecturesTableauDeBord>();
                s.AddSingleton<ILecturesTableauDeBord>(_donnees);
            });
        });
    }

    public void Dispose()
    {
        _usine.Dispose();
        try { Directory.Delete(_dossier, true); } catch (IOException) { }
    }

    async Task<HttpClient> Client(string? utilisateur)
    {
        await _usine.Services.GetRequiredService<ServiceTableauDeBord>().Actualiser();
        var http = _usine.CreateClient();
        http.DefaultRequestHeaders.Add("X-Api-Key", Cle);
        if (utilisateur == null) return http;
        var r = await http.PostAsJsonAsync("/api/v1/connexion", new ConnexionRequest { Utilisateur = utilisateur, MotDePasse = "bon" });
        var jeton = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jeton").GetString();
        http.DefaultRequestHeaders.Authorization = new("Bearer", jeton);
        return http;
    }

    static async Task<Instantane> Instantane()
    {
        var s = new ServiceTableauDeBord(new FaussesLecturesTableauDeBord(), new OptionsFixes<TableauDeBordOptions>(new()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ServiceTableauDeBord>.Instance);
        return await s.Actualiser();
    }

    static readonly Perimetre Direction = new(Perimetre.Direction, null, "DIR");

    [Fact]
    public async Task Le_cube_comptable_croise_classes_et_mois_hors_a_nouveaux()
    {
        var i = await Instantane();
        var r = Comptabilite.Croiser(i, new RequeteCompta(null, "classe", "mois", null, null, null, null, null, null, null, null, null, null, null), Aujourdhui);

        var produits = r.Lignes.Single(l => l.Cle == "7");
        Assert.Equal("7 Produits", produits.Intitule);
        Assert.Equal(5000m, produits.Total["soldeCrediteur"]);
        Assert.Equal(5000m, produits.Cellules[Periodes.Mois(Aujourdhui)]["credit"]);
        // Classe 5 : la banque sans son à-nouveau de 1 000.
        Assert.Equal(3000m, r.Lignes.Single(l => l.Cle == "5").Total["solde"]);
        Assert.Equal(r.Total.Total["debit"], r.Total.Total["credit"]);
        // Classe 1 : seulement en à-nouveaux, donc absente.
        Assert.Equal(new[] { "4", "5", "6", "7" }, r.Lignes.Select(l => l.Cle));
    }

    [Fact]
    public async Task Le_cube_analytique_ventile_par_section_d_un_plan()
    {
        var i = await Instantane();
        var r = Comptabilite.Croiser(i, new RequeteCompta("analytique", "section", null, null, null, null, null, null, null, null, 1, null, null, null), Aujourdhui);
        Assert.Equal(1500m, r.Lignes.Single(l => l.Cle == "COMMERCE").Total["solde"]);
        Assert.Equal(500m, r.Lignes.Single(l => l.Cle == "ATELIER").Total["solde"]);
    }

    [Fact]
    public async Task La_synthese_comptable_calcule_resultat_et_tresorerie_depuis_les_a_nouveaux()
    {
        var i = await Instantane();
        var s = JsonSerializer.SerializeToElement(Comptabilite.Synthese(i, i.ExerciceDe(Aujourdhui), Aujourdhui, new SeuilsAlertes { Tresorerie = 10000 }, Direction));

        Assert.Equal(5000m, s.GetProperty("produits").GetDecimal());
        Assert.Equal(2000m, s.GetProperty("charges").GetDecimal());
        Assert.Equal(3000m, s.GetProperty("resultat").GetDecimal());
        Assert.Equal(4000m, s.GetProperty("produitsN1").GetDecimal());
        Assert.Equal(4000m, s.GetProperty("tresorerie").GetDecimal());
        Assert.Equal(12, s.GetProperty("mois").GetArrayLength());
        Assert.Contains(s.GetProperty("alertes").EnumerateArray(), a => a.GetProperty("Code").GetString() == "tresorerie");
    }

    [Fact]
    public async Task Le_cube_des_ventes_calcule_le_taux_de_marge_sur_les_totaux()
    {
        var i = await Instantane();
        var r = Commercial.Croiser(i, new RequeteVentes(null, "commercial", null, null, null, null, null, null, null, null, null, null, null), Direction, Aujourdhui);

        Assert.Equal(5000m, r.Total.Total["ca"]);
        Assert.Equal(1500m, r.Total.Total["marge"]);
        Assert.Equal(42.86m, r.Total.Total["taux"]);
        Assert.Equal("3", r.Lignes[0].Cle);
        Assert.Equal("Marie DUPONT", r.Lignes[0].Intitule);
    }

    [Fact]
    public async Task Le_cube_des_ventes_analyse_et_filtre_par_qualite_client()
    {
        var i = await Instantane();
        var parQualite = Commercial.Croiser(i, new RequeteVentes(null, "qualite", null, null, null, null, null, null, null, null, null, null, null), Direction, Aujourdhui);
        var tout = Commercial.Croiser(i, new RequeteVentes(null, "tiers", null, null, null, null, null, null, null, null, null, null, null), Direction, Aujourdhui);
        Assert.Contains(parQualite.Lignes, l => l.Cle == "Grossiste");
        Assert.Equal(tout.Total.Total["ca"], parQualite.Total.Total["ca"]);

        var grossistes = Commercial.Croiser(i, new RequeteVentes(null, "tiers", null, null, null, null, null, null, null, null, null, null, null, "grossiste"), Direction, Aujourdhui);
        Assert.All(grossistes.Lignes, l => Assert.Equal("CISEL", l.Cle));
        Assert.Equal(parQualite.Lignes.Single(l => l.Cle == "Grossiste").Total["ca"], grossistes.Total.Total["ca"]);
    }

    [Fact]
    public void La_plage_Du_Au_se_choisit_au_jour_pres()
    {
        var i = new Instantane
        {
            Exercices = [new Exercice(1, new DateTime(2026, 1, 1), new DateTime(2026, 12, 31))],
            Lignes =
            [
                new FaitLigne { Mois = new(2026, 3, 1), Date = new(2026, 3, 9), Article = "A", Tiers = "C", MontantHT = 100 },
                new FaitLigne { Mois = new(2026, 3, 1), Date = new(2026, 3, 10), Article = "A", Tiers = "C", MontantHT = 20 },
                new FaitLigne { Mois = new(2026, 3, 1), Date = new(2026, 3, 31), Article = "A", Tiers = "C", MontantHT = 3 },
            ],
        };
        decimal Ca(string? du, string? au) =>
            Commercial.Croiser(i, new RequeteVentes(null, "article", null, du, au, null, null, null, null, null, null, null, null), Direction, new DateTime(2026, 6, 1)).Total.Total["ca"];
        Assert.Equal(20, Ca("2026-03-10", "2026-03-10"));
        Assert.Equal(23, Ca("2026-03-10", "2026-03-31"));
        Assert.Equal(123, Ca("2026-03", "2026-03"));
        Assert.Equal(123, Ca(null, null));

        var au = new DateTime(2026, 12, 31);
        var du = new DateTime(2026, 3, 10);
        Assert.True(Periodes.Restreindre(i, "mois", "2026-03", ref du, ref au));
        Assert.Equal((new DateTime(2026, 3, 10), new DateTime(2026, 3, 31)), (du, au));
    }

    [Fact]
    public async Task Au_dela_de_la_limite_les_lignes_sont_regroupees_dans_Autres()
    {
        var i = await Instantane();
        var r = Commercial.Croiser(i, new RequeteVentes(null, "article", null, null, null, null, null, null, null, null, null, null, 10), Direction, Aujourdhui);
        Assert.Equal(2, r.Lignes.Count);

        var faits = Enumerable.Range(1, 30).Select(n => new FaitLigne { Article = $"A{n:00}", Tiers = "CISEL", Mois = Aujourdhui, MontantHT = n });
        var c = Cube.Pivoter(faits, new Axe<FaitLigne>("article", "Article", f => f.Article), null, 1, f => [f.MontantHT], ["ca"], (s, _) => s[0], "ca", 10);
        Assert.Equal(10, c.Lignes.Count);
        Assert.Equal(Cube.CleAutres, c.Lignes[^1].Cle);
        Assert.Equal(21, c.LignesMasquees);
        Assert.Equal(Enumerable.Range(1, 21).Sum(), c.Lignes[^1].Total["ca"]);
        Assert.Equal(465m, c.Total.Total["ca"]);
    }

    [Fact]
    public async Task La_balance_agee_ventile_les_echeances_par_retard()
    {
        var i = await Instantane();
        var t = Creances.Ventiler(Creances.Filtrer(i, 0, Direction, null, null), Aujourdhui);
        Assert.Equal(2000m, t.NonEchu);
        Assert.Equal(3000m, t.R31a60);
        Assert.Equal(-500m, t.Credits);
        Assert.Equal(4500m, t.Total);
        var f = Creances.Ventiler(Creances.Filtrer(i, 1, Direction, null, null), Aujourdhui);
        Assert.Equal(2000m, f.Plus90);
    }

    [Fact]
    public async Task Le_stock_signale_ruptures_et_dormants()
    {
        var i = await Instantane();
        var l = Stocks.Lignes(i, null, null, Aujourdhui, new SeuilsAlertes());
        Assert.Equal("rupture", l.Single(x => x.Article == "CHORFA").Statut);
        Assert.Equal("dormant", l.Single(x => x.Article == "BAOR01").Statut);
        Assert.Equal(7500m, Stocks.Totaux(i, null, null, Aujourdhui, new SeuilsAlertes()).ValeurDormante);
    }

    [Fact]
    public async Task Le_detail_d_une_cellule_se_traduit_en_filtres_d_ecritures()
    {
        var i = await Instantane();
        var r = new RequeteCompta(null, "compte", "mois", null, null, "7", null, null, null, null, null, null, null, null);
        var f = Comptabilite.Detail(i, r, "701000", Periodes.Mois(Aujourdhui), Aujourdhui)!;
        Assert.Equal(["701000"], f.Comptes);
        Assert.Equal(Periodes.DebutMois(Aujourdhui), f.Du);
        Assert.Equal(Periodes.FinMois(Aujourdhui), f.Au);

        var parType = Comptabilite.Detail(i, r with { Lignes = "typeJournal", Colonnes = null }, "2", null, Aujourdhui)!;
        Assert.Equal(["BQ"], parType.Journaux);
        Assert.Null(Comptabilite.Detail(i, r, Cube.CleAutres, null, Aujourdhui));
    }

    [Fact]
    public void La_prochaine_actualisation_est_la_premiere_heure_a_venir()
    {
        var maintenant = new DateTime(2026, 10, 6, 12, 30, 0);
        Assert.Equal(new DateTime(2026, 10, 6, 17, 0, 0), ServiceTableauDeBord.ProchaineHeure(maintenant, ["07:00", "12:00", "17:00"]));
        Assert.Equal(new DateTime(2026, 10, 7, 7, 0, 0), ServiceTableauDeBord.ProchaineHeure(maintenant.AddHours(5), ["07:00", "12:00", "17:00"]));
    }

    [Fact]
    public async Task Sans_connexion_le_tableau_de_bord_est_refuse()
    {
        var http = await Client(null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/v1/tableau-de-bord/etat")).StatusCode);
    }

    [Fact]
    public async Task Un_utilisateur_sans_profil_est_refuse()
    {
        var http = await Client("INCONNU");
        var r = await http.GetAsync("/api/v1/tableau-de-bord/etat");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Contains("TableauDeBord:Profils", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task La_direction_voit_tout_et_lit_les_listes_des_filtres()
    {
        var http = await Client("DIR");
        var etat = await http.GetFromJsonAsync<JsonElement>("/api/v1/tableau-de-bord/etat");
        Assert.Equal("Direction", etat.GetProperty("profil").GetString());
        Assert.True(etat.GetProperty("droits").GetProperty("compta").GetBoolean());
        Assert.Equal(2, etat.GetProperty("exercices").GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/v1/tableau-de-bord/compta/cube?lignes=journal&colonnes=mois")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/v1/tableau-de-bord/compta/synthese")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/v1/tableau-de-bord/compta/rapprochement")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/v1/tableau-de-bord/commercial/synthese")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/v1/tableau-de-bord/commercial/cube?domaine=achats&lignes=tiers")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/v1/tableau-de-bord/commercial/commandes")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/v1/tableau-de-bord/recouvrement/fournisseurs")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/v1/tableau-de-bord/stock")).StatusCode);

        var detail = await http.GetFromJsonAsync<JsonElement>($"/api/v1/tableau-de-bord/compta/detail?lignes=compte&cleLigne=701000");
        Assert.Equal(1, detail.GetArrayLength());
        Assert.Equal(["701000"], _donnees.DernierFiltre!.Comptes);
    }

    [Fact]
    public async Task Le_comptable_n_a_pas_le_tableau_commercial()
    {
        var http = await Client("COMPTA");
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/v1/tableau-de-bord/compta/synthese")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/api/v1/tableau-de-bord/commercial/synthese")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PutAsJsonAsync("/api/v1/tableau-de-bord/objectifs", new[] { new { mois = "2026-10", commercial = 0, montant = 1 } })).StatusCode);
    }

    [Fact]
    public async Task Le_vendeur_ne_voit_que_ses_clients_et_pas_la_comptabilite()
    {
        var http = await Client("MARIE");
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/api/v1/tableau-de-bord/compta/synthese")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/api/v1/tableau-de-bord/commercial/cube?domaine=achats")).StatusCode);

        var cube = await http.GetFromJsonAsync<JsonElement>("/api/v1/tableau-de-bord/commercial/cube?lignes=tiers");
        var lignes = cube.GetProperty("lignes").EnumerateArray().Select(l => l.GetProperty("cle").GetString()).ToList();
        Assert.Equal(["CISEL"], lignes);

        var recouvrement = await http.GetFromJsonAsync<JsonElement>("/api/v1/tableau-de-bord/recouvrement/clients");
        Assert.Equal(["CISEL"], recouvrement.GetProperty("tiers").EnumerateArray().Select(l => l.GetProperty("tiers").GetString()));
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/api/v1/tableau-de-bord/recouvrement/clients/BAGUES")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/api/v1/tableau-de-bord/recouvrement/fournisseurs")).StatusCode);
    }

    [Fact]
    public async Task Les_objectifs_donnent_le_taux_d_atteinte()
    {
        var http = await Client("DIR");
        var mois = Periodes.Mois(Aujourdhui);
        var r = await http.PutAsJsonAsync("/api/v1/tableau-de-bord/objectifs", new[] { new { mois, commercial = 0, montant = 10000 } });
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);

        var s = await http.GetFromJsonAsync<JsonElement>("/api/v1/tableau-de-bord/commercial/synthese");
        Assert.Equal(10000m, s.GetProperty("objectifMois").GetDecimal());
        Assert.Equal(50m, s.GetProperty("atteinteMois").GetDecimal());
        Assert.Equal(5000m, s.GetProperty("caMois").GetDecimal());

        var invalide = await http.PutAsJsonAsync("/api/v1/tableau-de-bord/objectifs", new[] { new { mois = "octobre", commercial = 0, montant = 1 } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalide.StatusCode);
    }

    [Fact]
    public async Task Une_partie_illisible_n_empeche_pas_les_autres()
    {
        var lectures = new FaussesLecturesTableauDeBord { StockEnPanne = true };
        var s = new ServiceTableauDeBord(lectures, new OptionsFixes<TableauDeBordOptions>(new()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ServiceTableauDeBord>.Instance);
        var i = await s.Actualiser();
        Assert.Empty(i.Stock);
        Assert.NotEmpty(i.Compta);
        Assert.Contains(i.Erreurs, e => e.StartsWith("stocks"));
    }
}

sealed class OptionsFixes<T>(T valeur) : Microsoft.Extensions.Options.IOptionsMonitor<T>
{
    public T CurrentValue => valeur;
    public T Get(string? name) => valeur;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

/// <summary>
/// Jeu de données : exercice civil en cours et précédent. Ce mois-ci : ventes 5 000 (CISEL 3 000 par Marie, BAGUES 2 000 par Paul),
/// achats 2 000, encaissement 3 000 ; à-nouveau banque 1 000 ; même mois N-1 : ventes 4 000.
/// </summary>
sealed class FaussesLecturesTableauDeBord : ILecturesTableauDeBord
{
    static readonly DateTime J = DateTime.Today;
    static readonly DateTime M = new(J.Year, J.Month, 1);
    public bool StockEnPanne;
    public FiltreDetailCompta? DernierFiltre;

    static Task<IReadOnlyList<T>> L<T>(params T[] t) => Task.FromResult<IReadOnlyList<T>>(t);

    public Task<IReadOnlyList<Exercice>> Exercices() => L(
        new Exercice(1, new DateTime(J.Year, 1, 1), new DateTime(J.Year, 12, 31)),
        new Exercice(2, new DateTime(J.Year - 1, 1, 1), new DateTime(J.Year - 1, 12, 31)));

    public Task<IReadOnlyList<FaitCompta>> Compta(DateTime depuis) => L(
        new FaitCompta { Mois = new DateTime(J.Year, 1, 1), Compte = "512000", Journal = "RAN", ANouveau = true, Debit = 1000, Nombre = 1 },
        new FaitCompta { Mois = new DateTime(J.Year, 1, 1), Compte = "110000", Journal = "RAN", ANouveau = true, Credit = 1000, Nombre = 1 },
        new FaitCompta { Mois = M, Compte = "411000", Journal = "VTE", Tiers = "CISEL", Debit = 6000, Nombre = 1 },
        new FaitCompta { Mois = M, Compte = "701000", Journal = "VTE", Credit = 5000, Nombre = 1 },
        new FaitCompta { Mois = M, Compte = "445710", Journal = "VTE", Credit = 1000, Nombre = 1 },
        new FaitCompta { Mois = M, Compte = "601000", Journal = "ACH", Debit = 2000, Nombre = 1 },
        new FaitCompta { Mois = M, Compte = "401000", Journal = "ACH", Tiers = "FOUR", Credit = 2000, Nombre = 1 },
        new FaitCompta { Mois = M, Compte = "512000", Journal = "BQ", Debit = 3000, Nombre = 1 },
        new FaitCompta { Mois = M, Compte = "411000", Journal = "BQ", Tiers = "CISEL", Credit = 3000, Nombre = 1 },
        new FaitCompta { Mois = M.AddYears(-1), Compte = "701000", Journal = "VTE", Credit = 4000, Nombre = 1 },
        new FaitCompta { Mois = M.AddYears(-1), Compte = "411000", Journal = "VTE", Tiers = "CISEL", Debit = 4000, Nombre = 1 });

    public Task<IReadOnlyList<FaitAnalytique>> Analytique(DateTime depuis) => L(
        new FaitAnalytique { Mois = M, Plan = 1, Section = "COMMERCE", Compte = "601000", Journal = "ACH", Montant = 1500 },
        new FaitAnalytique { Mois = M, Plan = 1, Section = "ATELIER", Compte = "601000", Journal = "ACH", Montant = 500 },
        new FaitAnalytique { Mois = M, Plan = 2, Section = "NORD", Compte = "601000", Journal = "ACH", Montant = 2000 });

    public Task<IReadOnlyList<FaitLigne>> Lignes(DateTime depuis) => L(
        new FaitLigne { Mois = M, Article = "CHORFA", Tiers = "CISEL", Commercial = 3, Depot = 1, MontantHT = 3000, Quantite = 2, Cout = 2000 },
        new FaitLigne { Mois = M, Article = "BAOR01", Tiers = "BAGUES", Commercial = 4, Depot = 1, MontantHT = 2000, Quantite = 1, Cout = 1500 },
        new FaitLigne { Mois = M.AddYears(-1), Article = "CHORFA", Tiers = "CISEL", Commercial = 3, Depot = 1, MontantHT = 4000, Quantite = 3, Cout = 3000 },
        new FaitLigne { Domaine = 1, Mois = M, Article = "CHORFA", Tiers = "FOUR", Depot = 1, MontantHT = 2000, Quantite = 3 });

    public Task<IReadOnlyList<FaitPiece>> Pieces(DateTime depuis) => L(
        new FaitPiece { Date = J, Piece = "FA00001", Tiers = "CISEL", Commercial = 3, MontantHT = 3000 },
        new FaitPiece { Date = J, Piece = "FA00002", Tiers = "BAGUES", Commercial = 4, MontantHT = 2000 },
        new FaitPiece { Date = J.AddYears(-1), Piece = "FA00000", Tiers = "CISEL", Commercial = 3, MontantHT = 4000 },
        new FaitPiece { Domaine = 1, Date = J, Piece = "FF00001", Tiers = "FOUR", MontantHT = 2000 });

    public Task<IReadOnlyList<FaitEnCours>> EnCours() => L(
        new FaitEnCours { Type = 1, Date = J.AddDays(-10), DateLivraison = J.AddDays(-2), Piece = "BC00001", Tiers = "CISEL", Commercial = 3, MontantHT = 800 },
        new FaitEnCours { Type = 0, Date = J, Piece = "DE00001", Tiers = "BAGUES", Commercial = 4, MontantHT = 1200 });

    public Task<IReadOnlyList<LigneStock>> Stock() => StockEnPanne
        ? throw new InvalidOperationException("Nom de colonne non valide : AS_MontSto")
        : L(new LigneStock { Article = "CHORFA", Depot = 1, Quantite = 0, Mini = 2 },
            new LigneStock { Article = "BAOR01", Depot = 1, Quantite = 5, Valeur = 7500 });

    public Task<IReadOnlyDictionary<string, DateTime>> DernieresSorties() =>
        Task.FromResult<IReadOnlyDictionary<string, DateTime>>(new Dictionary<string, DateTime> { ["CHORFA"] = J, ["BAOR01"] = J.AddDays(-200) });

    public Task<IReadOnlyList<EcheanceTiers>> Echeances() => L(
        new EcheanceTiers { Type = 0, Tiers = "CISEL", Date = J.AddDays(-75), Piece = "FA00010", DateEcheance = J.AddDays(-45), Montant = 3000 },
        new EcheanceTiers { Type = 0, Tiers = "CISEL", Date = J.AddDays(-5), Piece = "AV00001", Montant = -500 },
        new EcheanceTiers { Type = 0, Tiers = "BAGUES", Date = J, Piece = "FA00002", DateEcheance = J.AddDays(10), Montant = 2000 },
        new EcheanceTiers { Type = 1, Tiers = "FOUR", Date = J.AddDays(-130), Piece = "FF00009", DateEcheance = J.AddDays(-100), Montant = 2000 });

    public Task<IReadOnlyList<FaitReglement>> Reglements(DateTime depuis) => L(
        new FaitReglement { Mois = M, Type = 0, Mode = 1, Tiers = "CISEL", Montant = 3000, Nombre = 1 });

    public Task<IReadOnlyList<RefCompte>> Comptes() => L(new RefCompte { Numero = "701000", Intitule = "Ventes" }, new RefCompte { Numero = "70", Intitule = "Ventes de produits" });
    public Task<IReadOnlyList<RefJournal>> Journaux() => L(new RefJournal { Code = "VTE", Intitule = "Ventes", Type = 1 },
        new RefJournal { Code = "ACH", Intitule = "Achats", Type = 0 }, new RefJournal { Code = "BQ", Intitule = "Banque", Type = 2 },
        new RefJournal { Code = "RAN", Intitule = "Reports", Type = 3 });
    public Task<IReadOnlyList<RefTiers>> Tiers() => L(new RefTiers { Numero = "CISEL", Intitule = "Ciselure", Type = 0, Representant = 3, Categorie = 1, Qualite = "Grossiste" },
        new RefTiers { Numero = "BAGUES", Intitule = "Bagues & Co", Type = 0, Representant = 4, Categorie = 2, Qualite = "Détaillant" },
        new RefTiers { Numero = "FOUR", Intitule = "Fournisseur Or", Type = 1 });
    public Task<IReadOnlyList<RefCode>> Plans() => L(new RefCode { Numero = 1, Intitule = "Activité" }, new RefCode { Numero = 2, Intitule = "Région" });
    public Task<IReadOnlyList<RefSection>> Sections() => L(new RefSection { Plan = 1, Numero = "COMMERCE", Intitule = "Commerce" });
    public Task<IReadOnlyList<RefArticle>> Articles() => L(new RefArticle { Reference = "CHORFA", Designation = "Chaîne forçat Or", Famille = "OR" },
        new RefArticle { Reference = "BAOR01", Designation = "Bague Or", Famille = "OR" });
    public Task<IReadOnlyList<RefFamille>> Familles() => L(new RefFamille { Code = "OR", Intitule = "Bijoux or" });
    public Task<IReadOnlyList<RefCode>> Depots() => L(new RefCode { Numero = 1, Intitule = "Bijou SA" });
    public Task<IReadOnlyList<RefCode>> Collaborateurs() => L(new RefCode { Numero = 3, Intitule = "Marie DUPONT" }, new RefCode { Numero = 4, Intitule = "Paul MARTIN" });
    public Task<IReadOnlyList<RefCode>> ModesReglement() => L(new RefCode { Numero = 1, Intitule = "Espèces" });
    public Task<IReadOnlyList<RefCode>> CategoriesTarifaires() => L(new RefCode { Numero = 1, Intitule = "Détaillant" }, new RefCode { Numero = 2, Intitule = "Grossiste" });

    public Task<IReadOnlyList<DetailEcriture>> DetailCompta(FiltreDetailCompta f)
    {
        DernierFiltre = f;
        return L(new DetailEcriture { Date = J, Journal = "VTE", Piece = "FA00001", Compte = "701000", Credit = 5000 });
    }

    public Task<IReadOnlyList<DetailLigne>> DetailVentes(FiltreDetailVentes f) =>
        L(new DetailLigne { Date = J, Piece = "FA00001", Tiers = "CISEL", Article = "CHORFA", Quantite = 2, MontantHT = 3000, Cout = 2000 });
}
