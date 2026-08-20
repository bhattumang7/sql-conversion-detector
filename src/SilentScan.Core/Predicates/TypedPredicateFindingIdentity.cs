using System.Security.Cryptography;
using System.Text;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

/// <summary>
/// The shape-level identity of a <see cref="TypedPredicateFinding"/> - WHERE the column lives
/// (table + column name), HOW it's compared (operator), and WHAT it's compared against, described
/// at the level of detail that actually decides the verdict (a type category and, for
/// string-family operands, the collation) rather than incidental facts like the literal's exact
/// text or source location. Deliberately excludes source position: <see cref="TypedFindingDeduplicator"/>
/// needs two textually-identical occurrences of the same defect (the same CREATE re-issued across
/// several incremental upgrade scripts) to collapse to one, and <see cref="TypedPredicateFinding.Fingerprint"/>
/// needs to stay stable across two scans of the same repo at the same commit even if an
/// unrelated file earlier in scan order shifts a later finding's line number - a fingerprint that
/// changed with unrelated edits elsewhere would be useless for tracking a finding across scans.
/// </summary>
public static class TypedPredicateFindingIdentity
{
    public static string ComputeKey(PredicateOperand.Column column, PredicateOperand other, string operatorText)
    {
        var otherShape = other switch
        {
            PredicateOperand.Value { Type: { } type } => $"Value:{type.Category}:{(type.IsStringFamily ? type.Collation?.Name ?? "?" : string.Empty)}",
            PredicateOperand.Value => "Value:Unresolved",
            PredicateOperand.Column otherColumn => $"Column:{otherColumn.TableQualifiedName}.{otherColumn.ColumnName}",
            _ => "Unknown",
        };

        return string.Concat(column.TableQualifiedName, "\u0001", column.ColumnName, "\u0001", operatorText, "\u0001", otherShape);
    }

    /// <summary>
    /// A short, stable hash of <see cref="ComputeKey"/> - hashed (rather than exposing the raw
    /// key text) so the fingerprint has a fixed, predictable shape regardless of how long a table/
    /// column name happens to be, and so callers don't come to depend on the key's own internal
    /// text format as if it were a public contract.
    /// </summary>
    public static string ComputeFingerprint(PredicateOperand.Column column, PredicateOperand other, string operatorText)
    {
        var key = ComputeKey(column, other, operatorText);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash)[..16];
    }
}
