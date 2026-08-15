using SilentScan.Core.Catalog;

namespace SilentScan.Tests.Catalog;

public sealed class DatabaseCatalogTests
{
    [Fact]
    public void Find_ThreePartNameSelfReferencingTheScannedDatabase_ResolvesTheSameTableAsTheBareName()
    {
        // Real corpus code sometimes self-references its own database by full three-part name
        // (RM_.dbo.T) instead of the bare schema.table form, even though it's the SAME database
        // this catalog was built against - SchemaObjectNameHelper.Qualify keeps a database
        // qualifier distinct from a bare name (a real, necessary rule: db.dbo.T and dbo.T must
        // stay different catalog keys for a GENUINELY different database), so without this
        // normalization the self-reference would incorrectly resolve to nothing.
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
        // A prefix that does NOT match CurrentDatabaseName must never be stripped - that would
        // silently pretend a real cross-database reference is this database's own table, exactly
        // the "db.dbo.T and dbo.T must be different catalog keys" hazard Qualify's own doc
        // comment warns about. No second connection, per CLAUDE.md hard scope - stays Unknown.
        var catalog = new DatabaseCatalog { CurrentDatabaseName = "RM_AZ_Sample" };
        catalog.AddOrReplace(new CatalogTable("dbo", "tblCoordinatingAgencies", CatalogTableKind.Table, [], [], "test.sql", 1));

        Assert.Null(catalog.Find("RoutematchDirectory.dbo.tblCoordinatingAgencies"));
    }

    [Fact]
    public void Find_ThreePartNameWithNoCurrentDatabaseNameKnown_StaysUnresolved()
    {
        // File-mode/corpus scans (CatalogBuilder.Build, no live connection) never set
        // CurrentDatabaseName - a three-part reference there stays exactly as unresolvable as it
        // was before this feature existed, never a guess.
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(new CatalogTable("dbo", "tblCoordinatingAgencies", CatalogTableKind.Table, [], [], "test.sql", 1));

        Assert.Null(catalog.Find("AnyDatabase.dbo.tblCoordinatingAgencies"));
    }
}
