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
                var errors = result.Errors
                    .Select(e => new ParseErrorInfo(e.Line, e.Column, e.Number, e.Message))
                    .ToList();
                return new FileParseHealth(path, errors, result.BatchCount);
            })
            .ToList();

        return new ParseHealthReport(files);
    }
}
