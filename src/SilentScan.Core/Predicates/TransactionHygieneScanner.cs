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
        var walker = new ModuleWalker(parseResult.SourcePath, new DatabaseCatalog(), EmptyResolvedViews, rules: [rule]);
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


    private readonly record struct FlowState(TSqlStatement? OpenSite, bool Declined, bool ImplicitTransactionsOn, bool XactAbortOn)
    {
        public static readonly FlowState NotTracking = new(null, false, false, false);
        public static readonly FlowState DeclinedState = new(null, true, false, false);
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

            var statements = Unwrap(statementList.Statements);

            var finalState = AnalyzeSequential(statements, FlowState.NotTracking);

            if (finalState is { Declined: false, OpenSite: { } openSite })
            {
                var exitAnchor = statements.Count > 0 ? statements[^1] : openSite;
                Findings.Add(new TransactionHygieneFinding(
                    ClassifyOpenSiteKind(openSite),
                    sourcePath,
                    openSite.StartLine,
                    openSite.StartColumn,
                    exitAnchor.StartLine,
                    exitAnchor.StartColumn));
            }
        }

        private static IList<TSqlStatement> Unwrap(IList<TSqlStatement> statements) =>
            statements is [BeginEndBlockStatement singleBlock] ? singleBlock.StatementList.Statements : statements;

        private static TransactionHygieneFindingKind ClassifyOpenSiteKind(TSqlStatement openSite) =>
            openSite is BeginTransactionStatement
                ? TransactionHygieneFindingKind.UnresolvedOnSomePath
                : TransactionHygieneFindingKind.ImplicitTransactionUnresolvedOnSomePath;

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
                    case PredicateSetStatement { Options: var options, IsOn: var isOn }:
                        state = ApplySetOptions(state, options, isOn);
                        break;

                    case BeginTransactionStatement begin:
                        state = state.OpenSite is not null
                            ? FlowState.DeclinedState
                            : state with { OpenSite = begin };
                        break;

                    case CommitTransactionStatement:
                        state = state with { OpenSite = null };
                        break;

                    case RollbackTransactionStatement rollback:
                        state = rollback.Name is not null
                            ? state
                            : state with { OpenSite = null };
                        break;

                    case ReturnStatement or ThrowStatement:
                        return ExitWithUnresolvedFinding(state, statement);

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
                        state = MaybeOpenImplicitTransaction(state, statement);
                        break;
                }
            }

            return state;
        }

        private FlowState ExitWithUnresolvedFinding(FlowState state, TSqlStatement exitStatement)
        {
            if (state.OpenSite is { } openSite)
            {
                Findings.Add(new TransactionHygieneFinding(
                    ClassifyOpenSiteKind(openSite),
                    sourcePath,
                    openSite.StartLine,
                    openSite.StartColumn,
                    exitStatement.StartLine,
                    exitStatement.StartColumn));
            }

            return state with { OpenSite = null };
        }

        private static FlowState ApplySetOptions(FlowState state, SetOptions options, bool isOn)
        {
            if ((options & SetOptions.ImplicitTransactions) != 0)
            {
                state = state with { ImplicitTransactionsOn = isOn };
            }

            if ((options & SetOptions.XactAbort) != 0)
            {
                state = state with { XactAbortOn = isOn };
            }

            return state;
        }

        private static FlowState MaybeOpenImplicitTransaction(FlowState state, TSqlStatement statement) =>
            state.OpenSite is null && state.ImplicitTransactionsOn && OpensImplicitTransaction(statement)
                ? state with { OpenSite = statement }
                : state;

        private static bool OpensImplicitTransaction(TSqlStatement statement) => statement switch
        {
            InsertStatement or UpdateStatement or DeleteStatement or MergeStatement or TruncateTableStatement => true,
            CreateTableStatement or CreateIndexStatement => true,
            CreateViewStatement or CreateOrAlterViewStatement or AlterViewStatement or DropViewStatement => true,
            DropTableStatement or DropIndexStatement or AlterTableStatement => true,
            GrantStatement or RevokeStatement => true,
            OpenCursorStatement or FetchCursorStatement => true,
            SelectStatement { QueryExpression: QuerySpecification { FromClause: not null } } => true,
            _ => false,
        };

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

            if (enteringState is { OpenSite: { } doomedOpenSite, XactAbortOn: true })
            {
                foreach (var catchStatement in Unwrap(tryCatch.CatchStatements.Statements))
                {
                    if (catchStatement is CommitTransactionStatement commit)
                    {
                        Findings.Add(new TransactionHygieneFinding(
                            TransactionHygieneFindingKind.CommitAfterXactAbortDoomsTransaction,
                            sourcePath,
                            doomedOpenSite.StartLine,
                            doomedOpenSite.StartColumn,
                            commit.StartLine,
                            commit.StartColumn));
                    }
                }
            }

            var catchResult = AnalyzeSequential(tryCatch.CatchStatements.Statements, enteringState);

            return MergeBranches(tryResult, catchResult);
        }

        private static FlowState MergeBranches(FlowState a, FlowState b)
        {
            if (a.Declined || b.Declined)
            {
                return FlowState.DeclinedState;
            }

            var implicitTransactionsOn = a.ImplicitTransactionsOn && b.ImplicitTransactionsOn;
            var xactAbortOn = a.XactAbortOn && b.XactAbortOn;

            if (a.OpenSite is null && b.OpenSite is null)
            {
                return FlowState.NotTracking with { ImplicitTransactionsOn = implicitTransactionsOn, XactAbortOn = xactAbortOn };
            }

            if (a.OpenSite is null)
            {
                return b with { ImplicitTransactionsOn = implicitTransactionsOn, XactAbortOn = xactAbortOn };
            }

            if (b.OpenSite is null)
            {
                return a with { ImplicitTransactionsOn = implicitTransactionsOn, XactAbortOn = xactAbortOn };
            }

            return ReferenceEquals(a.OpenSite, b.OpenSite)
                ? a with { ImplicitTransactionsOn = implicitTransactionsOn, XactAbortOn = xactAbortOn }
                : FlowState.DeclinedState;
        }

        private static IList<TSqlStatement> ToStatementList(TSqlStatement statement) =>
            statement is BeginEndBlockStatement block ? block.StatementList.Statements : [statement];
    }
}
