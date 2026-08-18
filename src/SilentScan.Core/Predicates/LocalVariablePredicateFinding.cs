using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

/// <summary>
/// A predicate compares a column against a <c>DECLARE</c>d local variable's value - never a
/// formal <c>CREATE PROCEDURE</c>/function parameter, nor an <c>sp_executesql</c> parameter
/// (docs/detection-checklist.md Tier 2 "Local-variable predicates"). Distinct from every
/// sargability/verdict finding this codebase ships: the predicate is fully sargable and WILL seek
/// if the column is indexed - this stream must never imply otherwise. The claim is narrower and
/// purely structural: a local variable's value is invisible to the cardinality estimator the way
/// a parameter's sniffed value is not (Microsoft's own documented behavior - the optimizer falls
/// back to the column's average-density statistic instead of a value-specific histogram lookup),
/// which can produce a badly-wrong row-count estimate even though the ACCESS PATH is unaffected.
///
/// Deliberately carries no estimate magnitude and no verdict: this pass never traces the
/// variable's actual assigned value (CLAUDE.md "soundness first"), so it cannot claim the
/// estimate WAS wrong for a specific query, only that the shape is invisible to the estimator -
/// the same honesty <see cref="UnderLengthParameterFinding"/>'s own doc comment applies to
/// truncation risk. <see cref="FindingConfidence.Low"/> by default: whether a bad estimate
/// actually matters depends entirely on data distribution facts this pass cannot see.
///
/// Fully suppressed (never even constructed), not merely downgraded, when the enclosing statement
/// carries <c>OPTION (RECOMPILE)</c> or the enclosing procedure is <c>WITH RECOMPILE</c>: unlike
/// the SET-option streams' `ARITHABORT`-style near-misses, RECOMPILE genuinely and fully resolves
/// this specific risk - a per-execution recompile lets the optimizer see the variable's REAL
/// current value, the same visibility a sniffed parameter already has, so the "invisible to the
/// estimator" premise this finding exists to report no longer holds at all.
/// </summary>
public sealed record LocalVariablePredicateFinding(
    string TableQualifiedName,
    string ColumnName,
    bool Indexed,
    int Depth,
    string VariableName,
    string Operator,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.Low)
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}

