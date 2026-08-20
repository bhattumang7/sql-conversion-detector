using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md full-archive practitioner sweep §E, "Columnstore index present on a
/// table that is also a live DML target of transactional code" - computes the one small, reusable
/// fact <see cref="IndexDesignScanner.Scan"/>'s <c>dmlTargetTables</c> parameter needs: which base
/// tables are a direct INSERT/UPDATE/DELETE/MERGE target somewhere in the scanned corpus. The same
/// direct-target-only scope <see cref="CrossModuleLockOrderScanner"/>'s own write-target visitor
/// already uses (see that scanner's own doc comment) - never through a view, never through dynamic
/// SQL this pass can't see inside, and deliberately NOT gated on an open explicit transaction the
/// way that scanner's write-order tracking is: <see cref="IndexDesignFindingKind.ColumnstoreIndexOnDmlTargetTable"/>
/// cares about ANY write reaching the table, transacted or not, since a single-row DELETE outside
/// an explicit transaction still takes (and releases) the same rowgroup-granularity lock for the
/// duration of that one statement.
/// </summary>
public static class DmlTargetTableScanner
{
    public static IReadOnlySet<string> Scan(IEnumerable<SqlParseResult> parseResults, DatabaseCatalog catalog)
    {
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parseResult in parseResults)
        {
            var visitor = new Visitor(catalog, targets);
            parseResult.Fragment.Accept(visitor);
        }

        return targets;
    }

    private sealed class Visitor(DatabaseCatalog catalog, HashSet<string> targets) : TSqlFragmentVisitor
    {
        public override void ExplicitVisit(InsertStatement node)
        {
            RecordWrite(node.InsertSpecification.Target, node.WithCtesAndXmlNamespaces);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(UpdateStatement node)
        {
            RecordWrite(node.UpdateSpecification.Target, node.WithCtesAndXmlNamespaces);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(DeleteStatement node)
        {
            RecordWrite(node.DeleteSpecification.Target, node.WithCtesAndXmlNamespaces);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(MergeStatement node)
        {
            RecordWrite(node.MergeSpecification.Target, node.WithCtesAndXmlNamespaces);
            base.ExplicitVisit(node);
        }

        /// <summary>
        /// Only a direct <see cref="NamedTableReference"/> target resolving to a real base table
        /// counts - see this type's own doc comment. An unqualified target sharing its name with
        /// one of the statement's own CTEs (legal for UPDATE/DELETE/MERGE against a simple,
        /// updatable CTE) is declined rather than resolved against the catalog - a CTE is never
        /// schema-qualified, so it always shadows a same-named real base table for this
        /// statement's own lifetime, and resolving anyway would misattribute the write to an
        /// unrelated real table (the same bug class fixed across the Predicates layer's FROM-
        /// clause resolvers - this scanner has its own independent target-resolution path that
        /// needed the identical fix).
        /// </summary>
        private void RecordWrite(TableReference? target, WithCtesAndXmlNamespaces? withCtes)
        {
            if (target is not NamedTableReference named)
            {
                return;
            }

            if (named.SchemaObject.SchemaIdentifier is null && withCtes is { CommonTableExpressions: { } ctes }
                && ctes.Any(cte => string.Equals(cte.ExpressionName.Value, named.SchemaObject.BaseIdentifier.Value, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            var qualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(named.SchemaObject));
            if (catalog.Find(qualifiedName) is { Kind: CatalogTableKind.Table })
            {
                targets.Add(qualifiedName);
            }
        }
    }
}
