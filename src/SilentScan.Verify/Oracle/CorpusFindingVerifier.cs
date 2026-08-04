using SilentScan.Core.Catalog;
using SilentScan.Core.Predicates;
using SilentScan.Core.Rules;

namespace SilentScan.Verify.Oracle;

/// <summary>
/// Oracle-confirms a single corpus <see cref="TypedPredicateFinding"/> against a database the
/// finding's own repo DDL has already been deployed to (CLAUDE.md Verify workflow): builds a
/// self-authored probe, compiles it under SHOWPLAN_XML, and checks for CONVERT_IMPLICIT
/// applied to the finding's own column - never on whether the tiny/empty table seeks or scans.
/// A SeekPreserved finding claims the opposite of every other verdict - that the column does
/// NOT convert - so its confirmation is the absence of that column-side CONVERT_IMPLICIT, not
/// its presence; the other operand (or a joined column) converting instead is expected, not a
/// mismatch.
/// A RangeSeek and a ScanForced finding both produce that same column-side convert
/// (docs/audit-remediation-plan.md Phase 5.1, audit finding C1), so for those two verdicts
/// confirmation additionally requires the plan's seek/scan SHAPE to match: RangeSeek needs the
/// dynamic seek machinery (GetRangeThroughConvert) the collation family is supposed to enable;
/// ScanForced needs its absence - verified directly against the real engine before relying on
/// this signal (SQL_* collation: Convert present, GetRangeThroughConvert absent, plan is an
/// Index Scan; Windows collation: Convert present, GetRangeThroughConvert present, plan is an
/// Index Seek). The index-deployment check runs AFTER the probe, only when the column did
/// convert and the verdict makes a shape claim - it used to gate the probe itself, which meant
/// a genuinely unindexed column (the common case in real-world corpora, not the exception)
/// produced no oracle signal at all rather than the CONVERT_IMPLICIT confirmation that's fully
/// provable without an index (an audit finding: this silently discarded the tool's primary
/// signal for most real corpus findings).
/// </summary>
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
        // An Unknown verdict asserts nothing (CLAUDE.md: honestly uncertain, never a guess) - the
        // column-conversion check every other branch below relies on would otherwise happily
        // report Confirmed the moment a probe happened to show a conversion, even though Unknown
        // never claimed one would or wouldn't happen. Checked before any probe is even built, so
        // this can never depend on what a specific probe's plan XML happens to look like.
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
            // SeekPreserved's whole claim is the opposite of every other verdict's: the
            // predicate does NOT force this column to convert (the value/other side may still
            // convert instead, or nothing converts at all). Confirmation is therefore the
            // absence of a column-side CONVERT_IMPLICIT, not its presence - reusing the
            // "columnConverts == Confirmed" logic below for this verdict would demand the one
            // plan shape SeekPreserved specifically predicts will never happen.
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
            // No shape claim to check for any other verdict - the column-side conversion alone
            // is what this outcome confirms.
            return new CorpusFindingResult(finding, CorpusFindingOutcome.Confirmed, Detail: null);
        }

        // The plan-shape confirmation below (absence/presence of GetRangeThroughConvert) is
        // only a meaningful signal if the finding's column actually has a deployed index - a
        // trivial heap scan produces the identical "no dynamic range seek" shape as a genuine
        // ScanForced verdict, which would otherwise silently confirm a verdict the environment
        // never actually tested that distinction for.
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

    /// <summary>
    /// Deploys a scratch index just for this probe, recaptures the plan against it, and drops
    /// it again regardless of outcome - so a probe that fails after the index deployed never
    /// leaks it into a later probe on the same column. Returns null (not a failure outcome) when
    /// the column's own type couldn't be indexed at all, so the caller falls back to the
    /// ordinary ConfirmedUnindexed message.
    /// </summary>
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

    // docs/audit-remediation-plan.md Phase 5.2: distinguishes "this operand was a literal we
    // couldn't reconstruct as SQL text" (a real probe-fidelity caveat - substituting a variable
    // here would silently misrepresent the probe as equivalent to the original comparison) from
    // the ordinary "operand's type doesn't have T-SQL syntax to render" case.
    private static string NotProbeableReason(TypedPredicateFinding finding) =>
        finding.OtherOperand is PredicateOperand.Value { IsLiteral: true, LiteralText: null }
            ? "Literal operand could not be reconstructed as SQL text; declined to substitute a parameter, which would misrepresent probe fidelity."
            : "Other operand's type could not be rendered as T-SQL syntax.";

    /// <summary>
    /// Resolves, for each of the finding's own table references, whether it's actually an
    /// inline/multi-statement table-valued function needing a synthesized dummy argument list
    /// (<see cref="CorpusFindingProbeBuilder.Build"/>'s own <c>functionArguments</c> parameter) -
    /// an ordinary table costs one cheap sys.parameters round trip that returns null and changes
    /// nothing. Both the finding's own table AND the other side's (when it's a column too) are
    /// checked independently - a column-vs-column comparison can reference a function on either
    /// side, or both.
    /// </summary>
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
