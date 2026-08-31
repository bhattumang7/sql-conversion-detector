using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class DmlTargetTableScanner
{
    public static IReadOnlySet<string> Scan(IEnumerable<SqlParseResult> parseResults, DatabaseCatalog catalog, IScanStage? stage = null)
    {
        var targets = new HashSet<string>(catalog.IdentifierComparer);
        var rule = new Rule(catalog, targets);
        foreach (var parseResult in parseResults)
        {
            stage?.Advance(currentItem: parseResult.SourcePath);
            var walker = new ModuleWalker(parseResult.SourcePath, catalog, EmptyResolvedViews, rules: [rule]);
            parseResult.Fragment.Accept(walker);
        }

        return targets;
    }

    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    private sealed class Rule(DatabaseCatalog catalog, HashSet<string> targets) : IModuleRule
    {
        public void OnEnterInsertStatementScope(InsertStatement node, ModuleWalker walker) =>
            RecordWrite(node.InsertSpecification.Target, node.WithCtesAndXmlNamespaces);

        public void OnEnterUpdateStatementScope(UpdateStatement node, ScopeChain scopeChain, ModuleWalker walker) =>
            RecordWrite(node.UpdateSpecification.Target, node.WithCtesAndXmlNamespaces);

        public void OnEnterDeleteStatementScope(DeleteStatement node, ScopeChain scopeChain, ModuleWalker walker) =>
            RecordWrite(node.DeleteSpecification.Target, node.WithCtesAndXmlNamespaces);

        public void OnEnterMergeStatementScope(MergeStatement node, ScopeChain scopeChain, ModuleWalker walker) =>
            RecordWrite(node.MergeSpecification.Target, node.WithCtesAndXmlNamespaces);

        private void RecordWrite(TableReference? target, WithCtesAndXmlNamespaces? withCtes)
        {
            if (DmlWriteTargetResolver.TryResolve(target, withCtes, catalog) is { } qualifiedName)
            {
                targets.Add(qualifiedName);
            }
        }
    }
}
