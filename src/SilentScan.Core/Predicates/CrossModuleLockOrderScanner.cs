using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Common;

namespace SilentScan.Core.Predicates;

public static class CrossModuleLockOrderScanner
{
    internal sealed record ProcedureWriteOrder(
        string ProcedureQualifiedName, string SourcePath, int ProcedureLine, IReadOnlyList<(string TableQualifiedName, int Line)> Writes);

    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    public static IReadOnlyList<CrossModuleLockOrderFinding> Scan(IEnumerable<SqlParseResult> parseResults, DatabaseCatalog catalog)
    {
        var rules = ModuleWalkerRuleRunner.Run(parseResults, catalog, EmptyResolvedViews, CreateRule);
        return Harvest(catalog, rules);
    }

    internal static Rule CreateRule(string sourcePath, DatabaseCatalog catalog) => new(sourcePath, catalog);

    internal static IReadOnlyList<CrossModuleLockOrderFinding> Harvest(DatabaseCatalog catalog, IReadOnlyList<Rule> rules)
    {
        var procedures = rules.SelectMany(r => r.Orderings).ToList();

        var findings = new List<CrossModuleLockOrderFinding>();
        for (var i = 0; i < procedures.Count; i++)
        {
            for (var j = i + 1; j < procedures.Count; j++)
            {
                if (catalog.IdentifierComparer.Equals(procedures[i].ProcedureQualifiedName, procedures[j].ProcedureQualifiedName))
                {

                    continue;
                }

                findings.AddRange(FindInconsistentPairs(procedures[i], procedures[j], catalog.IdentifierComparer));
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

    private static IEnumerable<CrossModuleLockOrderFinding> FindInconsistentPairs(ProcedureWriteOrder a, ProcedureWriteOrder b, StringComparer identifierComparer)
    {
        for (var x = 0; x < a.Writes.Count; x++)
        {
            for (var y = x + 1; y < a.Writes.Count; y++)
            {
                var tableX = a.Writes[x];
                var tableY = a.Writes[y];

                var bXIndex = IndexOfTable(b.Writes, tableX.TableQualifiedName, identifierComparer);
                var bYIndex = IndexOfTable(b.Writes, tableY.TableQualifiedName, identifierComparer);
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

    private static int IndexOfTable(IReadOnlyList<(string TableQualifiedName, int Line)> writes, string tableQualifiedName, StringComparer identifierComparer)
    {
        for (var i = 0; i < writes.Count; i++)
        {
            if (identifierComparer.Equals(writes[i].TableQualifiedName, tableQualifiedName))
            {
                return i;
            }
        }

        return -1;
    }

    internal sealed class Rule(string sourcePath, DatabaseCatalog catalog) : IModuleRule
    {
        public List<ProcedureWriteOrder> Orderings { get; } = [];

        private int _openTransactionDepth;
        private List<(string TableQualifiedName, int Line)>? _writes;

        public void OnEnterCreateProcedureStatement(CreateProcedureStatement node, ModuleWalker walker) =>
            EnterProcedure();

        public void OnLeaveCreateProcedureStatement(CreateProcedureStatement node, ModuleWalker walker) =>
            LeaveProcedure(SchemaObjectNameHelper.Qualify(node.ProcedureReference.Name), node.StartLine);

        public void OnEnterAlterProcedureStatement(AlterProcedureStatement node, ModuleWalker walker) =>
            EnterProcedure();

        public void OnLeaveAlterProcedureStatement(AlterProcedureStatement node, ModuleWalker walker) =>
            LeaveProcedure(SchemaObjectNameHelper.Qualify(node.ProcedureReference.Name), node.StartLine);

        public void OnEnterCreateOrAlterProcedureStatement(CreateOrAlterProcedureStatement node, ModuleWalker walker) =>
            EnterProcedure();

        public void OnLeaveCreateOrAlterProcedureStatement(CreateOrAlterProcedureStatement node, ModuleWalker walker) =>
            LeaveProcedure(SchemaObjectNameHelper.Qualify(node.ProcedureReference.Name), node.StartLine);

        public void OnEnterBeginTransactionStatement(BeginTransactionStatement node, ModuleWalker walker) =>
            _openTransactionDepth++;

        public void OnEnterCommitTransactionStatement(CommitTransactionStatement node, ModuleWalker walker)
        {
#pragma warning disable S2583
            if (_openTransactionDepth > 0)
            {
                _openTransactionDepth--;
            }
#pragma warning restore S2583
        }

        public void OnEnterRollbackTransactionStatement(RollbackTransactionStatement node, ModuleWalker walker)
        {
#pragma warning disable S2583
            if (_openTransactionDepth > 0)
            {
                _openTransactionDepth--;
            }
#pragma warning restore S2583
        }

        public void OnEnterInsertStatementScope(InsertStatement node, ModuleWalker walker) =>
            RecordWrite(node.InsertSpecification.Target, node.StartLine, node.WithCtesAndXmlNamespaces);

        public void OnEnterUpdateStatementScope(UpdateStatement node, ScopeChain scopeChain, ModuleWalker walker) =>
            RecordWrite(node.UpdateSpecification.Target, node.StartLine, node.WithCtesAndXmlNamespaces);

        public void OnEnterDeleteStatementScope(DeleteStatement node, ScopeChain scopeChain, ModuleWalker walker) =>
            RecordWrite(node.DeleteSpecification.Target, node.StartLine, node.WithCtesAndXmlNamespaces);

        public void OnEnterMergeStatementScope(MergeStatement node, ScopeChain scopeChain, ModuleWalker walker) =>
            RecordWrite(node.MergeSpecification.Target, node.StartLine, node.WithCtesAndXmlNamespaces);

        private void EnterProcedure()
        {
            _openTransactionDepth = 0;
            _writes = [];
        }

        private void LeaveProcedure(string qualifiedName, int line)
        {
#pragma warning disable S2583
            if (_writes is { Count: >= 2 })
            {
                Orderings.Add(new ProcedureWriteOrder(qualifiedName, sourcePath, line, _writes));
            }
#pragma warning restore S2583

            _writes = null;
        }

        private void RecordWrite(TableReference? target, int line, WithCtesAndXmlNamespaces? withCtes)
        {
            if (_writes is null || _openTransactionDepth == 0)
            {
                return;
            }

            if (DmlWriteTargetResolver.TryResolve(target, withCtes, catalog) is not { } qualifiedName)
            {
                return;
            }

            if (IndexOfTable(_writes, qualifiedName, catalog.IdentifierComparer) < 0)
            {
                _writes.Add((qualifiedName, line));
            }
        }
    }
}
