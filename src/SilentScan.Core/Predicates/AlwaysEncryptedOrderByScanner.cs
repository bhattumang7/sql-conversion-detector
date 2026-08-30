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
        var visitor = new Visitor(parseResult.SourcePath, catalog);
        parseResult.Fragment.Accept(visitor);
        return
        [
            .. visitor.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];
    }

#pragma warning disable CS9107
    private sealed class Visitor(string sourcePath, DatabaseCatalog catalog)
        : ScopedSqlVisitorBase(sourcePath, catalog, EmptyResolvedViews, ledger: null, currentProcScope: null, callerScopeByCalleeScope: null)
#pragma warning restore CS9107
    {
        public List<AlwaysEncryptedOrderByFinding> Findings { get; } = [];

        protected override void OnQuerySpecificationScope(QuerySpecification node, ScopeChain scopeChain, Action continueDescent)
        {
            if (node.OrderByClause is { OrderByElements.Count: > 0 } orderByClause)
            {
                foreach (var element in orderByClause.OrderByElements)
                {
                    Inspect(element, scopeChain);
                }
            }

            continueDescent();
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
