using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public enum UnparameterizedDynamicSqlFindingKind
{
ConcatenatedValueInConstantSql,

ExecStringConcatenatesParameterizableValue,
}

public sealed record UnparameterizedDynamicSqlFinding(
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    UnparameterizedDynamicSqlFindingKind Kind,
    FindingConfidence Confidence = FindingConfidence.High)
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}

