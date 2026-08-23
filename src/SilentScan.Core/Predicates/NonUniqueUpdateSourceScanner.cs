using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates.Normalization;

namespace SilentScan.Core.Predicates;

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

            var (byAlias, ordered) = FromScopeResolver.ResolveForDataModification(spec.Target, spec.FromClause, ResolutionContext(withClause));
            if (!byAlias.TryGetValue(targetAlias, out var targetEntry) || targetEntry.Relation.QualifiedName is not { } targetQualifiedName)
            {
                return;
            }

            var scopeChain = new List<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)>
            {
                (byAlias, ordered),
            };

            if (PredicateSurvivalAnalyzer.IsUnsatisfiable(spec.WhereClause?.SearchCondition, columnRef => ResolveColumnFacts(columnRef, scopeChain)))
            {
                return;
            }

            foreach (var join in spec.FromClause!.TableReferences.SelectMany(PredicateTreeWalker.FlattenJoinNodes))
            {
                InspectJoin(join, targetAlias, targetQualifiedName, spec.SetClauses, byAlias);
            }
        }

        private FromScopeResolver.ResolutionContext ResolutionContext(WithCtesAndXmlNamespaces? withClause) =>
            new(catalog, EmptyResolvedViews, sourcePath, Ledger: null, CteResolver.Resolve(withClause, catalog, EmptyResolvedViews, sourcePath, ledger: null), ProcScope: null);

        private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

        private PredicateSurvivalAnalyzer.ColumnFacts ResolveColumnFacts(
            ColumnReferenceExpression columnRef, IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain)
        {
            if (ScalarExpressionResolver.ResolveColumnReference(columnRef, scopeChain, sourcePath, ledger: null) is not ColumnProvenance.BaseColumn baseColumn)
            {
                return default;
            }

            var catalogColumn = catalog.Find(baseColumn.TableQualifiedName)?.FindColumn(baseColumn.ColumnName);
            return new PredicateSurvivalAnalyzer.ColumnFacts(
                catalogColumn is null ? null : !catalogColumn.IsNullable,
                baseColumn.Type?.Collation?.IsCaseSensitive);
        }

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
