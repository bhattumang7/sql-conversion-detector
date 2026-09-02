using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Common;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates.Normalization;

namespace SilentScan.Core.Predicates;

public static class ViewCheckOptionContradictionScanner
{
    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    public static IReadOnlyList<ViewCheckOptionContradictionFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog, IReadOnlyList<ViewDefinition> views)
    {
        var rule = CreateRule(parseResult.SourcePath, catalog, views);
        var walker = new ModuleWalker(parseResult.SourcePath, catalog, EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return Harvest(rule);
    }

    internal static Rule CreateRule(string sourcePath, DatabaseCatalog catalog, IReadOnlyList<ViewDefinition> views) => new(sourcePath, catalog, views);

    internal static IReadOnlyList<ViewCheckOptionContradictionFinding> Harvest(Rule rule) =>
        [
            .. rule.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];

    internal sealed class Rule : IModuleRule
    {
        private readonly Dictionary<string, (string ColumnName, NumericValueRangeSet Domain)> _checkOptionDomains;
        private readonly StringComparer _identifierComparer;

        public List<ViewCheckOptionContradictionFinding> Findings { get; } = [];

        public Rule(string sourcePath, DatabaseCatalog catalog, IReadOnlyList<ViewDefinition> views)
        {
            SourcePath = sourcePath;
            _identifierComparer = catalog.IdentifierComparer;
            _checkOptionDomains = BuildCheckOptionDomains(views, _identifierComparer);
        }

        private string SourcePath { get; }

        public void OnEnterInsertStatementScope(InsertStatement node, ModuleWalker walker)
        {
            if (node.InsertSpecification.Target is not NamedTableReference target
                || node.InsertSpecification.InsertSource is not ValuesInsertSource valuesSource
                || node.InsertSpecification.Columns.Count == 0)
            {
                return;
            }

            var qualifiedName = SchemaObjectNameHelper.Qualify(target.SchemaObject);
            if (!_checkOptionDomains.TryGetValue(qualifiedName, out var entry))
            {
                return;
            }

            var columnIndex = IndexOfColumn(node.InsertSpecification.Columns, entry.ColumnName, _identifierComparer);
            if (columnIndex < 0)
            {
                return;
            }

            foreach (var rowValue in valuesSource.RowValues)
            {
                if (columnIndex >= rowValue.ColumnValues.Count)
                {
                    continue;
                }

                ReportIfContradicts(qualifiedName, entry.ColumnName, entry.Domain, rowValue.ColumnValues[columnIndex], rowValue);
            }
        }

        public void OnEnterUpdateStatementScope(UpdateStatement node, ScopeChain scopeChain, ModuleWalker walker)
        {
            if (node.UpdateSpecification.Target is not NamedTableReference target)
            {
                return;
            }

            var qualifiedName = SchemaObjectNameHelper.Qualify(target.SchemaObject);
            if (!_checkOptionDomains.TryGetValue(qualifiedName, out var entry))
            {
                return;
            }

            foreach (var setClause in node.UpdateSpecification.SetClauses)
            {
                if (setClause is not AssignmentSetClause { AssignmentKind: AssignmentKind.Equals } assignment
                    || assignment.Column is not { MultiPartIdentifier.Identifiers: { Count: > 0 } identifiers }
                    || !_identifierComparer.Equals(identifiers[^1].Value, entry.ColumnName))
                {
                    continue;
                }

                ReportIfContradicts(qualifiedName, entry.ColumnName, entry.Domain, assignment.NewValue, assignment);
            }
        }

        private void ReportIfContradicts(string viewQualifiedName, string columnName, NumericValueRangeSet domain, ScalarExpression valueExpression, TSqlFragment locationNode)
        {
            if (CheckConstraintDomainFolder.TryGetNumericLiteral(valueExpression) is not { } literal)
            {
                return;
            }

            if (!NumericValueRangeSet.ForEquals(literal).Intersect(domain).IsEmpty)
            {
                return;
            }

            Findings.Add(new ViewCheckOptionContradictionFinding(
                viewQualifiedName, columnName, SourcePath, locationNode.StartLine, locationNode.StartColumn));
        }

        private static int IndexOfColumn(IList<ColumnReferenceExpression> columns, string columnName, StringComparer comparer)
        {
            for (var i = 0; i < columns.Count; i++)
            {
                var identifiers = columns[i].MultiPartIdentifier?.Identifiers;
                if (identifiers is { Count: > 0 } && comparer.Equals(identifiers[^1].Value, columnName))
                {
                    return i;
                }
            }

            return -1;
        }

        private static Dictionary<string, (string ColumnName, NumericValueRangeSet Domain)> BuildCheckOptionDomains(IReadOnlyList<ViewDefinition> views, StringComparer comparer)
        {
            var domains = new Dictionary<string, (string, NumericValueRangeSet)>(comparer);

            foreach (var view in views)
            {
                if (!view.WithCheckOption
                    || view.SelectStatement.QueryExpression is not QuerySpecification { WhereClause.SearchCondition: { } condition })
                {
                    continue;
                }

                var referencedColumns = new HashSet<string>(comparer);
                condition.Accept(new ColumnNameCollector(referencedColumns));
                if (referencedColumns.Count != 1)
                {
                    continue;
                }

                var columnName = referencedColumns.Single();
                var domain = CheckConstraintDomainFolder.TryBuildRangeSet(condition, columnName, comparer);
                if (domain is null)
                {
                    continue;
                }

                domains[view.QualifiedName] = (columnName, domain);
            }

            return domains;
        }

        private sealed class ColumnNameCollector(HashSet<string> names) : TSqlFragmentVisitor
        {
            public override void ExplicitVisit(ColumnReferenceExpression node)
            {
                var identifiers = node.MultiPartIdentifier?.Identifiers;
                if (identifiers is { Count: > 0 })
                {
                    names.Add(identifiers[^1].Value);
                }

                base.ExplicitVisit(node);
            }
        }
    }
}
