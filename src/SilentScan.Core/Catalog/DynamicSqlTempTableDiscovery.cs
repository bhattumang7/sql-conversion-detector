using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates.DynamicSqlValue;
using SilentScan.Core.Common;

namespace SilentScan.Core.Catalog;

public static class DynamicSqlTempTableDiscovery
{
    private const string CreateTableMarker = "CREATE TABLE";

    public static DatabaseCatalog Discover(
        IEnumerable<SqlParseResult> parseResults, string? manifestDeclaredCollation = null, string? manifestTempdbCollation = null, int? compatibilityLevel = null,
        DatabaseCatalog? enclosingCatalog = null)
    {
        var wrapped = new List<SqlParseResult>();
        foreach (var result in parseResults)
        {
            if (result.HasErrors)
            {
                continue;
            }

            Dictionary<TSqlStatement, bool?>? ansiNullDfltStates = null;

            foreach (var script in DynamicSqlScannerV2.Scan(result).AnalyzableScripts)
            {
                if (script.Scope.ProcScope is not { Length: > 0 } scope
                    || !script.InnerText.Contains(CreateTableMarker, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var initialQuotedIdentifiers = enclosingCatalog?.ResolveDynamicSqlQuotedIdentifier(scope) ?? true;
                ansiNullDfltStates ??= AnsiNullDfltFlowResolver.Resolve(result.Fragment);
                var ansiNullDfltOverride = ResolveAnsiNullDfltOverrideAt(ansiNullDfltStates, script.CallSite.Line, script.CallSite.Column);
                if (TryWrapAsScopedProcedure(script.InnerText, scope, result.SourcePath, initialQuotedIdentifiers, compatibilityLevel, ansiNullDfltOverride) is { } wrappedResult)
                {
                    wrapped.Add(wrappedResult);
                }
            }
        }

        return wrapped.Count == 0 ? new DatabaseCatalog() : CatalogBuilder.Build(wrapped, manifestDeclaredCollation, manifestTempdbCollation, enclosingCatalog?.IsAnsiNullDefaultOn);
    }

    private static SqlParseResult? TryWrapAsScopedProcedure(
        string innerText, string scope, string sourcePath, bool initialQuotedIdentifiers, int? compatibilityLevel, bool? ansiNullDfltOverride)
    {
        var dotIndex = scope.IndexOf('.', StringComparison.Ordinal);
        if (dotIndex <= 0 || dotIndex == scope.Length - 1)
        {
            return null;
        }

        var schema = Bracket(scope[..dotIndex]);
        var name = Bracket(scope[(dotIndex + 1)..]);
        var ansiNullDfltPrefix = ansiNullDfltOverride switch
        {
            true => "SET ANSI_NULL_DFLT_ON ON; ",
            false => "SET ANSI_NULL_DFLT_OFF ON; ",
            null => string.Empty,
        };
        var wrapperSql = $"CREATE PROCEDURE [{schema}].[{name}] AS BEGIN {ansiNullDfltPrefix}{innerText} END";
        var parsed = SqlScriptParser.ParseText(sourcePath, wrapperSql, initialQuotedIdentifiers, compatibilityLevel);
        return parsed.HasErrors ? null : parsed;
    }

    private static string Bracket(string identifier) => identifier.Replace("]", "]]", StringComparison.Ordinal);

    private static bool? ResolveAnsiNullDfltOverrideAt(Dictionary<TSqlStatement, bool?> states, int line, int column)
    {
        bool? result = null;
        var bestLine = -1;
        var bestColumn = -1;
        foreach (var (statement, value) in states)
        {
            if (statement.StartLine > line || (statement.StartLine == line && statement.StartColumn > column))
            {
                continue;
            }

            if (statement.StartLine < bestLine || (statement.StartLine == bestLine && statement.StartColumn < bestColumn))
            {
                continue;
            }

            bestLine = statement.StartLine;
            bestColumn = statement.StartColumn;
            result = value;
        }

        return result;
    }
}
