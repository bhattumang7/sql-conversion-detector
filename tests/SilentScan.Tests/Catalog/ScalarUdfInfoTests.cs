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
