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

        public override void ExplicitVisit(BeginTransactionStatement node)
        {
            _openTransactionDepth++;
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CommitTransactionStatement node)
        {
            // Sonar (S2583) reports this guard as always false. That is a false positive: Sonar's
            // intraprocedural symbolic-execution engine only sees _openTransactionDepth's declared
            // default (0) at method entry - it has no way to know the field is also mutated by the
            // ExplicitVisit(BeginTransactionStatement) override above, which the ScriptDom traversal
            // framework invokes as an independent callback, not through any call chain reachable
            // from this method. In real T-SQL, this guard is reachable and load-bearing: a procedure
            // whose COMMIT/ROLLBACK count exceeds its BEGIN TRANSACTION count (a common defensive
            // pattern, e.g. an unconditional COMMIT after an already-closed conditional transaction)
            // would otherwise drive the counter negative, desynchronizing it from the real nesting
            // depth and corrupting the "inside an explicit transaction" gate RecordWrite relies on
            // for every write that follows.
#pragma warning disable S2583
            if (_openTransactionDepth > 0)
            {
                _openTransactionDepth--;
            }
#pragma warning restore S2583

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(RollbackTransactionStatement node)
        {
            // Same Sonar (S2583) false positive as ExplicitVisit(CommitTransactionStatement) above,
            // for the identical reason: this guard is reachable once BeginTransactionStatement has
            // run first, which Sonar's intraprocedural analysis cannot see across sibling visitor
            // callbacks. See that method's own comment for the full explanation.
#pragma warning disable S2583
            if (_openTransactionDepth > 0)
            {
                _openTransactionDepth--;
            }
#pragma warning restore S2583

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(InsertStatement node)
        {
            RecordWrite(node.InsertSpecification.Target, node.StartLine, node.WithCtesAndXmlNamespaces);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(UpdateStatement node)
        {
            RecordWrite(node.UpdateSpecification.Target, node.StartLine, node.WithCtesAndXmlNamespaces);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(DeleteStatement node)
        {
            RecordWrite(node.DeleteSpecification.Target, node.StartLine, node.WithCtesAndXmlNamespaces);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(MergeStatement node)
        {
            RecordWrite(node.MergeSpecification.Target, node.StartLine, node.WithCtesAndXmlNamespaces);
            base.ExplicitVisit(node);
        }

        private void VisitProcedure(string qualifiedName, int line, StatementList? statementList)
        {
            _openTransactionDepth = 0;
            _writes = [];

            statementList?.AcceptChildren(this);

            // Sonar (S2583) reports this as always false, for the same reason as the two guards
            // above: AcceptChildren dispatches back into this same visitor's ExplicitVisit(Insert/
            // Update/Delete/MergeStatement) overrides, which call RecordWrite and mutate _writes as
            // a side effect - Sonar's intraprocedural analysis has no visibility into that indirect,
            // framework-driven callback chain, so it only ever sees _writes as the empty list just
            // assigned above. In real T-SQL, a procedure body with two or more direct table writes
            // genuinely populates _writes past this point, and this guard is what decides whether
            // that procedure's write order is worth reporting at all.
#pragma warning disable S2583
            if (_writes.Count >= 2)
            {
                Orderings.Add(new ProcedureWriteOrder(qualifiedName, sourcePath, line, _writes));
            }
#pragma warning restore S2583

            _writes = null;
        }

        /// <summary>
        /// Only a direct <see cref="NamedTableReference"/> target resolving to a real base table
        /// counts - never a view (writing through a view is not a direct target this pass can
        /// prove locks the underlying table the same way), never a temp table/table variable
        /// (private per session, cannot deadlock across sessions), and only while inside an
        /// explicit transaction the procedure's own body opened. First occurrence per table wins -
        /// a later re-write of the same table doesn't change which table was locked FIRST. An
        /// unqualified target sharing its name with one of the statement's own CTEs (legal for
        /// UPDATE/DELETE/MERGE against a simple, updatable CTE) is declined rather than resolved
        /// against the catalog - see <see cref="DmlTargetTableScanner"/>'s own identical fix for
        /// why: a CTE is never schema-qualified, so it always shadows a same-named real base table
        /// for this statement's own lifetime.
        /// </summary>
        private void RecordWrite(TableReference? target, int line, WithCtesAndXmlNamespaces? withCtes)
        {
            if (_writes is null || _openTransactionDepth == 0 || target is not NamedTableReference named)
            {
                return;
            }

            if (named.SchemaObject.SchemaIdentifier is null && withCtes is { CommonTableExpressions: { } ctes }
                && ctes.Any(cte => string.Equals(cte.ExpressionName.Value, named.SchemaObject.BaseIdentifier.Value, StringComparison.OrdinalIgnoreCase)))
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
