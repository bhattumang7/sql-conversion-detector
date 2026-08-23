using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates.DynamicSqlValue;
using SilentScan.Core.Common;

namespace SilentScan.Core.Catalog;

public static class DynamicSqlTempTableDiscovery
{
    private const string CreateTableMarker = "CREATE TABLE";

    public static DatabaseCatalog Discover(IEnumerable<SqlParseResult> parseResults, string? manifestDeclaredCollation = null, string? manifestTempdbCollation = null)
    {
        var wrapped = new List<SqlParseResult>();
        foreach (var result in parseResults)
        {
            if (result.HasErrors)
            {
                continue;
            }

            foreach (var script in DynamicSqlScannerV2.Scan(result).AnalyzableScripts)
            {
                if (script.Scope.ProcScope is not { Length: > 0 } scope
                    || !script.InnerText.Contains(CreateTableMarker, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (TryWrapAsScopedProcedure(script.InnerText, scope, result.SourcePath) is { } wrappedResult)
                {
                    wrapped.Add(wrappedResult);
                }
            }
        }

        return wrapped.Count == 0 ? new DatabaseCatalog() : CatalogBuilder.Build(wrapped, manifestDeclaredCollation, manifestTempdbCollation);
    }

    private static SqlParseResult? TryWrapAsScopedProcedure(string innerText, string scope, string sourcePath)
    {
        var dotIndex = scope.IndexOf('.', StringComparison.Ordinal);
        if (dotIndex <= 0 || dotIndex == scope.Length - 1)
        {
            return null;
        }

        var schema = Bracket(scope[..dotIndex]);
        var name = Bracket(scope[(dotIndex + 1)..]);
        var wrapperSql = $"CREATE PROCEDURE [{schema}].[{name}] AS BEGIN {innerText} END";
        var parsed = SqlScriptParser.ParseText(sourcePath, wrapperSql);
        return parsed.HasErrors ? null : parsed;
    }

    private static string Bracket(string identifier) => identifier.Replace("]", "]]", StringComparison.Ordinal);
}
