using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates.Normalization;

namespace SilentScan.Core.Predicates;

public static class NonUniqueUpdateSourceScanner
{
    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    public static IReadOnlyList<NonUniqueUpdateSourceFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
    {
        var rule = CreateRule(parseResult.SourcePath, catalog);
        var walker = new ModuleWalker(parseResult.SourcePath, catalog, EmptyResolvedViews, ledger: null, currentProcScope: null, callerScopeByCalleeScope: null, rules: [rule]);
        parseResult.Fragment.Accept(walker);
    return Harvest(rule);
    }
    internal static Rule CreateRule(string sourcePath, DatabaseCatalog catalog) => new(sourcePath, catalog);

    internal static IReadOnlyList<NonUniqueUpdateSourceFinding> Harvest(Rule rule) =>
            [
            .. rule.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];


    internal sealed class Rule(string sourcePath, DatabaseCatalog catalog) : IModuleRule
    {
        public List<NonUniqueUpdateSourceFinding> Findings { get; } = [];

        public void OnEnterUpdateStatementScope(UpdateStatement node, ScopeChain scopeChain, ModuleWalker walker)
        {
            var spec = node.UpdateSpecification;
            if (spec.FromClause is not null)
            {
                Inspect(spec, walker);
            }
        }

        private void Inspect(UpdateSpecification spec, ModuleWalker walker)
        {
            if (spec.Target is not NamedTableReference targetRef)
            {
                return;
            }

            var targetAlias = targetRef.Alias?.Value ?? targetRef.SchemaObject.BaseIdentifier.Value;

            var (byAlias, ordered) = FromScopeResolver.ResolveForDataModification(spec.Target, spec.FromClause, walker.CurrentResolutionContext());
            if (!byAlias.TryGetValue(targetAlias, out var targetEntry) || targetEntry.Relation.QualifiedName is not { } targetQualifiedName)
            {
                return;
            }

            var scopeChain = new List<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)>
            {
                (byAlias, ordered),
            };

            if (PredicateSurvivalAnalyzer.IsUnsatisfiable(spec.WhereClause?.SearchCondition, columnRef => walker.ResolveColumnFacts(columnRef, scopeChain)))
            {
                return;
            }

            foreach (var join in spec.FromClause!.TableReferences.SelectMany(PredicateTreeWalker.FlattenJoinNodes))
            {
                InspectJoin(join, targetAlias, targetQualifiedName, spec.SetClauses, byAlias);
            }
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
            if (catalog.IdentifierComparer.Equals(firstAlias, targetAlias)
                && secondAlias is not null && byAlias.TryGetValue(secondAlias, out var secondEntry)
                && !secondEntry.IsViewLayer && secondEntry.Relation.QualifiedName is { } secondQualifiedName)
            {
                (sourceAlias, sourceQualifiedName) = (secondAlias, secondQualifiedName);
            }
            else if (catalog.IdentifierComparer.Equals(secondAlias, targetAlias)
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

            var joinColumns = JoinKeyUniqueness.EqualityColumnsQualifiedBy(join.SearchCondition, sourceAlias, catalog.IdentifierComparer);
            if (joinColumns.Count == 0)
            {

                return;
            }

            if (JoinKeyUniqueness.IsProvenUniqueOver(sourceTable, joinColumns, catalog.IdentifierComparer))
            {
                return;
            }

            var setColumnNames = setClauses
                .OfType<AssignmentSetClause>()
                .Where(sc => ReferencesAlias(sc.NewValue, sourceAlias))
                .Select(sc => sc.Column.MultiPartIdentifier.Identifiers[^1].Value)
                .Distinct(catalog.IdentifierComparer)
                .ToList();

            if (setColumnNames.Count == 0)
            {

                return;
            }

            Findings.Add(new NonUniqueUpdateSourceFinding(
                targetQualifiedName, sourceQualifiedName, joinColumns, setColumnNames,
                sourcePath, join.StartLine, join.StartColumn));
        }

        private bool ReferencesAlias(ScalarExpression expression, string alias)
        {
            var collector = new ColumnAliasHelpers.RawColumnReferenceCollector();
            expression.Accept(collector);
            return collector.References.Any(columnRef => ColumnAliasHelpers.ColumnNameIfQualifiedByAlias(columnRef, alias, catalog.IdentifierComparer) is not null);
        }
    }
}
