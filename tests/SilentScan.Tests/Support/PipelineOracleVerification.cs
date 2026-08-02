using SilentScan.Core.Predicates;
using SilentScan.Verify;
using SilentScan.Verify.Deployment;
using SilentScan.Verify.Oracle;

namespace SilentScan.Tests.Support;

/// <summary>
/// Reuses the same oracle machinery the corpus study uses (<see cref="CorpusFindingVerifier"/>)
/// so that pipeline-level fixture tests - the ones asserting a <c>Verdict</c> against
/// <see cref="ScanReportBuilder"/>'s <c>TypedFindings</c> output - stop trusting only that our
/// own static passes agree with themselves. A wrong-direction lineage or predicate-extraction
/// bug is exactly the kind of thing a text-only fixture test cannot catch: the fixture's
/// verdict and the assertion checking it come from the same code, so they can be consistently
/// wrong together forever. This deploys the fixture's own DDL (whitelist-filtered - never any
/// DML, per CLAUDE.md) to a disposable database and asks the real engine's plan XML whether the
/// column actually converted.
/// </summary>
public static class PipelineOracleVerification
{
    /// <summary>
    /// Oracle-verifies each of <paramref name="findings"/> against <paramref name="databaseName"/>,
    /// whose DDL is assumed already deployed (typically by an <see cref="OracleTestFixture"/>
    /// subclass's <c>InitializeAsync</c>).
    /// </summary>
    public static async Task<IReadOnlyList<CorpusFindingResult>> VerifyAsync(
        SqlServerOptions options,
        string databaseName,
        IReadOnlyList<TypedPredicateFinding> findings,
        CancellationToken cancellationToken = default)
    {
        var verifier = new CorpusFindingVerifier(options);
        var results = new List<CorpusFindingResult>(findings.Count);
        foreach (var finding in findings)
        {
            results.Add(await verifier.VerifyAsync(databaseName, finding, cancellationToken));
        }

        return results;
    }

    /// <summary>
    /// One-shot variant for tests that are not built on an <see cref="OracleTestFixture"/>
    /// subclass: deploys the whitelisted DDL batches in <paramref name="fixtureSql"/> to
    /// <paramref name="databaseName"/>, then verifies.
    /// </summary>
    public static async Task<IReadOnlyList<CorpusFindingResult>> DeployAndVerifyAsync(
        SqlServerOptions options,
        string databaseName,
        string fixtureSql,
        IReadOnlyList<TypedPredicateFinding> findings,
        CancellationToken cancellationToken = default)
    {
        await new ScriptDeployer(options).DeployWhitelistedDdlAsync(fixtureSql, databaseName, cancellationToken: cancellationToken);
        return await VerifyAsync(options, databaseName, findings, cancellationToken);
    }

    /// <summary>
    /// Asserts every result is oracle-confirmed - <see cref="CorpusFindingOutcome.Confirmed"/>,
    /// or <see cref="CorpusFindingOutcome.ConfirmedUnindexed"/> when the fixture's column
    /// carries no leading-key index (CorpusFindingVerifier already downgrades ScanForced/
    /// RangeSeek's plan-shape check to "confirmed unindexed" rather than asserting a shape
    /// distinction the environment never actually tested). A single unconfirmed finding fails
    /// with the plan-XML mismatch detail inline, not a bare boolean.
    /// </summary>
    public static void AssertAllConfirmed(IEnumerable<CorpusFindingResult> results)
    {
        foreach (var result in results)
        {
            Assert.True(
                result.Outcome is CorpusFindingOutcome.Confirmed or CorpusFindingOutcome.ConfirmedUnindexed,
                $"{result.Finding.Column.TableQualifiedName}.{result.Finding.Column.ColumnName} " +
                $"(verdict {result.Finding.Verdict}) was not oracle-confirmed: {result.Outcome}. {result.Detail}");
        }
    }
}
