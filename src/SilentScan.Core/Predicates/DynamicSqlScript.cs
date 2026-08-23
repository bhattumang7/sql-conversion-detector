using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

public readonly record struct SourceSpan(string SourcePath, int Line, int Column);

public sealed record DynamicSqlScope(string? ProcScope, SchemaObjectName? TriggerTarget)
{
    public static readonly DynamicSqlScope None = new(null, null);
}

public sealed record PlaceholderOccurrence(int InnerStartOffset, int Length, SqlType Type, SourceSpan Origin);

public sealed record DynamicSqlScript(
    SourceSpan CallSite,
    string InnerText,
    DynamicSqlSegmentMap SegmentMap,
    string? ParameterDeclarationText,
    DynamicSqlScope Scope,
    IReadOnlyDictionary<string, string>? ArgumentBindings = null,
    FindingConfidence Confidence = FindingConfidence.High,
    IReadOnlyList<PlaceholderOccurrence>? PlaceholderOccurrences = null,
    bool IsExecString = false);
