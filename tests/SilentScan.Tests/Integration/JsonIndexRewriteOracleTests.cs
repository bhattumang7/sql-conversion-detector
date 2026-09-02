using SilentScan.Verify;
using SilentScan.Verify.Deployment;
using SilentScan.Verify.Oracle;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class JsonIndexRewriteOracleTests : IAsyncLifetime
{
    private const string JsonIndexName = "IX_T3_Payload";

    private static readonly SqlServerOptions Options = new(
        Host: "localhost",
        Port: int.TryParse(Environment.GetEnvironmentVariable("SILENTSCAN_SQL2025_PORT"), out var port) ? port : 14331,
        UserId: "sa",
        Password: Environment.GetEnvironmentVariable("SILENTSCAN_SA_PASSWORD") ?? "SilentScan!Dev2026");

    private readonly string _databaseName = $"{nameof(JsonIndexRewriteOracleTests)}_{Guid.NewGuid():N}";

    private const string Ddl = """
        SET QUOTED_IDENTIFIER ON;
        GO
        CREATE TABLE dbo.T3 (Id INT IDENTITY PRIMARY KEY, Payload JSON NOT NULL);
        GO
        DECLARE @i INT = 0;
        WHILE @i < 5000
        BEGIN
            INSERT INTO dbo.T3 (Payload) VALUES (JSON_OBJECT('status': CASE WHEN @i = 2500 THEN 'shipped' ELSE 'pending' END));
            SET @i += 1;
        END
        GO
        CREATE JSON INDEX IX_T3_Payload ON dbo.T3(Payload);
        GO
        UPDATE STATISTICS dbo.T3;
        GO
        """;

    public async Task InitializeAsync()
    {
        await new DatabaseProvisioner(Options).CreateFreshAsync(_databaseName);
        await new ScriptDeployer(Options).DeployAsync(Ddl, _databaseName);
    }

    public async Task DisposeAsync() =>
        await new DatabaseProvisioner(Options).DropIfExistsAsync(_databaseName);

    [Fact]
    public async Task JsonContainsEqualityPredicate_SeeksTheJsonIndex()
    {
        var planXml = await new PlanXmlCapture(Options).CaptureAsync(
            _databaseName, "SELECT COUNT(*) FROM dbo.T3 WHERE JSON_CONTAINS(Payload, 'shipped', '$.status') = 1;");

        Assert.True(IndexAccessDetector.HasIndexSeek(planXml, JsonIndexName));
    }

    [Fact]
    public async Task JsonValueEqualityPredicate_NeverSeeksTheJsonIndex_EvenThoughOneExists()
    {
        var planXml = await new PlanXmlCapture(Options).CaptureAsync(
            _databaseName, "SELECT COUNT(*) FROM dbo.T3 WHERE JSON_VALUE(Payload, '$.status') = 'shipped';");

        Assert.False(IndexAccessDetector.HasIndexSeek(planXml, JsonIndexName));
        Assert.Contains("Clustered Index Scan", planXml, StringComparison.Ordinal);
    }
}
