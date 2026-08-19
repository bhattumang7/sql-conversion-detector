using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Verify.Catalog;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Integration;

/// <summary>
/// <see cref="LiveCatalogReader"/>'s scalar-UDF registry (docs/detection-checklist.md Tier 1
/// #1) - <c>is_schema_bound</c>/<c>is_inlineable</c> straight from <c>sys.sql_modules</c>,
/// oracle-verified against the real engine rather than assumed. The connected Docker instance is
/// SQL Server 2022 (CLAUDE.md), so <c>is_inlineable</c> is always expected to be present here;
/// the pre-2019 fallback path (catching SqlException 207) has no oracle to exercise against on
/// this engine and is covered by its own unit-level reasoning in the reader's doc comment instead.
///
/// The CLR half of this registry has no automated oracle test here, deliberately: loading a real
/// SQLCLR assembly requires a .NET Framework-targeted build this repo's toolchain (.NET 10 SDK
/// only) cannot produce, and no other stream in this codebase deploys one either. The per-
/// function failure-isolation behavior <c>ReadClrScalarUdfInfoAsync</c> depends on
/// (<c>OBJECTPROPERTYEX</c> throwing SqlException 10342 for one unloadable assembly must not
/// blank out every other CLR scalar UDF's data-access info) was instead oracle-verified directly
/// against the local production copy's real EXTERNAL_ACCESS assemblies per CLAUDE.md - see that
/// method's own doc comment in <c>LiveCatalogReader</c> for what the check found and why the
/// reader is shaped the way it is as a result.
/// </summary>
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
        // The engine's own is_inlineable answer must win over a clean static blocker scan - this
        // asserts the ENGINE side of that contract (file-mode's static scan is asserted
        // separately in ScalarUdfInfoTests).
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_NotInlineable", out var info));
        Assert.False(info!.EngineIsInlineable);
    }

    [Fact]
    public async Task ReadAsync_FunctionUsingGoto_EngineReportsNotInlineable()
    {
        // Oracle discovery 2026-08-17 while parity-checking ScalarUdfInlineabilityScanner's own
        // closed blocker list against real corpus functions the list didn't explain: GOTO/label
        // usage is a genuine, previously-unrecorded FROID blocker, confirmed directly against a
        // real deployed function and its GOTO-free control (below).
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_Goto", out var info));
        Assert.False(info!.EngineIsInlineable);
    }

    [Fact]
    public async Task ReadAsync_PlainFunction_EngineReportsInlineableAsGotoFreeControl()
    {
        // Same shape as fn_Goto (a DECLARE, an IF, a SET, a RETURN) with the GOTO/label removed -
        // isolates GOTO itself as the blocker rather than the surrounding IF/SET control flow.
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_Plain", out var info));
        Assert.True(info!.EngineIsInlineable);
    }

    [Fact]
    public async Task ReadAsync_FunctionUsingCte_EngineReportsNotInlineable()
    {
        // Oracle-confirmed 2026-08-20 (real Docker probe: an otherwise-identical function with a
        // WITH clause added to its body flips is_inlineable from 1 to 0) - matches the public,
        // documented "CTE" reason in sys.dm_xe_map_values('scalar_udf_inlining_blocked_reasons').
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_Cte", out var info));
        Assert.False(info!.EngineIsInlineable);
    }

    [Fact]
    public async Task ReadAsync_FunctionWithTableValuedParameter_EngineReportsNotInlineable()
    {
        // Oracle-confirmed 2026-08-20 (real Docker probe): a scalar UDF taking a table-valued
        // (READONLY) parameter reports is_inlineable = 0 regardless of what the body does with it.
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_Tvp", out var info));
        Assert.False(info!.EngineIsInlineable);
    }

    [Fact]
    public async Task ReadAsync_FunctionWithOrderByNoTop_EngineReportsNotInlineable()
    {
        // Oracle-confirmed 2026-08-20 (real Docker probe): ORDER BY with no TOP defeats
        // is_inlineable; the identical query with TOP 1 added (below) inlines cleanly.
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
        // Oracle-confirmed 2026-08-20 (real Docker probe, also tested individually for
        // .query()/.exist()/.nodes()/.modify() - all five report is_inlineable = 0).
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_XmlValue", out var info));
        Assert.False(info!.EngineIsInlineable);
    }

    [Fact]
    public async Task ReadAsync_FunctionDeclaringXmlVariableWithNoMethodCall_EngineReportsInlineable()
    {
        // Isolates the XML METHOD CALL as the blocker, not the XML data type itself.
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_XmlNoMethod", out var info));
        Assert.True(info!.EngineIsInlineable);
    }

    [Fact]
    public async Task ReadAsync_FunctionQueryingSystemCatalog_EngineReportsNotInlineable()
    {
        // Oracle-confirmed 2026-08-20 (real Docker probe): querying sys.objects defeats
        // is_inlineable; calling a system function alone (SUSER_SNAME(), below) does not.
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
        // Oracle-confirmed 2026-08-20 (real Docker probe): STRING_AGG blocks inlining even without
        // the separate self-referencing accumulator-assignment shape.
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_StringAgg", out var info));
        Assert.False(info!.EngineIsInlineable);
    }

    [Fact]
    public async Task ReadAsync_FunctionWithSelectAccumulatorAssignment_EngineReportsNotInlineable()
    {
        // Oracle discovery 2026-08-17: the `SELECT @v = expr(@v) FROM t` running-concatenation-
        // aggregate idiom (real production code uses this in place of STRING_AGG/FOR XML PATH) is
        // a genuine, previously-unrecorded FROID blocker - a plain `SELECT @v = expr FROM t` that
        // does not read its own target variable inlines cleanly (see MergeFileModeExtras test
        // below for the file-mode static-scan side of this same claim).
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_Accum", out var info));
        Assert.False(info!.EngineIsInlineable);
    }

    [Fact]
    public async Task MergeFileModeExtras_BackfillsBlockerReasonWithoutLosingEngineFlags()
    {
        // Mirrors what LiveScanRunner actually does: read live, then merge a CatalogBuilder pass
        // over the SAME module text (LiveModuleReader's reparse in the real runner) - the merge
        // must keep the engine's own EngineIsInlineable=false (stronger truth) while backfilling
        // the InlineabilityBlocker explanation the live reader itself never computes.
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
