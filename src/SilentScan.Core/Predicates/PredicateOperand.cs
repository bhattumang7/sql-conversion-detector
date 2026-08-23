using System.Text.Json.Serialization;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

[JsonPolymorphic]
[JsonDerivedType(typeof(Column), "Column")]
[JsonDerivedType(typeof(Value), "Value")]
public abstract record PredicateOperand
{
    private PredicateOperand()
    {
    }

public sealed record Column(
        string TableQualifiedName,
        string ColumnName,
        SqlType? Type,
        bool? Indexed,
        int Depth,
        ColumnProvenance Provenance,
        string? ImmediateRelationQualifiedName = null,
        string? ImmediateColumnName = null,
        string? IndexName = null) : PredicateOperand;

public sealed record Value(
        SqlType? Type, bool IsLiteral = false, string? LiteralText = null,
        string? VariableName = null, bool IsFormalParameter = false) : PredicateOperand;
}
