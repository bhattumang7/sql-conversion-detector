using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class RecursiveCteAnchorTypeMismatchScanner
{
    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    public static IReadOnlyList<RecursiveCteAnchorTypeMismatchFinding> Scan(
        SqlParseResult parseResult, DatabaseCatalog catalog, IReadOnlyDictionary<string, ResolvedRelation> resolvedViews)
    {
        var rule = CreateRule(parseResult.SourcePath, catalog, resolvedViews);
        var walker = new ModuleWalker(parseResult.SourcePath, catalog, EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return Harvest(rule);
    }

    internal static Rule CreateRule(string sourcePath, DatabaseCatalog catalog, IReadOnlyDictionary<string, ResolvedRelation> resolvedViews) =>
        new(sourcePath, catalog, resolvedViews);

    internal static IReadOnlyList<RecursiveCteAnchorTypeMismatchFinding> Harvest(Rule rule) =>
        [
            .. rule.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];

    internal sealed class Rule(string sourcePath, DatabaseCatalog catalog, IReadOnlyDictionary<string, ResolvedRelation> resolvedViews) : IModuleRule
    {
        public List<RecursiveCteAnchorTypeMismatchFinding> Findings { get; } = [];

        public void OnEnterSelectStatementScope(SelectStatement node, ModuleWalker walker) =>
            Inspect(node.WithCtesAndXmlNamespaces, walker.CurrentProcScope);

        public void OnEnterUpdateStatementScope(UpdateStatement node, ScopeChain scopeChain, ModuleWalker walker) =>
            Inspect(node.WithCtesAndXmlNamespaces, walker.CurrentProcScope);

        public void OnEnterDeleteStatementScope(DeleteStatement node, ScopeChain scopeChain, ModuleWalker walker) =>
            Inspect(node.WithCtesAndXmlNamespaces, walker.CurrentProcScope);

        public void OnEnterMergeStatementScope(MergeStatement node, ScopeChain scopeChain, ModuleWalker walker) =>
            Inspect(node.WithCtesAndXmlNamespaces, walker.CurrentProcScope);

        public void OnEnterInsertStatementScope(InsertStatement node, ModuleWalker walker) =>
            Inspect(node.WithCtesAndXmlNamespaces, walker.CurrentProcScope);

        private void Inspect(WithCtesAndXmlNamespaces? withClause, string? procScope)
        {
            if (withClause is null)
            {
                return;
            }

            var mismatches = new List<RecursiveCteTypeMismatch>();
            CteResolver.Resolve(withClause, catalog, resolvedViews, sourcePath, ledger: null, procScope, mismatches);

            foreach (var mismatch in mismatches)
            {
                Findings.Add(new RecursiveCteAnchorTypeMismatchFinding(
                    mismatch.CteName, mismatch.ColumnName, mismatch.AnchorType.ToString(), mismatch.RecursiveType.ToString(),
                    mismatch.SourcePath, mismatch.Line, mismatch.Column));
            }
        }
    }
}
