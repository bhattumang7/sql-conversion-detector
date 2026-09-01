using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class GeneratedAlwaysColumnAssignmentScanner
{
    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    public static IReadOnlyList<GeneratedAlwaysColumnAssignmentFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
    {
        var rule = CreateRule(parseResult.SourcePath, catalog);
        var walker = new ModuleWalker(parseResult.SourcePath, catalog, EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return Harvest(rule);
    }

    internal static Rule CreateRule(string sourcePath, DatabaseCatalog catalog) => new(sourcePath, catalog);

    internal static IReadOnlyList<GeneratedAlwaysColumnAssignmentFinding> Harvest(Rule rule) =>
        [
            .. rule.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];

    internal sealed class Rule(string sourcePath, DatabaseCatalog catalog) : IModuleRule
    {
        public List<GeneratedAlwaysColumnAssignmentFinding> Findings { get; } = [];

        public void OnEnterInsertStatementScope(InsertStatement node, ModuleWalker walker) =>
            InspectInsert(node.InsertSpecification.Target, node.WithCtesAndXmlNamespaces, node.InsertSpecification.Columns, node.InsertSpecification.InsertSource);

        public void OnEnterUpdateStatementScope(UpdateStatement node, ScopeChain scopeChain, ModuleWalker walker) =>
            InspectUpdate(node.UpdateSpecification.Target, node.WithCtesAndXmlNamespaces, node.UpdateSpecification.SetClauses);

        public void OnEnterMergeStatementScope(MergeStatement node, ScopeChain scopeChain, ModuleWalker walker)
        {
            var table = ResolveTarget(node.MergeSpecification.Target, node.WithCtesAndXmlNamespaces);
            if (table is null)
            {
                return;
            }

            foreach (var actionClause in node.MergeSpecification.ActionClauses)
            {
                switch (actionClause.Action)
                {
                    case UpdateMergeAction update:
                        InspectSetClauses(table, update.SetClauses);
                        break;
                    case InsertMergeAction insert:
                        InspectInsertColumns(table, insert.Columns, insert.Source);
                        break;
                }
            }
        }

        private void InspectInsert(TableReference? target, WithCtesAndXmlNamespaces? withCtes, IList<ColumnReferenceExpression> columns, InsertSource insertSource)
        {
            var table = ResolveTarget(target, withCtes);
            if (table is null)
            {
                return;
            }

            InspectInsertColumns(table, columns, insertSource);
        }

        private void InspectInsertColumns(CatalogTable table, IList<ColumnReferenceExpression> columns, InsertSource insertSource)
        {
            if (!table.Columns.Any(c => c.IsGeneratedAlwaysPeriod))
            {
                return;
            }

            var values = insertSource as ValuesInsertSource;

            if (columns.Count > 0)
            {
                InspectExplicitInsertColumnList(table, columns, values);
                return;
            }

            if (values is not null)
            {
                InspectImplicitInsertColumnList(table, values);
            }
        }

        private void InspectExplicitInsertColumnList(CatalogTable table, IList<ColumnReferenceExpression> columns, ValuesInsertSource? values)
        {
            for (var i = 0; i < columns.Count; i++)
            {
                var name = columns[i].MultiPartIdentifier.Identifiers[^1].Value;
                var column = table.FindColumn(name, catalog.IdentifierComparer);
                if (column is not { IsGeneratedAlwaysPeriod: true })
                {
                    continue;
                }

                if (values is null)
                {
                    Report(GeneratedAlwaysColumnAssignmentKind.ExplicitInsertValue, table.QualifiedName, column.Name, columns[i]);
                    continue;
                }

                ReportNonDefaultRowValues(table.QualifiedName, column.Name, values, i);
            }
        }

        private void InspectImplicitInsertColumnList(CatalogTable table, ValuesInsertSource values)
        {
            for (var i = 0; i < table.Columns.Count; i++)
            {
                var column = table.Columns[i];
                if (column.IsGeneratedAlwaysPeriod)
                {
                    ReportNonDefaultRowValues(table.QualifiedName, column.Name, values, i);
                }
            }
        }

        private void ReportNonDefaultRowValues(string tableQualifiedName, string columnName, ValuesInsertSource values, int ordinal)
        {
            foreach (var columnValues in values.RowValues.Select(row => row.ColumnValues))
            {
                if (ordinal < columnValues.Count && columnValues[ordinal] is not DefaultLiteral)
                {
                    Report(GeneratedAlwaysColumnAssignmentKind.ExplicitInsertValue, tableQualifiedName, columnName, columnValues[ordinal]);
                }
            }
        }

        private void InspectUpdate(TableReference? target, WithCtesAndXmlNamespaces? withCtes, IList<SetClause> setClauses)
        {
            var table = ResolveTarget(target, withCtes);
            if (table is null)
            {
                return;
            }

            InspectSetClauses(table, setClauses);
        }

        private void InspectSetClauses(CatalogTable table, IList<SetClause> setClauses)
        {
            foreach (var setClause in setClauses)
            {
                if (setClause is not AssignmentSetClause { Column: { } columnRef })
                {
                    continue;
                }

                var name = columnRef.MultiPartIdentifier.Identifiers[^1].Value;
                var column = table.FindColumn(name, catalog.IdentifierComparer);
                if (column is not { IsGeneratedAlwaysPeriod: true })
                {
                    continue;
                }

                Report(GeneratedAlwaysColumnAssignmentKind.ExplicitUpdateValue, table.QualifiedName, column.Name, columnRef);
            }
        }

        private CatalogTable? ResolveTarget(TableReference? target, WithCtesAndXmlNamespaces? withCtes)
        {
            var qualifiedName = DmlWriteTargetResolver.TryResolve(target, withCtes, catalog);
            return qualifiedName is null ? null : catalog.Find(qualifiedName);
        }

        private void Report(GeneratedAlwaysColumnAssignmentKind kind, string tableQualifiedName, string columnName, TSqlFragment location) =>
            Findings.Add(new GeneratedAlwaysColumnAssignmentFinding(kind, tableQualifiedName, columnName, sourcePath, location.StartLine, location.StartColumn));
    }
}
