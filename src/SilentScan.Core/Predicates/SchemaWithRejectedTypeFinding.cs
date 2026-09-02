using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public sealed record SchemaWithRejectedTypeFinding(
    string ColumnName,
    string TypeDisplay,
    SchemaWithRejectedTypeKind Kind,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.SchemaWithRejectedTypeRuleId(Kind);

    public SourceSpan Location => new(SourcePath, Line, Column);
}

public enum SchemaWithRejectedTypeKind
{
    OpenXmlClrType,
    OpenRowsetLegacyType,
    OpenRowsetClrType,
    OpenRowsetXml,
}
