using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public enum SchemaboundAliasTypeKind
{
    Parameter,
    ReturnType,
    TableColumn,
}

public sealed record SchemaboundAliasTypeFinding(
    string FunctionQualifiedName,
    SchemaboundAliasTypeKind Kind,
    string MemberName,
    string AliasTypeQualifiedName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.SchemaboundAliasTypeRuleId;

    public SourceSpan Location => new(SourcePath, Line, Column);
}
