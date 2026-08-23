using Microsoft.Data.SqlClient;
using SilentScan.Core.Predicates;

namespace SilentScan.Verify.Oracle;

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
