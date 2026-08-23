using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public enum ModuleCompileFlagFindingKind
{
    RecompilesEveryCall,

    TableValuedFunctionReturnUsesDatabaseCollation,
}

public sealed record ModuleCompileFlagFinding(
    ModuleCompileFlagFindingKind Kind,
    string ModuleQualifiedName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High)
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}

