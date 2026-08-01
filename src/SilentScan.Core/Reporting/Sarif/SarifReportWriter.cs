using System.Text.Json;
using System.Text.Json.Serialization;
using SilentScan.Core.Lineage;
using SilentScan.Core.Predicates;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Reporting.Sarif;

/// <summary>
/// Converts a <see cref="ScanReport"/> to SARIF 2.1.0 JSON (CLAUDE.md: "SARIF export so the
/// tool doubles as a CI gate later"). Rule IDs and levels are stable across runs so CI
/// baselining/suppression works.
/// </summary>
public static class SarifReportWriter
{
    private const string ToolName = "SilentScan";
    private const string ToolVersion = "0.1.0";

    private const string LevelError = "error";
    private const string LevelWarning = "warning";
    private const string LevelNote = "note";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Write(ScanReport report)
    {
        var results = new List<SarifResult>();
        results.AddRange(report.Tier1Findings.Select(ToResult));
        results.AddRange(report.TypedFindings.Select(ToResult));
        results.AddRange(report.DynamicSqlFindings.Select(ToResult));
        results.AddRange(report.ExpressionDerivedFindings.Select(ToResult));

        // No public repository exists for this project yet, so informationUri (optional in
        // the SARIF spec) is omitted rather than pointed at a URL that doesn't resolve.
        var log = new SarifLog(
            "https://raw.githubusercontent.com/oasis-tcs/sarif-spec/master/Schemata/sarif-schema-2.1.0.json",
            "2.1.0",
            [new SarifRun(new SarifTool(new SarifDriver(ToolName, ToolVersion, InformationUri: null, SarifRuleCatalog.AllRules)), results)]);

        return JsonSerializer.Serialize(log, JsonOptions);
    }

    private static SarifResult ToResult(SargabilityFinding finding)
    {
        var ruleId = SarifRuleCatalog.Tier1RuleId(finding.Kind);
        var level = finding.Kind == SargabilityFindingKind.LikePatternNotLiteral ? LevelNote : LevelWarning;
        var detail = finding.Detail is null ? string.Empty : $" ({finding.Detail})";
        var message = $"Column '{finding.ColumnName}' is used in a non-sargable predicate{detail}.{DynamicSqlOriginNote(finding.DynamicSqlCallSite)}";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, finding.Column);
    }

    private static SarifResult ToResult(TypedPredicateFinding finding)
    {
        var ruleId = SarifRuleCatalog.VerdictRuleId(finding.Verdict);
        var level = finding.Verdict switch
        {
            Verdict.ScanForced => LevelError,
            Verdict.RangeSeek => LevelWarning,
            _ => LevelNote,
        };

        var depthNote = DescribeDepth(finding.Column.Depth);
        var indexNote = finding.Column.Indexed ? ", indexed" : ", not indexed";
        var message = $"{finding.Verdict}: '{finding.Column.TableQualifiedName}.{finding.Column.ColumnName}'{indexNote}{depthNote}.{DynamicSqlOriginNote(finding.DynamicSqlCallSite)}";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, finding.ColumnPosition);
    }

    private static SarifResult ToResult(ExpressionDerivedFinding finding)
    {
        var chain = string.Join(" <- ", finding.TransformationChain.Select(DescribeTransformationSite));
        var underlying = finding.UnderlyingBaseColumns.Count == 0
            ? "no traceable base column"
            : string.Join(", ", finding.UnderlyingBaseColumns.Select(bc => $"{bc.TableQualifiedName}.{bc.ColumnName}{(bc.Indexed ? " (indexed)" : " (not indexed)")}"));
        var message = $"Column '{finding.ColumnName}' is a computed expression by the time it reaches this predicate ({chain}); underlying: {underlying}.{DynamicSqlOriginNote(finding.DynamicSqlCallSite)}";

        return BuildResult(SarifRuleCatalog.ExpressionDerivedRuleId, LevelError, message, finding.SourcePath, finding.Line, finding.ColumnPosition);
    }

    private static SarifResult ToResult(DynamicSqlFinding finding)
    {
        var ruleId = SarifRuleCatalog.DynamicSqlRuleId(finding.Outcome);
        var level = finding.Outcome == DynamicSqlOutcome.AnalyzedLiteral ? LevelNote : LevelWarning;

        var message = finding.Outcome switch
        {
            DynamicSqlOutcome.AnalyzedLiteral =>
                "Dynamic SQL call with a provably-constant argument; its contents were reparsed and analyzed like static SQL.",
            DynamicSqlOutcome.InnerParseFailed =>
                $"Dynamic SQL call's argument was provably constant but did not parse as T-SQL ({finding.Reason}).",
            _ => $"Dynamic SQL call's argument could not be statically analyzed ({finding.Reason}).",
        };

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, finding.Column);
    }

    private static string DynamicSqlOriginNote(SourceSpan? callSite) =>
        callSite is { } span ? $" (via dynamic SQL executed at {span.SourcePath}:{span.Line})" : string.Empty;

    private static string DescribeTransformationSite(TransformationSite site) =>
        site.SourcePath is null ? site.Description : $"{site.Description} at {site.SourcePath}:{site.Line}";

    private static string DescribeDepth(int depth)
    {
        if (depth == 0)
        {
            return string.Empty;
        }

        var layerWord = depth == 1 ? "layer" : "layers";
        return $" (inherited through {depth} view {layerWord})";
    }

    private static SarifResult BuildResult(string ruleId, string level, string message, string sourcePath, int line, int? startColumn) =>
        new(
            ruleId,
            level,
            new SarifMessage(message),
            [new SarifLocation(new SarifPhysicalLocation(new SarifArtifactLocation(ToUri(sourcePath)), new SarifRegion(line, startColumn)))]);

    private static string ToUri(string sourcePath) => sourcePath.Replace('\\', '/').Replace(" ", "%20", StringComparison.Ordinal);
}
