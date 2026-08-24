using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public sealed record AlwaysEncryptedOrderByFinding(
    string TableQualifiedName,
    string ColumnName,
    string EncryptionTypeDisplay,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}
