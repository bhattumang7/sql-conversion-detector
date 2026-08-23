using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Predicates;
using SilentScan.Verify;
using SilentScan.Verify.Deployment;
using SilentScan.Verify.Oracle;

namespace SilentScan.Tests.Integration;

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
        var finding = Finding("dbo.fn_Inlineable", ScalarUdfInlineability.NotInlineable);

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(ScalarUdfOutcome.NotConfirmed, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_NotInlineableFunctionMislabeledInlineable_IsNotConfirmed()
    {
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
