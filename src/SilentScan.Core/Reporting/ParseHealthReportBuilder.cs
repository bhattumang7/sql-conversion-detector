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

    public static ParseHealthReport BuildFromParseResults(IReadOnlyList<SqlParseResult> fileParseResults) =>
        new([.. fileParseResults.Select(ToFileParseHealth)]);

    internal static FileParseHealth ToFileParseHealth(SqlParseResult result)
    {
        var errors = result.Errors
            .Select(e => new ParseErrorInfo(e.Line, e.Column, e.Number, e.Message))
            .ToList();
        return new FileParseHealth(result.SourcePath, errors, result.BatchCount, result.UnanalyzedBatches);
    }
}
