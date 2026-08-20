using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates.DynamicSqlValue;
using SilentScan.Core.Common;

namespace SilentScan.Core.Catalog;

/// <summary>
/// Closes the gap left by <see cref="CatalogBuilder"/>'s own body-scanning pass: a
/// <c>CREATE TABLE #x (...)</c> that lives only inside a DYNAMICALLY BUILT SQL string (e.g.
/// <c>SET @ddl = @ddl + 'CREATE TABLE #Runs (...)'; EXEC (@ddl)</c>) is never a literal
/// <c>CreateTableStatement</c> node anywhere in the static AST <see cref="CatalogBuilder"/>
/// walks, so it was never registered - every later STATIC statement in the same module
/// referencing <c>#Runs</c> failed to resolve it, cascading into "FROM table reference"/"column
/// reference" skip counts (traced on a real production database: 6,516 occurrences, 98%
/// concentrated in two modules building a temp table this way). The dynamic SQL engine already
/// folds call sites like this to a fully-known literal script (<see cref="Predicates.DynamicSqlScript.InnerText"/>)
/// - this class re-parses any such script that looks like it might contain a
/// <c>CREATE TABLE</c>, wrapped in a synthetic <c>CREATE PROCEDURE</c> matching the call site's
/// own <see cref="Predicates.DynamicSqlScope.ProcScope"/> so <see cref="CatalogBuilder"/>'s existing
/// scope-tracking visitor registers the discovered shape under the EXACT SAME scope key a real
/// static body-declared temp table would get - no changes needed to <see cref="CatalogBuilder"/>
/// or to <c>FromScopeResolver</c>'s existing (scope, name) lookup, both already correct for a
/// temp table found this way.
///
/// Deliberately best-effort and catalog/call-graph-free: the dominant real-world shape (a chain
/// of literal string-concatenation building the whole CREATE TABLE text inside one proc, no
/// cross-proc parameter dependency) folds without either, and this discovery pass runs BEFORE
/// the main catalog is even built - the catalog it would need doesn't exist yet, that's the
/// whole reason this class exists. A site that genuinely needs a catalog to fold is simply not
/// discovered here; the LATER, full-fidelity dynamic SQL scan (with catalog and call graph, once
/// the real pipeline runs) is unaffected either way - it always re-derives its own results fresh.
/// </summary>
public static class DynamicSqlTempTableDiscovery
{
    private const string CreateTableMarker = "CREATE TABLE";

    /// <summary>
    /// Returns a catalog containing only the temp-table shapes discovered this way, scoped
    /// exactly like <see cref="CatalogBuilder"/>'s own output - merge it into the real catalog
    /// with <see cref="DatabaseCatalog.MergeFileModeExtras"/>, same as the static-body pass.
    /// </summary>
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

    /// <summary>
    /// <paramref name="scope"/> is already exactly the <c>schema.name</c> string
    /// <see cref="SchemaObjectNameHelper.Qualify"/> would produce for a real CREATE PROCEDURE -
    /// splitting it back at the first '.' and bracket-quoting each half reconstructs a header
    /// that re-qualifies to the IDENTICAL string, so <see cref="CatalogBuilder"/>'s visitor scopes
    /// the discovered table under the same key a real static declaration would use. A scope with
    /// no '.' (malformed/unexpected) or SQL that fails to reparse once wrapped declines - never a
    /// guess at the wrong scope, which would silently discard the finding rather than mis-scope it
    /// (a temp table registered under a scope no later lookup ever queries is simply inert, not
    /// wrong).
    /// </summary>
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
