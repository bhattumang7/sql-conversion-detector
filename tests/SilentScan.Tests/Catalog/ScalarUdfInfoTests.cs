using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;

namespace SilentScan.Tests.Catalog;

public sealed class ScalarUdfInfoTests
{
    private static DatabaseCatalog BuildFrom(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return CatalogBuilder.Build([result]);
    }

    [Fact]
    public void Build_PlainScalarFunction_RegistersTSqlKindNonSchemaBoundNoBlocker()
    {
        var catalog = BuildFrom("""
            CREATE FUNCTION dbo.fn_Clean (@x INT)
            RETURNS INT
            AS
            BEGIN
                RETURN @x + 1;
            END
            """);

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_Clean", out var info));
        Assert.NotNull(info);
        Assert.Equal(ScalarUdfKind.TSql, info.Kind);
        Assert.False(info.IsSchemaBound);
        Assert.Null(info.InlineabilityBlocker);
        Assert.Null(info.EngineIsInlineable);
        Assert.Null(info.ClrDataAccess);
    }

    [Fact]
    public void Build_TableValuedFunction_DoesNotRegisterScalarUdfInfo()
    {
        var catalog = BuildFrom("""
            CREATE FUNCTION dbo.fn_Orders ()
            RETURNS TABLE
            AS
            RETURN (SELECT 1 AS Id);
            """);

        Assert.False(catalog.TryGetScalarUdfInfo("dbo.fn_Orders", out _));
    }

    [Fact]
    public void Build_SchemaBoundFunction_RecordsSchemaBindingTrue()
    {
        var catalog = BuildFrom("""
            CREATE FUNCTION dbo.fn_Bound (@x INT)
            RETURNS INT
            WITH SCHEMABINDING
            AS
            BEGIN
                RETURN @x + 1;
            END
            """);

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_Bound", out var info));
        Assert.True(info!.IsSchemaBound);
    }

    [Fact]
    public void Build_FunctionUsingGetDate_RecordsInlineabilityBlocker()
    {
        var catalog = BuildFrom("""
            CREATE FUNCTION dbo.fn_Now (@x INT)
            RETURNS DATETIME
            AS
            BEGIN
                RETURN GETDATE();
            END
            """);

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_Now", out var info));
        Assert.Contains("GETDATE", info!.InlineabilityBlocker, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_FunctionUsingWhileLoop_RecordsInlineabilityBlocker()
    {
        var catalog = BuildFrom("""
            CREATE FUNCTION dbo.fn_Loop (@x INT)
            RETURNS INT
            AS
            BEGIN
                DECLARE @i INT = 0;
                WHILE @i < @x
                BEGIN
                    SET @i = @i + 1;
                END
                RETURN @i;
            END
            """);

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_Loop", out var info));
        Assert.Contains("WHILE", info!.InlineabilityBlocker, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_FunctionUsingGoto_RecordsInlineabilityBlocker()
    {

        var catalog = BuildFrom("""
            CREATE FUNCTION dbo.fn_Goto (@x INT)
            RETURNS INT
            AS
            BEGIN
                DECLARE @v INT = @x;
                IF @v IS NULL
                BEGIN
                    GOTO DONE;
                END
                SET @v = @v + 1;
                DONE:
                RETURN @v;
            END
            """);

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_Goto", out var info));
        Assert.Contains("GOTO", info!.InlineabilityBlocker, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_FunctionWithIfElseAndSetOnly_NoGotoNoBlocker()
    {

        var catalog = BuildFrom("""
            CREATE FUNCTION dbo.fn_NoGoto (@x INT)
            RETURNS INT
            AS
            BEGIN
                DECLARE @v INT = @x;
                IF @v IS NULL
                BEGIN
                    SET @v = 0;
                END
                SET @v = @v + 1;
                RETURN @v;
            END
            """);

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_NoGoto", out var info));
        Assert.Null(info!.InlineabilityBlocker);
    }

    [Fact]
    public void Build_FunctionUsingCte_RecordsInlineabilityBlocker()
    {

        var catalog = BuildFrom("""
            CREATE FUNCTION dbo.fn_Cte (@x INT)
            RETURNS INT
            AS
            BEGIN
                DECLARE @r INT;
                WITH cte AS (SELECT @x AS v)
                SELECT @r = v FROM cte;
                RETURN @r;
            END
            """);

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_Cte", out var info));
        Assert.Contains("CTE", info!.InlineabilityBlocker, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_FunctionWithNoCte_NoBlocker()
    {

        var catalog = BuildFrom("""
            CREATE FUNCTION dbo.fn_NoCte (@x INT)
            RETURNS INT
            AS
            BEGIN
                RETURN @x + 1;
            END
            """);

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_NoCte", out var info));
        Assert.Null(info!.InlineabilityBlocker);
    }

    [Fact]
    public void Build_FunctionWithTableValuedParameter_RecordsInlineabilityBlocker()
    {

        var catalog = BuildFrom("""
            CREATE TYPE dbo.IntList AS TABLE (v INT);
            GO
            CREATE FUNCTION dbo.fn_Tvp (@t dbo.IntList READONLY, @x INT)
            RETURNS INT
            AS
            BEGIN
                DECLARE @c INT;
                SELECT @c = COUNT(*) FROM @t;
                RETURN @c + @x;
            END
            """);

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_Tvp", out var info));
        Assert.Contains("table-valued parameter", info!.InlineabilityBlocker, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_FunctionWithOrderByAndNoTop_RecordsInlineabilityBlocker()
    {

        var catalog = BuildFrom("""
            CREATE TABLE dbo.OrderSource (v INT NOT NULL);
            GO
            CREATE FUNCTION dbo.fn_OrderByNoTop (@x INT)
            RETURNS INT
            AS
            BEGIN
                DECLARE @c INT;
                SELECT @c = v FROM dbo.OrderSource ORDER BY v;
                RETURN @c + @x;
            END
            """);

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_OrderByNoTop", out var info));
        Assert.Contains("ORDER BY", info!.InlineabilityBlocker, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_FunctionWithOrderByAndTop_NoBlocker()
    {

        var catalog = BuildFrom("""
            CREATE TABLE dbo.OrderSource (v INT NOT NULL);
            GO
            CREATE FUNCTION dbo.fn_OrderByWithTop (@x INT)
            RETURNS INT
            AS
            BEGIN
                DECLARE @c INT;
                SELECT TOP 1 @c = v FROM dbo.OrderSource ORDER BY v;
                RETURN @c + @x;
            END
            """);

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_OrderByWithTop", out var info));
        Assert.Null(info!.InlineabilityBlocker);
    }

    [Theory]
    [InlineData("DECLARE @c INT = @doc.value('(/a)[1]', 'INT');", "value")]
    [InlineData("DECLARE @c XML = @doc.query('/a');", "query")]
    [InlineData("DECLARE @c BIT = @doc.exist('/a');", "exist")]
    public void Build_FunctionUsingXmlInstanceMethod_RecordsInlineabilityBlocker(string statement, string methodName)
    {

        var catalog = BuildFrom($$"""
            CREATE FUNCTION dbo.fn_XmlMethod (@x INT)
            RETURNS INT
            AS
            BEGIN
                DECLARE @doc XML = '<a><b>1</b></a>';
                {{statement}}
                RETURN @x;
            END
            """);

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_XmlMethod", out var info));
        Assert.Contains(methodName, info!.InlineabilityBlocker, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_FunctionUsingXmlNodesShredding_RecordsInlineabilityBlocker()
    {
        var catalog = BuildFrom("""
            CREATE FUNCTION dbo.fn_XmlNodes (@x INT)
            RETURNS INT
            AS
            BEGIN
                DECLARE @doc XML = '<a><b>1</b></a>';
                DECLARE @c INT;
                SELECT TOP 1 @c = 1 FROM @doc.nodes('/a/b') AS t(c);
                RETURN @x + ISNULL(@c, 0);
            END
            """);

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_XmlNodes", out var info));
        Assert.Contains("nodes", info!.InlineabilityBlocker, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_FunctionUsingXmlModify_RecordsInlineabilityBlocker()
    {
        var catalog = BuildFrom("""
            CREATE FUNCTION dbo.fn_XmlModify (@x INT)
            RETURNS INT
            AS
            BEGIN
                DECLARE @doc XML = '<a/>';
                SET @doc.modify('insert <b/> into (/a)[1]');
                RETURN @x;
            END
            """);

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_XmlModify", out var info));
        Assert.Contains("modify", info!.InlineabilityBlocker, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_FunctionDeclaringXmlVariableWithNoMethodCall_NoBlocker()
    {

        var catalog = BuildFrom("""
            CREATE FUNCTION dbo.fn_XmlNoMethod (@x INT)
            RETURNS INT
            AS
            BEGIN
                DECLARE @doc XML = '<a/>';
                RETURN @x + 1;
            END
            """);

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_XmlNoMethod", out var info));
        Assert.Null(info!.InlineabilityBlocker);
    }

    [Fact]
    public void Build_FunctionQueryingSystemCatalog_RecordsInlineabilityBlocker()
    {

        var catalog = BuildFrom("""
            CREATE FUNCTION dbo.fn_SysAccess (@x INT)
            RETURNS INT
            AS
            BEGIN
                DECLARE @c INT;
                SELECT @c = COUNT(*) FROM sys.objects WHERE type = 'U';
                RETURN @c + @x;
            END
            """);

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_SysAccess", out var info));
        Assert.Contains("system catalog access", info!.InlineabilityBlocker, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_FunctionCallingSystemFunctionOnly_NoBlocker()
    {

        var catalog = BuildFrom("""
            CREATE FUNCTION dbo.fn_SuserName (@x INT)
            RETURNS INT
            AS
            BEGIN
                DECLARE @n SYSNAME = SUSER_SNAME();
                RETURN @x + LEN(@n);
            END
            """);

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_SuserName", out var info));
        Assert.Null(info!.InlineabilityBlocker);
    }

    [Fact]
    public void Build_FunctionUsingStringAgg_RecordsInlineabilityBlocker()
    {

        var catalog = BuildFrom("""
            CREATE TABLE dbo.AggSource (grp INT NOT NULL, s VARCHAR(50) NOT NULL);
            GO
            CREATE FUNCTION dbo.fn_StringAgg (@x INT)
            RETURNS VARCHAR(200)
            AS
            BEGIN
                DECLARE @r VARCHAR(200);
                SELECT @r = STRING_AGG(s, ',') FROM dbo.AggSource WHERE grp = @x;
                RETURN @r;
            END
            """);

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_StringAgg", out var info));
        Assert.Contains("STRING_AGG", info!.InlineabilityBlocker, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_FunctionWithSelectAccumulatorAssignment_RecordsInlineabilityBlocker()
    {

        var catalog = BuildFrom("""
            CREATE FUNCTION dbo.fn_Accum (@x INT)
            RETURNS VARCHAR(200)
            AS
            BEGIN
                DECLARE @s VARCHAR(200) = '';
                SELECT @s = COALESCE(@s + ',', '') + CAST(Val AS VARCHAR(20))
                FROM dbo.Source
                WHERE OwnerId = @x;
                RETURN @s;
            END
            """);

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_Accum", out var info));
        Assert.Contains("accumulator", info!.InlineabilityBlocker, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_FunctionWithPlainSelectAssignmentFromTable_NoAccumulatorBlocker()
    {

        var catalog = BuildFrom("""
            CREATE FUNCTION dbo.fn_PlainSelect (@x INT)
            RETURNS INT
            AS
            BEGIN
                DECLARE @v INT;
                SELECT @v = Val FROM dbo.Source WHERE OwnerId = @x;
                RETURN @v;
            END
            """);

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_PlainSelect", out var info));
        Assert.Null(info!.InlineabilityBlocker);
    }

    [Fact]
    public void Build_FunctionReferencingNonInlineableCallee_RecordsBlockerOneLevelDeep()
    {
        var catalog = BuildFrom("""
            CREATE FUNCTION dbo.fn_Inner (@x INT)
            RETURNS DATETIME
            AS
            BEGIN
                RETURN GETDATE();
            END
            GO
            CREATE FUNCTION dbo.fn_Outer (@x INT)
            RETURNS DATETIME
            AS
            BEGIN
                RETURN dbo.fn_Inner(@x);
            END
            """);

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_Outer", out var info));
        Assert.Contains("fn_Inner", info!.InlineabilityBlocker);
    }

    [Fact]
    public void Build_FunctionUsingTryCatch_RecordsInlineabilityBlocker()
    {
        var catalog = BuildFrom("""
            CREATE FUNCTION dbo.fn_TryCatch (@x INT)
            RETURNS INT
            AS
            BEGIN
                DECLARE @r INT = 0;
                BEGIN TRY
                    SET @r = @x / 1;
                END TRY
                BEGIN CATCH
                    SET @r = 0;
                END CATCH
                RETURN @r;
            END
            """);

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_TryCatch", out var info));
        Assert.Contains("TRY/CATCH", info!.InlineabilityBlocker, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_FunctionDeclaringTableVariable_RecordsInlineabilityBlocker()
    {
        var catalog = BuildFrom("""
            CREATE FUNCTION dbo.fn_TableVar (@x INT)
            RETURNS INT
            AS
            BEGIN
                DECLARE @t TABLE (Id INT);
                RETURN @x;
            END
            """);

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_TableVar", out var info));
        Assert.Contains("table variable", info!.InlineabilityBlocker, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_FunctionUsingExecuteStatement_RecordsInlineabilityBlocker()
    {
        var catalog = BuildFrom("""
            CREATE PROCEDURE dbo.usp_Helper AS SELECT 1;
            GO
            CREATE FUNCTION dbo.fn_Exec (@x INT)
            RETURNS INT
            AS
            BEGIN
                EXEC dbo.usp_Helper;
                RETURN @x;
            END
            """);

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_Exec", out var info));
        Assert.Contains("EXECUTE", info!.InlineabilityBlocker, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_FunctionDeclaringCursor_RecordsInlineabilityBlocker()
    {
        var catalog = BuildFrom("""
            CREATE FUNCTION dbo.fn_Cursor (@x INT)
            RETURNS INT
            AS
            BEGIN
                DECLARE cur CURSOR LOCAL FOR SELECT 1;
                RETURN @x;
            END
            """);

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_Cursor", out var info));
        Assert.Contains("cursor", info!.InlineabilityBlocker, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_FunctionWithMultipleReturnStatements_RecordsInlineabilityBlocker()
    {
        var catalog = BuildFrom("""
            CREATE FUNCTION dbo.fn_MultiReturn (@x INT)
            RETURNS INT
            AS
            BEGIN
                IF @x > 0
                    RETURN 1;
                RETURN 0;
            END
            """);

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_MultiReturn", out var info));
        Assert.Contains("multiple RETURN", info!.InlineabilityBlocker, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_RecursiveFunction_RecordsInlineabilityBlocker()
    {
        var catalog = BuildFrom("""
            CREATE FUNCTION dbo.fn_Recursive (@x INT)
            RETURNS INT
            AS
            BEGIN
                RETURN dbo.fn_Recursive(@x - 1);
            END
            """);

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_Recursive", out var info));
        Assert.Contains("recursive", info!.InlineabilityBlocker, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_FunctionReferencingDbts_RecordsInlineabilityBlocker()
    {
        var catalog = BuildFrom("""
            CREATE FUNCTION dbo.fn_Dbts ()
            RETURNS VARBINARY(8)
            AS
            BEGIN
                RETURN @@DBTS;
            END
            """);

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_Dbts", out var info));
        Assert.Contains("@@DBTS", info!.InlineabilityBlocker, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_ClrFunction_RegistersClrKindAndSkipsBlockerScan()
    {
        var catalog = BuildFrom(
            "CREATE FUNCTION dbo.fn_Clr (@x INT) RETURNS INT " +
            "EXTERNAL NAME [MyAssembly].[MyNamespace.MyClass].[MyMethod];");

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_Clr", out var info));
        Assert.Equal(ScalarUdfKind.Clr, info!.Kind);
        Assert.Null(info.InlineabilityBlocker);
    }

    [Fact]
    public void Build_DropFunction_RemovesScalarUdfInfo()
    {
        var catalog = BuildFrom("""
            CREATE FUNCTION dbo.fn_Temp (@x INT)
            RETURNS INT
            AS
            BEGIN
                RETURN @x;
            END
            GO
            DROP FUNCTION dbo.fn_Temp;
            """);

        Assert.False(catalog.TryGetScalarUdfInfo("dbo.fn_Temp", out _));
    }
}
