using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public enum UnparameterizedDynamicSqlFindingKind
{
    /// <summary>A proven-constant VALUE (never an identifier) was spliced into otherwise-fixed dynamic SQL text via string concatenation, on ANY call shape (EXEC(string) or sp_executesql). Fires regardless of which mechanism was used - unlike the sibling kind below, this is purely about the value ending up baked into the SQL TEXT rather than passed as a parameter/argument.</summary>
    ConcatenatedValueInConstantSql,

    /// <summary>Same concatenated-value fact, but ONLY on a genuine EXEC(string)/EXEC(@sql) call site - the sharper, actionable claim that sp_executesql's own @params mechanism was available (this call site never used it at all) and would have avoided the splice entirely.</summary>
    ExecStringConcatenatesParameterizableValue,
}

/// <summary>
/// docs/detection-checklist.md Tier 2 "Dynamic SQL quality", items 1+2 - a value this scanner
/// proved constant (CLAUDE.md's Tier A dynamic-SQL folding) was spliced into the assembled SQL
/// TEXT via string concatenation rather than authored as one whole literal or passed through
/// sp_executesql's own parameter mechanism. Detected by <see cref="DynamicSqlOperandPositionClassifier"/>
/// finding at least one <see cref="DynamicSqlSegmentMap.ConcatenationBoundaryOffsets"/> position
/// that lands inside a VALUE grammar position (never an identifier one - a concatenated table/
/// column name is a different, often unavoidable pattern, out of scope here) in the reparsed
/// script.
///
/// One record with a <see cref="Kind"/> discriminator, matching this codebase's established
/// "one record, one Kind enum, shared plumbing" shape (<c>SetOptionFinding</c>,
/// <c>ForcedSerialFinding</c>, <c>UntrustedConstraintFinding</c>) - both kinds are the same
/// underlying detection (concatenation into a value position) reported as two distinct claims:
/// <see cref="UnparameterizedDynamicSqlFindingKind.ConcatenatedValueInConstantSql"/> is the general
/// plan-cache-pollution report (oracle-confirmed below: two calls differing only in the spliced
/// literal value compile two distinct cached plans, where one parameterized sp_executesql call
/// reuses one), <see cref="UnparameterizedDynamicSqlFindingKind.ExecStringConcatenatesParameterizableValue"/>
/// is the narrower "you had sp_executesql's own parameter mechanism available and didn't use it"
/// claim, which only makes sense for a genuine EXEC(string) call. A single EXEC(string) call site
/// concatenating a value produces BOTH findings - different claims about the same fact, not a
/// duplicate. "Report, don't guess": <see cref="DynamicSqlOperandPositionClassifier"/> declines
/// (Ambiguous) whenever the spliced position's grammar role can't be determined, so neither kind
/// ever fires on an undetermined splice.
///
/// <see cref="SourcePath"/>/<see cref="Line"/>/<see cref="Column"/> point at the EXEC/sp_executesql
/// call site itself (not the mid-string splice position, which has no meaning to a reader outside
/// this scanner's own reparse) - the same call-site-anchoring <see cref="DynamicSqlFinding"/>
/// already uses. Not verdict-bearing (no seek/scan plan-shape claim); <see cref="Confidence"/> is
/// the assembling script's own <see cref="DynamicSqlScript.Confidence"/> at the moment this fires.
/// </summary>
public sealed record UnparameterizedDynamicSqlFinding(
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    UnparameterizedDynamicSqlFindingKind Kind,
    FindingConfidence Confidence = FindingConfidence.High)
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}

