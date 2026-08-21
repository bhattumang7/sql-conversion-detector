using SilentScan.Core.Parsing;

namespace SilentScan.Core.Reporting;

public static class ParseHealthReportBuilder
{
    public static ParseHealthReport Build(IReadOnlyList<string> sqlFilePaths)
    {
        var files = sqlFilePaths
            .Select(path =>
            {
                var result = SqlScriptParser.ParseFile(path);
                return ToFileParseHealth(result);
            })
            .ToList();

        return new ParseHealthReport(files);
    }

    /// <summary>
    /// Builds from ALREADY-PARSED results rather than re-parsing from disk - the shape a
    /// live-catalog corpus run needs: CLAUDE.md's dialect-sniffing signal is defined over "a
    /// repo's FILES" (this type's own doc comment: "did every .sql file in a corpus parse
    /// cleanly"), not over whatever subset of those files' DEFINITIONS survived deployment and
    /// got read back from <c>sys.sql_modules</c> - a file that is not T-SQL at all typically
    /// fails to deploy as DDL entirely, which would otherwise make it silently DISAPPEAR from a
    /// module-sourced denominator instead of counting against it, defeating the exact "a MySQL
    /// file parsed as T-SQL is noise" case dialect sniffing exists to catch.
    /// </summary>
    public static ParseHealthReport BuildFromParseResults(IReadOnlyList<SqlParseResult> fileParseResults) =>
        new([.. fileParseResults.Select(ToFileParseHealth)]);

    /// <summary>
    /// Shared with <see cref="ScanReportBuilder"/>'s own file-health mapping so the two never
    /// drift into two different opinions of what a <see cref="SqlParseResult"/> maps to.
    /// </summary>
    internal static FileParseHealth ToFileParseHealth(SqlParseResult result)
    {
        var errors = result.Errors
            .Select(e => new ParseErrorInfo(e.Line, e.Column, e.Number, e.Message))
            .ToList();
        return new FileParseHealth(result.SourcePath, errors, result.BatchCount, result.UnanalyzedBatches);
    }
}
