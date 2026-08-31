using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class AlwaysEncryptedOrderByScanner
{
    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    public static IReadOnlyList<AlwaysEncryptedOrderByFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
    {
        var rule = CreateRule(parseResult.SourcePath, catalog);
        var walker = new ModuleWalker(parseResult.SourcePath, catalog, EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
    return Harvest(rule);
    }
    internal static Rule CreateRule(string sourcePath, DatabaseCatalog catalog) => new(sourcePath, catalog);

    internal static IReadOnlyList<AlwaysEncryptedOrderByFinding> Harvest(Rule rule) =>
            [
            .. rule.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];


    internal sealed class Rule(string sourcePath, DatabaseCatalog catalog) : IModuleRule
    {
        public List<AlwaysEncryptedOrderByFinding> Findings { get; } = [];

        public void OnEnterQuerySpecificationScope(QuerySpecification node, ScopeChain scopeChain, ModuleWalker walker)
        {
            if (node.OrderByClause is { OrderByElements.Count: > 0 } orderByClause)
            {
                foreach (var element in orderByClause.OrderByElements)
                {
                    Inspect(element, scopeChain);
                }
            }
        }

        private void Inspect(ExpressionWithSortOrder element, ScopeChain scopeChain)
        {
            if (element.Expression is not ColumnReferenceExpression columnRef
                || BaseColumnResolver.ResolveBaseColumn(columnRef, sourcePath, scopeChain, catalog) is not { } resolved
                || catalog.Find(resolved.TableQualifiedName)?.FindColumn(resolved.ColumnName, catalog.IdentifierComparer) is not { } catalogColumn
                || catalogColumn.EncryptionType is not (Catalog.ColumnEncryptionType.Deterministic or Catalog.ColumnEncryptionType.Randomized))
            {
                return;
            }

            Findings.Add(new AlwaysEncryptedOrderByFinding(
                resolved.TableQualifiedName,
                resolved.ColumnName,
                catalogColumn.EncryptionType.ToString(),
                sourcePath,
                element.StartLine,
                element.StartColumn));
        }
    }
}
