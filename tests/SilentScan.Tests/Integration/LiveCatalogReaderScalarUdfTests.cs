using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Verify.Catalog;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class LiveCatalogReaderScalarUdfTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(LiveCatalogReaderScalarUdfTests);

    protected override string Ddl => """
        CREATE FUNCTION dbo.fn_Plain (@x INT)
        RETURNS INT
        AS
        BEGIN
            RETURN @x + 1;
        END;
        GO
        CREATE FUNCTION dbo.fn_Bound (@x INT)
        RETURNS INT
        WITH SCHEMABINDING
        AS
        BEGIN
            RETURN @x + 1;
        END;
        GO
        CREATE FUNCTION dbo.fn_NotInlineable (@x INT)
        RETURNS DATETIME
        AS
        BEGIN
            RETURN GETDATE();
        END;
        GO
        CREATE FUNCTION dbo.fn_ForSchema (@x INT)
        RETURNS INT
        AS
        BEGIN
            RETURN @x + 1;
        END;
        GO
        CREATE TABLE dbo.AccumSource (
            OwnerId INT NOT NULL,
            Val INT NOT NULL
        );
        GO
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
        END;
        GO
        CREATE FUNCTION dbo.fn_Accum (@x INT)
        RETURNS VARCHAR(200)
        AS
        BEGIN
            DECLARE @s VARCHAR(200) = '';
            SELECT @s = COALESCE(@s + ',', '') + CAST(Val AS VARCHAR(20))
            FROM dbo.AccumSource
            WHERE OwnerId = @x;
            RETURN @s;
        END;
        GO
        CREATE FUNCTION dbo.fn_Cte (@x INT)
        RETURNS INT
        AS
        BEGIN
            DECLARE @r INT;
            WITH cte AS (SELECT @x AS v)
            SELECT @r = v FROM cte;
            RETURN @r;
        END;
        GO
        CREATE TYPE dbo.IntList AS TABLE (v INT);
        GO
        CREATE FUNCTION dbo.fn_Tvp (@t dbo.IntList READONLY, @x INT)
        RETURNS INT
        AS
        BEGIN
            DECLARE @c INT;
            SELECT @c = COUNT(*) FROM @t;
            RETURN @c + @x;
        END;
        GO
        CREATE TABLE dbo.OrderSource (v INT NOT NULL);
        GO
        CREATE FUNCTION dbo.fn_OrderByNoTop (@x INT)
        RETURNS INT
        AS
        BEGIN
            DECLARE @c INT;
            SELECT @c = v FROM dbo.OrderSource ORDER BY v;
            RETURN @c + @x;
        END;
        GO
        CREATE FUNCTION dbo.fn_OrderByWithTop (@x INT)
        RETURNS INT
        AS
        BEGIN
            DECLARE @c INT;
            SELECT TOP 1 @c = v FROM dbo.OrderSource ORDER BY v;
            RETURN @c + @x;
        END;
        GO
        CREATE FUNCTION dbo.fn_XmlValue (@x INT)
        RETURNS INT
        AS
        BEGIN
            DECLARE @doc XML = '<a><b>1</b></a>';
            DECLARE @c INT = @doc.value('(/a/b)[1]', 'INT');
            RETURN @c + @x;
        END;
        GO
        CREATE FUNCTION dbo.fn_XmlNoMethod (@x INT)
        RETURNS INT
        AS
        BEGIN
            DECLARE @doc XML = '<a/>';
            RETURN @x + 1;
        END;
        GO
        CREATE FUNCTION dbo.fn_SysAccess (@x INT)
        RETURNS INT
        AS
        BEGIN
            DECLARE @c INT;
            SELECT @c = COUNT(*) FROM sys.objects WHERE type = 'U';
            RETURN @c + @x;
        END;
        GO
        CREATE FUNCTION dbo.fn_SuserName (@x INT)
        RETURNS INT
        AS
        BEGIN
            DECLARE @n SYSNAME = SUSER_SNAME();
            RETURN @x + LEN(@n);
        END;
        GO
        CREATE TABLE dbo.AggSource (grp INT NOT NULL, s VARCHAR(50) NOT NULL);
        GO
        CREATE FUNCTION dbo.fn_StringAgg (@x INT)
        RETURNS VARCHAR(200)
        AS
        BEGIN
            DECLARE @r VARCHAR(200);
            SELECT @r = STRING_AGG(s, ',') FROM dbo.AggSource WHERE grp = @x;
            RETURN @r;
        END;
        GO
        CREATE TABLE dbo.SchemaDependent (
            Id INT NOT NULL,
            Computed AS dbo.fn_ForSchema(Id),
            Code INT NOT NULL DEFAULT (dbo.fn_ForSchema(0)),
            CONSTRAINT CK_SchemaDependent CHECK (dbo.fn_ForSchema(Id) > 0)
        );
        """;

    [Fact]
    public async Task ReadAsync_PlainScalarFunction_IsInlineableTrueNotSchemaBound()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_Plain", out var info));
        Assert.Equal(ScalarUdfKind.TSql, info!.Kind);
        Assert.False(info.IsSchemaBound);
        Assert.True(info.EngineIsInlineable);
    }

    [Fact]
    public async Task ReadAsync_SchemaBoundFunction_IsSchemaBoundTrue()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_Bound", out var info));
        Assert.True(info!.IsSchemaBound);
    }

    [Fact]
    public async Task ReadAsync_FunctionCallingGetDate_EngineReportsNotInlineable()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_NotInlineable", out var info));
        Assert.False(info!.EngineIsInlineable);
    }

    [Fact]
    public async Task ReadAsync_FunctionUsingGoto_EngineReportsNotInlineable()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_Goto", out var info));
        Assert.False(info!.EngineIsInlineable);
    }

    [Fact]
    public async Task ReadAsync_PlainFunction_EngineReportsInlineableAsGotoFreeControl()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_Plain", out var info));
        Assert.True(info!.EngineIsInlineable);
    }

    [Fact]
    public async Task ReadAsync_FunctionUsingCte_EngineReportsNotInlineable()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_Cte", out var info));
        Assert.False(info!.EngineIsInlineable);
    }

    [Fact]
    public async Task ReadAsync_FunctionWithTableValuedParameter_EngineReportsNotInlineable()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_Tvp", out var info));
        Assert.False(info!.EngineIsInlineable);
    }

    [Fact]
    public async Task ReadAsync_FunctionWithOrderByNoTop_EngineReportsNotInlineable()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_OrderByNoTop", out var info));
        Assert.False(info!.EngineIsInlineable);
    }

    [Fact]
    public async Task ReadAsync_FunctionWithOrderByAndTop_EngineReportsInlineable()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_OrderByWithTop", out var info));
        Assert.True(info!.EngineIsInlineable);
    }

    [Fact]
    public async Task ReadAsync_FunctionUsingXmlValueMethod_EngineReportsNotInlineable()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_XmlValue", out var info));
        Assert.False(info!.EngineIsInlineable);
    }

    [Fact]
    public async Task ReadAsync_FunctionDeclaringXmlVariableWithNoMethodCall_EngineReportsInlineable()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_XmlNoMethod", out var info));
        Assert.True(info!.EngineIsInlineable);
    }

    [Fact]
    public async Task ReadAsync_FunctionQueryingSystemCatalog_EngineReportsNotInlineable()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_SysAccess", out var info));
        Assert.False(info!.EngineIsInlineable);
    }

    [Fact]
    public async Task ReadAsync_FunctionCallingSystemFunctionOnly_EngineReportsInlineable()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_SuserName", out var info));
        Assert.True(info!.EngineIsInlineable);
    }

    [Fact]
    public async Task ReadAsync_FunctionUsingStringAgg_EngineReportsNotInlineable()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_StringAgg", out var info));
        Assert.False(info!.EngineIsInlineable);
    }

    [Fact]
    public async Task ReadAsync_FunctionWithSelectAccumulatorAssignment_EngineReportsNotInlineable()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_Accum", out var info));
        Assert.False(info!.EngineIsInlineable);
    }

    [Fact]
    public async Task MergeFileModeExtras_BackfillsBlockerReasonWithoutLosingEngineFlags()
    {
        var liveCatalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        var parseResult = SqlScriptParser.ParseText("fn_NotInlineable.sql", """
            CREATE FUNCTION dbo.fn_NotInlineable (@x INT)
            RETURNS DATETIME
            AS
            BEGIN
                RETURN GETDATE();
            END;
            """);
        var fileModeCatalog = CatalogBuilder.Build([parseResult]);

        liveCatalog.MergeFileModeExtras(fileModeCatalog);

        Assert.True(liveCatalog.TryGetScalarUdfInfo("dbo.fn_NotInlineable", out var info));
        Assert.False(info!.EngineIsInlineable);
        Assert.Contains("GETDATE", info.InlineabilityBlocker, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAsync_ComputedColumnDefaultAndCheckConstraint_AllReportScalarUdfDependency()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        var references = catalog.SchemaExpressions.Where(r => r.TableQualifiedName == "dbo.SchemaDependent").ToList();

        Assert.Contains(references, r => r.Kind == SchemaDependencyKind.ComputedColumn && r.DefinitionText.Contains("fn_ForSchema", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(references, r => r.Kind == SchemaDependencyKind.DefaultConstraint && r.DefinitionText.Contains("fn_ForSchema", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(references, r => r.Kind == SchemaDependencyKind.CheckConstraint && r.DefinitionText.Contains("fn_ForSchema", StringComparison.OrdinalIgnoreCase));
    }
}
