using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.TypeInference;
using SilentScan.Core.Common;

namespace SilentScan.Tests.Catalog;

public sealed class DynamicSqlTempTableDiscoveryTests
{
    private static DatabaseCatalog DiscoverFrom(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return DynamicSqlTempTableDiscovery.Discover([result]);
    }

    [Fact]
    public void Discover_CreateTableBuiltEntirelyFromLiteralConcatenation_RegistersUnderCallingProcScope()
    {
        var catalog = DiscoverFrom("""
            CREATE PROCEDURE dbo.usp_BuildRuns AS
            BEGIN
                DECLARE @ddl NVARCHAR(MAX) = ''
                SET @ddl = @ddl + 'CREATE TABLE #Runs ('
                SET @ddl = @ddl + 'RunID INT NOT NULL, '
                SET @ddl = @ddl + 'RunDate DATE NOT NULL'
                SET @ddl = @ddl + ')'
                EXEC (@ddl)
            END
            """);

        var table = catalog.Find("#Runs", "dbo.usp_BuildRuns");
        Assert.NotNull(table);
        Assert.Equal(CatalogTableKind.TemporaryTable, table.Kind);
        Assert.Equal(2, table.Columns.Count);
        Assert.Equal(SqlTypeCategory.Int, table.FindColumn("RunID")!.Type!.Category);
        Assert.Equal(SqlTypeCategory.Date, table.FindColumn("RunDate")!.Type!.Category);
    }

    [Fact]
    public void Discover_NoDynamicSql_ReturnsEmptyCatalog()
    {
        var catalog = DiscoverFrom("CREATE TABLE dbo.Orders (OrderId INT NOT NULL);");

        Assert.Empty(catalog.Tables);
    }

    [Fact]
    public void Discover_DynamicSqlWithoutCreateTable_DoesNotAttemptToParseIt()
    {
        var catalog = DiscoverFrom("""
            CREATE PROCEDURE dbo.usp_RunReport AS
            BEGIN
                EXEC ('SELECT 1')
            END
            """);

        Assert.Empty(catalog.Tables);
    }

    [Fact]
    public void Discover_CreateTableInsideUnfoldableDynamicSql_DeclinesRatherThanGuesses()
    {
        var ddlResult = SqlScriptParser.ParseText("ddl.sql", "CREATE TABLE dbo.T (Col NVARCHAR(MAX) NOT NULL);");
        Assert.False(ddlResult.HasErrors);
        var procResult = SqlScriptParser.ParseText("test.sql", """
            CREATE PROCEDURE dbo.usp_BuildRuns AS
            BEGIN
                DECLARE @ddl NVARCHAR(MAX)
                SELECT @ddl = Col FROM dbo.T
                EXEC (@ddl)
            END
            """);
        Assert.False(procResult.HasErrors);

        var catalog = DynamicSqlTempTableDiscovery.Discover([ddlResult, procResult]);

        Assert.Empty(catalog.Tables);
    }

    [Fact]
    public void MergeFileModeExtras_DiscoveredSyntheticWrapperHasNoParameters_DoesNotClobberTheRealProcedureSCatalogedParameters()
    {
        var procSql = """
            CREATE PROCEDURE dbo.usp_BuildRuns @RunDate DATE, @Flag INT AS
            BEGIN
                DECLARE @ddl NVARCHAR(MAX) = ''
                SET @ddl = @ddl + 'CREATE TABLE #Runs ('
                SET @ddl = @ddl + 'RunID INT NOT NULL'
                SET @ddl = @ddl + ')'
                EXEC (@ddl)
            END
            """;
        var procResult = SqlScriptParser.ParseText("test.sql", procSql);
        Assert.False(procResult.HasErrors, string.Join("; ", procResult.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([procResult]);
        Assert.True(catalog.TryGetProcedureParameters("dbo.usp_BuildRuns", out var beforeMerge));
        Assert.Equal(2, beforeMerge.Count);

        var discovered = DynamicSqlTempTableDiscovery.Discover([procResult]);
        catalog.MergeFileModeExtras(discovered);

        Assert.True(catalog.TryGetProcedureParameters("dbo.usp_BuildRuns", out var afterMerge));
        Assert.Equal(2, afterMerge.Count);
        Assert.Equal("@RunDate", afterMerge[0].Name);
        Assert.Equal("@Flag", afterMerge[1].Name);
    }
}
