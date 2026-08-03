using SilentScan.Live;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Integration;

/// <summary>
/// A module CREATEd under <c>SET QUOTED_IDENTIFIER OFF</c> uses <c>"..."</c> as a legacy string
/// literal (the classic <c>EXEC("...")</c> idiom) - legal T-SQL, but ScriptDOM rejects it outright
/// when parsed under the parser's QI-ON default, since <c>"</c> becomes an identifier delimiter
/// instead. The live path must read each module's own <c>sys.sql_modules.uses_quoted_identifier</c>
/// ground truth and parse it accordingly, rather than silently misclassifying such a module as
/// broken T-SQL and dropping it. (The QI-ON contrast - the same text genuinely fails to parse
/// under QI ON - is covered at the unit level by SqlScriptParserTests: a module shaped this way
/// cannot be CREATEd on a real server under QI ON in the first place, since "..." there is a
/// syntax error at CREATE time, not merely a ScriptDOM parse difference.)
/// </summary>
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
