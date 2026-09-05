using System.Text.Json.Serialization;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Lineage;

[JsonPolymorphic]
[JsonDerivedType(typeof(BaseColumn), "BaseColumn")]
[JsonDerivedType(typeof(Declared), "Declared")]
[JsonDerivedType(typeof(Cast), "Cast")]
[JsonDerivedType(typeof(Expression), "Expression")]
[JsonDerivedType(typeof(Union), "Union")]
[JsonDerivedType(typeof(Unknown), "Unknown")]
public abstract record ColumnProvenance
{
    private ColumnProvenance()
    {
    }

    public sealed record BaseColumn(string TableQualifiedName, string ColumnName, SqlType? Type, int Depth = 0, bool IsNullableSide = false, bool IsNonDeterministic = false) : ColumnProvenance;

    public sealed record Declared(SqlType Type, string? TableQualifiedName = null, int Depth = 0) : ColumnProvenance;

    public sealed record Cast(SqlType ExplicitType, ColumnProvenance Inner, string? OriginSourcePath = null, int OriginLine = 0, int Depth = 0) : ColumnProvenance;

    public sealed record Expression(SqlType? InferredType, IReadOnlyList<ColumnProvenance> Inputs, string? OriginSourcePath = null, int OriginLine = 0, int Depth = 0) : ColumnProvenance;

    public sealed record Union(IReadOnlyList<ColumnProvenance> Branches) : ColumnProvenance;

    public sealed record Unknown(string Reason) : ColumnProvenance;
}
