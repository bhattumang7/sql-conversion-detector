using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Common;

namespace SilentScan.Core.Predicates;

public static class SelfReferencingDmlScanner
{
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

    private sealed class Visitor(string sourcePath, DatabaseCatalog catalog, IReadOnlyDictionary<string, ViewExpansionOrigin> viewExpansionMap) : TSqlFragmentVisitor
    {
        public List<SelfReferencingDmlFinding> Findings { get; } = [];

        public override void ExplicitVisit(InsertStatement node)
        {
            var spec = node.InsertSpecification;
            if (spec.Target is NamedTableReference targetRef && !HasLiteralTopOne(spec.TopRowFilter))
            {
                var targetQualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(targetRef.SchemaObject));
                var cteNames = CteNamesOf(node.WithCtesAndXmlNamespaces);

                var match = spec.InsertSource is SelectInsertSource select ? FindMatchInFragment(select.Select, targetQualifiedName, cteNames) : null;
                Report(match, "INSERT", targetQualifiedName, node);
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(UpdateStatement node)
        {
            var spec = node.UpdateSpecification;
            if (!HasLiteralTopOne(spec.TopRowFilter)
                && ResolveDataModificationTarget(spec.Target, spec.FromClause, node.WithCtesAndXmlNamespaces) is { } targetQualifiedName)
            {
                var cteNames = CteNamesOf(node.WithCtesAndXmlNamespaces);
                var match = FindMatchInFromClauseExtras(spec.FromClause, spec.Target, targetQualifiedName, cteNames)
                    ?? FindMatchInFragment(spec.WhereClause, targetQualifiedName, cteNames)
                    ?? spec.SetClauses.OfType<AssignmentSetClause>()
                        .Select(sc => FindMatchInFragment(sc.NewValue, targetQualifiedName, cteNames))
                        .FirstOrDefault(m => m is not null);
                Report(match, "UPDATE", targetQualifiedName, node);
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(DeleteStatement node)
        {
            var spec = node.DeleteSpecification;
            if (!HasLiteralTopOne(spec.TopRowFilter)
                && ResolveDataModificationTarget(spec.Target, spec.FromClause, node.WithCtesAndXmlNamespaces) is { } targetQualifiedName)
            {
                var cteNames = CteNamesOf(node.WithCtesAndXmlNamespaces);
                var match = FindMatchInFromClauseExtras(spec.FromClause, spec.Target, targetQualifiedName, cteNames)
                    ?? FindMatchInFragment(spec.WhereClause, targetQualifiedName, cteNames);
                Report(match, "DELETE", targetQualifiedName, node);
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(MergeStatement node)
        {
            var spec = node.MergeSpecification;
            if (!HasLiteralTopOne(spec.TopRowFilter)
                && ResolveMergeTarget(spec, node.WithCtesAndXmlNamespaces) is { } targetQualifiedName)
            {
                var cteNames = CteNamesOf(node.WithCtesAndXmlNamespaces);
                var match = FindMatchInFragment(spec.TableReference, targetQualifiedName, cteNames)
                    ?? FindMatchInFragment(spec.SearchCondition, targetQualifiedName, cteNames)
                    ?? spec.ActionClauses
                        .Select(actionClause => FindMatchInFragment(actionClause, targetQualifiedName, cteNames))
                        .FirstOrDefault(m => m is not null);
                Report(match, "MERGE", targetQualifiedName, node);
            }

            base.ExplicitVisit(node);
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

        private string? ResolveDataModificationTarget(TableReference target, FromClause? fromClause, WithCtesAndXmlNamespaces? withClause)
        {
            if (target is not NamedTableReference named)
            {
                return null;
            }

            var alias = named.Alias?.Value ?? named.SchemaObject.BaseIdentifier.Value;
            var (byAlias, _) = FromScopeResolver.ResolveForDataModification(target, fromClause, ResolutionContext(withClause));
            return byAlias.TryGetValue(alias, out var entry) ? entry.Relation.QualifiedName : null;
        }

        private string? ResolveMergeTarget(MergeSpecification spec, WithCtesAndXmlNamespaces? withClause)
        {
            if (spec.Target is not NamedTableReference named)
            {
                return null;
            }

            var alias = spec.TableAlias?.Value ?? named.SchemaObject.BaseIdentifier.Value;
            var (byAlias, _) = FromScopeResolver.ResolveForMerge(spec.Target, spec.TableAlias, spec.TableReference, ResolutionContext(withClause));
            return byAlias.TryGetValue(alias, out var entry) ? entry.Relation.QualifiedName : null;
        }

        private FromScopeResolver.ResolutionContext ResolutionContext(WithCtesAndXmlNamespaces? withClause) =>
            new(catalog, EmptyResolvedViews, sourcePath, Ledger: null, CteResolver.Resolve(withClause, catalog, EmptyResolvedViews, sourcePath, ledger: null), ProcScope: null);

        private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

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
