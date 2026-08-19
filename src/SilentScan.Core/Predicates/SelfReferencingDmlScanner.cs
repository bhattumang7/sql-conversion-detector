using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 2 "Halloween Protection and self-referencing DML". Reuses
/// <see cref="FromScopeResolver.ResolveForDataModification"/>/<see cref="FromScopeResolver.ResolveForMerge"/>
/// (the same UPDATE/MERGE-scope resolution <see cref="TypedPredicateExtractor"/>/
/// <see cref="NonUniqueUpdateSourceScanner"/> already use) purely to learn the write target's own
/// resolved qualified name and FROM-clause alias - never for column resolution.
/// </summary>
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
            if (spec.Target is NamedTableReference targetRef)
            {
                var targetQualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(targetRef.SchemaObject));

                // ValuesInsertSource/ExecuteInsertSource carry no table reference of their own to
                // re-read the target through - only a SELECT-sourced INSERT can self-reference.
                var match = spec.InsertSource is SelectInsertSource select ? FindMatchInFragment(select.Select, targetQualifiedName) : null;
                Report(match, "INSERT", targetQualifiedName, node);
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(UpdateStatement node)
        {
            var spec = node.UpdateSpecification;
            if (ResolveDataModificationTarget(spec.Target, spec.FromClause, node.WithCtesAndXmlNamespaces) is { } targetQualifiedName)
            {
                var match = FindMatchInFromClauseExtras(spec.FromClause, spec.Target, targetQualifiedName)
                    ?? FindMatchInFragment(spec.WhereClause, targetQualifiedName)
                    ?? spec.SetClauses.OfType<AssignmentSetClause>()
                        .Select(sc => FindMatchInFragment(sc.NewValue, targetQualifiedName))
                        .FirstOrDefault(m => m is not null);
                Report(match, "UPDATE", targetQualifiedName, node);
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(DeleteStatement node)
        {
            var spec = node.DeleteSpecification;
            if (ResolveDataModificationTarget(spec.Target, spec.FromClause, node.WithCtesAndXmlNamespaces) is { } targetQualifiedName)
            {
                var match = FindMatchInFromClauseExtras(spec.FromClause, spec.Target, targetQualifiedName)
                    ?? FindMatchInFragment(spec.WhereClause, targetQualifiedName);
                Report(match, "DELETE", targetQualifiedName, node);
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(MergeStatement node)
        {
            var spec = node.MergeSpecification;
            if (ResolveMergeTarget(spec, node.WithCtesAndXmlNamespaces) is { } targetQualifiedName)
            {
                var match = FindMatchInFragment(spec.TableReference, targetQualifiedName)
                    ?? FindMatchInFragment(spec.SearchCondition, targetQualifiedName)
                    ?? spec.ActionClauses
                        .Select(actionClause => FindMatchInFragment(actionClause, targetQualifiedName))
                        .FirstOrDefault(m => m is not null);
                Report(match, "MERGE", targetQualifiedName, node);
            }

            base.ExplicitVisit(node);
        }

        private void Report(SelfReferencingDmlFinding? match, string statementKind, string targetQualifiedName, TSqlFragment locationNode)
        {
            if (match is { } finding)
            {
                Findings.Add(finding with { StatementKind = statementKind, TargetTableQualifiedName = targetQualifiedName, Line = locationNode.StartLine, Column = locationNode.StartColumn });
            }
        }

        /// <summary>Target resolution shared by UPDATE/DELETE - with an extra FROM clause, T-SQL requires the target's own alias to already be one of its entries (<see cref="FromScopeResolver.ResolveForDataModification"/>'s own doc comment); with none, the target IS the whole scope.</summary>
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

        /// <summary>MergeSpecification's own inherited <c>Target</c> is the INTO target; its alias lives separately in <c>TableAlias</c> (see <see cref="FromScopeResolver.ResolveForMerge"/>'s own doc comment on this naming trap).</summary>
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

        /// <summary>
        /// A CTE name shadows a same-named base table for its own statement's lifetime, including
        /// an updatable-CTE write target (<c>WITH cte AS (...) UPDATE cte SET ...</c> is valid
        /// T-SQL) - resolving through the catalog instead silently matched an unrelated real
        /// table sharing the CTE's name (2026-08 audit).
        /// </summary>
        private FromScopeResolver.ResolutionContext ResolutionContext(WithCtesAndXmlNamespaces? withClause) =>
            new(catalog, EmptyResolvedViews, sourcePath, Ledger: null, CteResolver.Resolve(withClause, catalog, EmptyResolvedViews, sourcePath, ledger: null), ProcScope: null);

        private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

        /// <summary>
        /// Walks the target's OWN FROM clause for a SECOND, independent reference to the same
        /// table - the target's own single canonical entry (matched by alias, which T-SQL forbids
        /// duplicating within one FROM clause) is skipped exactly once so it is never mistaken for
        /// a re-read of itself; every other entry is a genuine extra read.
        /// </summary>
        private SelfReferencingDmlFinding? FindMatchInFromClauseExtras(FromClause? fromClause, TableReference target, string targetQualifiedName)
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
                if (!skippedTargetEntry && string.Equals(alias, targetAlias, StringComparison.OrdinalIgnoreCase))
                {
                    skippedTargetEntry = true;
                    continue;
                }

                if (TryClassify(reference, targetQualifiedName) is { } finding)
                {
                    return finding;
                }
            }

            return null;
        }

        /// <summary>Walks an arbitrary read-side fragment (a subquery, a SET-clause new value, a MERGE action body) with no skip logic at all - the write target's own reference is never part of any of these fragments, so every match found here is a genuine, unambiguous extra read.</summary>
        private SelfReferencingDmlFinding? FindMatchInFragment(TSqlFragment? fragment, string targetQualifiedName)
        {
            if (fragment is null)
            {
                return null;
            }

            var collector = new NamedTableReferenceCollector();
            fragment.Accept(collector);

            return collector.References
                .Select(reference => TryClassify(reference, targetQualifiedName))
                .FirstOrDefault(finding => finding is not null);
        }

        private SelfReferencingDmlFinding? TryClassify(NamedTableReference reference, string targetQualifiedName)
        {
            var qualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(reference.SchemaObject));

            if (string.Equals(qualifiedName, targetQualifiedName, StringComparison.OrdinalIgnoreCase))
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
