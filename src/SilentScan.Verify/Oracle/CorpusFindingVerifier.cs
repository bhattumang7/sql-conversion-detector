using SilentScan.Core.Predicates;

namespace SilentScan.Verify.Oracle;

/// <summary>
/// Oracle-confirms a single corpus <see cref="TypedPredicateFinding"/> against a database the
/// finding's own repo DDL has already been deployed to (CLAUDE.md Verify workflow): builds a
/// self-authored probe, compiles it under SHOWPLAN_XML, and checks for CONVERT_IMPLICIT
/// applied to the finding's own column - never on whether the tiny/empty table seeks or scans.
/// </summary>
public sealed class CorpusFindingVerifier
{
    private readonly PlanXmlCapture _planXmlCapture;

    public CorpusFindingVerifier(SqlServerOptions options)
    {
        _planXmlCapture = new PlanXmlCapture(options);
    }

    public async Task<CorpusFindingResult> VerifyAsync(
        string database, TypedPredicateFinding finding, CancellationToken cancellationToken = default)
    {
        var probe = CorpusFindingProbeBuilder.Build(finding);
        if (probe is null)
        {
            return new CorpusFindingResult(finding, CorpusFindingOutcome.NotProbeable, "Other operand's type could not be rendered as T-SQL syntax.");
        }

        string planXml;
        try
        {
            planXml = await _planXmlCapture.CaptureAsync(database, probe, cancellationToken);
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
        {
            return new CorpusFindingResult(finding, CorpusFindingOutcome.ProbeFailed, ex.Message);
        }

        var conversions = ConvertImplicitDetector.FindColumnConversions(planXml);
        var (schema, table) = SplitQualifiedName(finding.Column.TableQualifiedName);
        var confirmed = conversions.Any(c =>
            string.Equals(c.Table, table, StringComparison.OrdinalIgnoreCase)
            && (schema is null || string.Equals(c.Schema, schema, StringComparison.OrdinalIgnoreCase))
            && string.Equals(c.Column, finding.Column.ColumnName, StringComparison.OrdinalIgnoreCase));

        return new CorpusFindingResult(
            finding,
            confirmed ? CorpusFindingOutcome.Confirmed : CorpusFindingOutcome.NotConfirmed,
            Detail: null);
    }

    private static (string? Schema, string Table) SplitQualifiedName(string qualifiedName)
    {
        var parts = qualifiedName.Split('.', 2);
        return parts.Length == 2 ? (parts[0], parts[1]) : (null, parts[0]);
    }
}
