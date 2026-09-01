using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public enum OnlineRebuildLegacyLobKind
{
    AlterTableRebuild,
    AlterIndexAllRebuild,
}

public sealed record OnlineRebuildLegacyLobFinding(
    OnlineRebuildLegacyLobKind Kind,
    string TableQualifiedName,
    string ColumnName,
    string TypeDisplay,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.OnlineRebuildLegacyLobRuleId(Kind);

    public SourceSpan Location => new(SourcePath, Line, Column);
}
