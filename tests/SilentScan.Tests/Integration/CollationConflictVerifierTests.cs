using SilentScan.Core.Predicates;
using SilentScan.Verify;
using SilentScan.Verify.Deployment;
using SilentScan.Verify.Oracle;

namespace SilentScan.Tests.Integration;

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

        var finding = new CollationConflictFinding(
            "dbo.CC1", "Code", "SQL_Latin1_General_CP1_CI_AS",
            "dbo.CC3", "Code", "SQL_Latin1_General_CP1_CI_AS",
            "=", "file.sql", 1, 1);

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(CollationConflictOutcome.NotConfirmed, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_GreatestOverConflictingCollations_ConfirmsCompileFailure()
    {
        var finding = new CollationConflictFinding(
            "dbo.CC1", "Code", "SQL_Latin1_General_CP1_CI_AS",
            "dbo.CC2", "Code", "Latin1_General_CI_AS",
            "GREATEST", "file.sql", 1, 1);

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(CollationConflictOutcome.Confirmed, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_LeastOverMatchingCollations_IsNotConfirmed()
    {
        var finding = new CollationConflictFinding(
            "dbo.CC1", "Code", "SQL_Latin1_General_CP1_CI_AS",
            "dbo.CC3", "Code", "SQL_Latin1_General_CP1_CI_AS",
            "LEAST", "file.sql", 1, 1);

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
