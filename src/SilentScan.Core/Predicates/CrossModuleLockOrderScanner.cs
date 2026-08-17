using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md "DBA-script family sweep (2026-08-17)" §D "Cross-module analysis" -
/// see <see cref="CrossModuleLockOrderFinding"/> for the full precision story, oracle evidence,
/// and the explicit v1 scope-down (top-level procedures' own direct bodies only, no call-graph
/// traversal). This is a WHOLE-SCAN pass, not a per-file one: the same table pair must be seen
/// written in opposite order by two DIFFERENT procedures, which can live in different files.
/// </summary>
public static class CrossModuleLockOrderScanner
{
    /// <summary>One top-level procedure's own direct write order: every base table its own body
    /// writes inside an explicit transaction, first-occurrence line only, in the order first
    /// written.</summary>
    private sealed record ProcedureWriteOrder(
        string ProcedureQualifiedName, string SourcePath, int ProcedureLine, IReadOnlyList<(string TableQualifiedName, int Line)> Writes);

    public static IReadOnlyList<CrossModuleLockOrderFinding> Scan(IEnumerable<SqlParseResult> parseResults, DatabaseCatalog catalog)
    {
        var procedures = new List<ProcedureWriteOrder>();
        foreach (var result in parseResults)
        {
            var visitor = new Visitor(result.SourcePath, catalog);
            result.Fragment.Accept(visitor);
            procedures.AddRange(visitor.Orderings);
        }

        var findings = new List<CrossModuleLockOrderFinding>();
        for (var i = 0; i < procedures.Count; i++)
        {
            for (var j = i + 1; j < procedures.Count; j++)
            {
                if (string.Equals(procedures[i].ProcedureQualifiedName, procedures[j].ProcedureQualifiedName, StringComparison.OrdinalIgnoreCase))
                {
                    // Same procedure name seen twice (e.g. a re-declared/duplicate CREATE across
                    // files this scan doesn't try to disambiguate) - comparing it against itself
                    // is not a cross-module claim.
                    continue;
                }

                findings.AddRange(FindInconsistentPairs(procedures[i], procedures[j]));
            }
        }

        return
        [
            .. findings
                .OrderBy(f => f.FirstTableQualifiedName, StringComparer.Ordinal)
                .ThenBy(f => f.SecondTableQualifiedName, StringComparer.Ordinal)
                .ThenBy(f => f.FirstTableFirstOrdering.ProcedureQualifiedName, StringComparer.Ordinal)
                .ThenBy(f => f.SecondTableFirstOrdering.ProcedureQualifiedName, StringComparer.Ordinal),
        ];
    }

    private static IEnumerable<CrossModuleLockOrderFinding> FindInconsistentPairs(ProcedureWriteOrder a, ProcedureWriteOrder b)
    {
        for (var x = 0; x < a.Writes.Count; x++)
        {
            for (var y = x + 1; y < a.Writes.Count; y++)
            {
                var tableX = a.Writes[x];
                var tableY = a.Writes[y];

                var bXIndex = IndexOfTable(b.Writes, tableX.TableQualifiedName);
                var bYIndex = IndexOfTable(b.Writes, tableY.TableQualifiedName);
                if (bXIndex < 0 || bYIndex < 0 || bXIndex < bYIndex)
                {
                    // b doesn't write both tables within its own explicit transaction, or writes
                    // them in the SAME relative order as a - not an inconsistency.
                    continue;
                }

                // a writes tableX then tableY; b writes tableY then tableX - opposite order,
                // confirmed. Canonicalize which table is "first"/"second" so the same pair always
                // produces the same finding shape regardless of scan/enumeration order.
                if (string.CompareOrdinal(tableX.TableQualifiedName, tableY.TableQualifiedName) <= 0)
                {
                    yield return new CrossModuleLockOrderFinding(
                        tableX.TableQualifiedName, tableY.TableQualifiedName,
                        new LockOrderProcedureSite(a.ProcedureQualifiedName, a.SourcePath, a.ProcedureLine, tableX.Line, tableY.Line),
                        new LockOrderProcedureSite(b.ProcedureQualifiedName, b.SourcePath, b.ProcedureLine, b.Writes[bXIndex].Line, b.Writes[bYIndex].Line));
                }
                else
                {
                    yield return new CrossModuleLockOrderFinding(
                        tableY.TableQualifiedName, tableX.TableQualifiedName,
                        new LockOrderProcedureSite(b.ProcedureQualifiedName, b.SourcePath, b.ProcedureLine, b.Writes[bYIndex].Line, b.Writes[bXIndex].Line),
                        new LockOrderProcedureSite(a.ProcedureQualifiedName, a.SourcePath, a.ProcedureLine, tableY.Line, tableX.Line));
                }
            }
        }
    }

    private static int IndexOfTable(IReadOnlyList<(string TableQualifiedName, int Line)> writes, string tableQualifiedName)
    {
        for (var i = 0; i < writes.Count; i++)
        {
            if (string.Equals(writes[i].TableQualifiedName, tableQualifiedName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Scoped to top-level <c>CREATE/ALTER/CREATE OR ALTER PROCEDURE</c> only (the v1 scope-down -
    /// see <see cref="CrossModuleLockOrderFinding"/>'s own doc comment). Tracks an open-transaction
    /// depth exactly like <see cref="WaitForScanner"/>'s own established convention (BEGIN TRAN
    /// increments, COMMIT/ROLLBACK decrements; a structural, straight-line-reading-order signal,
    /// not real control-flow analysis) purely to gate which DML targets count as "inside an
    /// explicit transaction."
    /// </summary>
    private sealed class Visitor(string sourcePath, DatabaseCatalog catalog) : TSqlFragmentVisitor
    {
        public List<ProcedureWriteOrder> Orderings { get; } = [];

        private int _openTransactionDepth;
        private List<(string TableQualifiedName, int Line)>? _writes;

        public override void ExplicitVisit(CreateProcedureStatement node) =>
            VisitProcedure(SchemaObjectNameHelper.Qualify(node.ProcedureReference.Name), node.StartLine, node.StatementList);

        public override void ExplicitVisit(AlterProcedureStatement node) =>
            VisitProcedure(SchemaObjectNameHelper.Qualify(node.ProcedureReference.Name), node.StartLine, node.StatementList);

        public override void ExplicitVisit(CreateOrAlterProcedureStatement node) =>
            VisitProcedure(SchemaObjectNameHelper.Qualify(node.ProcedureReference.Name), node.StartLine, node.StatementList);

        private void VisitProcedure(string qualifiedName, int line, StatementList? statementList)
        {
            _openTransactionDepth = 0;
            _writes = [];

            statementList?.AcceptChildren(this);

            if (_writes.Count >= 2)
            {
                Orderings.Add(new ProcedureWriteOrder(qualifiedName, sourcePath, line, _writes));
            }

            _writes = null;
        }

        public override void ExplicitVisit(BeginTransactionStatement node)
        {
            _openTransactionDepth++;
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CommitTransactionStatement node)
        {
            if (_openTransactionDepth > 0)
            {
                _openTransactionDepth--;
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(RollbackTransactionStatement node)
        {
            if (_openTransactionDepth > 0)
            {
                _openTransactionDepth--;
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(InsertStatement node)
        {
            RecordWrite(node.InsertSpecification.Target, node.StartLine);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(UpdateStatement node)
        {
            RecordWrite(node.UpdateSpecification.Target, node.StartLine);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(DeleteStatement node)
        {
            RecordWrite(node.DeleteSpecification.Target, node.StartLine);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(MergeStatement node)
        {
            RecordWrite(node.MergeSpecification.Target, node.StartLine);
            base.ExplicitVisit(node);
        }

        /// <summary>
        /// Only a direct <see cref="NamedTableReference"/> target resolving to a real base table
        /// counts - never a view (writing through a view is not a direct target this pass can
        /// prove locks the underlying table the same way), never a temp table/table variable
        /// (private per session, cannot deadlock across sessions), and only while inside an
        /// explicit transaction the procedure's own body opened. First occurrence per table wins -
        /// a later re-write of the same table doesn't change which table was locked FIRST.
        /// </summary>
        private void RecordWrite(TableReference? target, int line)
        {
            if (_writes is null || _openTransactionDepth == 0 || target is not NamedTableReference named)
            {
                return;
            }

            var qualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(named.SchemaObject));
            if (catalog.Find(qualifiedName) is not { Kind: CatalogTableKind.Table })
            {
                return;
            }

            if (IndexOfTable(_writes, qualifiedName) < 0)
            {
                _writes.Add((qualifiedName, line));
            }
        }
    }
}
