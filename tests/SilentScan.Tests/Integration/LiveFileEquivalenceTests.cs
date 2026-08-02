using SilentScan.Core.Parsing;
using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;
using SilentScan.Live;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Integration;

/// <summary>
/// Proves file-mode and live-mode agree end to end: the exact same SQL text is (a) parsed and
/// run through <see cref="ScanReportBuilder"/> as a plain file-mode scan, and (b) deployed to a
/// fresh Docker database and run through <see cref="LiveScanRunner"/>. Every column-level
/// verdict must match, with one legitimate exception: live mode may UPGRADE a file-mode
/// <see cref="Verdict.Unknown"/> to a real verdict (engine-read collations/types are strictly
/// more precise than DDL-text inference - CLAUDE.md), but must never DOWNGRADE the other
/// direction or disagree on a verdict both sides actually resolved. The fixture avoids any
/// database-default-collation dependency (every string column/comparison is either typed
/// unambiguously by precedence or carries its own explicit COLLATE) so this is a fair
/// comparison rather than one that would trivially differ on a --collation guess file mode
/// never had to make.
/// </summary>
[Trait("Category", "Oracle")]
public sealed class LiveFileEquivalenceTests : OracleTestFixture
{
    private const string FixtureSql = """
        CREATE TABLE dbo.Orders (
            OrderId INT NOT NULL PRIMARY KEY,
            OrderCode varchar(30) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
            INDEX IX_OrderCode (OrderCode));
        GO
        CREATE VIEW dbo.vOrders AS SELECT OrderId, OrderCode FROM dbo.Orders;
        GO
        CREATE PROCEDURE dbo.usp_FindOrder @OrderCode NVARCHAR(30)
        AS
        BEGIN
            SELECT OrderId FROM dbo.vOrders WHERE OrderCode = @OrderCode;
        END
        """;

    protected override string DatabaseName => nameof(LiveFileEquivalenceTests);

    protected override string Ddl => FixtureSql;

    [Fact]
    public async Task FileModeAndLiveMode_AgreeOnEveryVerdict_ExceptLiveMayUpgradeUnknown()
    {
        var parseResult = SqlScriptParser.ParseText("fixture.sql", FixtureSql);
        var fileReport = ScanReportBuilder.BuildFromParseResults([parseResult]);

        var liveResult = await LiveScanRunner.RunAsync(Options.BuildConnectionString(DatabaseName));

        var fileVerdicts = fileReport.TypedFindings.ToDictionary(
            f => (f.Column.TableQualifiedName, f.Column.ColumnName), f => f.Verdict);
        var liveVerdicts = liveResult.Report.TypedFindings.ToDictionary(
            f => (f.Column.TableQualifiedName, f.Column.ColumnName), f => f.Verdict);

        Assert.Equal(fileVerdicts.Keys.OrderBy(k => k), liveVerdicts.Keys.OrderBy(k => k));

        foreach (var (key, fileVerdict) in fileVerdicts)
        {
            var liveVerdict = liveVerdicts[key];
            var agrees = fileVerdict == liveVerdict;
            var liveUpgradedAnUnknown = fileVerdict == Verdict.Unknown && liveVerdict != Verdict.Unknown;

            Assert.True(
                agrees || liveUpgradedAnUnknown,
                $"{key.TableQualifiedName}.{key.ColumnName}: file-mode said {fileVerdict}, live-mode said {liveVerdict} - " +
                "only an Unknown-to-real upgrade is a legitimate difference.");
        }

        // The fixture's one predicate is a genuine cross-mode agreement, not just "both sides
        // found nothing" - a would-be false pass this assertion set alone couldn't catch.
        Assert.NotEmpty(fileVerdicts);
        var (_, agreedVerdict) = fileVerdicts.Single();
        Assert.Equal(Verdict.ScanForced, agreedVerdict);
    }
}
