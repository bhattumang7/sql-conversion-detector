using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public enum NativelyCompiledInterpretedCalleeKind
{
    ExecutedProcedure,
    CalledFunction,
}

public sealed record NativelyCompiledInterpretedCalleeFinding(
    string ModuleQualifiedName,
    NativelyCompiledInterpretedCalleeKind Kind,
    string CalleeQualifiedName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.NativelyCompiledInterpretedCalleeRuleId;

    public SourceSpan Location => new(SourcePath, Line, Column);
}
