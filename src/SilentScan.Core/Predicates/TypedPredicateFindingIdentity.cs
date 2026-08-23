using System.Security.Cryptography;
using System.Text;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

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

    public static string ComputeFingerprint(PredicateOperand.Column column, PredicateOperand other, string operatorText)
    {
        var key = ComputeKey(column, other, operatorText);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash)[..16];
    }
}
