using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;

namespace SilentScan.Tests.Catalog;

/// <summary>
/// The scalar-UDF stream's own catalog registry (docs/detection-checklist.md Tier 1 #1) -
/// schemabinding, CLR-vs-T-SQL classification, and the Appendix-3 inlineability blocker scan,
/// all populated at CREATE/ALTER FUNCTION time exactly like the existing return-type registry.
/// </summary>
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
        // Oracle-confirmed 2026-08-17 (LiveCatalogReaderScalarUdfTests.
        // ReadAsync_FunctionUsingGoto_EngineReportsNotInlineable): a real GOTO/label defeats
        // sys.sql_modules.is_inlineable - a genuine blocker this closed list did not previously
        // recognize, found while parity-checking it against real corpus functions.
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
        // GOTO-free control: same IF/SET shape as fn_Goto, no GOTO/label - isolates GOTO itself
        // as the blocker rather than the surrounding IF.
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
        // Oracle-confirmed 2026-08-20 (LiveCatalogReaderScalarUdfTests.
        // ReadAsync_FunctionUsingCte_EngineReportsNotInlineable): a CTE anywhere in the body
        // defeats sys.sql_modules.is_inlineable.
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
        // No-CTE control, otherwise identical shape to fn_Cte - isolates the CTE itself as the
        // blocker rather than the surrounding accumulator-assignment pattern.
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
        // Oracle-confirmed 2026-08-20 (LiveCatalogReaderScalarUdfTests.
        // ReadAsync_FunctionWithTableValuedParameter_EngineReportsNotInlineable): a scalar UDF
        // taking a table-valued (READONLY) parameter reports is_inlineable = 0 regardless of what
        // the body itself does with it - checked from the parameter list, not the body scan.
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
        // Oracle-confirmed 2026-08-20 (LiveCatalogReaderScalarUdfTests.
        // ReadAsync_FunctionWithOrderByNoTop_EngineReportsNotInlineable): ORDER BY with no TOP
        // defeats sys.sql_modules.is_inlineable, matching the documented "OrderByWithoutTop"
        // reason.
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
        // TOP control, otherwise identical shape - isolates ORDER-BY-without-TOP as the blocker
        // rather than ORDER BY itself.
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
        // Oracle-confirmed 2026-08-20 (all three tested individually against the real engine,
        // plus .nodes()/.modify() below - LiveCatalogReaderScalarUdfTests): an XML data-type
        // instance method call blocks inlining; declaring an XML-typed variable alone does not
        // (see Build_FunctionDeclaringXmlVariableWithNoMethodCall_NoBlocker).
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
        // No-method-call control, otherwise identical: isolates the XML METHOD CALL as the
        // blocker, not the XML data type itself (oracle-confirmed 2026-08-20).
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
        // Oracle-confirmed 2026-08-20 (LiveCatalogReaderScalarUdfTests.
        // ReadAsync_FunctionQueryingSystemCatalog_EngineReportsNotInlineable): querying sys.objects
        // defeats sys.sql_modules.is_inlineable, matching the documented "SystemDataAccess" reason.
        // Calling a system FUNCTION alone (SUSER_SNAME()) does not - isolated below.
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
        // Isolates system CATALOG TABLE access as the blocker - a system FUNCTION call alone is a
        // different, unblocked shape.
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
    public void Build_FunctionWithSelectAccumulatorAssignment_RecordsInlineabilityBlocker()
    {
        // Oracle-confirmed 2026-08-17 (LiveCatalogReaderScalarUdfTests.
        // ReadAsync_FunctionWithSelectAccumulatorAssignment_EngineReportsNotInlineable): a
        // `SELECT @v = expr(@v) FROM t` running-concatenation aggregate - the real idiom this
        // codebase's own corpus uses in place of STRING_AGG/FOR XML PATH - defeats
        // is_inlineable, a genuine blocker this closed list did not previously recognize.
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
        // Same FROM-clause SELECT-assignment shape as fn_Accum, but the assigned expression does
        // not read the target variable's own prior value - isolates the self-reference, not the
        // FROM clause itself, as the blocker.
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
