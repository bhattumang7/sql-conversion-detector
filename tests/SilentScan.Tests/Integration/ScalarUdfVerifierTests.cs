using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Predicates;
using SilentScan.Verify;
using SilentScan.Verify.Deployment;
using SilentScan.Verify.Oracle;

namespace SilentScan.Tests.Integration;

/// <summary>
/// Exercises <see cref="ScalarUdfVerifier"/> end-to-end against the real oracle
/// (docs/detection-checklist.md Tier 1 #1). Both plan-XML markers this stream depends on -
/// <c>&lt;UserDefinedFunction&gt;</c> for a call the engine does not fold away, and
/// <c>ContainsInlineScalarTsqlUdfs="1"</c> for one it inlines away entirely (SQL 2019+ FROID) -
/// were hand-verified directly against this same Docker instance before being hardcoded into
/// <see cref="ScalarUdfVerifier"/>, including the surprising case the two-probe design exists to
/// route around: a scalar UDF called INSIDE a view still gets folded away under
/// <c>OPTION (USE HINT('DISABLE_TSQL_SCALAR_UDF_INLINING'))</c>, even though the identical call
/// made directly at the top level does not (the hint doesn't propagate into a view's own
/// algebrized definition) - which is exactly why <see cref="ScalarUdfProbeBuilder"/> always
/// probes the underlying function directly, never through the view.
/// </summary>
[Trait("Category", "Oracle")]
public sealed class ScalarUdfVerifierTests : IAsyncLifetime
{
    private const string DatabaseName = "SilentScanScalarUdfVerifierTest";

    private readonly SqlServerOptions _options = SqlServerOptions.LocalDocker;
    private readonly DatabaseProvisioner _provisioner;
    private readonly ScalarUdfVerifier _verifier;

    public ScalarUdfVerifierTests()
    {
        _provisioner = new DatabaseProvisioner(_options);
        _verifier = new ScalarUdfVerifier(_options);
    }

    public async Task InitializeAsync()
    {
        await _provisioner.CreateFreshAsync(DatabaseName);

        const string ddl = """
            ALTER DATABASE CURRENT SET COMPATIBILITY_LEVEL = 160;
            GO
            CREATE FUNCTION dbo.fn_Inlineable(@x INT)
            RETURNS INT
            AS
            BEGIN
                RETURN @x + 1;
            END;
            GO
            CREATE FUNCTION dbo.fn_NotInlineable(@x INT)
            RETURNS DATETIME
            AS
            BEGIN
                RETURN GETDATE();
            END;
            GO
            """;
        await new ScriptDeployer(_options).DeployAsync(ddl, DatabaseName);
    }

    public async Task DisposeAsync() =>
        await _provisioner.DropIfExistsAsync(DatabaseName);

    private static ScalarUdfFinding Finding(string functionQualifiedName, ScalarUdfInlineability inlineability) => new(
        ScalarUdfFindingKind.PredicateInvocation,
        functionQualifiedName,
        functionQualifiedName,
        ScalarUdfKind.TSql,
        inlineability,
        InlineabilityBlocker: null,
        IsSchemaBound: false,
        ConstantArgumentsNotFolded: false,
        ClrDataAccess: null,
        Context: ScalarUdfContext.Where,
        SchemaDependencyKind: null,
        SourcePath: "file.sql",
        Line: 1,
        Column: 1);

    [Fact]
    public async Task VerifyAsync_NotInlineableFunctionClaimedNotInlineable_Confirmed()
    {
        var finding = Finding("dbo.fn_NotInlineable", ScalarUdfInlineability.NotInlineable);

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(ScalarUdfOutcome.Confirmed, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_InlineableFunctionClaimedInlineable_Confirmed()
    {
        var finding = Finding("dbo.fn_Inlineable", ScalarUdfInlineability.Inlineable);

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(ScalarUdfOutcome.Confirmed, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_UnknownInlineability_ConfirmedOnFunctionReferenceAlone()
    {
        var finding = Finding("dbo.fn_Inlineable", ScalarUdfInlineability.Unknown);

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(ScalarUdfOutcome.Confirmed, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_InlineableFunctionMislabeledNotInlineable_IsNotConfirmed()
    {
        // Negative control: the engine actually inlines dbo.fn_Inlineable away (ContainsInline
        // ScalarTsqlUdfs) - a finding wrongly claiming NotInlineable must not be confirmed, proving
        // the natural-probe cross-check actually discriminates rather than rubber-stamping any
        // Inlineability value.
        var finding = Finding("dbo.fn_Inlineable", ScalarUdfInlineability.NotInlineable);

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(ScalarUdfOutcome.NotConfirmed, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_NotInlineableFunctionMislabeledInlineable_IsNotConfirmed()
    {
        // Negative control in the other direction: dbo.fn_NotInlineable's GETDATE() call means
        // the engine never inlines it away, even naturally - a finding wrongly claiming Inlineable
        // must not be confirmed either.
        var finding = Finding("dbo.fn_NotInlineable", ScalarUdfInlineability.Inlineable);

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(ScalarUdfOutcome.NotConfirmed, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_UnknownFunctionName_IsNotProbeable()
    {
        var finding = Finding("dbo.fn_DoesNotExist", ScalarUdfInlineability.Unknown);

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(ScalarUdfOutcome.NotProbeable, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_SchemaDependencyFinding_IsNeverProbed()
    {
        var finding = Finding("dbo.fn_Inlineable", ScalarUdfInlineability.Unknown)
            with { Kind = ScalarUdfFindingKind.SchemaDependency, SchemaDependencyKind = SchemaDependencyKind.ComputedColumn };

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(ScalarUdfOutcome.NotProbeable, result.Outcome);
    }
}
