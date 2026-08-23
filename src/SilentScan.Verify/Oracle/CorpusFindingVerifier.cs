using SilentScan.Core.Catalog;
using SilentScan.Core.Predicates;
using SilentScan.Core.Rules;
using SilentScan.Core.TypeInference;

namespace SilentScan.Verify.Oracle;

public sealed class CorpusFindingVerifier
{
    private readonly PlanXmlCapture _planXmlCapture;
    private readonly IndexDeploymentChecker _indexChecker;
    private readonly FunctionParameterReader _functionParameterReader;

    public CorpusFindingVerifier(SqlServerOptions options)
    {
        _planXmlCapture = new PlanXmlCapture(options);
        _indexChecker = new IndexDeploymentChecker(options);
        _functionParameterReader = new FunctionParameterReader(options);
    }

    public async Task<CorpusFindingResult> VerifyAsync(
        string database, TypedPredicateFinding finding, CancellationToken cancellationToken = default)
    {
        if (finding.Verdict == Verdict.Unknown)
        {
            return new CorpusFindingResult(finding, CorpusFindingOutcome.NotApplicable, "Verdict is Unknown - makes no claim for the oracle to confirm or refute.");
        }

        var functionArguments = await ResolveFunctionArgumentsAsync(database, finding, cancellationToken);
        var probe = CorpusFindingProbeBuilder.Build(finding, functionArguments);
        if (probe is null)
        {
            return new CorpusFindingResult(finding, CorpusFindingOutcome.NotProbeable, NotProbeableReason(finding));
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

        if (finding.Verdict == Verdict.SeekPreserved)
        {
            return columnConverts
                ? new CorpusFindingResult(finding, CorpusFindingOutcome.NotConfirmed, DescribeMismatch(finding.Verdict, columnConverts: true, planXml, conversions))
                : new CorpusFindingResult(finding, CorpusFindingOutcome.Confirmed, Detail: null);
        }

        if (!columnConverts)
        {
            return new CorpusFindingResult(finding, CorpusFindingOutcome.NotConfirmed, DescribeMismatch(finding.Verdict, columnConverts: false, planXml, conversions));
        }

        if (finding.Verdict is not (Verdict.ScanForced or Verdict.RangeSeek))
        {
            return new CorpusFindingResult(finding, CorpusFindingOutcome.Confirmed, Detail: null);
        }

        var hasIndex = await _indexChecker.HasLeadingKeyIndexAsync(
            database, finding.Column.TableQualifiedName, finding.Column.ColumnName, cancellationToken);
        if (!hasIndex)
        {
            var scratchResult = await TryConfirmViaScratchIndexAsync(database, finding, probe, cancellationToken);
            return scratchResult ?? new CorpusFindingResult(
                finding, CorpusFindingOutcome.ConfirmedUnindexed,
                $"CONVERT_IMPLICIT confirmed on '{finding.Column.ColumnName}', but no deployed index has it as its leading key on {finding.Column.TableQualifiedName} - the {finding.Verdict} shape distinction could not be verified.");
        }

        var confirmed = MatchesPredictedPlanShape(finding.Verdict, columnConverts, planXml);

        return new CorpusFindingResult(
            finding,
            confirmed ? CorpusFindingOutcome.Confirmed : CorpusFindingOutcome.NotConfirmed,
            Detail: confirmed ? null : DescribeMismatch(finding.Verdict, columnConverts, planXml, conversions));
    }

private async Task<CorpusFindingResult?> TryConfirmViaScratchIndexAsync(
        string database, TypedPredicateFinding finding, string probe, CancellationToken cancellationToken)
    {
        var indexName = await _indexChecker.TryDeployScratchIndexAsync(
            database, finding.Column.TableQualifiedName, finding.Column.ColumnName, cancellationToken);
        if (indexName is null)
        {
            return null;
        }

        try
        {
            var planXmlWithIndex = await _planXmlCapture.CaptureAsync(database, probe, cancellationToken);
            var conversions = ConvertImplicitDetector.FindColumnConversions(planXmlWithIndex);
            var confirmed = MatchesPredictedPlanShape(finding.Verdict, columnConverts: true, planXmlWithIndex);

            return new CorpusFindingResult(
                finding,
                confirmed ? CorpusFindingOutcome.ConfirmedViaScratchIndex : CorpusFindingOutcome.NotConfirmed,
                confirmed
                    ? "Confirmed against a scratch index deployed for this probe only - the corpus's own DDL does not index this column."
                    : DescribeMismatch(finding.Verdict, columnConverts: true, planXmlWithIndex, conversions));
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
        {
            return new CorpusFindingResult(finding, CorpusFindingOutcome.ProbeFailed, ex.Message);
        }
        finally
        {
            await _indexChecker.DropIndexIfExistsAsync(database, finding.Column.TableQualifiedName, indexName, cancellationToken);
        }
    }

    private static string DescribeMismatch(
        Verdict verdict, bool columnConverts, string planXml, IReadOnlyList<ConvertImplicitFinding> observedConversions)
    {
        if (verdict == Verdict.SeekPreserved && columnConverts)
        {
            return "Expected no column-side conversion for verdict SeekPreserved, but CONVERT_IMPLICIT was observed on the column.";
        }

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

    private static string NotProbeableReason(TypedPredicateFinding finding) =>
        finding.OtherOperand is PredicateOperand.Value { IsLiteral: true, LiteralText: null }
            ? "Literal operand could not be reconstructed as SQL text; declined to substitute a parameter, which would misrepresent probe fidelity."
            : "Other operand's type could not be rendered as T-SQL syntax.";

private async Task<IReadOnlyDictionary<string, IReadOnlyList<SqlType>>?> ResolveFunctionArgumentsAsync(
        string database, TypedPredicateFinding finding, CancellationToken cancellationToken)
    {
        Dictionary<string, IReadOnlyList<SqlType>>? result = null;

        await TryAddAsync(finding.Column.ImmediateRelationQualifiedName ?? finding.Column.TableQualifiedName);
        if (finding.OtherOperand is PredicateOperand.Column otherColumn)
        {
            await TryAddAsync(otherColumn.ImmediateRelationQualifiedName ?? otherColumn.TableQualifiedName);
        }

        return result;

        async Task TryAddAsync(string qualifiedName)
        {
            var parameterTypes = await _functionParameterReader.TryGetParameterTypesAsync(database, qualifiedName, cancellationToken);
            if (parameterTypes is null)
            {
                return;
            }

            result ??= new Dictionary<string, IReadOnlyList<SqlType>>(StringComparer.OrdinalIgnoreCase);
            result[qualifiedName] = parameterTypes;
        }
    }

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
