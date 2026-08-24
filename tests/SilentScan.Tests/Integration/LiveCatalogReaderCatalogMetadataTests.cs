using Microsoft.Data.SqlClient;
using SilentScan.Tests.Support;
using SilentScan.Verify.Catalog;
using SilentScan.Core.Catalog;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class LiveCatalogReaderCatalogMetadataTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(LiveCatalogReaderCatalogMetadataTests);

    protected override string Ddl => """
        ALTER DATABASE CURRENT SET RECURSIVE_TRIGGERS ON;
        GO
        ALTER DATABASE CURRENT SET AUTO_CREATE_STATISTICS OFF;
        GO
        CREATE TABLE dbo.Widgets (
            WidgetId INT NOT NULL PRIMARY KEY,
            Code VARCHAR(20) NOT NULL);
        GO
        CREATE STATISTICS ST_Widgets_Code ON dbo.Widgets (Code) WITH NORECOMPUTE;
        GO
        CREATE SYNONYM dbo.WidgetsAlias FOR dbo.Widgets;
        GO
        CREATE SYNONYM dbo.WidgetsCrossDb FOR OtherDatabaseSeed.dbo.Widgets;
        GO
        CREATE VIEW dbo.WidgetsPlainView AS SELECT WidgetId, Code FROM dbo.Widgets;
        GO
        CREATE VIEW dbo.WidgetsIndexedView WITH SCHEMABINDING AS SELECT WidgetId, Code FROM dbo.Widgets;
        GO
        CREATE UNIQUE CLUSTERED INDEX IX_WidgetsIndexedView ON dbo.WidgetsIndexedView (WidgetId);
        GO
        CREATE FUNCTION dbo.fn_InlineTvf()
        RETURNS TABLE
        AS
        RETURN (SELECT WidgetId FROM dbo.Widgets);
        GO
        CREATE FUNCTION dbo.fn_MultiStatementTvf()
        RETURNS @Result TABLE (WidgetId INT NOT NULL)
        AS
        BEGIN
            INSERT INTO @Result SELECT WidgetId FROM dbo.Widgets;
            RETURN;
        END;
        GO
        CREATE RULE dbo.PositiveAmountRule AS @value > 0;
        GO
        CREATE TABLE dbo.RuledTable (Id INT NOT NULL PRIMARY KEY, Amount INT NOT NULL);
        GO
        EXEC sys.sp_bindrule 'dbo.PositiveAmountRule', 'dbo.RuledTable.Amount';
        GO
        CREATE TABLE dbo.UnruledTable (Id INT NOT NULL PRIMARY KEY, Amount INT NOT NULL);
        GO
        CREATE PARTITION FUNCTION PF_WidgetRange (INT) AS RANGE LEFT FOR VALUES (100, 200);
        GO
        CREATE PARTITION SCHEME PS_WidgetRange AS PARTITION PF_WidgetRange ALL TO ([PRIMARY]);
        GO
        CREATE TABLE dbo.PartitionedWidgets (
            WidgetId INT NOT NULL,
            INDEX CIX_PartitionedWidgets CLUSTERED (WidgetId))
        ON PS_WidgetRange (WidgetId);
        GO
        CREATE TABLE dbo.FullTextSource (
            Id INT NOT NULL,
            Notes NVARCHAR(200) NOT NULL,
            CONSTRAINT PK_FullTextSource PRIMARY KEY (Id));
        GO
        CREATE FULLTEXT CATALOG FT_Catalog AS DEFAULT;
        GO
        CREATE FULLTEXT INDEX ON dbo.FullTextSource (Notes) KEY INDEX PK_FullTextSource ON FT_Catalog;
        GO
        CREATE TABLE dbo.PlainSource (
            Id INT NOT NULL PRIMARY KEY,
            Notes NVARCHAR(200) NOT NULL);
        GO
        CREATE TABLE dbo.SecuredRowsFilter (Id INT NOT NULL PRIMARY KEY, OwnerId INT NOT NULL);
        GO
        CREATE TABLE dbo.SecuredRowsBlock (Id INT NOT NULL PRIMARY KEY, OwnerId INT NOT NULL);
        GO
        CREATE FUNCTION dbo.fn_OwnerPredicate(@OwnerId INT)
        RETURNS TABLE
        WITH SCHEMABINDING
        AS
        RETURN SELECT 1 AS fn_result WHERE @OwnerId = 1;
        GO
        CREATE SECURITY POLICY dbo.SecuredRowsFilterPolicy
        ADD FILTER PREDICATE dbo.fn_OwnerPredicate(OwnerId) ON dbo.SecuredRowsFilter
        WITH (STATE = ON);
        GO
        CREATE SECURITY POLICY dbo.SecuredRowsBlockPolicy
        ADD BLOCK PREDICATE dbo.fn_OwnerPredicate(OwnerId) ON dbo.SecuredRowsBlock AFTER INSERT
        WITH (STATE = OFF);
        """;

    [Fact]
    public async Task ReadAsync_CompatibilityLevel_MatchesDeployedDatabase()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT compatibility_level FROM sys.databases WHERE database_id = DB_ID();";
        var real = (byte)(await command.ExecuteScalarAsync())!;

        Assert.Equal(real, (byte)catalog.CompatibilityLevel!.Value);
    }

    [Fact]
    public async Task ReadAsync_RecursiveTriggersToggledOn_ReadsRealFlagFromSysDatabases()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        Assert.True(catalog.IsRecursiveTriggersEnabled);
    }

    [Fact]
    public async Task ReadAsync_AutoCreateStatsToggledOff_ReadsRealFlagFromSysDatabases()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        Assert.False(catalog.IsAutoCreateStatsOn);
    }

    [Fact]
    public async Task ReadAsync_NestedTriggers_MatchesRealServerConfiguration()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CAST(value_in_use AS INT) FROM sys.configurations WHERE name = 'nested triggers';";
        var real = (int)(await command.ExecuteScalarAsync())! != 0;

        Assert.Equal(real, catalog.IsNestedTriggersEnabled);
    }

    [Fact]
    public async Task ReadAsync_Statistics_ReadsRealNoRecomputeAndKeyColumnFromSysStats()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        var table = catalog.Find("dbo.Widgets");
        Assert.NotNull(table);
        var stat = Assert.Single(table!.EffectiveStatistics, s => s.Name == "ST_Widgets_Code");
        Assert.True(stat.NoRecompute);
        Assert.False(stat.IsAutoCreated);
        Assert.Equal(["Code"], stat.KeyColumns);
    }

    [Fact]
    public async Task ReadAsync_LocalSynonym_ResolvesToUnderlyingTable()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        Assert.Equal("dbo.Widgets", catalog.ResolveSynonymName("dbo.WidgetsAlias"));
    }

    [Fact]
    public async Task ReadAsync_CrossDatabaseSynonym_ResolvesWithDatabasePrefixPreserved()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        Assert.Equal("OtherDatabaseSeed.dbo.Widgets", catalog.ResolveSynonymName("dbo.WidgetsCrossDb"));
    }

    [Fact]
    public async Task ReadAsync_SchemaBoundIndexedView_IsFlaggedIndexedViewButPlainViewIsNot()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        Assert.True(catalog.IsIndexedView("dbo.WidgetsIndexedView"));
        Assert.False(catalog.IsIndexedView("dbo.WidgetsPlainView"));
    }

    [Fact]
    public async Task ReadAsync_View_CompiledColumnsMatchRealColumnOrderFromSysColumns()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        Assert.True(catalog.TryGetViewCompiledColumns("dbo.WidgetsPlainView", out var columns));
        Assert.Equal(["WidgetId", "Code"], columns);
    }

    [Fact]
    public async Task ReadAsync_InlineTableValuedFunction_KindIsInline()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        Assert.True(catalog.TryGetTableValuedFunctionKind("dbo.fn_InlineTvf", out var kind));
        Assert.Equal(TableValuedFunctionKind.Inline, kind);
    }

    [Fact]
    public async Task ReadAsync_MultiStatementTableValuedFunction_KindIsMultiStatement()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        Assert.True(catalog.TryGetTableValuedFunctionKind("dbo.fn_MultiStatementTvf", out var kind));
        Assert.Equal(TableValuedFunctionKind.MultiStatement, kind);
    }

    [Fact]
    public async Task ReadAsync_RuleBoundColumn_TableIsFlaggedHasRuleConstraintButUnruledTableIsNot()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        var ruled = catalog.Find("dbo.RuledTable");
        Assert.NotNull(ruled);
        Assert.True(ruled!.HasRuleConstraint);

        var unruled = catalog.Find("dbo.UnruledTable");
        Assert.NotNull(unruled);
        Assert.False(unruled!.HasRuleConstraint);
    }

    [Fact]
    public async Task ReadAsync_FullTextIndexedTable_IsFlaggedButPlainTableIsNot()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        var indexed = catalog.Find("dbo.FullTextSource");
        Assert.NotNull(indexed);
        Assert.True(indexed!.HasFullTextIndex);

        var plain = catalog.Find("dbo.PlainSource");
        Assert.NotNull(plain);
        Assert.False(plain!.HasFullTextIndex);
    }

    [Fact]
    public async Task ReadAsync_PartitionedTable_PartitionSchemeAndFilegroupsResolveFromRealPartitionCatalog()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        var partitioned = catalog.Find("dbo.PartitionedWidgets");
        Assert.NotNull(partitioned);
        Assert.Equal("PS_WidgetRange", partitioned!.PartitionSchemeName);
        Assert.Equal("PRIMARY", catalog.FindPartitionFilegroup("PS_WidgetRange", 1));
        Assert.Equal("PRIMARY", catalog.FindPartitionFilegroup("PS_WidgetRange", 2));
        Assert.Equal("PRIMARY", catalog.FindPartitionFilegroup("PS_WidgetRange", 3));

        var unpartitioned = catalog.Find("dbo.Widgets");
        Assert.NotNull(unpartitioned);
        Assert.Null(unpartitioned!.PartitionSchemeName);
    }

    [Fact]
    public async Task ReadAsync_NonPartitionedTable_ResolvesRealPrimaryFilegroupFromSysFilegroups()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        var table = catalog.Find("dbo.Widgets");
        Assert.NotNull(table);
        Assert.Equal("PRIMARY", table!.FilegroupName);
        Assert.False(table.FilegroupIsReadOnly);
    }

    [Fact]
    public async Task ReadAsync_EnabledFilterSecurityPolicy_ReadsRealPredicateFromSysSecurityPredicates()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        var predicate = Assert.Single(catalog.SecurityPredicates, p => p.PolicyQualifiedName == "dbo.SecuredRowsFilterPolicy");
        Assert.Equal("dbo.SecuredRowsFilter", predicate.TargetTableQualifiedName);
        Assert.True(predicate.IsFilterPredicate);
        Assert.True(predicate.IsPolicyEnabled);
        Assert.Contains("fn_OwnerPredicate", predicate.PredicateDefinitionText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAsync_DisabledBlockSecurityPolicy_ReadsRealDisabledStateAndBlockKindFromSysSecurityPredicates()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        var predicate = Assert.Single(catalog.SecurityPredicates, p => p.PolicyQualifiedName == "dbo.SecuredRowsBlockPolicy");
        Assert.Equal("dbo.SecuredRowsBlock", predicate.TargetTableQualifiedName);
        Assert.False(predicate.IsFilterPredicate);
        Assert.False(predicate.IsPolicyEnabled);
    }

    [Fact]
    public async Task ReadAsync_TableWithoutCdc_NeverFlaggedCdcPartitionSwitchDisallowed()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        var table = catalog.Find("dbo.Widgets");
        Assert.NotNull(table);
        Assert.False(table!.CdcPartitionSwitchDisallowed);
    }
}
