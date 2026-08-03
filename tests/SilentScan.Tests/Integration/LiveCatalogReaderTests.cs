using SilentScan.Core.Catalog;
using SilentScan.Live.Catalog;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Integration;

/// <summary>
/// <see cref="LiveCatalogReader"/> builds a <see cref="DatabaseCatalog"/> straight from live
/// engine metadata instead of inferring it from parsed DDL text - this locks in that the
/// resulting catalog shape (types, per-column collations, the indexed flag, computed-column
/// types the engine itself resolved, type aliases) matches what the same DDL would produce
/// through the file-mode <c>CatalogBuilder</c> path, against the real oracle rather than a
/// hand-maintained expectation of what <c>sys.columns</c>/<c>sys.indexes</c> return.
/// </summary>
[Trait("Category", "Oracle")]
public sealed class LiveCatalogReaderTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(LiveCatalogReaderTests);

    protected override string Ddl => """
        CREATE TABLE dbo.Customers (
            CustomerId INT NOT NULL PRIMARY KEY,
            Email varchar(100) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
            DisplayName AS (Email + '!'),
            INDEX IX_Email (Email));
        GO
        CREATE TYPE dbo.PhoneNumber FROM VARCHAR(20) NOT NULL;
        """;

    [Fact]
    public async Task ReadAsync_TableColumns_MatchDeployedDdl()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        var table = Assert.Single(catalog.Tables, t => t.Name == "Customers");
        Assert.Equal("dbo", table.SchemaName);

        var customerId = table.FindColumn("CustomerId");
        Assert.NotNull(customerId);
        Assert.Equal(SqlTypeCategory.Int, customerId!.Type!.Category);
        Assert.False(customerId.IsComputed);

        var email = table.FindColumn("Email");
        Assert.NotNull(email);
        Assert.Equal(SqlTypeCategory.VarChar, email!.Type!.Category);
        Assert.Equal(100, email.Type.Length);
        Assert.Equal("SQL_Latin1_General_CP1_CI_AS", email.Type.Collation?.Name);
        Assert.False(email.IsNullable);
    }

    [Fact]
    public async Task ReadAsync_ComputedColumn_TypeIsEngineResolvedNotReDerived()
    {
        // Unlike file mode (which must re-derive a computed column's type from its defining
        // expression via ComputedColumnTypeResolver), live mode gets the engine's own already-
        // resolved type straight from sys.columns - this asserts that type actually reads back
        // as the string family it really is, not Unknown/null.
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        var table = Assert.Single(catalog.Tables, t => t.Name == "Customers");
        var displayName = table.FindColumn("DisplayName");
        Assert.NotNull(displayName);
        Assert.True(displayName!.IsComputed);
        Assert.False(displayName.IsPersisted);
        Assert.NotNull(displayName.Type);
        Assert.Equal(SqlTypeCategory.VarChar, displayName.Type!.Category);
    }

    [Fact]
    public async Task ReadAsync_IndexedColumn_IsFlaggedIndexed()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        var table = Assert.Single(catalog.Tables, t => t.Name == "Customers");
        Assert.True(table.IsIndexedColumn("Email"));
        Assert.True(table.IsIndexedColumn("CustomerId"));
        Assert.False(table.IsIndexedColumn("DisplayName"));
    }

    [Fact]
    public async Task ReadAsync_TypeAlias_ResolvesToUnderlyingType()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        Assert.True(catalog.TypeAliases.TryGetValue("dbo.PhoneNumber", out var underlying));
        Assert.Equal(SqlTypeCategory.VarChar, underlying!.Category);
        Assert.Equal(20, underlying.Length);
    }

    [Fact]
    public async Task ReadAsync_DatabaseDefaultCollation_MatchesDeployedDatabase()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        Assert.NotNull(catalog.DefaultCollation);
        Assert.False(string.IsNullOrWhiteSpace(catalog.DefaultCollation!.Name));
    }
}
