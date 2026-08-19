using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md "DBA-script family sweep (2026-08-17)" §A "float/real as an
/// equality-predicate target" - see <see cref="FloatEqualityFinding"/> for the full scope/
/// precision story, including why this is a standalone type/scanner rather than folded into
/// <see cref="TypedPredicateExtractor"/>'s type-conversion-verdict machinery.
///
/// Reuses <see cref="DirectBaseTableResolver"/>'s "flatten the join tree to its direct base-table
/// leaves, matched by alias" shape rather than the full <see cref="Lineage.FromScopeResolver"/>
/// scope-chain/lineage machinery - a real, known v1 scope limit (a float/real predicate reached
/// through a view/CTE/derived table is left unanalyzed, not guessed at).
/// </summary>
public static class FloatEqualityPredicateScanner
{
    public static IReadOnlyList<FloatEqualityFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
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
        public List<FloatEqualityFinding> Findings { get; } = [];

        public override void ExplicitVisit(QuerySpecification node)
        {
            var tables = DirectBaseTableResolver.ResolveDirectBaseTables(catalog, node.FromClause?.TableReferences);
            // node.WhereClause.SearchCondition is null for a positioned "WHERE CURRENT OF
            // @cursor" - a WhereClause with no boolean search condition at all, not a normal
            // filter predicate - so this checks the condition itself, not just the clause.
            if (node.WhereClause?.SearchCondition is { } whereCondition)
            {
                Inspect(whereCondition, tables);
            }

            InspectJoinOnClauses(node.FromClause?.TableReferences, tables);

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(UpdateStatement node)
        {
            var spec = node.UpdateSpecification;
            var tables = DirectBaseTableResolver.ResolveDirectBaseTables(catalog, spec.FromClause?.TableReferences, spec.Target);
            // See ExplicitVisit(QuerySpecification)'s own comment - a positioned "WHERE CURRENT
            // OF @cursor" carries a null SearchCondition.
            if (spec.WhereClause?.SearchCondition is { } whereCondition)
            {
                Inspect(whereCondition, tables);
            }

            InspectJoinOnClauses(spec.FromClause?.TableReferences, tables);

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(DeleteStatement node)
        {
            var spec = node.DeleteSpecification;
            var tables = DirectBaseTableResolver.ResolveDirectBaseTables(catalog, spec.FromClause?.TableReferences, spec.Target);
            // See ExplicitVisit(QuerySpecification)'s own comment - a positioned "WHERE CURRENT
            // OF @cursor" carries a null SearchCondition.
            if (spec.WhereClause?.SearchCondition is { } whereCondition)
            {
                Inspect(whereCondition, tables);
            }

            InspectJoinOnClauses(spec.FromClause?.TableReferences, tables);

            base.ExplicitVisit(node);
        }

        /// <summary>
        /// A JOIN's own ON clause is a filter position exactly like WHERE - inspected here, against
        /// the same direct-base-table scope already resolved for the whole FROM clause, rather than
        /// via a separate <see cref="TSqlFragmentVisitor.ExplicitVisit(QualifiedJoin)"/> override
        /// (which would need its own copy of the enclosing scope threaded through a field/stack for
        /// no benefit, since every join in one FROM clause shares the identical <paramref
        /// name="tables"/> scope this method already has in hand).
        /// </summary>
        private void InspectJoinOnClauses(IList<TableReference>? tableReferences, Dictionary<string, CatalogTable> tables)
        {
            if (tableReferences is null || tables.Count == 0)
            {
                return;
            }

            foreach (var reference in tableReferences)
            {
                foreach (var join in PredicateTreeWalker.FlattenJoinNodes(reference).Where(j => j.SearchCondition is not null))
                {
                    Inspect(join.SearchCondition!, tables);
                }
            }
        }

        private void Inspect(BooleanExpression searchCondition, Dictionary<string, CatalogTable> tables)
        {
            if (tables.Count == 0)
            {
                return;
            }

            var collector = new EqualityCollector();
            searchCondition.Accept(collector);
            foreach (var comparison in collector.Comparisons)
            {
                InspectEquality(comparison, tables);
            }
        }

        private void InspectEquality(BooleanComparisonExpression comparison, Dictionary<string, CatalogTable> tables)
        {
            foreach (var side in new[] { comparison.FirstExpression, comparison.SecondExpression })
            {
                if (side is not ColumnReferenceExpression columnRef
                    || DirectBaseTableResolver.TryResolveColumn(columnRef, tables) is not { } resolved
                    || resolved.Column.Type?.Category is not (SqlTypeCategory.Real or SqlTypeCategory.Float))
                {
                    continue;
                }

                Findings.Add(new FloatEqualityFinding(
                    resolved.Table.QualifiedName,
                    resolved.Column.Name,
                    resolved.Column.Type!.ToString(),
                    sourcePath,
                    comparison.StartLine,
                    comparison.StartColumn));

                // One finding per predicate site, even when both sides resolve to a float/real
                // column (Col1 = Col2 between two such columns) - the site itself is the unit of
                // reporting, not each operand independently.
                return;
            }
        }

        /// <summary>
        /// Collects every <see cref="BooleanComparisonExpression"/> reachable from a search
        /// condition WITHOUT descending into a nested <see cref="QuerySpecification"/> - a
        /// subquery (EXISTS/IN/scalar) inside this WHERE has its own FROM scope, and is reached
        /// separately (and correctly re-scoped) by the outer visitor's own
        /// <see cref="Visitor.ExplicitVisit(QuerySpecification)"/> override when normal traversal
        /// gets there, so stopping here avoids inspecting the same comparison twice - once with
        /// this (wrong, outer) scope and once with its own (correct) scope.
        /// </summary>
        private sealed class EqualityCollector : TSqlFragmentVisitor
        {
            public List<BooleanComparisonExpression> Comparisons { get; } = [];

            public override void ExplicitVisit(BooleanComparisonExpression node)
            {
                if (node.ComparisonType == BooleanComparisonType.Equals)
                {
                    Comparisons.Add(node);
                }

                base.ExplicitVisit(node);
            }

            public override void ExplicitVisit(QuerySpecification node)
            {
                // Deliberately does not call base.ExplicitVisit(node) / AcceptChildren - see this
                // class's own doc comment.
            }
        }
    }
}
