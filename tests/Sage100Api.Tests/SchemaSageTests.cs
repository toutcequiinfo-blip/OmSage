using Sage100Api.Lectures;
using Xunit;

namespace Sage100Api.Tests;

public sealed class SchemaSageTests
{
    const string Requete = "SELECT NULLIF(e.DO_RefExterne, '') AS IdExterne FROM F_DOCENTETE e WHERE DO_RefExterne = @id";

    [Fact]
    public void En_V12_la_requete_reste_identique()
    {
        SchemaSage.Imposer("base-v12", "DO_RefExterne");
        Assert.Same(Requete, SchemaSage.Adapter(Requete, "base-v12"));
    }

    [Fact]
    public void En_V9_l_identifiant_est_lu_dans_l_information_libre_IdBorne()
    {
        SchemaSage.Imposer("base-v9", "[IdBorne]");
        Assert.Equal("SELECT NULLIF(e.[IdBorne], '') AS IdExterne FROM F_DOCENTETE e WHERE [IdBorne] = @id", SchemaSage.Adapter(Requete, "base-v9"));
    }

    [Fact]
    public void Sans_information_libre_l_identifiant_est_vide()
    {
        SchemaSage.Imposer("base-v9-nue", null);
        Assert.Equal("SELECT NULLIF(CAST(NULL AS varchar(69)), '') AS IdExterne FROM F_DOCENTETE e WHERE CAST(NULL AS varchar(69)) = @id",
            SchemaSage.Adapter(Requete, "base-v9-nue"));
    }

    [Fact]
    public void Une_base_injoignable_garde_le_fonctionnement_V12()
    {
        const string injoignable = "Server=127.0.0.1,1;Database=x;User Id=x;Password=x;Connect Timeout=1;Encrypt=false";
        Assert.Same(Requete, SchemaSage.Adapter(Requete, injoignable));
    }
}
