using SilentScan.Core.Predicates;
using SilentScan.Verify;
using SilentScan.Verify.Deployment;
using SilentScan.Verify.Oracle;

namespace SilentScan.Tests.Support;

public static class PipelineOracleVerification
{
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

public static void AssertAllConfirmed(IEnumerable<CorpusFindingResult> results)
    {
        foreach (var result in results)
        {
            Assert.True(
                result.Outcome is CorpusFindingOutcome.Confirmed or CorpusFindingOutcome.ConfirmedUnindexed or CorpusFindingOutcome.ConfirmedViaScratchIndex,
                $"{result.Finding.Column.TableQualifiedName}.{result.Finding.Column.ColumnName} " +
                $"(verdict {result.Finding.Verdict}) was not oracle-confirmed: {result.Outcome}. {result.Detail}");
        }
    }
}
