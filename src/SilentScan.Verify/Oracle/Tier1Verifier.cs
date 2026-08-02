using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Core.Predicates;

namespace SilentScan.Verify.Oracle;

/// <summary>
/// Roadmap Phase E3: oracle-confirms a <see cref="SargabilityFinding"/> - previously Tier-1 had
/// zero presence in the corpus verify pipeline at all, only the classifier's own fixture tests.
/// The signal is plan SHAPE, not CONVERT_IMPLICIT (a syntactic wrap like <c>UPPER(Code)</c> is
/// not an implicit conversion at all): oracle-verified directly (Docker) that a wrapped
/// predicate against an indexed column produces an Index Scan (or Compute Scalar + Index Scan),
/// never an Index Seek, while the same column compared bare produces an Index Seek - so absence
/// of "Index Seek" anywhere in the plan is the confirming signal. Reuses
/// <see cref="IndexDeploymentChecker"/>'s scratch-index mechanism exactly like
/// <see cref="CorpusFindingVerifier"/> does, since the same "most corpus columns are unindexed"
/// problem applies here too.
/// </summary>
public sealed class Tier1Verifier
{
    private const string IndexSeekMarker = "PhysicalOp=\"Index Seek\"";

    private readonly PlanXmlCapture _planXmlCapture;
    private readonly IndexDeploymentChecker _indexChecker;

    public Tier1Verifier(SqlServerOptions options)
    {
        _planXmlCapture = new PlanXmlCapture(options);
        _indexChecker = new IndexDeploymentChecker(options);
    }

    public async Task<Tier1Result> VerifyAsync(
        string database, SargabilityFinding finding, DatabaseCatalog catalog, CancellationToken cancellationToken = default)
    {
        var probe = Tier1ProbeBuilder.Build(finding, catalog);
        if (probe is null)
        {
            return new Tier1Result(finding, Tier1Outcome.NotProbeable, "No rendered predicate fragment, resolved table, or resolvable column type.");
        }

        // finding.Indexed already resolved this through the same catalog/lineage machinery the
        // typed-finding path uses - reused here rather than a second HasLeadingKeyIndexAsync
        // round trip for the common (already indexed) case, falling back to the scratch-index
        // path exactly like CorpusFindingVerifier only when it's false/null.
        if (finding.Indexed == true)
        {
            return await CaptureAndClassifyAsync(database, finding, probe, cancellationToken);
        }

        var indexName = await _indexChecker.TryDeployScratchIndexAsync(
            database, finding.TableQualifiedName!, finding.ColumnName, cancellationToken);
        if (indexName is null)
        {
            return new Tier1Result(
                finding, Tier1Outcome.ConfirmedUnindexed,
                $"'{finding.ColumnName}' has no deployed index on {finding.TableQualifiedName} - there is no seek to have lost, so the plan-shape signal cannot be checked.");
        }

        try
        {
            return await CaptureAndClassifyAsync(database, finding, probe, cancellationToken, viaScratchIndex: true);
        }
        finally
        {
            await _indexChecker.DropIndexIfExistsAsync(database, finding.TableQualifiedName!, indexName, cancellationToken);
        }
    }

    private async Task<Tier1Result> CaptureAndClassifyAsync(
        string database, SargabilityFinding finding, string probe, CancellationToken cancellationToken, bool viaScratchIndex = false)
    {
        string planXml;
        try
        {
            planXml = await _planXmlCapture.CaptureAsync(database, probe, cancellationToken);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            return new Tier1Result(finding, Tier1Outcome.ProbeFailed, ex.Message);
        }

        var hasIndexSeek = planXml.Contains(IndexSeekMarker, StringComparison.Ordinal);
        if (hasIndexSeek)
        {
            return new Tier1Result(finding, Tier1Outcome.NotConfirmed, "The plan used an Index Seek despite the syntactic wrap - the finding's claim did not hold against the real engine.");
        }

        return new Tier1Result(
            finding,
            viaScratchIndex ? Tier1Outcome.ConfirmedViaScratchIndex : Tier1Outcome.Confirmed,
            viaScratchIndex ? "Confirmed against a scratch index deployed for this probe only - the corpus's own DDL does not index this column." : null);
    }
}
