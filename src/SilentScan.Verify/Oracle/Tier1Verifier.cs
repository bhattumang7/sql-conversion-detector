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
        // path exactly like CorpusFindingVerifier only when it's false/null. Either way the plan-
        // shape check itself needs the REAL index name, not just "an index exists" - checking for
        // an Index Seek anywhere in the whole plan document would confirm off a completely
        // unrelated index (a different table, a lookup, a spool feed).
        if (finding.Indexed == true)
        {
            // Resolved concurrently with (not gating) the capture below - a table the catalog
            // believes exists but was dropped/renamed live must still surface as ProbeFailed from
            // the capture attempt itself, not a premature NotProbeable from this lookup alone
            // finding nothing (OBJECT_ID() on a missing table just returns no rows, no error).
            var indexName = await _indexChecker.TryGetLeadingKeyIndexNameAsync(
                database, finding.TableQualifiedName!, finding.ColumnName, cancellationToken);
            return await CaptureAndClassifyAsync(database, finding, probe, indexName, cancellationToken);
        }

        var scratchIndexName = await _indexChecker.TryDeployScratchIndexAsync(
            database, finding.TableQualifiedName!, finding.ColumnName, cancellationToken);
        if (scratchIndexName is null)
        {
            return new Tier1Result(
                finding, Tier1Outcome.UnindexedNotProbeable,
                $"'{finding.ColumnName}' has no deployed index on {finding.TableQualifiedName} - there is no seek to have lost, so the plan-shape signal cannot be checked.");
        }

        try
        {
            return await CaptureAndClassifyAsync(database, finding, probe, scratchIndexName, cancellationToken, viaScratchIndex: true);
        }
        finally
        {
            await _indexChecker.DropIndexIfExistsAsync(database, finding.TableQualifiedName!, scratchIndexName, cancellationToken);
        }
    }

    private async Task<Tier1Result> CaptureAndClassifyAsync(
        string database, SargabilityFinding finding, string probe, string? indexName, CancellationToken cancellationToken, bool viaScratchIndex = false)
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

        if (indexName is null)
        {
            // A real plan was captured, but the finding's own leading-key index could not be
            // re-resolved live (the catalog and the live server disagree on a column the finding
            // itself was told is indexed) - there is no specific index name left to scope the
            // plan-shape check to, so this declines rather than falling back to an unscoped
            // whole-plan check.
            return new Tier1Result(
                finding, Tier1Outcome.NotProbeable,
                $"'{finding.ColumnName}' was reported indexed but no leading-key index for it could be re-resolved live on {finding.TableQualifiedName} - the catalog and the live server disagree.");
        }

        var hasIndexSeek = IndexAccessDetector.HasIndexSeek(planXml, indexName);
        if (hasIndexSeek)
        {
            return new Tier1Result(finding, Tier1Outcome.NotConfirmed, "The plan used an Index Seek on this finding's own index despite the syntactic wrap - the finding's claim did not hold against the real engine.");
        }

        return new Tier1Result(
            finding,
            viaScratchIndex ? Tier1Outcome.ConfirmedViaScratchIndex : Tier1Outcome.Confirmed,
            viaScratchIndex ? "Confirmed against a scratch index deployed for this probe only - the corpus's own DDL does not index this column." : null);
    }
}
