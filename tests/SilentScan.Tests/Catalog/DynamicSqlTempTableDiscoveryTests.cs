using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;

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
        // The real-world shape this exists for: a #temp table's own CREATE TABLE text is built
        // purely from string-literal concatenation (no variable/column dependency), then handed
        // to EXEC - never a literal CreateTableStatement CatalogBuilder's own static pass would
        // ever see. The discovered shape must land under the SAME scope key
        // (SchemaObjectNameHelper.Qualify's own "schema.name" format) a real static body-declared
        // temp table would use, so FromScopeResolver's existing (scope, name) lookup finds it
        // without any changes on that side.
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
        // Near-miss: ordinary static SQL with no dynamic SQL call sites at all contributes
        // nothing here - CatalogBuilder's own static pass already covers this case, so this
        // discovery pass must not duplicate (or otherwise disturb) it.
        var catalog = DiscoverFrom("CREATE TABLE dbo.Orders (OrderId INT NOT NULL);");

        Assert.Empty(catalog.Tables);
    }

    [Fact]
    public void Discover_DynamicSqlWithoutCreateTable_DoesNotAttemptToParseIt()
    {
        // A folded dynamic SQL script that never mentions CREATE TABLE at all - the cheap
        // substring pre-filter should skip it entirely rather than wrapping and reparsing every
        // analyzable script found anywhere in the database.
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
        // The DDL text itself depends on something this scanner cannot fold (a bare column
        // reference) - the whole call site stays Unanalyzable, so there is no InnerText to
        // extract a CREATE TABLE from at all. Confirms this pass never invents a shape from a
        // call site the dynamic SQL engine itself declined.
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
}
