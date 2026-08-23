using SilentScan.Live;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class LiveScanRunnerQuotedIdentifierTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(LiveScanRunnerQuotedIdentifierTests);

    protected override string Ddl => """
        CREATE TABLE dbo.Orders (OrderId INT NOT NULL PRIMARY KEY);
        """;

    [Fact]
    public async Task RunAsync_ModuleCreatedUnderQuotedIdentifierOff_ParsesAndIsAnalyzed()
    {
        const string quotedIdentifierOffProc = """
            SET QUOTED_IDENTIFIER OFF
            GO
            CREATE PROCEDURE dbo.usp_LegacyExec AS
            BEGIN
                EXEC("SELECT 1")
            END
            """;
        await new SilentScan.Verify.Deployment.ScriptDeployer(Options).DeployAsync(quotedIdentifierOffProc, DatabaseName);

        var result = await LiveScanRunner.RunAsync(Options.BuildConnectionString(DatabaseName));

        Assert.Empty(result.UnanalyzableModules);
        Assert.Equal(1, result.ModulesAnalyzed);
        Assert.Equal(0, result.Report.ParseHealth.FilesWithErrors);
    }
}
