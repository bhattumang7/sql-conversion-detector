using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public enum SessionDateSettingKind
{
    DateFormat,
    DateFirst,
}

public sealed record SessionDateSettingFinding(
    SessionDateSettingKind Kind,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.Low)
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}

