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
        return (await s.Actualiser()).Retenir(DocumentsCa.Defaut);
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
    public async Task La_production_compare_les_matieres_consommees_a_la_nomenclature()
    {
        var i = await Instantane();
        var r = new RequeteProduction(null, null, null, null, null, null, null, null, null, null);
        var s = JsonSerializer.SerializeToElement(Production.Synthese(i, r, Aujourdhui));
        var t = s.GetProperty("totaux");
        Assert.Equal(1, t.GetProperty("bons").GetInt32());
        Assert.Equal(5000m, t.GetProperty("valeur").GetDecimal());
        Assert.Equal(2610m, t.GetProperty("matieres").GetDecimal());
        // Théorique : 20 OR18 à 100 (prix moyen des sorties), 10 FERMOIR à 50, 1 EMBAL à 10.
        Assert.Equal(2510m, t.GetProperty("theorique").GetDecimal());
        Assert.Equal(4.0m, t.GetProperty("ecartPourcent").GetDecimal());

        var m = JsonSerializer.SerializeToElement(Production.Matieres(i, r, Aujourdhui)).GetProperty("matieres").EnumerateArray().ToList();
        var or18 = m.Single(x => x.GetProperty("article").GetString() == "OR18");
        Assert.Equal(1m, or18.GetProperty("ecart").GetDecimal());
        Assert.Equal(100m, or18.GetProperty("ecartValeur").GetDecimal());
        Assert.Equal(2m, or18.GetProperty("autresSorties").GetDecimal());
        Assert.Equal(m[0].GetProperty("article").GetString(), "OR18");
    }

    [Fact]
    public async Task L_approvisionnement_eclate_les_commandes_par_nomenclature_et_propose_les_achats()
    {
        var i = await Instantane();
        var l = Production.Lignes(i, new RequeteProduction(null, null, null, null, null, null, 90, 15, 7, 30), Aujourdhui);
        // 3 CHORFA réservés sans stock : 6 OR18, 3 FERMOIR et 1 EMBAL à sortir.
        var or18 = l.Single(x => x.Article == "OR18");
        Assert.Equal(6m, or18.BesoinFabrication);
        Assert.Equal(10, or18.Delai);
        Assert.Equal("fiche", or18.SourceDelai);
        Assert.Equal("ok", or18.Statut);

        var fermoir = l.Single(x => x.Article == "FERMOIR");
        Assert.Equal(3m, fermoir.BesoinFabrication);
        Assert.Equal(20, fermoir.Delai);
        Assert.Equal("historique", fermoir.SourceDelai);
        Assert.Equal("urgent", fermoir.Statut);
        Assert.Equal(4.34m, fermoir.AProposer);

        var embal = l.Single(x => x.Article == "EMBAL");
        Assert.Equal("rupture", embal.Statut);
        Assert.Equal("defaut", embal.SourceDelai);
        Assert.Equal(1.58m, embal.AProposer);
        Assert.DoesNotContain(l, x => x.Article == "CHORFA");

        Assert.Equal(15m, Production.Arrondir(12, 5, 10));
        Assert.Equal(10m, Production.Arrondir(3, 0, 10));
        Assert.Equal(0m, Production.Arrondir(-2, 5, 10));
    }

    [Fact]
    public async Task Les_previsions_donnent_la_quantite_a_produire_et_le_composant_limitant()
    {
        var i = await Instantane();
        var p = JsonSerializer.SerializeToElement(Production.Previsions(i, new RequeteProduction(null, null, null, null, null, null, null, null, null, null), Aujourdhui));
        var chorfa = p.GetProperty("lignes").EnumerateArray().Single(x => x.GetProperty("article").GetString() == "CHORFA");
        Assert.Equal(3m, chorfa.GetProperty("aProduire").GetDecimal());
        Assert.Equal(5m, chorfa.GetProperty("fabricable").GetDecimal());
        Assert.Equal("FERMOIR", chorfa.GetProperty("bloquant").GetString());
        Assert.Equal(12, chorfa.GetProperty("ventes").GetArrayLength());
    }

    [Fact]
    public async Task Les_administrateurs_choisissent_les_onglets_de_chaque_profil_et_utilisateur()
    {
        var compta = await Client("COMPTA");
        var etat = await compta.GetFromJsonAsync<JsonElement>("/api/v1/tableau-de-bord/etat");
        Assert.Contains("compta/balance", etat.GetProperty("onglets").EnumerateArray().Select(x => x.GetString()));
        Assert.False(etat.GetProperty("droits").GetProperty("administration").GetBoolean());
        // Direction par la configuration mais pas administrateur Sage : pas d'administration.
        var dir = await Client("DIR");
        Assert.Equal(HttpStatusCode.Forbidden, (await dir.GetAsync("/api/v1/tableau-de-bord/administration/droits")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await compta.GetAsync("/api/v1/tableau-de-bord/administration/utilisateurs")).StatusCode);

        var admin = await Client("ADMIN");
        var droits = await admin.GetFromJsonAsync<JsonElement>("/api/v1/tableau-de-bord/administration/droits");
        Assert.Equal(3, droits.GetProperty("droits").GetProperty("tableaux").GetArrayLength());
        Assert.Contains("COMPTA", droits.GetProperty("connus").EnumerateArray().Select(x => x.GetProperty("login").GetString()));

        // Le profil Comptable perd la balance et gagne le stock.
        var r = await admin.PutAsJsonAsync("/api/v1/tableau-de-bord/administration/droits/profil",
            new { profil = "Comptable", onglets = new[] { "compta/synthese", "compta/explorateur", "commercial/stock", "inconnu/x" } });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        etat = await compta.GetFromJsonAsync<JsonElement>("/api/v1/tableau-de-bord/etat");
        Assert.Equal(["commercial/stock", "compta/explorateur", "compta/synthese"], etat.GetProperty("onglets").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(HttpStatusCode.OK, (await compta.GetAsync("/api/v1/tableau-de-bord/stock")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await compta.GetAsync("/api/v1/tableau-de-bord/compta/rapprochement")).StatusCode);

        // Un utilisateur : profil imposé et onglets propres, puis retour au profil.
        await admin.PutAsJsonAsync("/api/v1/tableau-de-bord/administration/droits/utilisateur",
            new { login = "COMPTA", profil = "Commercial", onglets = new[] { "production/appro" }, administrateur = true });
        etat = await compta.GetFromJsonAsync<JsonElement>("/api/v1/tableau-de-bord/etat");
        Assert.Equal("Commercial", etat.GetProperty("profil").GetString());
        Assert.Equal(["production/appro", "administration/droits", "administration/utilisateurs"], etat.GetProperty("onglets").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(HttpStatusCode.Forbidden, (await compta.GetAsync("/api/v1/tableau-de-bord/compta/synthese")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await compta.GetAsync("/api/v1/tableau-de-bord/production/appro")).StatusCode);

        await admin.PutAsJsonAsync("/api/v1/tableau-de-bord/administration/droits/utilisateur", new { login = "COMPTA", administrateur = false });
        await admin.PutAsJsonAsync("/api/v1/tableau-de-bord/administration/droits/profil", new { profil = "Comptable" });
        etat = await compta.GetFromJsonAsync<JsonElement>("/api/v1/tableau-de-bord/etat");
        Assert.Equal("Comptable", etat.GetProperty("profil").GetString());
        Assert.Contains("compta/balance", etat.GetProperty("onglets").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(HttpStatusCode.Forbidden, (await compta.GetAsync("/api/v1/tableau-de-bord/administration/droits")).StatusCode);
    }

    [Fact]
    public async Task Les_utilisateurs_actifs_sont_listes_avec_leur_societe_et_leur_poste()
    {
        var vendeur = await Client("MARIE");
        await vendeur.GetAsync("/api/v1/tableau-de-bord/etat");
        var admin = await Client("ADMIN");
        var liste = await admin.GetFromJsonAsync<JsonElement>("/api/v1/tableau-de-bord/administration/utilisateurs");
        var marie = liste.EnumerateArray().Single(x => x.GetProperty("login").GetString() == "MARIE");
        Assert.False(string.IsNullOrEmpty(marie.GetProperty("dossier").GetString()));
        Assert.True(marie.GetProperty("requetes").GetInt32() >= 1);
        Assert.Equal(0, marie.GetProperty("inactifMinutes").GetInt32());
        // Sans jeton, la session Windows n'est pas demandée.
        var anonyme = _usine.CreateClient();
        anonyme.DefaultRequestHeaders.Add("X-Api-Key", Cle);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonyme.PostAsync("/api/v1/session-windows", null)).StatusCode);
    }

    [Fact]
    public async Task Le_tableau_production_est_reserve_a_la_direction_et_aux_commerciaux()
    {
        var http = await Client("DIR");
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/v1/tableau-de-bord/production/synthese")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/v1/tableau-de-bord/production/appro?vue=tous")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/v1/tableau-de-bord/production/produit/CHORFA")).StatusCode);
        var compta = await Client("COMPTA");
        Assert.Equal(HttpStatusCode.Forbidden, (await compta.GetAsync("/api/v1/tableau-de-bord/production/previsions")).StatusCode);
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
        Assert.Contains("Administration", await r.Content.ReadAsStringAsync());
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
    public async Task Les_bons_de_livraison_et_de_retour_comptent_dans_le_CA_seulement_si_coches()
    {
        var http = await Client("DIR");
        decimal Ca(JsonElement cube) => cube.GetProperty("total").GetProperty("total").GetProperty("ca").GetDecimal();
        var avant = await http.GetFromJsonAsync<JsonElement>("/api/v1/tableau-de-bord/commercial/cube?lignes=article");
        Assert.False((await http.GetFromJsonAsync<JsonElement>("/api/v1/tableau-de-bord/reglages")).GetProperty("documents").GetProperty("livraisons").GetBoolean());

        var r = await http.PutAsJsonAsync("/api/v1/tableau-de-bord/reglages", new { documents = new { livraisons = true, retours = true, avoirsFinanciers = false } });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var apres = await http.GetFromJsonAsync<JsonElement>("/api/v1/tableau-de-bord/commercial/cube?lignes=article");
        Assert.Equal(Ca(avant) + 1000 - 300, Ca(apres));
        var s = await http.GetFromJsonAsync<JsonElement>("/api/v1/tableau-de-bord/commercial/synthese");
        Assert.Equal(5700m, s.GetProperty("caMois").GetDecimal());

        await http.PutAsJsonAsync("/api/v1/tableau-de-bord/reglages", new { documents = new { livraisons = true, retours = false, avoirsFinanciers = false } });
        Assert.Equal(Ca(avant) + 1000, Ca(await http.GetFromJsonAsync<JsonElement>("/api/v1/tableau-de-bord/commercial/cube?lignes=article")));

        await http.PutAsJsonAsync("/api/v1/tableau-de-bord/reglages", new { documents = new { livraisons = true, factures = false } });
        Assert.Equal(1000m, Ca(await http.GetFromJsonAsync<JsonElement>("/api/v1/tableau-de-bord/commercial/cube?lignes=article")));

        var compta = await Client("COMPTA");
        Assert.Equal(HttpStatusCode.Forbidden, (await compta.PutAsJsonAsync("/api/v1/tableau-de-bord/reglages", new { documents = new { livraisons = false } })).StatusCode);
    }

    [Fact]
    public void Les_types_de_pieces_retenus_suivent_le_reglage()
    {
        var d = DocumentsCa.Defaut;
        Assert.True(d.Retient(6) && d.Retient(7, 1) && d.Retient(17, 2));
        Assert.False(d.Retient(3));
        Assert.True(new DocumentsCa(AvoirsFinanciers: true).Retient(15));
        var sansAvoir = new DocumentsCa(FacturesAvoir: false);
        Assert.False(sansAvoir.Retient(6, 2));
        Assert.True(sansAvoir.Retient(6, 1));
        Assert.Equal("(e.DO_Type IN (6, 7))", d.Condition(0));
        Assert.Equal("(e.DO_Type IN (13, 14) OR (e.DO_Type IN (16, 17) AND (e.DO_Provenance NOT IN (1, 2) OR e.DO_Provenance = 1)))",
            new DocumentsCa(Livraisons: true, Retours: true, FacturesAvoir: false).Condition(1));
        Assert.Equal("1 = 0", new DocumentsCa(Factures: false, FacturesRetour: false, FacturesAvoir: false).Condition(0));
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
    [Fact]
    public void Le_profil_suit_les_cases_de_la_fiche_collaborateur_Sage()
    {
        var o = new TableauDeBordOptions();
        string P(bool vendeur = false, bool chef = false, bool financier = false, bool recouvrement = false, int? co = 5, bool admin = false) =>
            Perimetre.De(new Utilisateur("USER", admin, co, "USER", null, vendeur, false, DateTime.UtcNow.AddHours(1), null, chef, financier, recouvrement), o, true).Profil;

        Assert.Equal(Perimetre.Direction, P(vendeur: true, chef: true, financier: true, recouvrement: true));
        Assert.Equal(Perimetre.Comptable, P(financier: true));
        Assert.Equal(Perimetre.Comptable, P(vendeur: true, recouvrement: true));
        Assert.Equal(Perimetre.Commercial, P(vendeur: true, chef: true));
        Assert.Equal(Perimetre.Vendeur, P(vendeur: true));
        Assert.Equal(Perimetre.Aucun, P());
        Assert.Equal(Perimetre.Aucun, P(chef: true, co: null));
        Assert.Equal(Perimetre.Direction, P(admin: true, co: null));
        o.Profils["USER"] = "Vendeur";
        Assert.Equal(Perimetre.Vendeur, P(chef: true, financier: true));
    }
    [Fact]
    public void La_semaine_suit_la_norme_ISO()
    {
        Assert.Equal("2026-S01", Periodes.Semaine(new DateTime(2025, 12, 29)));
        Assert.Equal("2025-S52", Periodes.Semaine(new DateTime(2025, 12, 28)));
        Assert.Equal(new DateTime(2026, 10, 5), Periodes.DebutSemaine("2026-S41"));
        Assert.Null(Periodes.DebutSemaine("2026-S54"));
        Assert.Equal("S41 2026 (05/10)", Periodes.IntituleSemaine("2026-S41"));
    }

    [Fact]
    public async Task Le_taux_de_transformation_compare_le_livre_au_commande()
    {
        var i = await Instantane();
        var du = Aujourdhui.AddDays(-60).ToString("yyyy-MM-dd");
        var au = Aujourdhui.ToString("yyyy-MM-dd");
        RequeteVentes R(string lignes, string? tiers = null) => new(null, lignes, null, du, au, null, null, tiers, null, null, null, null, null);

        var total = Transformation.Croiser(i, R("tiers"), Direction, Aujourdhui).Total.Total;
        Assert.Equal(4500m, total["commande"]);
        Assert.Equal(2500m, total["livre"]);
        Assert.Equal(1500m, total["encours"]);
        Assert.Equal(500m, total["nonservi"]);
        Assert.Equal(55.6m, total["transfo"]);
        Assert.Equal(40m, total["transfoQte"]);
        Assert.Equal(1000m, Transformation.Croiser(i, R("tiers", "!CISEL"), Direction, Aujourdhui).Total.Total["commande"]);

        var cisel = Transformation.Detail(i, R("tiers"), Direction, "CISEL", null, Aujourdhui)!;
        Assert.Equal(["BC00004", "BC00002"], cisel.Select(c => c.Piece));
        Assert.Equal("Non servie", cisel[0].Statut);
        Assert.Equal("Partielle", cisel[1].Statut);
        Assert.Equal(15, cisel[1].Delai);
        Assert.Equal("Livrée", Transformation.Detail(i, R("tiers"), Direction, "BAGUES", null, Aujourdhui)!.Single().Statut);
        Assert.Equal(["BC00003"], Transformation.Detail(i, R("semaine"), Direction, Periodes.Semaine(Aujourdhui.AddDays(-18)), null, Aujourdhui)!
            .Select(c => c.Piece).Where(p => p == "BC00003"));
    }

    [Fact]
    public async Task Les_filtres_acceptent_plusieurs_valeurs_et_les_exclusions()
    {
        var i = await Instantane();
        RequeteVentes V(string? tiers = null, string? commercial = null, string? qualite = null, string? famille = null, string? categorie = null) =>
            new(null, "tiers", null, null, null, null, famille, tiers, commercial, null, categorie, null, null, qualite);
        decimal Ca(RequeteVentes r) => Commercial.Croiser(i, r, Direction, Aujourdhui).Total.Total["ca"];

        Assert.Equal(5000m, Ca(V(tiers: "CISEL,BAGUES")));
        Assert.Equal(2000m, Ca(V(tiers: "!CISEL")));
        Assert.Equal(3000m, Ca(V(commercial: "!4")));
        Assert.Equal(5000m, Ca(V(qualite: "grossiste,Détaillant")));
        Assert.Equal(2000m, Ca(V(qualite: "!Grossiste")));
        Assert.Equal(0m, Ca(V(famille: "!OR")));
        Assert.Equal(5000m, Ca(V(famille: "OR,~")));
        Assert.Equal(3000m, Ca(V(categorie: "1,~")));

        // Détail d'une cellule : exclusions traduites en NOT IN, choix multiples en IN.
        var d = Commercial.Detail(i, V(tiers: "!CISEL", commercial: "3,4"), Direction, null, null, Aujourdhui)!;
        Assert.Equal(["CISEL"], d.TiersExclus!);
        Assert.Equal([3, 4], d.Commerciaux!);
        var q = Commercial.Detail(i, V(qualite: "!Grossiste"), Direction, null, null, Aujourdhui)!;
        Assert.Equal(["CISEL"], q.TiersExclus!);
        Assert.Null(q.TiersListe);

        RequeteCompta C(string? comptes = null, string? journaux = null, string? tiers = null) =>
            new(null, "journal", null, null, null, comptes, journaux, null, tiers, null, null, null, null, null);
        decimal Debit(RequeteCompta r) => Comptabilite.Croiser(i, r, Aujourdhui).Total.Total["debit"];
        Assert.Equal(Debit(C(journaux: "BQ")) + Debit(C(journaux: "ACH")), Debit(C(journaux: "BQ,ACH")));
        Assert.Equal(Debit(C()) - Debit(C(journaux: "VTE")), Debit(C(journaux: "!VTE")));
        Assert.Equal(Debit(C()) - Debit(C(comptes: "4")), Debit(C(comptes: "!4")));
        Assert.Equal(Debit(C()) - Debit(C(tiers: "CISEL")), Debit(C(tiers: "!CISEL")));
        var dc = Comptabilite.Detail(i, C(comptes: "!4", journaux: "!VTE", tiers: "CISEL,FOUR"), null, null, Aujourdhui)!;
        Assert.Equal(["4"], dc.ComptesExclus!);
        Assert.Equal(["VTE"], dc.JournauxExclus!);
        Assert.Equal(2, dc.TiersListe!.Count);
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
        new FaitLigne { Domaine = 1, Mois = M, Article = "CHORFA", Tiers = "FOUR", Depot = 1, MontantHT = 2000, Quantite = 3 },
        new FaitLigne { Type = 3, Mois = M, Date = J, Article = "CHORFA", Tiers = "CISEL", Commercial = 3, Depot = 1, MontantHT = 1000, Quantite = 1, Cout = 600 },
        new FaitLigne { Type = 4, Mois = M, Date = J, Article = "CHORFA", Tiers = "CISEL", Commercial = 3, Depot = 1, MontantHT = -300, Quantite = -1, Cout = -200 });

    public Task<IReadOnlyList<FaitPiece>> Pieces(DateTime depuis) => L(
        new FaitPiece { Date = J, Piece = "FA00001", Tiers = "CISEL", Commercial = 3, MontantHT = 3000 },
        new FaitPiece { Date = J, Piece = "FA00002", Tiers = "BAGUES", Commercial = 4, MontantHT = 2000 },
        new FaitPiece { Date = J.AddYears(-1), Piece = "FA00000", Tiers = "CISEL", Commercial = 3, MontantHT = 4000 },
        new FaitPiece { Domaine = 1, Date = J, Piece = "FF00001", Tiers = "FOUR", MontantHT = 2000 },
        new FaitPiece { Type = 3, Date = J, Piece = "BL00001", Tiers = "CISEL", Commercial = 3, MontantHT = 1000 },
        new FaitPiece { Type = 4, Date = J, Piece = "BR00001", Tiers = "CISEL", Commercial = 3, MontantHT = -300, Avoir = true });

    public Task<IReadOnlyList<FaitEnCours>> EnCours() => L(
        new FaitEnCours { Type = 1, Date = J.AddDays(-10), DateLivraison = J.AddDays(-2), Piece = "BC00001", Tiers = "CISEL", Commercial = 3, MontantHT = 800 },
        new FaitEnCours { Type = 0, Date = J, Piece = "DE00001", Tiers = "BAGUES", Commercial = 4, MontantHT = 1200 });

    // BC00002 : 2 CHORFA, 1 livré (BL), 1 en attente ; BC00003 : facturé en entier ; BC00004 : soldé sans livraison.
    public Task<IReadOnlyList<FaitCommande>> Commandes(DateTime depuis) => L(
        new FaitCommande { Commande = "BC00002", DateCommande = J.AddDays(-20), Piece = "BL00002", Type = 3, DateLivraison = J.AddDays(-5), Article = "CHORFA", Tiers = "CISEL", Commercial = 3, Quantite = 1, MontantHT = 1500 },
        new FaitCommande { Commande = "BC00002", DateCommande = J.AddDays(-20), Piece = "BC00002", Type = 1, Article = "CHORFA", Tiers = "CISEL", Commercial = 3, Quantite = 1, MontantHT = 1500 },
        new FaitCommande { Commande = "BC00003", DateCommande = J.AddDays(-18), Piece = "FA00003", Type = 6, DateLivraison = J.AddDays(-15), Article = "BAOR01", Tiers = "BAGUES", Commercial = 4, Quantite = 1, MontantHT = 1000 },
        new FaitCommande { Commande = "BC00004", DateCommande = J.AddDays(-10), Piece = "BC00004", Type = 1, Cloture = true, Article = "CHORFA", Tiers = "CISEL", Commercial = 3, Quantite = 2, MontantHT = 500 });

    public Task<IReadOnlyList<LigneStock>> Stock() => StockEnPanne
        ? throw new InvalidOperationException("Nom de colonne non valide : AS_MontSto")
        : L(new LigneStock { Article = "CHORFA", Depot = 1, Quantite = 0, Mini = 2, Reserve = 3 },
            new LigneStock { Article = "BAOR01", Depot = 1, Quantite = 5, Valeur = 7500 },
            new LigneStock { Article = "OR18", Depot = 1, Quantite = 30 },
            new LigneStock { Article = "FERMOIR", Depot = 1, Quantite = 5 });

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

    // Fabrication : CHORFA = 2 OR18 et 1 FERMOIR par unité (nomenclature décrite pour 2), plus 1 EMBAL par bon.
    // BF00001 : 10 CHORFA avec 21 OR18 (1 de trop), 10 FERMOIR, 1 EMBAL ; une sortie diverse de 2 OR18.
    public Task<IReadOnlyList<FaitMouvement>> Mouvements(DateTime depuis) => L(
        new FaitMouvement { Date = J.AddDays(-5), Piece = "BF00001", Compose = "CHORFA", Article = "CHORFA", Depot = 1, Entree = true, Quantite = 10, Valeur = 5000 },
        new FaitMouvement { Date = J.AddDays(-5), Piece = "BF00001", Compose = "CHORFA", Article = "OR18", Depot = 1, Quantite = 21, Valeur = 2100 },
        new FaitMouvement { Date = J.AddDays(-5), Piece = "BF00001", Compose = "CHORFA", Article = "FERMOIR", Depot = 1, Quantite = 10, Valeur = 500 },
        new FaitMouvement { Date = J.AddDays(-5), Piece = "BF00001", Compose = "CHORFA", Article = "EMBAL", Depot = 1, Quantite = 1, Valeur = 10 },
        new FaitMouvement { Type = 21, Date = J.AddDays(-3), Piece = "MS00001", Article = "OR18", Depot = 1, Quantite = 2, Valeur = 200 });

    public Task<IReadOnlyList<LigneNomenclature>> Nomenclatures() => L(
        new LigneNomenclature { Compose = "CHORFA", Composant = "OR18", Quantite = 4, QteComposition = 2 },
        new LigneNomenclature { Compose = "CHORFA", Composant = "FERMOIR", Quantite = 2, QteComposition = 2 },
        new LigneNomenclature { Compose = "CHORFA", Composant = "EMBAL", Quantite = 1, Fixe = true, QteComposition = 2 });

    public Task<IReadOnlyList<RefFournisseurArticle>> FournisseursArticles() => L(
        new RefFournisseurArticle { Article = "OR18", Fournisseur = "FOUR", Principal = true, Delai = 10, QteMini = 10, Colisage = 5, Prix = 100 });

    public Task<IReadOnlyList<FaitReception>> Receptions(DateTime depuis) => L(
        new FaitReception { Article = "FERMOIR", Fournisseur = "FOUR", DateCommande = J.AddDays(-60), DateReception = J.AddDays(-40), Quantite = 20 });

    public Task<IReadOnlyList<RefArticleProduction>> ArticlesProduction() => L(
        new RefArticleProduction { Reference = "CHORFA", Nomenclature = 1 }, new RefArticleProduction { Reference = "EMBAL", PrixAchat = 10 });

    public Task<IReadOnlyList<DetailEcriture>> DetailCompta(FiltreDetailCompta f)
    {
        DernierFiltre = f;
        return L(new DetailEcriture { Date = J, Journal = "VTE", Piece = "FA00001", Compte = "701000", Credit = 5000 });
    }

    public Task<IReadOnlyList<DetailLigne>> DetailVentes(FiltreDetailVentes f) =>
        L(new DetailLigne { Date = J, Piece = "FA00001", Tiers = "CISEL", Article = "CHORFA", Quantite = 2, MontantHT = 3000, Cout = 2000 });
}
