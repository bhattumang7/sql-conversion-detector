using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

/// <summary>
/// A predicate compares a column against a parameter/variable/expression declared with a
/// meaningfully LONGER length than the column itself (docs/detection-checklist.md Tier 1
/// "Oversized and MAX-typed parameters" #2, e.g. a <c>varchar(200)</c> parameter compared against
/// a <c>varchar(50)</c> column). Deliberately NOT verdict-bearing and carries no plan-XML oracle
/// claim - falsified directly against the Docker oracle that a bare compile-only equality-
/// predicate probe shows IDENTICAL memory grants regardless of the parameter's declared length;
/// the real, oracle-confirmed effect (SQL Server estimates a varchar(n) operand's average row
/// size as n/2 for grant purposes) only shows up once the oversized value feeds an operator that
/// must buffer/sort/hash it (e.g. ORDER BY), which is a plan-SHAPE-dependent fact this pass has
/// no way to know holds for a given predicate site - so this stays a catalog+AST structural
/// report (lower severity, per the checklist's own framing), not a claim about a specific plan's
/// memory grant. A live-mode enhancement reading real memory grants out of the plan cache
/// (mirroring the SilentScan.Live plan-cache reader's own workload-observed-conversion pattern)
/// is left as explicit future follow-up rather than guessed here.
/// <see cref="FindingConfidence.Low"/>, matching the "lower severity" framing above: the oracle
/// actively disproved the naive claim in the common case (identical memory grants), and the real
/// effect only shows up under a plan shape this pass cannot confirm holds for any given site.
/// </summary>
public sealed record OversizedParameterFinding(
    string TableQualifiedName,
    string ColumnName,
    int ColumnLength,
    int OtherOperandLength,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.Low)
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}

