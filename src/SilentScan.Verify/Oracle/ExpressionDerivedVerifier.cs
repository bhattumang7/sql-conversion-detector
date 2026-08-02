using Microsoft.Data.SqlClient;
using SilentScan.Core.Predicates;

namespace SilentScan.Verify.Oracle;

/// <summary>
/// Roadmap Phase E3: oracle-confirms an <see cref="ExpressionDerivedFinding"/> - previously
/// zero presence in the corpus verify pipeline at all. Same plan-shape signal
/// <see cref="Tier1Verifier"/> uses (absence of "Index Seek" anywhere in the plan), for the same
/// reason: an expression-derived column is a computed value by the time the predicate sees it,
/// never an implicit conversion, so CONVERT_IMPLICIT is not the right signal here either.
/// </summary>
public sealed class ExpressionDerivedVerifier
{
    private const string IndexSeekMarker = "PhysicalOp=\"Index Seek\"";

    private readonly PlanXmlCapture _planXmlCapture;

    public ExpressionDerivedVerifier(SqlServerOptions options)
    {
        _planXmlCapture = new PlanXmlCapture(options);
    }

    public async Task<ExpressionDerivedResult> VerifyAsync(
        string database, ExpressionDerivedFinding finding, CancellationToken cancellationToken = default)
    {
        var probe = ExpressionDerivedProbeBuilder.Build(finding);
        if (probe is null)
        {
            return new ExpressionDerivedResult(
                finding, ExpressionDerivedOutcome.NotProbeable,
                "No rendered predicate fragment, or the column came from an inline derived table/CTE rather than a real catalog view/TVF.");
        }

        if (!finding.UnderlyingBaseColumns.Any(bc => bc.Indexed))
        {
            return new ExpressionDerivedResult(
                finding, ExpressionDerivedOutcome.ConfirmedUnindexed,
                "No underlying base column is indexed - there is no seek to have lost, so the plan-shape signal cannot be checked.");
        }

        string planXml;
        try
        {
            planXml = await _planXmlCapture.CaptureAsync(database, probe, cancellationToken);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            return new ExpressionDerivedResult(finding, ExpressionDerivedOutcome.ProbeFailed, ex.Message);
        }

        var hasIndexSeek = planXml.Contains(IndexSeekMarker, StringComparison.Ordinal);
        return hasIndexSeek
            ? new ExpressionDerivedResult(finding, ExpressionDerivedOutcome.NotConfirmed, "The plan used an Index Seek despite the expression-derived column - the finding's claim did not hold against the real engine.")
            : new ExpressionDerivedResult(finding, ExpressionDerivedOutcome.Confirmed, Detail: null);
    }
}
