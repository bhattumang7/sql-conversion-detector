using SilentScan.Core.Predicates;
using SilentScan.Verify;
using SilentScan.Verify.Deployment;
using SilentScan.Verify.Oracle;

namespace SilentScan.Tests.Integration;

/// <summary>
/// Roadmap Phase E3: exercises <see cref="CollationConflictVerifier"/> end-to-end against the
/// real oracle - closes the gap where CollationConflictFinding had zero presence in the corpus
/// verify pipeline (only ad-hoc unit-level typing tests confirmed the classifier's own logic,
/// never that the resulting comparison genuinely fails to compile against a real engine).
/// </summary>
[Trait("Category", "Oracle")]
public sealed class CollationConflictVerifierTests : IAsyncLifetime
{
    private const string DatabaseName = "SilentScanCollationConflictVerifierTest";

    private readonly SqlServerOptions _options = SqlServerOptions.LocalDocker;
    private readonly DatabaseProvisioner _provisioner;
    private readonly CollationConflictVerifier _verifier;

    public CollationConflictVerifierTests()
    {
        _provisioner = new DatabaseProvisioner(_options);
        _verifier = new CollationConflictVerifier(_options);
    }

    public async Task InitializeAsync()
    {
        await _provisioner.CreateFreshAsync(DatabaseName);
        await new ScriptDeployer(_options).DeployAsync(
            """
            CREATE TABLE dbo.CC1 (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
            GO
            CREATE TABLE dbo.CC2 (Code VARCHAR(20) COLLATE Latin1_General_CI_AS NOT NULL);
            GO
            CREATE TABLE dbo.CC3 (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
            GO
            """,
            DatabaseName);
    }

    public async Task DisposeAsync() =>
        await _provisioner.DropIfExistsAsync(DatabaseName);

    [Fact]
    public async Task VerifyAsync_GenuinelyConflictingCollations_ConfirmsCompileFailure()
    {
        var finding = new CollationConflictFinding(
            "dbo.CC1", "Code", "SQL_Latin1_General_CP1_CI_AS",
            "dbo.CC2", "Code", "Latin1_General_CI_AS",
            "=", "file.sql", 1, 1);

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(CollationConflictOutcome.Confirmed, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_SameCollationOnBothColumns_IsNotConfirmed()
    {
        // A negative control: two columns sharing the same collation compile fine - proves the
        // probe itself isn't just always failing for some unrelated reason.
        var finding = new CollationConflictFinding(
            "dbo.CC1", "Code", "SQL_Latin1_General_CP1_CI_AS",
            "dbo.CC3", "Code", "SQL_Latin1_General_CP1_CI_AS",
            "=", "file.sql", 1, 1);

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(CollationConflictOutcome.NotConfirmed, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_TableNoLongerInDeployedSchema_ReturnsProbeFailed()
    {
        var finding = new CollationConflictFinding(
            "dbo.DoesNotExist", "Code", "SQL_Latin1_General_CP1_CI_AS",
            "dbo.CC2", "Code", "Latin1_General_CI_AS",
            "=", "file.sql", 1, 1);

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(CollationConflictOutcome.ProbeFailed, result.Outcome);
    }
}
