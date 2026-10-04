using Sage100Api.Lectures;
using Xunit;

namespace Sage100Api.Tests;

/// <summary>Règles de prix de Sage (Tarification.Calculer), sans base : tarif client, catégorie tarifaire, gammes, conditionnements, remises.</summary>
public sealed class TarificationTests
{
    static readonly Conditionnement Unite = new("ECRIN", 1, "Unité", 1, null, null, true);
    static readonly Conditionnement Carton = new("ECRIN", 2, "Carton de 12", 12, "ECRIN/12", "ECR12", false);

    static DonneesTarifs Donnees(IReadOnlyList<TarifArticle>? articles = null, IReadOnlyList<TarifGamme>? gammes = null,
        IReadOnlyList<TarifConditionnement>? conds = null, IReadOnlyList<TarifQuantite>? qtes = null) =>
        new([new CategorieTarif(1, "Détaillants", false), new CategorieTarif(2, "Grossistes", false)], articles ?? [], gammes ?? [],
            [Unite, Carton], conds ?? [], qtes ?? []);

    static PrixCalcule Prix(DonneesTarifs t, string article = "CHORFA", decimal prixFiche = 1071, string client = "CISEL", int categorie = 2,
        string? gamme1 = null, Conditionnement? cond = null, decimal quantite = 1) =>
        Tarification.Calculer(t, article, prixFiche, false, client, categorie, gamme1, null, cond, quantite);

    [Fact]
    public void Sans_tarif_le_prix_de_la_fiche_article_s_applique()
    {
        var p = Prix(Donnees());
        Assert.Equal(1071m, p.PrixUnitaire);
        Assert.Equal("article", p.Origine);
        Assert.Empty(p.Remises);
    }

    [Fact]
    public void Le_client_suit_le_prix_de_sa_categorie_tarifaire()
    {
        var t = Donnees([new TarifArticle("CHORFA", 2, null, 900, false, 0, 0), new TarifArticle("CHORFA", 1, null, 1200, false, 0, 0)]);
        Assert.Equal(900m, Prix(t, categorie: 2).PrixUnitaire);
        Assert.Equal(1200m, Prix(t, categorie: 1).PrixUnitaire);
        Assert.Equal("categorie", Prix(t).Origine);
    }

    [Fact]
    public void Le_tarif_propre_au_client_passe_avant_sa_categorie()
    {
        var t = Donnees([new TarifArticle("CHORFA", 2, null, 900, false, 0, 0), new TarifArticle("CHORFA", null, "CISEL", 850, false, 0, 0)]);
        var p = Prix(t);
        Assert.Equal(850m, p.PrixUnitaire);
        Assert.Equal("client", p.Origine);
        Assert.Equal(900m, Prix(t, client: "BAGUES").PrixUnitaire);
    }

    [Fact]
    public void Une_categorie_sans_prix_garde_le_prix_de_la_fiche_et_applique_sa_remise()
    {
        var t = Donnees([new TarifArticle("BAAR01", 2, null, 0, false, 10, 0)]);
        var p = Prix(t, "BAAR01", 372);
        Assert.Equal(372m, p.PrixUnitaire);
        Assert.Equal(new RemiseTarif(1, 10), Assert.Single(p.Remises));
        Assert.Equal(334.8m, p.PrixNet);
    }

    [Fact]
    public void Un_tarif_hors_remise_n_applique_pas_la_remise()
    {
        var t = Donnees([new TarifArticle("BAAR01", 2, null, 0, false, 10, 0, HorsRemise: true)]);
        Assert.Empty(Prix(t, "BAAR01", 372).Remises);
    }

    [Fact]
    public void Une_valeur_de_gamme_peut_avoir_son_prix_pour_la_categorie()
    {
        var t = Donnees(gammes: [new TarifGamme("BAOR01", 2, null, "54", null, 2000)]);
        Assert.Equal(2000m, Prix(t, "BAOR01", 2292, gamme1: "54").PrixUnitaire);
        Assert.Equal(2292m, Prix(t, "BAOR01", 2292, gamme1: "52").PrixUnitaire);
        Assert.Equal(2292m, Prix(t, "BAOR01", 2292, categorie: 1, gamme1: "54").PrixUnitaire);
    }

    [Fact]
    public void Un_carton_a_son_prix_ou_vaut_son_contenu_et_le_prix_unitaire_est_par_unite_de_vente()
    {
        var t = Donnees(conds: [new TarifConditionnement("ECRIN", 2, null, 2, 96)]);
        var carton = Prix(t, "ECRIN", 10, cond: Carton);
        Assert.Equal(96m, carton.Prix);
        Assert.Equal(8m, carton.PrixUnitaire);
        // Autre catégorie, sans prix de carton : 12 fois l'unité.
        var detail = Prix(t, "ECRIN", 10, categorie: 1, cond: Carton);
        Assert.Equal(120m, detail.Prix);
        Assert.Equal(10m, detail.PrixUnitaire);
        Assert.Equal(10m, Prix(t, "ECRIN", 10, cond: Unite).Prix);
    }

    [Fact]
    public void Les_tranches_par_quantite_donnent_la_remise_ou_le_prix_net()
    {
        var remises = Donnees([new TarifArticle("CHORFA", 2, null, 900, false, 0, 1)], qtes:
        [
            new TarifQuantite("CHORFA", 2, null, 4, [], 0),
            new TarifQuantite("CHORFA", 2, null, 9, [new RemiseTarif(1, 5)], 0),
            new TarifQuantite("CHORFA", 2, null, 999999, [new RemiseTarif(1, 10), new RemiseTarif(1, 2)], 0),
        ]);
        Assert.Equal(900m, Prix(remises, quantite: 2).PrixNet);
        Assert.Equal(855m, Prix(remises, quantite: 5).PrixNet);
        Assert.Equal(793.8m, Prix(remises, quantite: 20).PrixNet); // 900 x 0,90 x 0,98

        var prixNet = Donnees([new TarifArticle("CHORFA", 2, null, 900, false, 0, 3)], qtes:
            [new TarifQuantite("CHORFA", 2, null, 9, [], 880), new TarifQuantite("CHORFA", 2, null, 999999, [], 850)]);
        Assert.Equal(880m, Prix(prixNet, quantite: 3).PrixUnitaire);
        Assert.Equal(850m, Prix(prixNet, quantite: 10).PrixUnitaire);
    }

    [Fact]
    public void Une_remise_en_montant_est_par_unite_de_vente()
    {
        Assert.Equal(90m, Tarification.Net(96, [new RemiseTarif(0, 0.5m)], 12));
        Assert.Equal(0m, Tarification.Net(10, [new RemiseTarif(0, 20)]));
    }

    [Fact]
    public void Un_tarif_TTC_est_signale()
    {
        var t = Donnees([new TarifArticle("CHORFA", 2, null, 1080, true, 0, 0)]);
        Assert.True(Prix(t).PrixTTC);
    }

    [Theory]
    [InlineData("a01", 1, null)]
    [InlineData("a32", 32, null)]
    [InlineData("A01", null, "A01")]
    [InlineData("CISEL", null, "CISEL")]
    public void Une_reference_tarifaire_designe_une_categorie_ou_un_client(string refCF, int? categorie, string? client)
    {
        Assert.Equal((categorie, client), LecturesTarifsSql.Cible(refCF));
    }
}
