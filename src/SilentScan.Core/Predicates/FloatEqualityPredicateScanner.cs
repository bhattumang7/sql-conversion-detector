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
/// Reuses the same "flatten the join tree to its direct base-table leaves, matched by alias" shape
/// <see cref="NonUniqueUpdateSourceScanner"/> already established, rather than the full
/// <see cref="Lineage.FromScopeResolver"/> scope-chain/lineage machinery - a real, known v1 scope
/// limit (a float/real predicate reached through a view/CTE/derived table is left unanalyzed, not
/// guessed at), matching that scanner's own precedent.
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
            var tables = ResolveDirectBaseTables(node.FromClause?.TableReferences);
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
            var tables = ResolveDirectBaseTables(spec.FromClause?.TableReferences, spec.Target);
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
            var tables = ResolveDirectBaseTables(spec.FromClause?.TableReferences, spec.Target);
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
                foreach (var join in FlattenJoins(reference))
                {
                    if (join.SearchCondition is not null)
                    {
                        Inspect(join.SearchCondition, tables);
                    }
                }
            }
        }

        private static IEnumerable<QualifiedJoin> FlattenJoins(TableReference tableReference)
        {
            switch (tableReference)
            {
                case QualifiedJoin join:
                    foreach (var t in FlattenJoins(join.FirstTableReference))
                    {
                        yield return t;
                    }

                    foreach (var t in FlattenJoins(join.SecondTableReference))
                    {
                        yield return t;
                    }

                    yield return join;
                    break;

                case JoinParenthesisTableReference parenthesis:
                    foreach (var t in FlattenJoins(parenthesis.Join))
                    {
                        yield return t;
                    }

                    break;
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
                    || TryResolveColumn(columnRef, tables) is not { } resolved
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

        private static (CatalogTable Table, CatalogColumn Column)? TryResolveColumn(
            ColumnReferenceExpression columnRef, Dictionary<string, CatalogTable> tables)
        {
            var identifiers = columnRef.MultiPartIdentifier?.Identifiers;
            if (identifiers is null || identifiers.Count == 0)
            {
                return null;
            }

            var columnName = identifiers[^1].Value;

            if (identifiers.Count >= 2)
            {
                var alias = identifiers[^2].Value;
                if (tables.TryGetValue(alias, out var table) && table.FindColumn(columnName) is { } column)
                {
                    return (table, column);
                }

                return null;
            }

            // Unqualified reference - only safe to resolve when exactly one table is in scope, to
            // avoid guessing which of several tables an ambiguous bare column name belongs to.
            if (tables.Count == 1)
            {
                var table = tables.Values.Single();
                if (table.FindColumn(columnName) is { } column)
                {
                    return (table, column);
                }
            }

            return null;
        }

        /// <summary>
        /// Flattens the FROM clause's join tree to its direct <see cref="NamedTableReference"/>
        /// leaves, keyed by alias (or bare table name when unaliased) - only a leaf that resolves
        /// to a real base <see cref="CatalogTableKind.Table"/> is kept; a view, TVF, derived table,
        /// or unresolved reference is silently excluded rather than guessed at (this scanner's own
        /// known v1 scope limit - see this type's own doc comment). <paramref name="extraTarget"/>
        /// covers an UPDATE/DELETE with no explicit FROM clause at all, where the statement's own
        /// target table is the only thing in scope.
        /// </summary>
        private Dictionary<string, CatalogTable> ResolveDirectBaseTables(
            IList<TableReference>? tableReferences, TableReference? extraTarget = null)
        {
            var tables = new Dictionary<string, CatalogTable>(StringComparer.OrdinalIgnoreCase);

            if (extraTarget is not null && ResolveDirectBaseTable(extraTarget) is { } targetEntry)
            {
                tables[targetEntry.Alias] = targetEntry.Table;
            }

            if (tableReferences is null)
            {
                return tables;
            }

            foreach (var reference in tableReferences)
            {
                foreach (var leaf in FlattenTableReferences(reference))
                {
                    if (ResolveDirectBaseTable(leaf) is { } entry)
                    {
                        tables[entry.Alias] = entry.Table;
                    }
                }
            }

            return tables;
        }

        private (string Alias, CatalogTable Table)? ResolveDirectBaseTable(TableReference tableReference)
        {
            if (tableReference is not NamedTableReference named)
            {
                return null;
            }

            var alias = named.Alias?.Value ?? named.SchemaObject.BaseIdentifier.Value;
            var qualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(named.SchemaObject));
            return catalog.Find(qualifiedName) is { Kind: CatalogTableKind.Table } table ? (alias, table) : null;
        }

        private static IEnumerable<TableReference> FlattenTableReferences(TableReference tableReference)
        {
            switch (tableReference)
            {
                case QualifiedJoin join:
                    foreach (var t in FlattenTableReferences(join.FirstTableReference))
                    {
                        yield return t;
                    }

                    foreach (var t in FlattenTableReferences(join.SecondTableReference))
                    {
                        yield return t;
                    }

                    break;

                case JoinParenthesisTableReference parenthesis:
                    foreach (var t in FlattenTableReferences(parenthesis.Join))
                    {
                        yield return t;
                    }

                    break;

                default:
                    yield return tableReference;
                    break;
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
