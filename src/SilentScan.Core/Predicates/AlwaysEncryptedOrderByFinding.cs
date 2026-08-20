using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

/// <summary>
/// An <c>ORDER BY</c> clause references a column carrying Always Encrypted's own
/// <c>ENCRYPTED WITH (...)</c> catalog property (<c>sys.columns.encryption_type</c> is not
/// <c>NULL</c>) - a statement that never compiles, oracle-confirmed against the standing Docker
/// instance: SQL Server rejects sorting on ciphertext outright (Msg 33277, "Encryption scheme
/// mismatch") for BOTH <c>DETERMINISTIC</c> and <c>RANDOMIZED</c> columns, regardless of whether
/// the connecting client is itself Always-Encrypted-enabled - ordering by ciphertext bytes has no
/// relationship to the plaintext order, so the engine refuses the statement rather than produce a
/// meaningless sort. This is a hard compile failure, the same class of finding as
/// <see cref="CollationConflictFinding"/>/<c>Verdict.OperandClash</c> - a definitive fact about
/// whether the statement can run at all, not a plan-shape or performance claim.
///
/// Deliberately narrow v1 scope, matching this codebase's established restraint for a standalone
/// catalog-fact scanner (<see cref="FloatEqualityFinding"/>'s own precedent): only a column
/// referenced directly by a top-level <c>SELECT</c>/<c>UPDATE</c>... <c>ORDER BY</c> clause,
/// resolved through <see cref="Lineage.FromScopeResolver"/>'s real per-statement scope chain. A
/// window function's own <c>OVER (... ORDER BY ...)</c> and an encrypted column reached only
/// through a view/CTE/derived-table layer are out of scope for this pass - a real, known gap, not
/// a silently guessed one.
/// </summary>
public sealed record AlwaysEncryptedOrderByFinding(
    string TableQualifiedName,
    string ColumnName,
    string EncryptionTypeDisplay,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High)
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}
