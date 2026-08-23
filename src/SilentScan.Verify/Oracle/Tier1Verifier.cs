using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Core.Predicates;

namespace SilentScan.Verify.Oracle;

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

        if (finding.Indexed == true)
        {

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
