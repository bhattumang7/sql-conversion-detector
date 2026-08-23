using Microsoft.Data.SqlClient;
using SilentScan.Core.Predicates;

namespace SilentScan.Verify.Oracle;

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
