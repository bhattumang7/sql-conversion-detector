using System.Text.Json.Serialization;


namespace SilentScan.Core.Predicates;

public enum UndersizedDeclarationSite
{
    TableColumn,
    Declaration,
}

public sealed record UndersizedDeclarationFinding(
    UndersizedDeclarationSite Site,
    string QualifiedOrVariableName,
    string TypeDescription,
    int Length,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.Low) : IFinding
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}

