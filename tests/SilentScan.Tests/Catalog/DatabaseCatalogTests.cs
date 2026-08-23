using SilentScan.Core.Catalog;
using SilentScan.Core.Common;

namespace SilentScan.Tests.Catalog;

public sealed class DatabaseCatalogTests
{
    [Fact]
    public void Find_ThreePartNameSelfReferencingTheScannedDatabase_ResolvesTheSameTableAsTheBareName()
    {

        var catalog = new DatabaseCatalog { CurrentDatabaseName = "RM_AZ_Sample" };
        catalog.AddOrReplace(new CatalogTable("dbo", "tblCoordinatingAgencies", CatalogTableKind.Table, [], [], "test.sql", 1));

        var bare = catalog.Find("dbo.tblCoordinatingAgencies");
        var selfQualified = catalog.Find("RM_AZ_Sample.dbo.tblCoordinatingAgencies");

        Assert.NotNull(bare);
        Assert.Same(bare, selfQualified);
    }

    [Fact]
    public void Find_ThreePartNameSelfReferenceIsCaseInsensitive()
    {
        var catalog = new DatabaseCatalog { CurrentDatabaseName = "RM_AZ_Sample" };
        catalog.AddOrReplace(new CatalogTable("dbo", "tblTrips", CatalogTableKind.Table, [], [], "test.sql", 1));

        Assert.NotNull(catalog.Find("rm_az_sample.dbo.tblTrips"));
    }

    [Fact]
    public void Find_ThreePartNameReferencingAGenuinelyDifferentDatabase_StaysUnresolved()
    {

        var catalog = new DatabaseCatalog { CurrentDatabaseName = "RM_AZ_Sample" };
        catalog.AddOrReplace(new CatalogTable("dbo", "tblCoordinatingAgencies", CatalogTableKind.Table, [], [], "test.sql", 1));

        Assert.Null(catalog.Find("RoutematchDirectory.dbo.tblCoordinatingAgencies"));
    }

    [Fact]
    public void Find_ThreePartNameWithNoCurrentDatabaseNameKnown_StaysUnresolved()
    {

        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(new CatalogTable("dbo", "tblCoordinatingAgencies", CatalogTableKind.Table, [], [], "test.sql", 1));

        Assert.Null(catalog.Find("AnyDatabase.dbo.tblCoordinatingAgencies"));
    }
}
