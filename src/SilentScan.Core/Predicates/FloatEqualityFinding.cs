using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md "DBA-script family sweep (2026-08-17)" §A "float/real as an index
/// key column or an equality-predicate target" - the AST-level, sharper half of that item (the
/// catalog-only half, "used as an index key column at all", ships as <see
/// cref="IndexDesignFindingKind.FloatOrRealIndexKeyColumn"/> on the existing <see
/// cref="IndexDesignFinding"/> type). An actual equality comparison (<c>=</c>) in a WHERE/ON
/// clause between a bare column reference resolved through the catalog to a <c>float</c>/<c>real</c>
/// column and any other operand.
///
/// <b>A correctness finding, not a performance one</b> - the reason it is not just another
/// <see cref="TypedPredicateExtractor"/>/<see cref="Rules.VerdictClassifier"/> verdict. Every other
/// stream in that machinery answers "can the engine seek this predicate" - <c>float</c>/<c>real</c>
/// are IEEE-754 binary floating-point, which cannot represent every decimal value exactly, so two
/// values a person would call "the same number" (most commonly, the identical business quantity
/// computed by two different logically-equivalent expressions, or written by two different client
/// drivers with different literal formatting) can carry a different bit pattern and therefore
/// compare UNEQUAL under <c>=</c> even when both sides are otherwise perfectly well-typed and even
/// perfectly indexed - the predicate can silently return the wrong ROWS, not merely a slower plan.
/// <c>Verdict</c>'s <c>SeekPreserved</c>/<c>RangeSeek</c>/<c>ScanForced</c> vocabulary has no member
/// for "this comparison can return a wrong answer regardless of plan shape", and CLAUDE.md's own
/// type-conversion-rule template is explicitly about seek loss, not exactness - folding this into
/// that machinery would either misuse an existing verdict to mean something it doesn't, or force a
/// new verdict member whose meaning is orthogonal to every other one it sits beside. A small,
/// standalone type keeps the two concerns (can it seek vs. can it be wrong) visibly separate in the
/// finding schema itself.
///
/// <b>Deliberately narrow, precision-first v1 scope</b> (matching this codebase's established
/// restraint for a standalone scanner, e.g. <see cref="NonUniqueUpdateSourceScanner"/>'s own "known
/// v1 scope limit" framing): only resolves a column reference through a DIRECT base-table alias in
/// the immediate statement's own FROM clause (the same <c>ResolveDirectBaseTable</c> shape <see
/// cref="NonUniqueUpdateSourceScanner"/> already established) or, when unambiguous, a single
/// unqualified table in scope - never through a view, CTE, derived table, or lineage-resolved
/// column provenance. A predicate against a float/real column reached through a view/CTE layer is
/// left unanalyzed rather than guessed at; this is a real, known gap, not a silent one, and could be
/// widened to lineage-resolved columns in a future pass without changing this type's own shape.
/// Only a top-level <c>=</c> comparison is examined - <c>&lt;&gt;</c>/range operators against a
/// float/real column carry a related but distinct risk (an off-by-representation-error boundary,
/// not a silent equality miss) this v1 does not claim to cover.
///
/// No plan-XML oracle applies - this is a claim about IEEE-754 value representation, not plan
/// shape; well-established, uncontroversial floating-point behavior (every mainstream RDBMS/
/// language shares it), confirmed directly against the standing Docker instance anyway for
/// extra, on-brand confidence rather than because the claim needed it to ship (matching <see
/// cref="IndexDesignFindingKind.RandomClusteredKeyGuidDefault"/>'s own precedent for a
/// well-documented, non-plan-shape claim). Engine-version insensitive: IEEE-754 float/real
/// representation is unchanged across every SQL Server version.
/// </summary>
public sealed record FloatEqualityFinding(
    string TableQualifiedName,
    string ColumnName,
    string TypeDisplay,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High)
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}

