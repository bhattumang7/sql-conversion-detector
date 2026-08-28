using SilentScan.Core.Catalog;
using SilentScan.Core.Common;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Catalog;

public sealed class DatabaseCatalogTests
{
    [Fact]
    public void Find_NoDefaultCollationKnown_TreatsDifferentlyCasedNamesAsTheSameObject()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(new CatalogTable("dbo", "Customers", CatalogTableKind.Table, [], [], "test.sql", 1));

        Assert.NotNull(catalog.Find("dbo.customers"));
    }

    [Fact]
    public void Find_CaseSensitiveDefaultCollation_KeepsDifferentlyCasedNamesAsDistinctObjects()
    {
        var catalog = new DatabaseCatalog { DefaultCollation = new Collation("SQL_Latin1_General_CP1_CS_AS") };
        var upper = new CatalogTable("dbo", "Customers", CatalogTableKind.Table, [], [], "test.sql", 1);
        var lower = new CatalogTable("dbo", "customers", CatalogTableKind.Table, [], [], "test.sql", 2);
        catalog.AddOrReplace(upper);
        catalog.AddOrReplace(lower);

        Assert.Same(upper, catalog.Find("dbo.Customers"));
        Assert.Same(lower, catalog.Find("dbo.customers"));
    }

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
