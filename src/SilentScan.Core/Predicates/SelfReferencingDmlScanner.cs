using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Common;

namespace SilentScan.Core.Predicates;

public static class SelfReferencingDmlScanner
{
    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    public static IReadOnlyList<SelfReferencingDmlFinding> Scan(
        SqlParseResult parseResult, DatabaseCatalog catalog, IReadOnlyDictionary<string, ViewExpansionOrigin> viewExpansionMap)
    {
        var visitor = new Visitor(parseResult.SourcePath, catalog, viewExpansionMap);
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
    private sealed class Visitor(string sourcePath, DatabaseCatalog catalog, IReadOnlyDictionary<string, ViewExpansionOrigin> viewExpansionMap)
        : ScopedSqlVisitorBase(sourcePath, catalog, EmptyResolvedViews, ledger: null, currentProcScope: null, callerScopeByCalleeScope: null)
#pragma warning restore CS9107
    {
        public List<SelfReferencingDmlFinding> Findings { get; } = [];

        protected override void OnInsertStatementScope(InsertStatement node, Action continueDescent)
        {
            var spec = node.InsertSpecification;
            if (spec.Target is NamedTableReference targetRef && !HasLiteralTopOne(spec.TopRowFilter))
            {
                var targetQualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(targetRef.SchemaObject));
                var cteNames = CteNamesOf(node.WithCtesAndXmlNamespaces);

                var match = spec.InsertSource is SelectInsertSource select ? FindMatchInFragment(select.Select, targetQualifiedName, cteNames) : null;
                Report(match, "INSERT", targetQualifiedName, node);
            }

            continueDescent();
        }

        protected override void OnUpdateStatementScope(UpdateStatement node, ScopeChain scopeChain, Action continueDescent)
        {
            var spec = node.UpdateSpecification;
            var updateTargetAlias = (spec.Target as NamedTableReference)?.Alias?.Value ?? (spec.Target as NamedTableReference)?.SchemaObject.BaseIdentifier.Value;
            if (!HasLiteralTopOne(spec.TopRowFilter)
                && ResolveTargetQualifiedName(spec.Target, updateTargetAlias, scopeChain) is { } targetQualifiedName)
            {
                var cteNames = CteNamesOf(node.WithCtesAndXmlNamespaces);
                var match = FindMatchInFromClauseExtras(spec.FromClause, spec.Target, targetQualifiedName, cteNames)
                    ?? FindMatchInFragment(spec.WhereClause, targetQualifiedName, cteNames)
                    ?? spec.SetClauses.OfType<AssignmentSetClause>()
                        .Select(sc => FindMatchInFragment(sc.NewValue, targetQualifiedName, cteNames))
                        .FirstOrDefault(m => m is not null);
                Report(match, "UPDATE", targetQualifiedName, node);
            }

            continueDescent();
        }

        protected override void OnDeleteStatementScope(DeleteStatement node, ScopeChain scopeChain, Action continueDescent)
        {
            var spec = node.DeleteSpecification;
            var deleteTargetAlias = (spec.Target as NamedTableReference)?.Alias?.Value ?? (spec.Target as NamedTableReference)?.SchemaObject.BaseIdentifier.Value;
            if (!HasLiteralTopOne(spec.TopRowFilter)
                && ResolveTargetQualifiedName(spec.Target, deleteTargetAlias, scopeChain) is { } targetQualifiedName)
            {
                var cteNames = CteNamesOf(node.WithCtesAndXmlNamespaces);
                var match = FindMatchInFromClauseExtras(spec.FromClause, spec.Target, targetQualifiedName, cteNames)
                    ?? FindMatchInFragment(spec.WhereClause, targetQualifiedName, cteNames);
                Report(match, "DELETE", targetQualifiedName, node);
            }

            continueDescent();
        }

        protected override void OnMergeStatementScope(MergeStatement node, ScopeChain scopeChain, Action continueDescent)
        {
            var spec = node.MergeSpecification;
            var mergeTargetAlias = spec.TableAlias?.Value ?? (spec.Target as NamedTableReference)?.SchemaObject.BaseIdentifier.Value;
            if (!HasLiteralTopOne(spec.TopRowFilter)
                && ResolveTargetQualifiedName(spec.Target, mergeTargetAlias, scopeChain) is { } targetQualifiedName)
            {
                var cteNames = CteNamesOf(node.WithCtesAndXmlNamespaces);
                var match = FindMatchInFragment(spec.TableReference, targetQualifiedName, cteNames)
                    ?? FindMatchInFragment(spec.SearchCondition, targetQualifiedName, cteNames)
                    ?? spec.ActionClauses
                        .Select(actionClause => FindMatchInFragment(actionClause, targetQualifiedName, cteNames))
                        .FirstOrDefault(m => m is not null);
                Report(match, "MERGE", targetQualifiedName, node);
            }

            continueDescent();
        }

        private static bool HasLiteralTopOne(TopRowFilter? topRowFilter)
        {
            if (topRowFilter is not { Percent: false } filter)
            {
                return false;
            }

            var expression = filter.Expression;
            while (expression is ParenthesisExpression parenthesized)
            {
                expression = parenthesized.Expression;
            }

            return expression is IntegerLiteral { Value: "1" };
        }

        private HashSet<string> CteNamesOf(WithCtesAndXmlNamespaces? withClause) =>
            CteNameHelper.Names(withClause, catalog.IdentifierComparer);

        private void Report(SelfReferencingDmlFinding? match, string statementKind, string targetQualifiedName, TSqlFragment locationNode)
        {
            if (match is { } finding)
            {
                Findings.Add(finding with { StatementKind = statementKind, TargetTableQualifiedName = targetQualifiedName, Line = locationNode.StartLine, Column = locationNode.StartColumn });
            }
        }

        private static string? ResolveTargetQualifiedName(TableReference target, string? alias, ScopeChain scopeChain)
        {
            if (target is not NamedTableReference || alias is null || scopeChain.Count == 0)
            {
                return null;
            }

            return scopeChain[0].ByAlias.TryGetValue(alias, out var entry) ? entry.Relation.QualifiedName : null;
        }

        private SelfReferencingDmlFinding? FindMatchInFromClauseExtras(FromClause? fromClause, TableReference target, string targetQualifiedName, HashSet<string> cteNames)
        {
            if (fromClause is null)
            {
                return null;
            }

            var targetAlias = target is NamedTableReference named ? named.Alias?.Value ?? named.SchemaObject.BaseIdentifier.Value : null;

            var collector = new NamedTableReferenceCollector();
            fromClause.Accept(collector);

            var skippedTargetEntry = false;
            foreach (var reference in collector.References)
            {
                var alias = reference.Alias?.Value ?? reference.SchemaObject.BaseIdentifier.Value;
                if (!skippedTargetEntry && catalog.IdentifierComparer.Equals(alias, targetAlias))
                {
                    skippedTargetEntry = true;
                    continue;
                }

                if (TryClassify(reference, targetQualifiedName, cteNames) is { } finding)
                {
                    return finding;
                }
            }

            return null;
        }

        private SelfReferencingDmlFinding? FindMatchInFragment(TSqlFragment? fragment, string targetQualifiedName, HashSet<string> cteNames)
        {
            if (fragment is null)
            {
                return null;
            }

            var collector = new NamedTableReferenceCollector();
            fragment.Accept(collector);

            return collector.References
                .Select(reference => TryClassify(reference, targetQualifiedName, cteNames))
                .FirstOrDefault(finding => finding is not null);
        }

        private SelfReferencingDmlFinding? TryClassify(NamedTableReference reference, string targetQualifiedName, HashSet<string> cteNames)
        {
            if (reference.SchemaObject.SchemaIdentifier is null && cteNames.Contains(reference.SchemaObject.BaseIdentifier.Value))
            {
                return null;
            }

            var qualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(reference.SchemaObject));

            if (catalog.IdentifierComparer.Equals(qualifiedName, targetQualifiedName))
            {
                return new SelfReferencingDmlFinding(
                    SelfReferencingDmlFindingKind.DirectTableReference, StatementKind: "", targetQualifiedName, qualifiedName,
                    sourcePath, Line: 0, Column: 0);
            }

            if (catalog.Find(qualifiedName) is null
                && viewExpansionMap.TryGetValue(qualifiedName, out var origin)
                && origin.BaseTables.Contains(targetQualifiedName))
            {
                return new SelfReferencingDmlFinding(
                    SelfReferencingDmlFindingKind.ThroughView, StatementKind: "", targetQualifiedName, qualifiedName,
                    sourcePath, Line: 0, Column: 0);
            }

            return null;
        }

        private sealed class NamedTableReferenceCollector : TSqlFragmentVisitor
        {
            public List<NamedTableReference> References { get; } = [];

            public override void ExplicitVisit(NamedTableReference node)
            {
                References.Add(node);
                base.ExplicitVisit(node);
            }
        }
    }
}
