using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Reporting;

public static class ScanReportBuilder
{
    public static ScanReport Build(IReadOnlyList<string> sqlFilePaths)
    {
        var parser = new SqlScriptParser();
        return BuildFromParseResults(sqlFilePaths.Select(parser.ParseFile).ToList());
    }

    /// <summary>
    /// Builds a report from already-parsed sources. Exists separately from <see cref="Build"/>
    /// so callers that need to preprocess text before parsing (e.g. the corpus scanner
    /// substituting DNN's {databaseOwner}/{objectQualifier} template tokens) can parse in
    /// memory via <see cref="SqlScriptParser.ParseText"/> instead of writing temp files.
    /// </summary>
    public static ScanReport BuildFromParseResults(IReadOnlyList<SqlParseResult> allParseResults)
    {
        var fileHealth = new List<FileParseHealth>();
        var cleanParseResults = new List<SqlParseResult>();

        foreach (var result in allParseResults)
        {
            var errors = result.Errors
                .Select(e => new ParseErrorInfo(e.Line, e.Column, e.Number, e.Message))
                .ToList();
            fileHealth.Add(new FileParseHealth(result.SourcePath, errors));

            if (errors.Count == 0)
            {
                cleanParseResults.Add(result);
            }
        }

        var tier1Findings = cleanParseResults.SelectMany(NonSargablePredicateScanner.Scan).ToList();

        var dynamicSqlExtractions = cleanParseResults.Select(DynamicSqlScanner.Scan).ToList();
        var dynamicSqlFindings = dynamicSqlExtractions.SelectMany(r => r.Findings).ToList();
        var dynamicSqlScripts = dynamicSqlExtractions.SelectMany(r => r.AnalyzableScripts).ToList();

        // Catalog/lineage need every cleanly-parsed file together, so views can resolve
        // against tables (and other views) declared in a different file.
        var catalog = CatalogBuilder.Build(cleanParseResults);
        var lineage = LineageResolver.Resolve(catalog, cleanParseResults);
        var extractionResults = cleanParseResults.Select(r => TypedPredicateExtractor.Extract(r, catalog, lineage)).ToList();
        var typedFindings = extractionResults.SelectMany(r => r.TypedFindings).ToList();
        var expressionDerivedFindings = extractionResults.SelectMany(r => r.ExpressionDerivedFindings).ToList();
        var skippedConstructs = new List<SkippedConstruct>();
        skippedConstructs.AddRange(catalog.Skipped.Entries);
        skippedConstructs.AddRange(lineage.Skipped.Entries);
        skippedConstructs.AddRange(extractionResults.SelectMany(r => r.SkippedConstructs));

        // Tier A of the dynamic SQL policy (CLAUDE.md): reparse provably-constant EXEC/
        // sp_executesql arguments through the same pipeline and fold their findings in,
        // remapped back to their true source location.
        var dynamicSqlResult = DynamicSqlPipeline.Analyze(dynamicSqlScripts, catalog, lineage);
        dynamicSqlFindings = [.. dynamicSqlFindings, .. dynamicSqlResult.Findings];
        tier1Findings = [.. tier1Findings, .. dynamicSqlResult.Tier1Findings];
        typedFindings = [.. typedFindings, .. dynamicSqlResult.TypedFindings];
        expressionDerivedFindings = [.. expressionDerivedFindings, .. dynamicSqlResult.ExpressionDerivedFindings];
        skippedConstructs.AddRange(dynamicSqlResult.SkippedConstructs);

        typedFindings = [.. typedFindings.Where(f => f.Verdict != Verdict.SeekPreserved)];

        // Deterministic output ordering (CLAUDE.md), then CLAUDE.md's Pass 4 rank:
        // SCAN_FORCED + indexed + depth>=1 first.
        tier1Findings = [.. tier1Findings.OrderBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column)];
        dynamicSqlFindings = [.. dynamicSqlFindings.OrderBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line)];
        typedFindings = [.. typedFindings
            .OrderBy(f => VerdictRank(f.Verdict))
            .ThenByDescending(f => f.Column.Indexed)
            .ThenByDescending(f => f.Column.Depth)
            .ThenBy(f => f.SourcePath, StringComparer.Ordinal)
            .ThenBy(f => f.Line)];
        expressionDerivedFindings = [.. expressionDerivedFindings.OrderBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line)];
        var orderedSkippedConstructs = skippedConstructs
            .OrderBy(s => s.Pass)
            .ThenBy(s => s.SourcePath, StringComparer.Ordinal)
            .ThenBy(s => s.Line)
            .ThenBy(s => s.Column)
            .ToList();

        return new ScanReport(new ParseHealthReport(fileHealth), tier1Findings, typedFindings, dynamicSqlFindings, expressionDerivedFindings, orderedSkippedConstructs);
    }

    private static int VerdictRank(Verdict verdict) => verdict switch
    {
        Verdict.ScanForced => 0,
        Verdict.RangeSeek => 1,
        Verdict.Unknown => 2,
        _ => 3,
    };
}
