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
    private readonly PlanXmlCapture _planXmlCapture;
    private readonly IndexDeploymentChecker _indexChecker;

    public ExpressionDerivedVerifier(SqlServerOptions options)
    {
        _planXmlCapture = new PlanXmlCapture(options);
        _indexChecker = new IndexDeploymentChecker(options);
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

        // Resolve the REAL index name(s) behind every indexed underlying base column, so the
        // plan-shape check below can be scoped to exactly those indexes rather than asking "is
        // there an Index Seek anywhere in the whole plan" - a plan touching an unrelated indexed
        // table elsewhere would otherwise flip an unrelated true finding to NotConfirmed.
        var indexNames = new List<string>();
        foreach (var baseColumn in finding.UnderlyingBaseColumns.Where(bc => bc.Indexed))
        {
            var indexName = await _indexChecker.TryGetLeadingKeyIndexNameAsync(
                database, baseColumn.TableQualifiedName, baseColumn.ColumnName, cancellationToken);
            if (indexName is not null)
            {
                indexNames.Add(indexName);
            }
        }

        if (indexNames.Count == 0)
        {
            return new ExpressionDerivedResult(
                finding, ExpressionDerivedOutcome.UnindexedNotProbeable,
                "No underlying base column has a leading-key index that could be re-resolved live - there is no seek to have lost, so no plan was ever captured.");
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

        var hasIndexSeek = indexNames.Any(indexName => IndexAccessDetector.HasIndexSeek(planXml, indexName));
        return hasIndexSeek
            ? new ExpressionDerivedResult(finding, ExpressionDerivedOutcome.NotConfirmed, "The plan used an Index Seek on one of this finding's own underlying-column indexes despite the expression-derived column - the finding's claim did not hold against the real engine.")
            : new ExpressionDerivedResult(finding, ExpressionDerivedOutcome.Confirmed, Detail: null);
    }
}
