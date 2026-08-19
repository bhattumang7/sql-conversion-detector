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
