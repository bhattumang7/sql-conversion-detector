using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Common;

namespace SilentScan.Core.Predicates;

public static class UnindexedTempTableUsageScanner
{
    public static IReadOnlyList<UnindexedTempTableUsageFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
    {
        var rule = CreateRule(catalog);
        var walker = new ModuleWalker(parseResult.SourcePath, catalog, EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return Harvest(parseResult, catalog, rule);
    }

    internal static Rule CreateRule(DatabaseCatalog catalog) => new(catalog);

    internal static IReadOnlyList<UnindexedTempTableUsageFinding> Harvest(SqlParseResult parseResult, DatabaseCatalog catalog, Rule rule)
    {
        var tempIdentifierComparer = TypeInference.Collation.IdentifierComparer(catalog.EffectiveTempdbCollation);
        var findings = new List<UnindexedTempTableUsageFinding>();

        foreach (var declaration in rule.Declarations)
        {
            var usage = rule.Usages.FirstOrDefault(u =>
                u.Scope == declaration.Scope
                && tempIdentifierComparer.Equals(u.TempTableName, declaration.TempTableName));

            if (usage is null)
            {
                continue;
            }

            var temp = catalog.Find(declaration.TempQualifiedName, declaration.Scope);
            if (temp is null || temp.Indexes.Count != 0)
            {
                continue;
            }

            findings.Add(new UnindexedTempTableUsageFinding(
                usage.Kind,
                declaration.TempQualifiedName,
                parseResult.SourcePath,
                declaration.Line,
                usage.Line,
                usage.Column));
        }

        return
        [
            .. findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.DeclarationLine),
        ];
    }

    internal sealed record Declaration(string TempTableName, string TempQualifiedName, string? Scope, int Line);

    internal sealed record Usage(string TempTableName, string? Scope, UnindexedTempTableUsageKind Kind, int Line, int Column);

    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    internal sealed class Rule(DatabaseCatalog catalog) : IModuleRule
    {
        public List<Declaration> Declarations { get; } = [];

        public List<Usage> Usages { get; } = [];

        public void OnEnterSelectStatementScope(SelectStatement node, ModuleWalker walker)
        {
            if (node.Into is { BaseIdentifier.Value: var tempName } into && tempName.StartsWith('#'))
            {
                var qualified = catalog.Find(SchemaObjectNameHelper.Qualify(into), walker.CurrentProcScope)?.QualifiedName
                    ?? SchemaObjectNameHelper.Qualify(into);
                Declarations.Add(new Declaration(tempName, qualified, walker.CurrentProcScope, node.StartLine));
            }
        }

        public void OnEnterJoinSearchCondition(QualifiedJoin node, ModuleWalker walker)
        {
            TryRecordJoinOperand(node.FirstTableReference, node, walker);
            TryRecordJoinOperand(node.SecondTableReference, node, walker);
        }

        public void OnEnterQuerySpecificationScope(QuerySpecification node, ScopeChain scopeChain, ModuleWalker walker)
        {
            var tableReferences = node.FromClause?.TableReferences;
            if (tableReferences is null)
            {
                return;
            }

            if (node.WhereClause is { } where
                && tableReferences is [NamedTableReference { SchemaObject.BaseIdentifier.Value: var soloName }]
                && soloName.StartsWith('#'))
            {
                Usages.Add(new Usage(soloName, walker.CurrentProcScope, UnindexedTempTableUsageKind.FilteredInWhere, where.StartLine, where.StartColumn));
            }

            if (tableReferences.Count >= 2)
            {
                foreach (var reference in tableReferences)
                {
                    TryRecordJoinOperand(reference, reference, walker);
                }
            }

            foreach (var reference in tableReferences)
            {
                foreach (var unqualified in PredicateTreeWalker.FlattenUnqualifiedJoins(reference))
                {
                    if (unqualified.UnqualifiedJoinType != UnqualifiedJoinType.CrossJoin)
                    {
                        continue;
                    }

                    TryRecordJoinOperand(unqualified.FirstTableReference, unqualified, walker);
                    TryRecordJoinOperand(unqualified.SecondTableReference, unqualified, walker);
                }
            }
        }

        private void TryRecordJoinOperand(TableReference side, TSqlFragment joinNode, ModuleWalker walker)
        {
            if (side is NamedTableReference { SchemaObject.BaseIdentifier.Value: var name } && name.StartsWith('#'))
            {
                Usages.Add(new Usage(name, walker.CurrentProcScope, UnindexedTempTableUsageKind.JoinOperand, joinNode.StartLine, joinNode.StartColumn));
            }
        }
    }
}
