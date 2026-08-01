using SilentScan.Core.Catalog;
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
        var dynamicSqlFindings = cleanParseResults.SelectMany(DynamicSqlScanner.Scan).ToList();

        // Catalog/lineage need every cleanly-parsed file together, so views can resolve
        // against tables (and other views) declared in a different file.
        var catalog = CatalogBuilder.Build(cleanParseResults);
        var lineage = LineageResolver.Resolve(catalog, cleanParseResults);
        var typedFindings = cleanParseResults
            .SelectMany(r => TypedPredicateExtractor.Extract(r, catalog, lineage))
            .Where(f => f.Verdict != Verdict.SeekPreserved)
            .ToList();

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

        return new ScanReport(new ParseHealthReport(fileHealth), tier1Findings, typedFindings, dynamicSqlFindings);
    }

    private static int VerdictRank(Verdict verdict) => verdict switch
    {
        Verdict.ScanForced => 0,
        Verdict.RangeSeek => 1,
        Verdict.Unknown => 2,
        _ => 3,
    };
}
