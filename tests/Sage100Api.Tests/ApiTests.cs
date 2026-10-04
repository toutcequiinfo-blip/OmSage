using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sage100Api.Contracts;
using Sage100Api.Extensions;
using Sage100Api.Lectures;
using Sage100Api.Worker;
using Xunit;

namespace Sage100Api.Tests;

/// <summary>Tests de l'API sans Sage : le worker et les lectures SQL sont simulés.</summary>
public sealed class ApiTests : IDisposable
{
    const string Cle = "cle-de-test";
    // Dossier propre à chaque test : le journal et la base des extensions (positions GPS) y sont créés.
    readonly string _dossier = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"api-{Guid.NewGuid():N}")).FullName;
    string _journal => Path.Combine(_dossier, "journal.db");
    readonly FauxWorker _worker = new();
    readonly FaussesLectures _lectures = new();
    readonly FaussesLecturesErp _erp = new();
    readonly WebApplicationFactory<Program> _usine;
    readonly HttpClient _http;

    public ApiTests()
    {
        _usine = Usine(connexion: false);
        _http = _usine.CreateClient();
        _http.DefaultRequestHeaders.Add("X-Api-Key", Cle);
    }

    WebApplicationFactory<Program> Usine(bool connexion) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Sage:CheminJournal", _journal);
            b.UseSetting("Sage:ClesApi:borne-test", Cle);
            b.UseSetting("Authentification:Active", connexion ? "true" : "false");
            b.UseSetting("Authentification:CleSignature", Convert.ToBase64String(new byte[32]));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<IWorkerClient>();
                s.AddSingleton<IWorkerClient>(_worker);
                s.RemoveAll<ILecturesSage>();
                s.AddSingleton<ILecturesSage>(_lectures);
                s.RemoveAll<ILecturesErp>();
                s.AddSingleton<ILecturesErp>(_erp);
            });
        });

    HttpClient ClientAvecConnexion(WebApplicationFactory<Program> usine, string? jeton = null)
    {
        var http = usine.CreateClient();
        http.DefaultRequestHeaders.Add("X-Api-Key", Cle);
        if (jeton != null) http.DefaultRequestHeaders.Authorization = new("Bearer", jeton);
        return http;
    }

    async Task<string> Jeton(HttpClient http, string utilisateur)
    {
        var r = await http.PostAsJsonAsync("/api/v1/connexion", new ConnexionRequest { Utilisateur = utilisateur, MotDePasse = "bon" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jeton").GetString()!;
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
    public async Task L_application_borne_est_servie_sans_cle()
    {
        var anonyme = _usine.CreateClient();
        var page = await anonyme.GetAsync("/borne/");
        var manifeste = await anonyme.GetAsync("/borne/manifest.webmanifest");
        var api = await anonyme.GetAsync("/api/v1/catalogue");

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Borne Sage 100", await page.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, manifeste.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
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
    public async Task Une_gamme2_sans_gamme1_renvoie_422()
    {
        var c = Commande("BORNE1-000004");
        c.Lignes[0].Gamme2 = "Or jaune";
        var r = await _http.PostAsJsonAsync("/api/v1/commandes", c);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal(0, _worker.Appels(Operations.CreerCommande));
    }

    [Fact]
    public async Task Le_catalogue_contient_les_valeurs_de_gamme()
    {
        var json = await _http.GetFromJsonAsync<JsonElement>("/api/v1/catalogue");
        var g = json.GetProperty("gammes")[0];
        Assert.Equal("BAOR01", g.GetProperty("article").GetString());
        Assert.Equal("52", g.GetProperty("gamme1").GetString());
    }

    [Fact]
    public async Task Une_commande_au_dela_du_stock_est_refusee_sans_appeler_Sage()
    {
        _lectures.Stocks["CHORFA"] = new Article("CHORFA", "Chaîne forçat", null, null, 1071, 3, 2);
        var c = Commande("BORNE1-000005");
        c.Lignes[0].Quantite = 2;

        var r = await _http.PostAsJsonAsync("/api/v1/commandes", c);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Contains("Stock insuffisant", await r.Content.ReadAsStringAsync());
        Assert.Equal(0, _worker.Appels(Operations.CreerCommande));
        // Une fois le stock réapprovisionné, la même commande passe.
        _lectures.Stocks["CHORFA"] = new Article("CHORFA", "Chaîne forçat", null, null, 1071, 10, 2);
        Assert.Equal(HttpStatusCode.Created, (await _http.PostAsJsonAsync("/api/v1/commandes", c)).StatusCode);
    }

    [Fact]
    public async Task Le_stock_d_un_article_a_gamme_est_controle_par_valeur()
    {
        _lectures.Stocks["BAOR01"] = new Article("BAOR01", "Bague Or", null, null, 2292, 4, 0, Gamme1: "Taille");
        var c = new CommandeRequest
        {
            IdExterne = "BORNE1-000020", Client = "CISEL",
            Lignes = { new LigneCommande { Article = "BAOR01", Quantite = 2, Gamme1 = "52" } },
        };

        var refus = await _http.PostAsJsonAsync("/api/v1/commandes", c);
        c.Lignes[0].Gamme1 = "54";
        var accepte = await _http.PostAsJsonAsync("/api/v1/commandes", c);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refus.StatusCode);
        Assert.Contains("52 : 1 disponible(s), 2 demandé(s)", await refus.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Created, accepte.StatusCode);
        var catalogue = await _http.GetFromJsonAsync<JsonElement>("/api/v1/catalogue");
        Assert.Equal(3m, catalogue.GetProperty("gammes")[1].GetProperty("stockDisponible").GetDecimal());
    }

    [Fact]
    public async Task Le_controle_de_stock_suit_l_option_stocks_negatifs_de_Sage()
    {
        _lectures.Stocks["CHORFA"] = new Article("CHORFA", "Chaîne forçat", null, null, 1071, 0, 0);
        _lectures.NegatifAutorise = true;
        var r1 = await _http.PostAsJsonAsync("/api/v1/commandes", Commande("BORNE1-000006"));
        _lectures.NegatifAutorise = false;
        _lectures.Stocks["CHORFA"] = new Article("CHORFA", "Chaîne forçat", null, null, 1071, 0, 0, SuiviStock: false);
        var r2 = await _http.PostAsJsonAsync("/api/v1/commandes", Commande("BORNE1-000007"));

        Assert.Equal(HttpStatusCode.Created, r1.StatusCode); // stocks négatifs autorisés
        Assert.Equal(HttpStatusCode.Created, r2.StatusCode); // article sans suivi de stock
        var catalogue = await _http.GetFromJsonAsync<JsonElement>("/api/v1/catalogue");
        Assert.True(catalogue.GetProperty("controleStock").GetBoolean());
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

    [Fact]
    public async Task Une_commande_saisie_dans_Sage_s_encaisse_par_son_numero_de_piece()
    {
        var p = new EncaissementRequest { IdExterne = "BORNE1-20261003-090000-AB12-P1", Mode = "Espèces", Montant = 1003.2 };

        var r1 = await _http.PostAsJsonAsync("/api/v1/commandes/piece/bc00042/encaissements", p);
        var r2 = await _http.PostAsJsonAsync("/api/v1/commandes/piece/BC00042/encaissements", p);
        var catalogue = await _http.GetFromJsonAsync<JsonElement>("/api/v1/catalogue");

        Assert.Equal(HttpStatusCode.Created, r1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        Assert.Equal("BC00042", _worker.DernierePiece);
        Assert.Equal(1, _worker.Appels(Operations.CreerEncaissement));
        var c = catalogue.GetProperty("commandesOuvertes")[0];
        Assert.Equal("BC00042", c.GetProperty("piece").GetString());
        Assert.Equal(1003.2m, c.GetProperty("reste").GetDecimal());
    }

    [Fact]
    public async Task Connexion_exigee_commande_refusee_sans_utilisateur()
    {
        using var usine = Usine(connexion: true);
        var r = await ClientAvecConnexion(usine).PostAsJsonAsync("/api/v1/commandes", Commande("BORNE1-000010"));
        var faux = await ClientAvecConnexion(usine, "abc.def").PostAsJsonAsync("/api/v1/commandes", Commande("BORNE1-000010"));

        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Contains("CONNEXION_REQUISE", await r.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, faux.StatusCode);
        Assert.Equal(0, _worker.Appels(Operations.CreerCommande));
        var catalogue = await ClientAvecConnexion(usine).GetFromJsonAsync<JsonElement>("/api/v1/catalogue");
        Assert.True(catalogue.GetProperty("authentification").GetBoolean());
        Assert.True(catalogue.GetProperty("exigerCaissier").GetBoolean());
    }

    [Fact]
    public async Task Mauvais_mot_de_passe_renvoie_401_puis_blocage_apres_5_essais()
    {
        using var usine = Usine(connexion: true);
        var http = ClientAvecConnexion(usine);
        var mauvais = new ConnexionRequest { Utilisateur = "MARIE", MotDePasse = "faux" };

        var r = await http.PostAsJsonAsync("/api/v1/connexion", mauvais);
        for (int i = 0; i < 4; i++) await http.PostAsJsonAsync("/api/v1/connexion", mauvais);
        var bloque = await http.PostAsJsonAsync("/api/v1/connexion", new ConnexionRequest { Utilisateur = "MARIE", MotDePasse = "bon" });

        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Contains("ACCES_REFUSE", await r.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.TooManyRequests, bloque.StatusCode);
        Assert.Equal(5, _worker.Appels(Operations.VerifierUtilisateur));
    }

    [Fact]
    public async Task La_commande_porte_le_collaborateur_de_l_utilisateur_connecte()
    {
        using var usine = Usine(connexion: true);
        var http = ClientAvecConnexion(usine);
        var connexion = await (await http.PostAsJsonAsync("/api/v1/connexion", new ConnexionRequest { Utilisateur = "MARIE", MotDePasse = "bon" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var jeton = connexion.GetProperty("jeton").GetString()!;

        var r = await ClientAvecConnexion(usine, jeton).PostAsJsonAsync("/api/v1/commandes", Commande("BORNE1-000011"));

        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal("DUPONT", connexion.GetProperty("collaborateur").GetProperty("nom").GetString());
        Assert.True(connexion.GetProperty("peutEncaisser").GetBoolean());
        Assert.Equal("MARIE", _worker.DernierAuteur?.Utilisateur);
        Assert.Equal("DUPONT", _worker.DernierAuteur?.CollaborateurNom);
        Assert.Equal("Marie", _worker.DernierAuteur?.CollaborateurPrenom);
    }

    [Fact]
    public async Task Un_utilisateur_non_caissier_ne_peut_pas_encaisser()
    {
        using var usine = Usine(connexion: true);
        var http = ClientAvecConnexion(usine);
        var vendeur = ClientAvecConnexion(usine, await Jeton(http, "PAUL"));
        var caissiere = ClientAvecConnexion(usine, await Jeton(http, "MARIE"));
        await vendeur.PostAsJsonAsync("/api/v1/commandes", Commande("BORNE1-000012"));
        var p = new EncaissementRequest { IdExterne = "BORNE1-000012-P1", Mode = "Espèces", Montant = 100 };

        var refuse = await vendeur.PostAsJsonAsync("/api/v1/commandes/BORNE1-000012/encaissements", p);
        var accepte = await caissiere.PostAsJsonAsync("/api/v1/commandes/BORNE1-000012/encaissements", p);

        Assert.Equal(HttpStatusCode.Forbidden, refuse.StatusCode);
        Assert.Contains("Caissier", await refuse.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Created, accepte.StatusCode);
        Assert.Equal(1, _worker.Appels(Operations.CreerEncaissement));
    }

    [Fact]
    public async Task Le_detail_d_un_bon_de_commande_donne_ses_lignes()
    {
        var r = await _http.GetFromJsonAsync<JsonElement>("/api/v1/commandes-ouvertes/BC00042");
        var absent = await _http.GetAsync("/api/v1/commandes-ouvertes/BC99999");

        Assert.Equal(1003.2m, r.GetProperty("entete").GetProperty("netAPayer").GetDecimal());
        Assert.Equal("CHORFA", r.GetProperty("lignes")[0].GetProperty("article").GetString());
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
    }

    [Fact]
    public void La_balance_agee_ventile_les_echeances_par_retard()
    {
        var jour = new DateTime(2026, 10, 4);
        Echeance E(string client, decimal montant, int retard) =>
            new() { Client = client, Intitule = client, Date = jour.AddDays(-retard - 30), DateEcheance = jour.AddDays(-retard), Montant = montant };

        var b = Recouvrement.BalanceAgee(new[] { E("A", 100, -5), E("A", 200, 10), E("A", 300, 45), E("A", 400, 120), E("A", -50, 3), E("B", 80, 0), E("C", 60, 70), E("C", -60, 1) }, jour);

        var a = Assert.Single(b, x => x.Client == "A");
        Assert.Equal((950m, 100m, 200m, 300m, 0m, 400m, -50m, 120), (a.Total, a.NonEchu, a.Retard1a30, a.Retard31a60, a.Retard61a90, a.RetardPlus90, a.Credits, a.RetardMaxJours));
        Assert.Equal(80m, Assert.Single(b, x => x.Client == "B").NonEchu);
        Assert.DoesNotContain(b, x => x.Client == "C"); // soldé par un avoir
        Assert.Equal("A", b[0].Client); // les plus en retard d'abord
    }

    [Fact]
    public async Task Une_position_gps_s_enregistre_et_revient_sur_la_fiche_client()
    {
        var invalide = await _http.PutAsJsonAsync("/api/v1/geolocalisation/client/CISEL", new PositionRequest(95, 47.5, null, null));
        var ok = await _http.PutAsJsonAsync("/api/v1/geolocalisation/client/CISEL", new PositionRequest(-18.9137, 47.5361, 12, "livreur"));
        await _http.PutAsJsonAsync("/api/v1/geolocalisation/adresse-livraison/7", new PositionRequest(-18.95, 47.52, null, null));
        var fiche = await _http.GetFromJsonAsync<JsonElement>("/api/v1/clients/CISEL/fiche");
        var livraisons = await _http.GetFromJsonAsync<JsonElement>("/api/v1/livraisons/a-livrer");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalide.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(-18.9137, fiche.GetProperty("position").GetProperty("latitude").GetDouble());
        Assert.Equal(-18.95, fiche.GetProperty("adressesLivraison")[0].GetProperty("position").GetProperty("latitude").GetDouble());
        Assert.Equal(-18.95, livraisons[0].GetProperty("position").GetProperty("latitude").GetDouble()); // adresse du document
        Assert.Equal(-18.9137, livraisons[1].GetProperty("position").GetProperty("latitude").GetDouble()); // sinon celle du client
    }

    [Fact]
    public async Task Enregistrer_une_position_exige_un_utilisateur_quand_la_connexion_est_active()
    {
        using var usine = Usine(connexion: true);
        var http = ClientAvecConnexion(usine);
        var sans = await http.PutAsJsonAsync("/api/v1/geolocalisation/client/CISEL", new PositionRequest(1, 2, null, null));
        var avec = await ClientAvecConnexion(usine, await Jeton(http, "PAUL")).PutAsJsonAsync("/api/v1/geolocalisation/client/CISEL", new PositionRequest(1, 2, null, null));

        Assert.Equal(HttpStatusCode.Unauthorized, sans.StatusCode);
        Assert.Equal("PAUL", (await avec.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("utilisateur").GetString());
    }

    [Fact]
    public async Task Les_types_de_document_et_tables_inconnus_sont_refuses()
    {
        var type = await _http.GetAsync("/api/v1/documents?type=bon");
        var detail = await _http.GetAsync("/api/v1/documents/facture/FA00001");
        var table = await _http.GetAsync("/api/v1/modifications?table=F_COMPTET&depuis=2026-10-01");

        Assert.Equal(HttpStatusCode.BadRequest, type.StatusCode);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Equal(6, _erp.DernierType);
        Assert.Equal(HttpStatusCode.BadRequest, table.StatusCode);
    }

    [Fact]
    public async Task Une_activite_crm_s_enregistre_une_fois_et_revient_dans_la_vue_360()
    {
        using var usine = Usine(connexion: true);
        var http = ClientAvecConnexion(usine);
        var marie = ClientAvecConnexion(usine, await Jeton(http, "MARIE"));
        var visite = new ActiviteRequest("cisel", "visite", "Présentation collection", "Intéressé par les bagues or", null, null, null, null, null, null, -18.91, 47.53);
        var relance = new ActiviteRequest("CISEL", "appel", "Rappeler pour le devis", null, "a-faire", DateTime.UtcNow.AddDays(3), null, null, null, null, null, null);

        var sans = await http.PutAsJsonAsync("/api/v1/crm/activites/a1", visite);
        var invalide = await marie.PutAsJsonAsync("/api/v1/crm/activites/a2", visite with { Type = "dejeuner" });
        await marie.PutAsJsonAsync("/api/v1/crm/activites/a1", visite);
        var rejouee = await (await marie.PutAsJsonAsync("/api/v1/crm/activites/a1", visite)).Content.ReadFromJsonAsync<JsonElement>();
        await marie.PutAsJsonAsync("/api/v1/crm/activites/a3", relance);
        var synthese = await marie.GetFromJsonAsync<JsonElement>("/api/v1/crm/clients/CISEL/synthese");
        var portefeuille = await marie.GetFromJsonAsync<JsonElement>("/api/v1/crm/portefeuille");

        Assert.Equal(HttpStatusCode.Unauthorized, sans.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalide.StatusCode);
        Assert.Equal("fait", rejouee.GetProperty("statut").GetString());
        Assert.Equal(3, rejouee.GetProperty("collaborateur").GetInt32()); // collaborateur de MARIE
        Assert.Equal("MARIE", rejouee.GetProperty("utilisateur").GetString());
        Assert.Equal(1, synthese.GetProperty("activitesRecentes").GetArrayLength()); // pas de doublon
        Assert.Equal("CISEL", synthese.GetProperty("activitesRecentes")[0].GetProperty("client").GetString());
        Assert.Equal("Rappeler pour le devis", synthese.GetProperty("prochainesActions")[0].GetProperty("sujet").GetString());
        Assert.Equal(12000m, synthese.GetProperty("indicateurs").GetProperty("caDouzeMois").GetDecimal());
        Assert.Equal(2, portefeuille.GetArrayLength());
        Assert.NotEqual(JsonValueKind.Null, portefeuille[0].GetProperty("derniereActivite").ValueKind); // CISEL a une visite
        Assert.Equal(JsonValueKind.Null, portefeuille[1].GetProperty("derniereActivite").ValueKind);
    }

    [Fact]
    public async Task Une_tournee_recopie_les_pieces_garde_la_preuve_et_refuse_les_doublons()
    {
        using var usine = Usine(connexion: true);
        var http = ClientAvecConnexion(usine);
        var marie = ClientAvecConnexion(usine, await Jeton(http, "MARIE"));
        var tournee = new TourneeRequest(new DateTime(2026, 10, 5), "Matin", 4, 1, ["bc00043", "BC00042"]);
        const string signature = "data:image/png;base64,iVBORw0KGgo=";

        var sans = await http.PutAsJsonAsync("/api/v1/livraisons/tournees/t1", tournee);
        var inconnue = await marie.PutAsJsonAsync("/api/v1/livraisons/tournees/t1", tournee with { Pieces = ["BC99999"] });
        var creee = await (await marie.PutAsJsonAsync("/api/v1/livraisons/tournees/t1", tournee)).Content.ReadFromJsonAsync<JsonElement>();
        var doublon = await marie.PutAsJsonAsync("/api/v1/livraisons/tournees/t2", tournee with { Pieces = ["BC00042"] });
        var echecSansMotif = await marie.PutAsJsonAsync("/api/v1/livraisons/tournees/t1/arrets/BC00042", new CompteRenduArret("echec", null, null, null, null, null, null));
        var livre = await (await marie.PutAsJsonAsync("/api/v1/livraisons/tournees/t1/arrets/bc00042",
            new CompteRenduArret("livre", null, "M. Rabe", null, signature, -18.9, 47.5))).Content.ReadFromJsonAsync<JsonElement>();
        var retrait = await marie.PutAsJsonAsync("/api/v1/livraisons/tournees/t1", tournee with { Pieces = ["BC00043"] });
        var suppression = await marie.DeleteAsync("/api/v1/livraisons/tournees/t1");
        var lue = await marie.GetFromJsonAsync<JsonElement>("/api/v1/livraisons/tournees/t1");
        var image = await marie.GetAsync("/api/v1/livraisons/tournees/t1/arrets/BC00042/signature");
        var position = await marie.GetFromJsonAsync<JsonElement>("/api/v1/geolocalisation/adresse-livraison/7");
        var suivi = await marie.GetFromJsonAsync<JsonElement>("/api/v1/livraisons/suivi/BC00042");

        Assert.Equal(HttpStatusCode.Unauthorized, sans.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, inconnue.StatusCode);
        Assert.Equal("BC00043", creee.GetProperty("arrets")[0].GetProperty("piece").GetString());
        Assert.Equal("CISEL", creee.GetProperty("arrets")[1].GetProperty("client").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, doublon.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, echecSansMotif.StatusCode);
        Assert.True(livre.GetProperty("signe").GetBoolean());
        Assert.Equal("MARIE", livre.GetProperty("utilisateur").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, retrait.StatusCode); // BC00042 est livrée
        Assert.Equal(HttpStatusCode.Conflict, suppression.StatusCode);
        Assert.Equal("en-cours", lue.GetProperty("statut").GetString());
        Assert.Equal(1, lue.GetProperty("nbTraites").GetInt32());
        Assert.Equal("image/png", image.Content.Headers.ContentType?.MediaType);
        Assert.Equal(-18.9, position.GetProperty("latitude").GetDouble()); // apprise à la livraison
        Assert.Equal("livre", suivi[0].GetProperty("statut").GetString());
    }

    [Fact]
    public void L_itineraire_passe_par_le_plus_court_chemin_et_met_les_pieces_sans_gps_a_la_fin()
    {
        // Trois points alignés vers l'est, donnés dans le désordre, départ à l'ouest.
        var points = new List<Itineraire.Point> { new("C", 0, 0.3), new("A", 0, 0.1), new("B", 0, 0.2) };
        var r = Itineraire.Ordonner(points, (0, 0), retour: false, ["X"]);

        Assert.Equal(["A", "B", "C", "X"], r.Ordre);
        Assert.Equal(["X"], r.SansPosition);
        Assert.InRange(r.DistanceKm, 33.0, 34.0);
    }

    public void Dispose()
    {
        _http.Dispose();
        _usine.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dossier, recursive: true);
    }

    sealed class FauxWorker : IWorkerClient
    {
        readonly Dictionary<string, int> _appels = new();
        public (string Code, string Message)? ProchaineErreur;
        public string? DernierePiece;
        public Auteur? DernierAuteur;

        public int Appels(string op) => _appels.GetValueOrDefault(op);

        public Task<WorkerResponse> Envoyer(string operation, object? donnees, CancellationToken ct = default)
        {
            _appels[operation] = Appels(operation) + 1;
            if (ProchaineErreur is { } e)
            {
                ProchaineErreur = null;
                return Task.FromResult(new WorkerResponse { Ok = false, CodeErreur = e.Code, MessageErreur = e.Message });
            }
            if (donnees is ConnexionRequest login && login.MotDePasse != "bon")
                return Task.FromResult(new WorkerResponse { Ok = false, CodeErreur = CodesErreur.AccesRefuse, MessageErreur = "Mot de passe incorrect" });
            object resultat = donnees switch
            {
                CommandeWorkerRequest c => Commande(c),
                EncaissementCommandeRequest p => Encaissement(p),
                ConnexionRequest l => new UtilisateurVerifie { Utilisateur = l.Utilisateur },
                _ => new { sage = true },
            };
            return Task.FromResult(new WorkerResponse { Ok = true, Resultat = JsonSerializer.SerializeToElement(resultat, WorkerProtocol.Json) });
        }

        CommandeResult Commande(CommandeWorkerRequest c)
        {
            DernierAuteur = c.Auteur;
            return new CommandeResult { IdExterne = c.Commande.IdExterne, Piece = "BC00100", NetAPayer = 1303.2 };
        }

        EncaissementResult Encaissement(EncaissementCommandeRequest p)
        {
            DernierePiece = p.PieceCommande;
            return new EncaissementResult { IdExterne = p.Encaissement.IdExterne, PieceCommande = p.PieceCommande, Montant = p.Encaissement.Montant };
        }
    }

    sealed class FaussesLecturesErp : ILecturesErp
    {
        public int? DernierType;
        public Task<FicheClient?> FicheClient(string numero) => Task.FromResult<FicheClient?>(numero == "CISEL"
            ? new FicheClient { Numero = "CISEL", Intitule = "Ciselure", AdressesLivraison = [new AdresseLivraison { Numero = 7, Client = "CISEL", Ville = "Antananarivo" }] }
            : null);
        public Task<IReadOnlyList<ContactClient>> Contacts(string client) => Task.FromResult<IReadOnlyList<ContactClient>>([]);
        public Task<IReadOnlyList<AdresseLivraison>> AdressesLivraison(string client) => Task.FromResult<IReadOnlyList<AdresseLivraison>>([]);
        public Task<IReadOnlyList<CollaborateurFiche>> Collaborateurs() => Task.FromResult<IReadOnlyList<CollaborateurFiche>>([]);
        public Task<IReadOnlyList<Depot>> Depots() => Task.FromResult<IReadOnlyList<Depot>>([]);
        public Task<IReadOnlyList<EnteteDocument>> Documents(int? type, string? client, DateTime? du, DateTime? au, bool? cloture, int page, int taille)
        {
            DernierType = type;
            return Task.FromResult<IReadOnlyList<EnteteDocument>>([]);
        }
        public Task<DetailDocument?> Document(int type, string piece)
        {
            DernierType = type;
            return Task.FromResult<DetailDocument?>(new DetailDocument(new EnteteDocument { Type = TypesDocument.Nom(type), Piece = piece }, []));
        }
        public Task<IReadOnlyList<ALivrer>> ALivrer(DateTime? jusquAu, int? depot) => Task.FromResult<IReadOnlyList<ALivrer>>(
            [new ALivrer { Type = "commande", Piece = "BC00042", Client = "CISEL", AdresseLivraison = 7 }, new ALivrer { Type = "commande", Piece = "BC00043", Client = "CISEL" }]);
        public Task<IReadOnlyList<Echeance>> Echeances(string? client) => Task.FromResult<IReadOnlyList<Echeance>>([]);
        public Task<IReadOnlyList<Modification>?> Modifications(string table, DateTime depuis, int taille) => Task.FromResult<IReadOnlyList<Modification>?>([]);
        public Task<IndicateursClient> Indicateurs(string client, DateTime aujourdhui) => Task.FromResult(new IndicateursClient { CaDouzeMois = 12000m });
        public Task<IReadOnlyList<ClientPortefeuille>> Portefeuille(int collaborateur) => Task.FromResult<IReadOnlyList<ClientPortefeuille>>(collaborateur == 3
            ? [new ClientPortefeuille { Numero = "CISEL", Intitule = "Ciselure" }, new ClientPortefeuille { Numero = "BAGUES", Intitule = "Bagues & Co" }]
            : []);
    }

    sealed class FaussesLectures : ILecturesSage
    {
        public readonly Dictionary<string, Article> Stocks = new();
        public bool NegatifAutorise;
        public Task<bool> StockNegatifAutorise() => Task.FromResult(NegatifAutorise);
        public Task<IReadOnlyList<Client>> Clients(string? recherche, int page, int taille) =>
            Task.FromResult<IReadOnlyList<Client>>(new[] { new Client("CISEL", "Ciselure", null, null, null) });
        public Task<Client?> Client(string numero) => Task.FromResult<Client?>(null);
        public Task<IReadOnlyList<Article>> Articles(string? recherche, string? famille, int page, int taille) =>
            Task.FromResult<IReadOnlyList<Article>>(Array.Empty<Article>());
        public Task<Article?> Article(string reference) => Task.FromResult(Stocks.GetValueOrDefault(reference));
        public Task<IReadOnlyList<ModeReglement>> ModesReglement() => Task.FromResult<IReadOnlyList<ModeReglement>>(Array.Empty<ModeReglement>());
        public Task<IReadOnlyList<EnumereGamme>> Gammes(string? article = null) =>
            Task.FromResult<IReadOnlyList<EnumereGamme>>(new[] { new EnumereGamme("BAOR01", "52", null, null, 1, 0), new EnumereGamme("BAOR01", "54", null, null, 3, 0) });
        public Task<string?> PieceCommande(string idExterne) => Task.FromResult<string?>(null);
        public Task<Collaborateur?> CollaborateurUtilisateur(string utilisateur) => Task.FromResult(utilisateur switch
        {
            "MARIE" => new Collaborateur(3, "DUPONT", "Marie", true, true),
            "PAUL" => new Collaborateur(4, "MARTIN", "Paul", true, false),
            _ => (Collaborateur?)null,
        });
        public Task<IReadOnlyList<CommandeOuverte>> CommandesOuvertes(string? recherche, int taille) =>
            Task.FromResult<IReadOnlyList<CommandeOuverte>>(new[] { new CommandeOuverte("BC00042", new DateTime(2026, 10, 2), "CISEL", "Ciselure", "BC00042", null, 1303.2m, 300m) });
        public Task<DetailPiece?> DetailCommande(string piece) => Task.FromResult(piece == "BC00042"
            ? new DetailPiece(new CommandeOuverte("BC00042", new DateTime(2026, 10, 2), "CISEL", "Ciselure", "BC00042", null, 1303.2m, 300m, 1003.2m), 1086m,
                new[] { new LignePiece("CHORFA", "Chaîne forçat Or", null, null, 1, 1086m, 1086m, 1303.2m) })
            : null);
    }
}
