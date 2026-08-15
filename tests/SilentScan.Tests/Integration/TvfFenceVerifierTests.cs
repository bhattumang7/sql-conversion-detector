using SilentScan.Core.Predicates;
using SilentScan.Verify;
using SilentScan.Verify.Deployment;
using SilentScan.Verify.Oracle;

namespace SilentScan.Tests.Integration;

/// <summary>
/// Exercises <see cref="TvfFenceVerifier"/> end-to-end against the real oracle
/// (docs/detection-checklist.md Tier 1 #2). The plan-XML marker itself (<c>PhysicalOp="Table-
/// valued function"</c> for a real fence, absent for an inline TVF; <c>StatementType="INSERT
/// EXEC"</c> for INSERT...EXEC) was hand-verified directly against this same Docker instance
/// before being hardcoded into <see cref="TvfFenceVerifier"/> - this locks that verification in
/// as a regression test rather than leaving it a one-off manual check.
/// </summary>
[Trait("Category", "Oracle")]
public sealed class TvfFenceVerifierTests : IAsyncLifetime
{
    private const string DatabaseName = "SilentScanTvfFenceVerifierTest";

    private readonly SqlServerOptions _options = SqlServerOptions.LocalDocker;
    private readonly DatabaseProvisioner _provisioner;
    private readonly TvfFenceVerifier _verifier;

    public TvfFenceVerifierTests()
    {
        _provisioner = new DatabaseProvisioner(_options);
        _verifier = new TvfFenceVerifier(_options);
    }

    public async Task InitializeAsync()
    {
        await _provisioner.CreateFreshAsync(DatabaseName);

        const string ddl = """
            CREATE FUNCTION dbo.fn_Fence(@Id INT)
            RETURNS @T TABLE (Id INT)
            AS
            BEGIN
                INSERT INTO @T (Id) SELECT @Id;
                RETURN;
            END;
            GO
            CREATE FUNCTION dbo.itvf_NotAFence(@Id INT)
            RETURNS TABLE
            AS
            RETURN (SELECT @Id AS Id);
            GO
            CREATE PROCEDURE dbo.usp_GetIds AS
            BEGIN
                SELECT 1 AS Id;
            END;
            GO
            CREATE PROCEDURE dbo.usp_NoResultSet AS
            BEGIN
                DECLARE @x INT = 1;
            END;
            GO
            """;
        await new ScriptDeployer(_options).DeployAsync(ddl, DatabaseName);
    }

    public async Task DisposeAsync() =>
        await _provisioner.DropIfExistsAsync(DatabaseName);

    private static TvfFenceFinding FunctionFinding(TvfFenceFindingKind kind, string functionQualifiedName) => new(
        kind, functionQualifiedName, functionQualifiedName, Core.Catalog.TableValuedFunctionKind.MultiStatement, "file.sql", 1, 1);

    [Fact]
    public async Task VerifyAsync_MultiStatementTvfReference_ConfirmsTableValuedFunctionOperator()
    {
        var finding = FunctionFinding(TvfFenceFindingKind.FromOrJoin, "dbo.fn_Fence");

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(TvfFenceOutcome.Confirmed, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_MislabeledInlineTvf_IsNotConfirmed()
    {
        // Negative control: a (deliberately mislabeled) finding claiming an inline TVF is a
        // fence must not be confirmed - proves the plan-shape signal actually distinguishes the
        // two, not just that SOME plan XML was captured.
        var finding = FunctionFinding(TvfFenceFindingKind.FromOrJoin, "dbo.itvf_NotAFence");

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(TvfFenceOutcome.NotConfirmed, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_CorrelatedApplyKind_StillConfirmsViaDummyArguments()
    {
        // CorrelatedApply's own source arguments reference an outer row this probe has no scope
        // for - proves the dummy-argument substitution (TvfFenceProbeBuilder never reuses the
        // finding's own ReferenceFragmentText) still reaches the same underlying confirmation.
        var finding = FunctionFinding(TvfFenceFindingKind.CorrelatedApply, "dbo.fn_Fence");

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(TvfFenceOutcome.Confirmed, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_InsertExec_ConfirmsInsertExecStatementType()
    {
        var finding = new TvfFenceFinding(
            TvfFenceFindingKind.InsertExec, FunctionQualifiedName: null, "dbo.usp_GetIds", FunctionKind: null, "file.sql", 1, 1);

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(TvfFenceOutcome.Confirmed, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_InsertExecProcedureWithNoResultSet_IsNotProbeable()
    {
        var finding = new TvfFenceFinding(
            TvfFenceFindingKind.InsertExec, FunctionQualifiedName: null, "dbo.usp_NoResultSet", FunctionKind: null, "file.sql", 1, 1);

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(TvfFenceOutcome.NotProbeable, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_UnknownFunctionName_IsNotProbeable()
    {
        var finding = FunctionFinding(TvfFenceFindingKind.Standalone, "dbo.fn_DoesNotExist");

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(TvfFenceOutcome.NotProbeable, result.Outcome);
    }
}
