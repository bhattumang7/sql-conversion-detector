namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md "Second full-archive practitioner sweep" §G: "String concatenation
/// via the <c>+</c> operator silently nulls the entire result when any operand is NULL" - unlike
/// <c>CONCAT()</c>, which treats every NULL operand as an empty string, T-SQL's <c>+</c> operator
/// propagates a single NULL operand to NULL for the WHOLE expression, with no error raised. Code
/// building a display string or a composite key from nullable columns with <c>+</c> and no
/// <c>ISNULL</c>/<c>COALESCE</c> guard silently produces NULL instead of a partial string - a real
/// semantic-preservation gotcha, not a style complaint.
///
/// Oracle-confirmed directly (Docker instance, disposable scratch database, dropped immediately
/// after): <c>SELECT 'a' + NULL + 'b'</c> genuinely evaluated to NULL, while
/// <c>SELECT CONCAT('a', NULL, 'b')</c> genuinely evaluated to <c>'ab'</c> - the identical NULL
/// operand, two different silent outcomes depending purely on which operator built the string.
/// Version-insensitive: this is ANSI NULL-propagation semantics for the arithmetic-family <c>+</c>
/// operator, unaffected by compatibility level, and independent of <c>ANSI_NULLS</c> (which governs
/// <c>= NULL</c> comparisons, not concatenation).
///
/// <b>AST+catalog-decidable, syntax-only (no oracle needed per rule instance)</b>: a <c>+</c>
/// binary-expression CHAIN (flattened through any nested <c>+</c>/parenthesis wrapping, so
/// <c>a + b + c</c> is inspected as one three-leaf chain, not three separate two-leaf findings) is a
/// candidate only when every leaf resolves, with no guessing, to one of: a string literal; a column
/// reference resolved through the immediate statement's own FROM-clause alias scope (direct base
/// table only - the same "known v1 scope limit" restraint <see cref="FloatEqualityPredicateScanner"/>
/// and <see cref="NonUniqueUpdateSourceScanner"/> already established, never a view/CTE/derived-
/// table/lineage-resolved column) to a catalog column whose <see cref="Catalog.SqlTypeCategory"/> is
/// one of the char-family types (<c>char</c>/<c>varchar</c>/<c>nchar</c>/<c>nvarchar</c>/
/// <c>text</c>/<c>ntext</c>); or an <c>ISNULL</c>/<c>COALESCE</c> call whose own arguments are
/// themselves each one of these same shapes, recursively - a guarded leaf never counts as the
/// "nullable operand" trigger below, since the whole point of the guard is that it can no longer be
/// NULL. Any other leaf shape (a variable/parameter, a plain function call, a subquery, a column
/// resolved to a non-string catalog category) makes the WHOLE chain Unknown and the finding declines
/// entirely, rather than guessing whether that leaf's runtime type is really a string (T-SQL data
/// type precedence means a non-string operand actually changes <c>+</c> from concatenation to
/// arithmetic addition, a materially different statement this pass is not making a claim about).
/// This mirrors the T-SQL precedence fact directly: char-family types sit at the BOTTOM of the data
/// type precedence list, so <c>+</c> is only ever string concatenation when every operand is
/// genuinely string-typed - if one side were numeric, the string side would itself convert to that
/// numeric type instead, a different (and separately out-of-scope) failure shape.
///
/// The finding fires only when at least one leaf is an UNGUARDED, catalog-nullable column
/// reference - a chain built entirely from literals and non-nullable columns can never silently
/// null out, and a chain where every nullable operand is already wrapped in <c>ISNULL</c>/
/// <c>COALESCE</c> (either per-operand, e.g. <c>a + ISNULL(b, '')</c>, or wrapping the ENTIRE chain,
/// e.g. <c>ISNULL(a + b, '')</c>) is already safe and never fires.
///
/// <see cref="FindingConfidence.High"/>: the NULL-propagation mechanism itself is unconditional,
/// oracle-confirmed engine behavior with zero workload dependence, matching this project's own
/// precedent for a structurally-unambiguous engine-semantics claim (<see
/// cref="DefaultNullableConstraintFinding"/>'s own High tier for an analogous "the schema/code
/// silently does something other than what it reads like it does" mismatch) - whether any real
/// caller ever actually supplies a NULL for the flagged column is workload-dependent, exactly like
/// <see cref="DefaultNullableConstraintFinding"/>'s own risk framing, so this is a real but
/// workload-dependent RISK (SARIF Warning), not a proven-wrong-result-today claim (which would be
/// SARIF Error).
/// </summary>
public sealed record StringConcatNullFinding(
    string TableQualifiedName,
    string ColumnName,
    string SourcePath,
    int Line,
    int Column,
    FindingConfidence Confidence = FindingConfidence.High);
