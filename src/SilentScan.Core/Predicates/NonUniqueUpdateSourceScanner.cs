using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 2 "UPDATE ... FROM without source uniqueness" - a standalone
/// scanner. Reuses <see cref="Lineage.FromScopeResolver.ResolveForDataModification"/> (the same
/// UPDATE-scope resolution <c>TypedPredicateExtractor</c>/<c>NotInNullableSubqueryScanner</c>
/// already use) and the same JOIN-tree-flattening/AND-only-flattening shape
/// <see cref="PartialCompositeForeignKeyJoinScanner"/> already established for "does a JOIN's own
/// ON clause equate the columns a catalog structure says it needs to."
///
/// Only examines a JOIN where one side is unambiguously the UPDATE's own target (matched by
/// alias, resolved against the same scope every column reference in the statement resolves
/// through) - a join two hops away from the target is a materially different claim this scanner
/// does not make (a known v1 scope limit, not a silently-missed case).
/// </summary>
public static class NonUniqueUpdateSourceScanner
{
    public static IReadOnlyList<NonUniqueUpdateSourceFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
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

    private sealed class Visitor(string sourcePath, DatabaseCatalog catalog) : TSqlFragmentVisitor
    {
        public List<NonUniqueUpdateSourceFinding> Findings { get; } = [];

        public override void ExplicitVisit(UpdateStatement node)
        {
            var spec = node.UpdateSpecification;
            if (spec.FromClause is not null)
            {
                Inspect(spec, node.WithCtesAndXmlNamespaces);
            }

            base.ExplicitVisit(node);
        }

        private void Inspect(UpdateSpecification spec, WithCtesAndXmlNamespaces? withClause)
        {
            if (spec.Target is not NamedTableReference targetRef)
            {
                return;
            }

            var targetAlias = targetRef.Alias?.Value ?? targetRef.SchemaObject.BaseIdentifier.Value;

            var (byAlias, _) = FromScopeResolver.ResolveForDataModification(spec.Target, spec.FromClause, ResolutionContext(withClause));
            if (!byAlias.TryGetValue(targetAlias, out var targetEntry) || targetEntry.Relation.QualifiedName is not { } targetQualifiedName)
            {
                return;
            }

            foreach (var join in spec.FromClause!.TableReferences.SelectMany(PredicateTreeWalker.FlattenJoinNodes))
            {
                InspectJoin(join, targetAlias, targetQualifiedName, spec.SetClauses, byAlias);
            }
        }

        /// <summary>
        /// A CTE is never schema-qualified, so it always shadows a same-named real base table for
        /// its statement's own lifetime, including an updatable-CTE UPDATE target - resolving
        /// through the catalog instead (cteRelations always null, pre-fix) silently matched the
        /// target against an unrelated real table sharing the CTE's name (2026-08 audit).
        /// </summary>
        private FromScopeResolver.ResolutionContext ResolutionContext(WithCtesAndXmlNamespaces? withClause) =>
            new(catalog, EmptyResolvedViews, sourcePath, Ledger: null, CteResolver.Resolve(withClause, catalog, EmptyResolvedViews, sourcePath, ledger: null), ProcScope: null);

        private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

        /// <summary>
        /// The alias a <see cref="NamedTableReference"/> is known by in its own FROM clause -
        /// matches <see cref="ExplicitVisit(UpdateStatement)"/>'s own <c>targetAlias</c> extraction.
        /// Any other <see cref="TableReference"/> shape (subquery, TVF, another nested JOIN) is
        /// unresolvable to a single alias here and correctly falls through to null - a known,
        /// stated v1 scope limit (a join two hops from the target, or through something other than
        /// a direct base table, is a materially different claim this scanner does not make).
        /// </summary>
        private static string? AliasOf(TableReference reference) =>
            reference is NamedTableReference named ? named.Alias?.Value ?? named.SchemaObject.BaseIdentifier.Value : null;

        private void InspectJoin(
            QualifiedJoin join, string targetAlias, string targetQualifiedName,
            IList<SetClause> setClauses, Dictionary<string, ScopeEntry> byAlias)
        {
            var firstAlias = AliasOf(join.FirstTableReference);
            var secondAlias = AliasOf(join.SecondTableReference);

            string sourceAlias, sourceQualifiedName;
            if (string.Equals(firstAlias, targetAlias, StringComparison.OrdinalIgnoreCase)
                && secondAlias is not null && byAlias.TryGetValue(secondAlias, out var secondEntry)
                && !secondEntry.IsViewLayer && secondEntry.Relation.QualifiedName is { } secondQualifiedName)
            {
                (sourceAlias, sourceQualifiedName) = (secondAlias, secondQualifiedName);
            }
            else if (string.Equals(secondAlias, targetAlias, StringComparison.OrdinalIgnoreCase)
                && firstAlias is not null && byAlias.TryGetValue(firstAlias, out var firstEntry)
                && !firstEntry.IsViewLayer && firstEntry.Relation.QualifiedName is { } firstQualifiedName)
            {
                (sourceAlias, sourceQualifiedName) = (firstAlias, firstQualifiedName);
            }
            else
            {
                return;
            }

            var sourceTable = catalog.Find(sourceQualifiedName);
            if (sourceTable is null)
            {
                return;
            }

            var joinColumns = JoinKeyUniqueness.EqualityColumnsQualifiedBy(join.SearchCondition, sourceAlias);
            if (joinColumns.Count == 0)
            {
                // No equality this scanner could resolve back to the source alias's own columns -
                // could be a non-equality join predicate, or a column this pass couldn't resolve.
                // Left unanalyzed rather than guessed at either way.
                return;
            }

            if (JoinKeyUniqueness.IsProvenUniqueOver(sourceTable, joinColumns))
            {
                return;
            }

            var setColumnNames = setClauses
                .OfType<AssignmentSetClause>()
                .Where(sc => ReferencesAlias(sc.NewValue, sourceAlias))
                .Select(sc => sc.Column.MultiPartIdentifier.Identifiers[^1].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (setColumnNames.Count == 0)
            {
                // The join exists but the SET clause never reads from this source - no observable
                // risk, since which of the matching source rows "won" changes nothing.
                return;
            }

            Findings.Add(new NonUniqueUpdateSourceFinding(
                targetQualifiedName, sourceQualifiedName, joinColumns, setColumnNames,
                sourcePath, join.StartLine, join.StartColumn));
        }

        private static bool ReferencesAlias(ScalarExpression expression, string alias)
        {
            var collector = new ColumnAliasHelpers.RawColumnReferenceCollector();
            expression.Accept(collector);
            return collector.References.Any(columnRef => ColumnAliasHelpers.ColumnNameIfQualifiedByAlias(columnRef, alias) is not null);
        }
    }
}
