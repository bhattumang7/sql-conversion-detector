using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class TransactionHygieneScanner
{
    public static IReadOnlyList<TransactionHygieneFinding> Scan(SqlParseResult parseResult)
    {
        var rule = CreateRule(parseResult.SourcePath);
        var walker = new ModuleWalker(parseResult.SourcePath, new DatabaseCatalog(), EmptyResolvedViews, ledger: null, currentProcScope: null, callerScopeByCalleeScope: null, rules: [rule]);
        parseResult.Fragment.Accept(walker);
    return Harvest(rule);
    }
    internal static Rule CreateRule(string sourcePath) => new(sourcePath);

    internal static IReadOnlyList<TransactionHygieneFinding> Harvest(Rule rule) =>
            [
            .. rule.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.BeginTransactionLine)
                .ThenBy(f => f.BeginTransactionColumn)
                .ThenBy(f => f.UnresolvedExitLine)
                .ThenBy(f => f.UnresolvedExitColumn),
        ];


    private readonly record struct FlowState(BeginTransactionStatement? OpenSite, bool Declined)
    {
        public static readonly FlowState NotTracking = new(null, false);
        public static readonly FlowState DeclinedState = new(null, true);
    }

    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    internal sealed class Rule(string sourcePath) : IModuleRule
    {
        public List<TransactionHygieneFinding> Findings { get; } = [];

        public void OnEnterProcedureOrFunctionBody(ProcedureStatementBodyBase node, ModuleWalker walker)
        {
            if (node is ProcedureStatementBody)
            {
                AnalyzeScope(node.StatementList);
            }
        }

        public void OnEnterTriggerBody(TriggerStatementBody node, ModuleWalker walker) => AnalyzeScope(node.StatementList);

        private void AnalyzeScope(StatementList? statementList)
        {

            if (statementList is null)
            {
                return;
            }

            var statements = statementList.Statements is [BeginEndBlockStatement singleBlock]
                ? singleBlock.StatementList.Statements
                : statementList.Statements;

            var finalState = AnalyzeSequential(statements, FlowState.NotTracking);

            if (finalState is { Declined: false, OpenSite: { } openSite })
            {
                var exitAnchor = statements.Count > 0 ? statements[^1] : (TSqlStatement)openSite;
                Findings.Add(new TransactionHygieneFinding(
                    TransactionHygieneFindingKind.UnresolvedOnSomePath,
                    sourcePath,
                    openSite.StartLine,
                    openSite.StartColumn,
                    exitAnchor.StartLine,
                    exitAnchor.StartColumn));
            }
        }

        private FlowState AnalyzeSequential(IList<TSqlStatement> statements, FlowState state)
        {
            foreach (var statement in statements)
            {
                if (state.Declined)
                {
                    return state;
                }

                switch (statement)
                {
                    case BeginTransactionStatement begin:
                        state = state.OpenSite is not null
                            ? FlowState.DeclinedState
                            : state with { OpenSite = begin };
                        break;

                    case CommitTransactionStatement or RollbackTransactionStatement:
                        state = state with { OpenSite = null };
                        break;

                    case ReturnStatement or ThrowStatement:
                        if (state.OpenSite is { } openSite)
                        {
                            Findings.Add(new TransactionHygieneFinding(
                                TransactionHygieneFindingKind.UnresolvedOnSomePath,
                                sourcePath,
                                openSite.StartLine,
                                openSite.StartColumn,
                                statement.StartLine,
                                statement.StartColumn));
                        }

                        return state with { OpenSite = null };

                    case GoToStatement:

                        return FlowState.DeclinedState;

                    case BeginEndBlockStatement block:
                        state = AnalyzeSequential(block.StatementList.Statements, state);
                        break;

                    case IfStatement ifStatement:
                        state = AnalyzeIf(ifStatement, state);
                        break;

                    case WhileStatement whileStatement:
                        state = AnalyzeWhile(whileStatement, state);
                        break;

                    case TryCatchStatement tryCatch:
                        state = AnalyzeTryCatch(tryCatch, state);
                        break;

                    default:

                        break;
                }
            }

            return state;
        }

        private FlowState AnalyzeIf(IfStatement ifStatement, FlowState enteringState)
        {
            if (enteringState.Declined)
            {
                return enteringState;
            }

            var thenResult = AnalyzeSequential(ToStatementList(ifStatement.ThenStatement), enteringState);

            var elseResult = ifStatement.ElseStatement is not null
                ? AnalyzeSequential(ToStatementList(ifStatement.ElseStatement), enteringState)
                : enteringState;

            return MergeBranches(thenResult, elseResult);
        }

        private FlowState AnalyzeWhile(WhileStatement whileStatement, FlowState enteringState)
        {
            if (enteringState.Declined)
            {
                return enteringState;
            }

            var bodyResult = AnalyzeSequential(ToStatementList(whileStatement.Statement), enteringState);
            return MergeBranches(enteringState, bodyResult);
        }

        private FlowState AnalyzeTryCatch(TryCatchStatement tryCatch, FlowState enteringState)
        {
            if (enteringState.Declined)
            {
                return enteringState;
            }

            var tryResult = AnalyzeSequential(tryCatch.TryStatements.Statements, enteringState);

            var catchResult = AnalyzeSequential(tryCatch.CatchStatements.Statements, enteringState);

            return MergeBranches(tryResult, catchResult);
        }

        private static FlowState MergeBranches(FlowState a, FlowState b)
        {
            if (a.Declined || b.Declined)
            {
                return FlowState.DeclinedState;
            }

            if (a.OpenSite is null && b.OpenSite is null)
            {
                return FlowState.NotTracking;
            }

            if (a.OpenSite is null)
            {
                return b;
            }

            if (b.OpenSite is null)
            {
                return a;
            }

            return ReferenceEquals(a.OpenSite, b.OpenSite) ? a : FlowState.DeclinedState;
        }

        private static IList<TSqlStatement> ToStatementList(TSqlStatement statement) =>
            statement is BeginEndBlockStatement block ? block.StatementList.Statements : [statement];
    }
}
