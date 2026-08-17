using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting;

namespace SilentScan.Tests.Reporting;

/// <summary>
/// The dynamic-SQL OUTPUT-summary fixpoint loop in <see cref="ScanReportBuilder.BuildFromParseResults"/>
/// runs up to 5 rounds, each scanning the same parsed modules with a growing
/// <c>outputSummaryIndex</c> - only that index differs between rounds, never the parse itself.
/// An earlier version of the streaming rewrite (every full-corpus phase reparsing fresh from a
/// lazy source instead of sharing one materialized list - see
/// <see cref="ScanReportBuilderStreamingSourceTests"/>) re-enumerated the lazy
/// <c>allParseResults</c> source on EVERY round, so a database whose OUTPUT chains needed several
/// rounds to converge reparsed its whole module corpus that many times over - measured directly:
/// on a 300-chain/depth-5 Docker fixture needing 5 rounds, this single stage went from 1.6s to
/// 8.2s (5.1x), becoming the slowest stage in the whole scan. The fix materializes the parsed
/// modules ONCE for this phase and reuses that across all rounds, since nothing about the parse
/// changes round to round.
///
/// This locks in the fix: a fixture whose OUTPUT chain genuinely needs 3 rounds to resolve must
/// still enumerate the underlying source only a small, ROUND-COUNT-INDEPENDENT number of times.
/// </summary>
public sealed class ScanReportBuilderDynamicSqlReparseTests
{
    [Fact]
    public void ThreeRoundOutputChain_EnumeratesSourceNoMoreThanASingleRoundWould()
    {
        // usp_L0 sets its OUTPUT directly (round 1 discovers its summary). usp_L1 calls usp_L0
        // and forwards its resolved value (round 1 can't resolve this yet - L0's summary isn't
        // known until AFTER round 1 folds it in - so round 2 discovers usp_L1's summary using
        // it). usp_Consumer calls usp_L1 and splices the result into dynamic SQL (round 3 is the
        // first round that can resolve THIS, using usp_L1's round-2 summary) - three rounds,
        // not one, confirming the fixture actually exercises the multi-round path this test
        // guards rather than trivially converging on round 1.
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

        // Proves the fixture genuinely needed the multi-round path: this only comes back
        // AnalyzedLiteral once usp_L1's OUTPUT has been resolved through usp_L0's own summary,
        // which round 1 alone cannot do - a stale/unresolved chain would report Unanalyzable
        // instead, since @Code would still look non-constant.
        var finding = Assert.Single(report.DynamicSqlFindings);
        Assert.Equal(DynamicSqlOutcome.AnalyzedLiteral, finding.Outcome);

        // The regression this guards: enumeration count must not scale with how many rounds the
        // OUTPUT-summary loop needed. Measured directly: materializing once (the fix) enumerates
        // the source a small, round-count-independent number of times - one per full-corpus phase
        // (7 at the baseline this test originally locked in, +2 for the MSTVF-as-fence stream's
        // own two full-corpus passes, +1 for the partial-composite-FK-join stream, +1 for the
        // SET-option stream, +1 for the catch-all-predicate stream, +1 for the NOT-IN-nullable-
        // subquery stream, +1 for the non-unique-UPDATE-source stream, +1 for the forced-serial-
        // construct-inventory stream, +1 for the multi-referenced-CTE stream, +1 for the post-
        // expansion-join-width stream, +1 for the select-star-in-nested-view stream, +1 for the
        // self-referencing-DML stream, +1 for the module-compile-flag stream, +3 for the
        // window-frame/WAITFOR/view-ordering streams, +1 for the transaction-hygiene stream
        // (docs/detection-checklist.md Tier 2 "Catch-all / kitchen-sink predicates", "NOT IN over
        // a nullable subquery column", "UPDATE ... FROM without source uniqueness", "Forced-serial
        // construct inventory", "Lineage-metric findings": "Multi-referenced CTE",
        // "Post-expansion join width", and "SELECT * inside a view or inline TVF", "Halloween
        // Protection and self-referencing DML", and "Small precise adds": WITH RECOMPILE / TVF
        // database-collation return, RANGE window-function frames, WAITFOR DELAY/TIME, TOP(100)
        // PERCENT/ORDER BY in a view or inline TVF, unresolved BEGIN TRANSACTION, +2 for the
        // "Hint and index-shape catalog checks" streams: composite index leading-column violation
        // and INDEX hint validity) - the same "one dedicated pass per stream" shape every other
        // stage in this method already uses; the local-variable-predicate stream reuses the
        // existing typed-predicate pass instead of adding its own, so it costs nothing here; the
        // untrusted-constraint/cascading-FK/nested-view-depth/temporal-table-history-index-gap
        // streams are catalog/lineage-only passes with no per-file enumeration at all, so they
        // cost nothing here either; +5 for the "Second OSS/commercial sweep" streams (SET
        // DATEFORMAT/DATEFIRST, true cartesian join, undersized declarations, TRUNCATE swallowed
        // by a non-rethrowing CATCH, unindexed SELECT INTO temp table usage) - four full-corpus
        // passes plus UndersizedDeclarationScanner's own declaration-side pass, its catalog-side
        // pass costing nothing here since it never enumerates usableParseResults); +1 for the
        // new OutputParameterScanner full-corpus pass (docs/detection-checklist.md "Second
        // OSS/commercial sweep": output parameter not populated on every code path) -
        // DatabaseConfigurationFindings costs nothing here, a single sys.databases/Query-Store
        // read with no per-file enumeration at all; +1 for the new
        // ParameterReassignmentPredicateScanner full-corpus pass (docs/detection-checklist.md
        // "Catch-all / kitchen-sink predicates" sibling: parameter overwritten before use in a
        // predicate); +1 for the CodeMetricScanner full-corpus pass; +1 for the new
        // FormattingScanner full-corpus pass (docs/detection-checklist.md Tier 4 "Formatting and
        // layout" - tab characters, statement/declaration line-sharing, unbraced conditional
        // bodies, dangling statements, redundant parentheses, missing file headers); +1 for the
        // new NamingScanner full-corpus pass (docs/detection-checklist.md Tier 4 "Naming and
        // identifiers" - reserved keyword as identifier, "sp_" prefix on a user routine,
        // unqualified CREATE, redundant type qualifier); +1 for the new DeadCodeScanner
        // full-corpus pass (docs/detection-checklist.md Tier 4 "Dead and duplicated code":
        // unreachable code, unused label, unused local variable, unused parameter, redundant
        // jump). Reverting to
        // re-enumerating per round pushes this fixture's 3 rounds well past that. 38 sits
        // strictly between the two (measured 38 with every fix/stream landed to date), so this
        // fails the moment the loop stops reusing its one materialization and starts scaling with
        // round count again, while still tolerating a future +/-1 shift from unrelated changes
        // elsewhere in the method (e.g. the next new full-corpus stream).
        Assert.True(
            countingSource.EnumerationCount <= 38,
            $"expected the source to be enumerated a small, round-count-independent number of times (measured: 38 with every fix/stream landed to date), but it was enumerated {countingSource.EnumerationCount} time(s) - the dynamic-SQL fixpoint loop likely regressed back to reparsing the corpus fresh on every round instead of materializing once and reusing it across rounds.");
    }

    /// <summary>Wraps a fixed sequence, counting how many independent enumerations it's ever asked for - never caching, so re-enumerating genuinely re-walks the source.</summary>
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
