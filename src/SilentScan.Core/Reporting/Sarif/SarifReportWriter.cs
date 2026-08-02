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

    // Read from the assembly's own version (Directory.Build.props' <Version>) rather than a
    // hardcoded literal - a hardcoded string silently stops tracking the tool's actual version
    // the moment someone forgets to update it by hand, which defeats SARIF's whole purpose of
    // letting CI baselining/suppression key off driver.version.
    private static readonly string ToolVersion =
        typeof(SarifReportWriter).Assembly.GetName().Version?.ToString() ?? "0.0.0";

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

        // A syntactic pattern is only worth a reader's full attention when it's confirmed on a
        // real, leading-key-indexed column - one where there was an actual seek to lose. finding
        // .Indexed is null (unresolved) far more often than it's false (resolved-and-confirmed-
        // unindexed) in real corpora, so both demote the same way: only Indexed == true keeps
        // the kind's normal severity. Without this, every syntactic hit on an unindexed or
        // unresolvable column reported at the same "warning" level as a genuine index-defeating
        // one - the single largest source of unranked noise this pass produced.
        var isConfirmedIndexed = finding.Indexed == true;
        var level = isConfirmedIndexed && finding.Kind != SargabilityFindingKind.LikePatternNotLiteral ? LevelWarning : LevelNote;
        var detail = finding.Detail is null ? string.Empty : $" ({finding.Detail})";
        var indexNote = finding.TableQualifiedName is { } table
            ? $" [{table}.{finding.ColumnName}, indexed={IndexedDisplay(finding.Indexed)}]"
            : string.Empty;
        var message = $"Column '{finding.ColumnName}' is used in a non-sargable predicate{detail}.{indexNote}{DynamicSqlOriginNote(finding.DynamicSqlCallSite)}";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, finding.Column);
    }

    private static SarifResult ToResult(TypedPredicateFinding finding)
    {
        var ruleId = SarifRuleCatalog.VerdictRuleId(finding.Verdict);
        var baseLevel = finding.Verdict switch
        {
            Verdict.ScanForced => LevelError,
            Verdict.RangeSeek => LevelWarning,
            _ => LevelNote,
        };

        // Mirrors the Tier-1 downgrade below: a ScanForced/RangeSeek verdict on a column with no
        // evidence it's indexed cost nothing extra beyond the conversion itself - there was no
        // seek to lose. Every corpus finding this tool has actually produced against real-world
        // repos has been on an unindexed column (an audit finding), so without this every one of
        // them reported at "error" regardless of whether an index was ever in play.
        var level = finding.Column.Indexed ? baseLevel : DowngradeOneLevel(baseLevel);

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

        // Same indexed-based downgrade as every other verdict-bearing finding kind: an
        // expression-derived column with no indexed base column underneath it isn't costing an
        // otherwise-available seek.
        var anyUnderlyingIndexed = finding.UnderlyingBaseColumns.Any(bc => bc.Indexed);
        var level = anyUnderlyingIndexed ? LevelError : DowngradeOneLevel(LevelError);

        return BuildResult(SarifRuleCatalog.ExpressionDerivedRuleId, level, message, finding.SourcePath, finding.Line, finding.ColumnPosition);
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

    private static string DowngradeOneLevel(string level) => level switch
    {
        LevelError => LevelWarning,
        LevelWarning => LevelNote,
        _ => LevelNote,
    };

    private static string IndexedDisplay(bool? indexed) => indexed is { } value ? value.ToString() : "unknown";

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

    /// <summary>
    /// Emits a real <c>file://</c> URI for an absolute path, or a percent-encoded relative
    /// reference otherwise - not the previous ad-hoc "swap backslashes, escape spaces" scheme,
    /// which produced a scheme-less string like <c>/home/user/repo/file.sql</c> that strict
    /// SARIF consumers (GitHub code scanning included) reject as an invalid
    /// artifactLocation.uri, and left every other reserved URI character (<c>#</c>, <c>?</c>,
    /// <c>%</c> itself, ...) unescaped.
    /// </summary>
    private static string ToUri(string sourcePath)
    {
        var normalized = sourcePath.Replace('\\', '/');
        if (Path.IsPathRooted(sourcePath))
        {
            return new Uri(normalized).AbsoluteUri;
        }

        return string.Join('/', normalized.Split('/').Select(Uri.EscapeDataString));
    }
}
