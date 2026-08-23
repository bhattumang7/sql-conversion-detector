using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Common;

namespace SilentScan.Core.Predicates;

public static class CrossModuleLockOrderScanner
{
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

                    continue;
                }

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

#pragma warning disable S2583
            if (_writes.Count >= 2)
            {
                Orderings.Add(new ProcedureWriteOrder(qualifiedName, sourcePath, line, _writes));
            }
#pragma warning restore S2583

            _writes = null;
        }

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
