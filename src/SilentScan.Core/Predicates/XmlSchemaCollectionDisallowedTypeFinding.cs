using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public sealed record XmlSchemaCollectionDisallowedTypeFinding(
    string SchemaCollectionQualifiedName,
    string XsdTypeName,
    XmlSchemaCollectionDisallowedTypeKind Kind,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.XmlSchemaCollectionDisallowedTypeRuleId(Kind);

    public SourceSpan Location => new(SourcePath, Line, Column);
}

public enum XmlSchemaCollectionDisallowedTypeKind
{
    NotationType,
    IdOrIdRefType,
}
