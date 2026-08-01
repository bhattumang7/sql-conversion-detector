using SilentScan.Core.Predicates;
using SilentScan.Core.Rules;

namespace SilentScan.Verify.Oracle;

/// <summary>
/// Oracle-confirms a single corpus <see cref="TypedPredicateFinding"/> against a database the
/// finding's own repo DDL has already been deployed to (CLAUDE.md Verify workflow): builds a
/// self-authored probe, compiles it under SHOWPLAN_XML, and checks for CONVERT_IMPLICIT
/// applied to the finding's own column - never on whether the tiny/empty table seeks or scans.
/// A RangeSeek and a ScanForced finding both produce that same column-side convert
/// (docs/audit-remediation-plan.md Phase 5.1, audit finding C1), so for those two verdicts
/// confirmation additionally requires the plan's seek/scan SHAPE to match: RangeSeek needs the
/// dynamic seek machinery (GetRangeThroughConvert) the collation family is supposed to enable;
/// ScanForced needs its absence - verified directly against the real engine before relying on
/// this signal (SQL_* collation: Convert present, GetRangeThroughConvert absent, plan is an
/// Index Scan; Windows collation: Convert present, GetRangeThroughConvert present, plan is an
/// Index Seek).
/// </summary>
public sealed class CorpusFindingVerifier
{
    private readonly PlanXmlCapture _planXmlCapture;
    private readonly IndexDeploymentChecker _indexChecker;

    public CorpusFindingVerifier(SqlServerOptions options)
    {
        _planXmlCapture = new PlanXmlCapture(options);
        _indexChecker = new IndexDeploymentChecker(options);
    }

    public async Task<CorpusFindingResult> VerifyAsync(
        string database, TypedPredicateFinding finding, CancellationToken cancellationToken = default)
    {
        var probe = CorpusFindingProbeBuilder.Build(finding);
        if (probe is null)
        {
            return new CorpusFindingResult(finding, CorpusFindingOutcome.NotProbeable, NotProbeableReason(finding));
        }

        // The plan-shape confirmation below (absence/presence of GetRangeThroughConvert) is
        // only a meaningful signal if the finding's column actually has a deployed index - a
        // trivial heap scan produces the identical "no dynamic range seek" shape as a genuine
        // ScanForced verdict, which would otherwise silently confirm a verdict the environment
        // never tested (best-effort DDL deployment can drop a CREATE INDEX batch).
        if (finding.Verdict is Verdict.ScanForced or Verdict.RangeSeek)
        {
            var hasIndex = await _indexChecker.HasLeadingKeyIndexAsync(
                database, finding.Column.TableQualifiedName, finding.Column.ColumnName, cancellationToken);
            if (!hasIndex)
            {
                return new CorpusFindingResult(
                    finding, CorpusFindingOutcome.IndexNotDeployed,
                    $"No deployed index has '{finding.Column.ColumnName}' as its leading key on {finding.Column.TableQualifiedName}.");
            }
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
        var columnConverts = conversions.Any(c =>
            string.Equals(c.Table, table, StringComparison.OrdinalIgnoreCase)
            && (schema is null || string.Equals(c.Schema, schema, StringComparison.OrdinalIgnoreCase))
            && string.Equals(c.Column, finding.Column.ColumnName, StringComparison.OrdinalIgnoreCase));

        var confirmed = MatchesPredictedPlanShape(finding.Verdict, columnConverts, planXml);

        return new CorpusFindingResult(
            finding,
            confirmed ? CorpusFindingOutcome.Confirmed : CorpusFindingOutcome.NotConfirmed,
            Detail: confirmed ? null : DescribeMismatch(finding.Verdict, columnConverts, planXml, conversions));
    }

    private static string DescribeMismatch(
        Verdict verdict, bool columnConverts, string planXml, IReadOnlyList<ConvertImplicitFinding> observedConversions)
    {
        if (!columnConverts)
        {
            var observed = observedConversions.Count == 0
                ? "no column-side CONVERT_IMPLICIT at all"
                : $"CONVERT_IMPLICIT on {string.Join(", ", observedConversions.Select(c => $"{c.Table}.{c.Column}"))} instead";
            return $"Expected a column-side conversion for verdict {verdict}, observed {observed}.";
        }

        var hasDynamicRangeSeek = planXml.Contains("GetRangeThroughConvert", StringComparison.Ordinal);
        return $"Column converted as predicted, but GetRangeThroughConvert was {(hasDynamicRangeSeek ? "present" : "absent")}, which does not match verdict {verdict}.";
    }

    // docs/audit-remediation-plan.md Phase 5.2: distinguishes "this operand was a literal we
    // couldn't reconstruct as SQL text" (a real probe-fidelity caveat - substituting a variable
    // here would silently misrepresent the probe as equivalent to the original comparison) from
    // the ordinary "operand's type doesn't have T-SQL syntax to render" case.
    private static string NotProbeableReason(TypedPredicateFinding finding) =>
        finding.OtherOperand is PredicateOperand.Value { IsLiteral: true, LiteralText: null }
            ? "Literal operand could not be reconstructed as SQL text; declined to substitute a parameter, which would misrepresent probe fidelity."
            : "Other operand's type could not be rendered as T-SQL syntax.";

    private static bool MatchesPredictedPlanShape(Verdict verdict, bool columnConverts, string planXml)
    {
        if (!columnConverts)
        {
            return false;
        }

        var hasDynamicRangeSeek = planXml.Contains("GetRangeThroughConvert", StringComparison.Ordinal);
        return verdict switch
        {
            Verdict.RangeSeek => hasDynamicRangeSeek,
            Verdict.ScanForced => !hasDynamicRangeSeek,
            _ => true,
        };
    }

    private static (string? Schema, string Table) SplitQualifiedName(string qualifiedName)
    {
        var parts = qualifiedName.Split('.', 2);
        return parts.Length == 2 ? (parts[0], parts[1]) : (null, parts[0]);
    }
}
