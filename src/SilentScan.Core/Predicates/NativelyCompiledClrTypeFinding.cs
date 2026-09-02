using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public enum NativelyCompiledClrTypeKind
{
    Parameter,
    LocalVariable,
}

public sealed record NativelyCompiledClrTypeFinding(
    string ModuleQualifiedName,
    NativelyCompiledClrTypeKind Kind,
    string MemberName,
    string TypeQualifiedName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.NativelyCompiledClrTypeRuleId;

    public SourceSpan Location => new(SourcePath, Line, Column);
}
