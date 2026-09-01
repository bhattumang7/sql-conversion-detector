using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class CursorCloseOnCommitScanner
{
    public static IReadOnlyList<CursorCloseOnCommitFinding> Scan(SqlParseResult parseResult)
    {
        var rule = CreateRule(parseResult.SourcePath);
        var walker = new ModuleWalker(parseResult.SourcePath, new DatabaseCatalog(), EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return Harvest(rule);
    }

    internal static Rule CreateRule(string sourcePath) => new(sourcePath);

    internal static IReadOnlyList<CursorCloseOnCommitFinding> Harvest(Rule rule) =>
        [
            .. rule.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.FetchLine)
                .ThenBy(f => f.FetchColumn),
        ];

    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    private readonly record struct CursorState(TSqlFragment OpenSite, bool SilentlyClosed, TSqlFragment? ClosingSite, bool ClosedByRollback);

    internal sealed class Rule(string sourcePath) : IModuleRule
    {
        public List<CursorCloseOnCommitFinding> Findings { get; } = [];

        private bool _cursorCloseOnCommitOn;
        private int _transactionDepth;
        private readonly Dictionary<string, CursorState> _cursors = new(StringComparer.OrdinalIgnoreCase);

        public void OnEnterProcedureOrFunctionBody(ProcedureStatementBodyBase node, ModuleWalker walker) => ResetScope();

        public void OnLeaveProcedureOrFunctionBody(ProcedureStatementBodyBase node, ModuleWalker walker) => ResetScope();

        public void OnEnterTriggerBody(TriggerStatementBody node, ModuleWalker walker) => ResetScope();

        public void OnLeaveTriggerBody(TriggerStatementBody node, ModuleWalker walker) => ResetScope();

        private void ResetScope()
        {
            _cursorCloseOnCommitOn = false;
            _transactionDepth = 0;
            _cursors.Clear();
        }

        public void OnEnterPredicateSetStatement(PredicateSetStatement node, ModuleWalker walker)
        {
            if ((node.Options & SetOptions.CursorCloseOnCommit) != 0)
            {
                _cursorCloseOnCommitOn = node.IsOn;
            }
        }

        public void OnEnterBeginTransactionStatement(BeginTransactionStatement node, ModuleWalker walker) => _transactionDepth++;

        public void OnEnterCommitTransactionStatement(CommitTransactionStatement node, ModuleWalker walker)
        {
            if (_transactionDepth > 0)
            {
                _transactionDepth--;
            }

            if (_transactionDepth == 0)
            {
                CloseOpenCursorsSilently(node, closedByRollback: false);
            }
        }

        public void OnEnterRollbackTransactionStatement(RollbackTransactionStatement node, ModuleWalker walker)
        {
            if (node.Name is not null)
            {
                return;
            }

            _transactionDepth = 0;
            CloseOpenCursorsSilently(node, closedByRollback: true);
        }

        private void CloseOpenCursorsSilently(TSqlFragment closingStatement, bool closedByRollback)
        {
            if (!_cursorCloseOnCommitOn)
            {
                return;
            }

            foreach (var name in _cursors.Keys.ToList())
            {
                var state = _cursors[name];
                if (!state.SilentlyClosed)
                {
                    _cursors[name] = state with { SilentlyClosed = true, ClosingSite = closingStatement, ClosedByRollback = closedByRollback };
                }
            }
        }

        public void OnEnterOpenCursorStatement(OpenCursorStatement node, ModuleWalker walker)
        {
            var name = ResolveTrackableCursorName(node.Cursor);
            if (name is null)
            {
                return;
            }

            _cursors[name] = new CursorState(node, SilentlyClosed: false, ClosingSite: null, ClosedByRollback: false);
        }

        public void OnEnterCloseCursorStatement(CloseCursorStatement node, ModuleWalker walker)
        {
            var name = ResolveTrackableCursorName(node.Cursor);
            if (name is not null)
            {
                _cursors.Remove(name);
            }
        }

        public void OnEnterDeallocateCursorStatement(DeallocateCursorStatement node, ModuleWalker walker)
        {
            var name = ResolveTrackableCursorName(node.Cursor);
            if (name is not null)
            {
                _cursors.Remove(name);
            }
        }

        public void OnEnterFetchCursorStatement(FetchCursorStatement node, ModuleWalker walker)
        {
            var name = ResolveTrackableCursorName(node.Cursor);
            if (name is null || !_cursors.TryGetValue(name, out var state) || !state.SilentlyClosed)
            {
                return;
            }

            Findings.Add(new CursorCloseOnCommitFinding(
                sourcePath,
                name,
                state.OpenSite.StartLine,
                state.OpenSite.StartColumn,
                state.ClosingSite!.StartLine,
                state.ClosingSite.StartColumn,
                state.ClosedByRollback,
                node.StartLine,
                node.StartColumn));

            _cursors.Remove(name);
        }

        private static string? ResolveTrackableCursorName(CursorId? cursor)
        {
            if (cursor is null || cursor.IsGlobal)
            {
                return null;
            }

            return cursor.Name?.Identifier?.Value;
        }
    }
}
