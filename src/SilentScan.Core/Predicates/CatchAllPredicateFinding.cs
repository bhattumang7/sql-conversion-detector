using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

/// <summary>
/// The "catch-all"/"kitchen-sink" optional-filter idiom - <c>WHERE (Col = @p OR @p IS NULL)</c>,
/// the classic pattern for one stored procedure serving many different optional search filters
/// with a single query (docs/detection-checklist.md Tier 2 "Catch-all / kitchen-sink
/// predicates"; canonical treatment: Erland Sommarskog, "Dynamic Search Conditions in T-SQL",
/// sommarskog.se). The optimizer must build ONE cached plan that stays correct for every possible
/// NULL/non-NULL combination of every parameter in the WHERE clause - it can't assume the column
/// is ever actually filtered, so it typically compiles to an index/table scan regardless of what
/// values are actually passed at a given execution.
///
/// <see cref="ParameterName"/> is always a formal <c>CREATE PROCEDURE</c>/function parameter
/// (or an <c>sp_executesql</c> parameter), never a <c>DECLARE</c>d local - a local variable's
/// value is fixed for the whole compile in a way that has no "must serve every caller-supplied
/// combination" story at all; that shape is <see cref="LocalVariablePredicateFinding"/>'s own,
/// separate concern, and the same AST match is never double-counted under both theories.
///
/// Not verdict-bearing (no <c>Verdict</c> field): the finding is a structural risk report, not a
/// claim about what a specific already-compiled plan is doing right now for this specific
/// procedure - a cached plan really could still be seeking today, depending on which value first
/// compiled it. The underlying MECHANISM (this shape typically forces a scan) is oracle-confirmed
/// once, generally, in <c>CatchAllPredicateOracleTests</c>, not per finding.
///
/// Fully suppressed (never even constructed), not merely downgraded, when the enclosing statement
/// carries <c>OPTION (RECOMPILE)</c> or the enclosing procedure is <c>WITH RECOMPILE</c> - a
/// per-execution recompile lets the optimizer see the parameter's REAL value on every call and
/// build a plan specific to that NULL/non-NULL state, the same full resolution <see
/// cref="LocalVariablePredicateFinding"/>'s own doc comment describes for its own risk.
///
/// Deliberately narrower than a naive "any OR with an IS NULL nearby" match: both operands of the
/// OR must reference literally the SAME parameter name, and the compared column must be a bare
/// column reference (not wrapped - a wrapped column is the already-shipped Tier-1 sargability
/// stream's own, separate finding, and stacking a second finding on the identical wrap would be
/// noise, not signal).
/// </summary>
public sealed record CatchAllPredicateFinding(
    string TableQualifiedName,
    string ColumnName,
    bool Indexed,
    string ParameterName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High)
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}

