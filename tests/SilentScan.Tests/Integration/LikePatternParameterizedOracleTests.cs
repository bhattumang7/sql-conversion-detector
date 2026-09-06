using SilentScan.Verify;
using SilentScan.Verify.Deployment;
using SilentScan.Verify.Oracle;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class LikePatternParameterizedOracleTests : IAsyncLifetime
{
    private const string DatabaseName = "SilentScanLikeParameterizedOracleTest";

    private readonly SqlServerOptions _options = SqlServerOptions.LocalDocker;
    private readonly DatabaseProvisioner _provisioner;

    public LikePatternParameterizedOracleTests()
    {
        _provisioner = new DatabaseProvisioner(_options);
    }

    public async Task InitializeAsync()
    {
        await _provisioner.CreateFreshAsync(DatabaseName);
        await new ScriptDeployer(_options).DeployAsync(
            """
            CREATE TABLE dbo.Users (DisplayName VARCHAR(40) NOT NULL);
            GO
            CREATE INDEX IX_Users_DisplayName ON dbo.Users(DisplayName);
            GO
            CREATE PROCEDURE dbo.ProbeLike @p VARCHAR(40) AS
            BEGIN
                SELECT DisplayName FROM dbo.Users WHERE DisplayName LIKE @p;
            END
            GO
            CREATE PROCEDURE dbo.ProbeLikeLeadingWildcardConcat @p VARCHAR(40) AS
            BEGIN
                SELECT DisplayName FROM dbo.Users WHERE DisplayName LIKE '%' + @p;
            END
            GO
            """,
            DatabaseName);
    }

    public async Task DisposeAsync() =>
        await _provisioner.DropIfExistsAsync(DatabaseName);

    private async Task<bool> HasIndexSeek(string probe)
    {
        var planXml = await new PlanXmlCapture(_options).CaptureAsync(DatabaseName, probe);
        return IndexAccessDetector.HasIndexSeek(planXml, "IX_Users_DisplayName");
    }

    [Fact]
    public async Task ParameterReferencePattern_AttemptsIndexSeek() =>
        Assert.True(await HasIndexSeek("EXEC dbo.ProbeLike @p = 'Name1%';"));

    [Fact]
    public async Task ConcatenatedLeadingWildcardPattern_StillAttemptsIndexSeek() =>
        Assert.True(await HasIndexSeek("EXEC dbo.ProbeLikeLeadingWildcardConcat @p = 'Name1';"));
}
