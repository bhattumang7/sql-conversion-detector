using SilentScan.Core.Parsing;
using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;
using SilentScan.Live;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Integration;

/// <summary>
/// Roadmap Phase C2: before <see cref="Catalog.DatabaseCatalog.MergeFileModeExtras"/> existed,
/// live mode's catalog came straight from engine metadata alone and never learned about
/// synonyms, scalar UDF return types, or temp-table/table-variable shapes - all three exist only
/// as text inside a module body, which a live scan parses (for predicate analysis) but never fed
/// through <see cref="Catalog.CatalogBuilder"/>. That made a live scan of a synonym/UDF/temp-
/// table-heavy database strictly WORSE than scanning the same objects' scripted-out DDL from
/// disk - a predicate a file-mode scan resolved fine came back "no known DDL" in live mode purely
/// because of which pipeline read the schema. Mirrors <see cref="LiveFileEquivalenceTests"/>'s
/// same-SQL-text, both-modes-must-agree structure, extended to the three previously-live-blind
/// constructs.
/// </summary>
[Trait("Category", "Oracle")]
public sealed class LiveCatalogParityExtrasTests : OracleTestFixture
{
    private const string FixtureSql = """
        CREATE TABLE dbo.Orders (OrderId INT NOT NULL, OrderCode varchar(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, INDEX IX_OrderCode (OrderCode));
        GO
        CREATE SYNONYM dbo.OrdersSynonym FOR dbo.Orders;
        GO
        CREATE FUNCTION dbo.fn_DefaultCode() RETURNS nvarchar(20) AS BEGIN RETURN N'X' END;
        GO
        CREATE PROCEDURE dbo.usp_FindViaSynonym AS
            SELECT OrderId FROM dbo.OrdersSynonym WHERE OrderCode = dbo.fn_DefaultCode();
        GO
        CREATE TABLE dbo.Accounts (Code varchar(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, INDEX IX_Code (Code));
        GO
        CREATE PROCEDURE dbo.usp_FindViaTempTable AS
        BEGIN
            DECLARE @Codes TABLE (Code NVARCHAR(20) NOT NULL);
            INSERT INTO @Codes VALUES (N'A1');
            SELECT a.Code FROM dbo.Accounts a JOIN @Codes t ON a.Code = t.Code;
        END
        """;

    protected override string DatabaseNameSeed => nameof(LiveCatalogParityExtrasTests);

    protected override string Ddl => FixtureSql;

    private static Dictionary<(string Table, string Column), Verdict> VerdictsByColumn(
        IEnumerable<Core.Predicates.TypedPredicateFinding> findings) =>
        findings.ToDictionary(f => (f.Column.TableQualifiedName, f.Column.ColumnName), f => f.Verdict);

    [Fact]
    public async Task SynonymAndScalarUdfReference_LiveModeResolvesTheSameAsFileMode()
    {
        var parseResult = SqlScriptParser.ParseText("fixture.sql", FixtureSql);
        var fileReport = ScanReportBuilder.BuildFromParseResults([parseResult]);
        var liveResult = await LiveScanRunner.RunAsync(Options.BuildConnectionString(DatabaseName));

        var fileVerdicts = VerdictsByColumn(fileReport.TypedFindings);
        var liveVerdicts = VerdictsByColumn(liveResult.Report.TypedFindings);

        var key = ("dbo.Orders", "OrderCode");
        Assert.True(fileVerdicts.ContainsKey(key), "File mode should resolve the synonym+UDF predicate.");
        Assert.True(liveVerdicts.ContainsKey(key), "Live mode should resolve the synonym+UDF predicate too - the whole point of this fix.");
        Assert.Equal(fileVerdicts[key], liveVerdicts[key]);
        Assert.Equal(Verdict.ScanForced, liveVerdicts[key]);
    }

    [Fact]
    public async Task TableVariableJoin_LiveModeResolvesTheSameAsFileMode()
    {
        var parseResult = SqlScriptParser.ParseText("fixture.sql", FixtureSql);
        var fileReport = ScanReportBuilder.BuildFromParseResults([parseResult]);
        var liveResult = await LiveScanRunner.RunAsync(Options.BuildConnectionString(DatabaseName));

        var fileVerdicts = VerdictsByColumn(fileReport.TypedFindings);
        var liveVerdicts = VerdictsByColumn(liveResult.Report.TypedFindings);

        var key = ("dbo.Accounts", "Code");
        Assert.True(fileVerdicts.ContainsKey(key), "File mode should resolve the table-variable join predicate.");
        Assert.True(liveVerdicts.ContainsKey(key), "Live mode should resolve the table-variable join predicate too.");
        Assert.Equal(fileVerdicts[key], liveVerdicts[key]);
        Assert.Equal(Verdict.ScanForced, liveVerdicts[key]);
    }
}
