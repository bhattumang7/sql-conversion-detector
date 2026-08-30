using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting;

namespace SilentScan.Tests.Reporting;

public sealed class ScanReportBuilderDynamicSqlReparseTests
{
    [Fact]
    public void ThreeRoundOutputChain_EnumeratesSourceNoMoreThanASingleRoundWould()
    {

        const string Sql = """
            CREATE TABLE dbo.Customers (Code VARCHAR(20) NOT NULL);
            GO
            CREATE PROCEDURE dbo.usp_L0 @Out VARCHAR(20) OUTPUT AS
            BEGIN
                SET @Out = 'C1';
            END
            GO
            CREATE PROCEDURE dbo.usp_L1 @Out VARCHAR(20) OUTPUT AS
            BEGIN
                DECLARE @Inner VARCHAR(20);
                EXEC dbo.usp_L0 @Out = @Inner OUTPUT;
                SET @Out = @Inner;
            END
            GO
            CREATE PROCEDURE dbo.usp_Consumer AS
            BEGIN
                DECLARE @Code VARCHAR(20);
                EXEC dbo.usp_L1 @Out = @Code OUTPUT;
                EXEC('SELECT Code FROM dbo.Customers WHERE Code = ''' + @Code + '''');
            END
            """;

        var parseResult = SqlScriptParser.ParseText("chain.sql", Sql);
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([parseResult]);
        var countingSource = new EnumerationCountingSource([parseResult]);

        var report = ScanReportBuilder.BuildFromParseResults(countingSource, catalog);

        var finding = Assert.Single(report.Find<DynamicSqlFinding>("DynamicSqlScanner"));
        Assert.Equal(DynamicSqlOutcome.AnalyzedLiteral, finding.Outcome);

        Assert.True(
            countingSource.EnumerationCount <= 51,
            $"expected the source to be enumerated a small, round-count-independent number of times (measured: 51 with every fix/stream landed to date), but it was enumerated {countingSource.EnumerationCount} time(s) - the dynamic-SQL fixpoint loop likely regressed back to reparsing the corpus fresh on every round instead of materializing once and reusing it across rounds.");
    }

    private sealed class EnumerationCountingSource(IReadOnlyList<SqlParseResult> items) : IEnumerable<SqlParseResult>
    {
        public int EnumerationCount { get; private set; }

        public IEnumerator<SqlParseResult> GetEnumerator()
        {
            EnumerationCount++;
            return items.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
