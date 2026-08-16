using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;

namespace SilentScan.Core.Predicates;

/// <summary>
/// Whether a module's own body touches a table with a filtered index, or an indexed view - the
/// mandatory precision guard docs/detection-checklist.md's "SET options that silently disable
/// plan features" section requires before ANY of that stream's three sub-rules fire (an explicit
/// SET on a module that never touches either is noise, not a finding: per Microsoft's own
/// documented requirements for indexed views/filtered indexes, QUOTED_IDENTIFIER/
/// NUMERIC_ROUNDABORT/ARITHABORT only matter to a module that actually reads through one).
///
/// Direct references (every <see cref="NamedTableReference"/> anywhere in the module's own body,
/// not just top-level FROM-clause ones - a reference inside a subquery/CTE/UPDATE target still
/// counts) are walked against the catalog directly. A referenced VIEW's own transitive
/// containment (does the view ITSELF, however many layers down, read from a filtered-index
/// table) is answered for free from the ALREADY-RESOLVED <see cref="LineageCatalog"/> rather than
/// re-parsing or retaining the view's own AST - this deliberately reuses <see
/// cref="ColumnProvenanceAnalysis.FindUnderlyingBaseColumns"/>, the same mechanism
/// <c>ExpressionDerivedFinding</c> already traces underlying base columns through.
///
/// Deliberately does NOT recurse through a called PROCEDURE's own body (a module that calls a
/// helper proc which itself queries a filtered-index table is not detected here) -
/// <c>ScanReportBuilder</c>'s own documented design deliberately never holds every module's
/// parsed AST alive simultaneously (a live-mode reparse runs roughly 200x its source text's
/// size), and a proc-call-transitive walk would need exactly that - every callee's AST, for
/// every module, all at once. A false negative here is the honest trade against a real, measured
/// memory property of this codebase's scan pipeline, not a gap silently claimed as covered.
/// </summary>
public static class ModuleReachableObjectWalker
{
    public readonly record struct Touch(string ObjectQualifiedName, string? IndexName, bool IsIndexedView);

    public static bool TryFindTouch(TSqlFragment moduleBody, DatabaseCatalog catalog, LineageCatalog lineage, out Touch touch)
    {
        var collector = new TableReferenceCollector();
        moduleBody.Accept(collector);

        var visitedViews = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawName in collector.QualifiedNames)
        {
            var qualifiedName = catalog.ResolveSynonymName(rawName);

            if (catalog.IsIndexedView(qualifiedName))
            {
                touch = new Touch(qualifiedName, IndexName: null, IsIndexedView: true);
                return true;
            }

            if (catalog.Find(qualifiedName) is { } table)
            {
                if (table.Indexes.FirstOrDefault(i => i.IsFiltered) is { } filteredIndex)
                {
                    touch = new Touch(qualifiedName, filteredIndex.Name, IsIndexedView: false);
                    return true;
                }

                continue;
            }

            if (!visitedViews.Add(qualifiedName) || !lineage.AllRelations.TryGetValue(qualifiedName, out var relation))
            {
                continue;
            }

            foreach (var column in relation.Columns)
            {
                foreach (var baseColumn in ColumnProvenanceAnalysis.FindUnderlyingBaseColumns(column.Provenance))
                {
                    if (catalog.Find(baseColumn.TableQualifiedName)?.Indexes.FirstOrDefault(i => i.IsFiltered) is { } nestedFilteredIndex)
                    {
                        touch = new Touch(baseColumn.TableQualifiedName, nestedFilteredIndex.Name, IsIndexedView: false);
                        return true;
                    }
                }
            }
        }

        touch = default;
        return false;
    }

    private sealed class TableReferenceCollector : TSqlFragmentVisitor
    {
        public List<string> QualifiedNames { get; } = [];

        public override void Visit(NamedTableReference node) =>
            QualifiedNames.Add(SchemaObjectNameHelper.Qualify(node.SchemaObject));
    }
}
