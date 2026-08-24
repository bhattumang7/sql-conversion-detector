using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Common;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class DeadCodeScanner
{
    public static IReadOnlyList<DeadCodeFinding> Scan(SqlParseResult parseResult)
    {
        var visitor = new RoutineVisitor(parseResult.SourcePath);
        parseResult.Fragment.Accept(visitor);
        return
        [
            .. visitor.Findings
                .OrderBy(f => f.Kind)
                .ThenBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];
    }

    private sealed class RoutineVisitor(string sourcePath) : TSqlFragmentVisitor
    {
        public List<DeadCodeFinding> Findings { get; } = [];

        public override void ExplicitVisit(CreateProcedureStatement node) =>
            Analyze(SchemaObjectNameHelper.Qualify(node.ProcedureReference.Name), node.Parameters, node.StatementList);

        public override void ExplicitVisit(AlterProcedureStatement node) =>
            Analyze(SchemaObjectNameHelper.Qualify(node.ProcedureReference.Name), node.Parameters, node.StatementList);

        public override void ExplicitVisit(CreateOrAlterProcedureStatement node) =>
            Analyze(SchemaObjectNameHelper.Qualify(node.ProcedureReference.Name), node.Parameters, node.StatementList);

        public override void ExplicitVisit(CreateTriggerStatement node) =>
            Analyze(SchemaObjectNameHelper.Qualify(node.Name), [], node.StatementList);

        public override void ExplicitVisit(AlterTriggerStatement node) =>
            Analyze(SchemaObjectNameHelper.Qualify(node.Name), [], node.StatementList);

        public override void ExplicitVisit(CreateOrAlterTriggerStatement node) =>
            Analyze(SchemaObjectNameHelper.Qualify(node.Name), [], node.StatementList);

        private void Analyze(string moduleName, IList<ProcedureParameter> parameters, StatementList? statementList)
        {

            if (statementList is null)
            {
                return;
            }

            var statements = Unwrap(statementList);

            var collector = new UsageCollector();
            foreach (var statement in statements)
            {
                statement.Accept(collector);
            }

            collector.RedundantJumps.AddRange(UsageCollector.FindRedundantJumps(statements));

            foreach (var parameter in parameters.Where(p => p.Modifier != ParameterModifier.Output))
            {
                var name = parameter.VariableName.Value;
                if (!collector.RealUses.Contains(name))
                {
                    Findings.Add(new DeadCodeFinding(
                        DeadCodeFindingKind.UnusedParameter,
                        moduleName, sourcePath,
                        parameter.StartLine, parameter.StartColumn,
                        name));
                }
            }

            foreach (var (name, line, column) in collector.DeclaredVariables)
            {
                if (!collector.RealUses.Contains(name))
                {
                    Findings.Add(new DeadCodeFinding(
                        DeadCodeFindingKind.UnusedLocalVariable,
                        moduleName, sourcePath, line, column, name));
                }
            }

            foreach (var (name, line, column) in collector.Labels)
            {
                if (!collector.GotoTargets.Contains(name))
                {
                    Findings.Add(new DeadCodeFinding(
                        DeadCodeFindingKind.UnusedLabel,
                        moduleName, sourcePath, line, column, name,
                        FindingConfidence.High));
                }
            }

            Findings.AddRange(collector.RedundantJumps.Select(j =>
                new DeadCodeFinding(DeadCodeFindingKind.RedundantJump, moduleName, sourcePath, j.Line, j.Column, j.Label, FindingConfidence.High)));

            if (collector.Labels.Count == 0 && collector.GotoCount == 0)
            {
                var reachability = new ReachabilityWalker(moduleName, sourcePath);
                reachability.AnalyzeSequential(statements);
                Findings.AddRange(reachability.Findings);
            }
        }

        private static IList<TSqlStatement> Unwrap(StatementList statementList) =>
            statementList.Statements is [BeginEndBlockStatement singleBlock]
                ? singleBlock.StatementList.Statements
                : statementList.Statements;
    }

    private sealed class UsageCollector : TSqlFragmentVisitor
    {
        public List<(string Name, int Line, int Column)> DeclaredVariables { get; } = [];

        public HashSet<string> RealUses { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<(string Name, int Line, int Column)> Labels { get; } = [];

        public HashSet<string> GotoTargets { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int GotoCount { get; private set; }

        public List<(string Label, int Line, int Column)> RedundantJumps { get; } = [];

        public override void ExplicitVisit(DeclareVariableStatement node)
        {
            foreach (var element in node.Declarations)
            {
                DeclaredVariables.Add((element.VariableName.Value, element.VariableName.StartLine, element.VariableName.StartColumn));

                element.Value?.Accept(this);
            }
        }

        public override void ExplicitVisit(SetVariableStatement node)
        {

            if (node.AssignmentKind != AssignmentKind.Equals)
            {
                node.Variable.Accept(this);
            }

            node.Expression?.Accept(this);
            node.CursorDefinition?.Accept(this);
            node.Identifier?.Accept(this);
            foreach (var parameter in node.Parameters)
            {
                parameter.Accept(this);
            }
        }

        public override void ExplicitVisit(SelectSetVariable node)
        {

            node.Expression?.Accept(this);
        }

        public override void ExplicitVisit(VariableReference node) => RealUses.Add(node.Name);

        public override void ExplicitVisit(LabelStatement node)
        {
            var name = node.Value.TrimEnd(':');
            Labels.Add((name, node.StartLine, node.StartColumn));
        }

        public override void ExplicitVisit(GoToStatement node)
        {
            GotoCount++;
            GotoTargets.Add(node.LabelName.Value);
        }

        public override void ExplicitVisit(StatementList node)
        {
            foreach (var statement in node.Statements)
            {
                statement.Accept(this);
            }

            RedundantJumps.AddRange(FindRedundantJumps(node.Statements));
        }

        public static IEnumerable<(string Label, int Line, int Column)> FindRedundantJumps(IList<TSqlStatement> statements)
        {
            for (var i = 0; i < statements.Count - 1; i++)
            {
                if (statements[i] is GoToStatement gotoStatement
                    && statements[i + 1] is LabelStatement nextLabel
                    && string.Equals(nextLabel.Value.TrimEnd(':'), gotoStatement.LabelName.Value, StringComparison.OrdinalIgnoreCase))
                {
                    yield return (gotoStatement.LabelName.Value, gotoStatement.StartLine, gotoStatement.StartColumn);
                }
            }
        }
    }

    private sealed class ReachabilityWalker(string moduleName, string sourcePath)
    {
        public List<DeadCodeFinding> Findings { get; } = [];

        public bool AnalyzeSequential(IList<TSqlStatement> statements)
        {
            var terminal = false;
            foreach (var statement in statements)
            {
                if (terminal)
                {
                    Findings.Add(new DeadCodeFinding(
                        DeadCodeFindingKind.UnreachableCode,
                        moduleName, sourcePath,
                        statement.StartLine, statement.StartColumn,
                        DetailText: null,
                        FindingConfidence.High));

                    return true;
                }

                terminal = AnalyzeStatement(statement);
            }

            return terminal;
        }

        private bool AnalyzeStatement(TSqlStatement statement)
        {
            switch (statement)
            {
                case ReturnStatement or ThrowStatement:
                    return true;

                case BeginEndBlockStatement block:
                    return AnalyzeSequential(block.StatementList.Statements);

                case IfStatement ifStatement:
                    var thenTerminal = AnalyzeSequential(ToStatementList(ifStatement.ThenStatement));

                    if (ifStatement.ElseStatement is null)
                    {
                        return false;
                    }

                    var elseTerminal = AnalyzeSequential(ToStatementList(ifStatement.ElseStatement));
                    return thenTerminal && elseTerminal;

                case WhileStatement whileStatement:

                    AnalyzeSequential(ToStatementList(whileStatement.Statement));
                    return false;

                case TryCatchStatement tryCatch:
                    var tryTerminal = AnalyzeSequential(tryCatch.TryStatements.Statements);
                    var catchTerminal = AnalyzeSequential(tryCatch.CatchStatements.Statements);
                    return tryTerminal && catchTerminal;

                default:
                    return false;
            }
        }

        private static IList<TSqlStatement> ToStatementList(TSqlStatement statement) =>
            statement is BeginEndBlockStatement block ? block.StatementList.Statements : [statement];
    }
}
