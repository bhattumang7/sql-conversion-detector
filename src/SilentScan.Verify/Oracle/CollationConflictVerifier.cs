using Microsoft.Data.SqlClient;
using SilentScan.Core.Predicates;

namespace SilentScan.Verify.Oracle;

/// <summary>
/// Roadmap Phase E3: oracle-confirms a <see cref="CollationConflictFinding"/> the same way
/// every other finding stream is confirmed - a self-authored probe compiled against the corpus
/// repo's own deployed DDL - but the signal here is different in kind: the finding's whole claim
/// is that the probe does NOT compile at all (SQL Server error 468), not a plan-shape fact about
/// one that does. Compile-only (SET SHOWPLAN_XML ON): the collation-conflict compile error fires
/// during compilation itself, before any plan would be produced, so nothing ever executes.
/// </summary>
public sealed class CollationConflictVerifier
{
    private const int CollationConflictErrorNumber = 468;

    private readonly PlanXmlCapture _planXmlCapture;

    public CollationConflictVerifier(SqlServerOptions options)
    {
        _planXmlCapture = new PlanXmlCapture(options);
    }

    public async Task<CollationConflictResult> VerifyAsync(
        string database, CollationConflictFinding finding, CancellationToken cancellationToken = default)
    {
        var probe = CollationConflictProbeBuilder.Build(finding);

        try
        {
            await _planXmlCapture.CaptureAsync(database, probe, cancellationToken);
            return new CollationConflictResult(
                finding, CollationConflictOutcome.NotConfirmed, "Probe compiled cleanly - no collation conflict was raised.");
        }
        catch (SqlException ex) when (ex.Number == CollationConflictErrorNumber)
        {
            return new CollationConflictResult(finding, CollationConflictOutcome.Confirmed, Detail: null);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            return new CollationConflictResult(finding, CollationConflictOutcome.ProbeFailed, ex.Message);
        }
    }
}
