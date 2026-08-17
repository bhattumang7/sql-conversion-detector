using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 4 "Dead and duplicated code" - see <see cref="DeadCodeFinding"/>
/// for the full scope, precision-guard, and severity documentation.
/// </summary>
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

    private static string QualifiedName(SchemaObjectName name) =>
        name.SchemaIdentifier is { } schema
            ? $"{schema.Value}.{name.BaseIdentifier.Value}"
            : name.BaseIdentifier.Value;

    private static IList<TSqlStatement> Unwrap(StatementList statementList) =>
        statementList.Statements is [BeginEndBlockStatement singleBlock]
            ? singleBlock.StatementList.Statements
            : statementList.Statements;

    private sealed class RoutineVisitor(string sourcePath) : TSqlFragmentVisitor
    {
        public List<DeadCodeFinding> Findings { get; } = [];

        public override void ExplicitVisit(CreateProcedureStatement node) =>
            Analyze(QualifiedName(node.ProcedureReference.Name), node.Parameters, node.StatementList);

        public override void ExplicitVisit(AlterProcedureStatement node) =>
            Analyze(QualifiedName(node.ProcedureReference.Name), node.Parameters, node.StatementList);

        public override void ExplicitVisit(CreateOrAlterProcedureStatement node) =>
            Analyze(QualifiedName(node.ProcedureReference.Name), node.Parameters, node.StatementList);

        public override void ExplicitVisit(CreateTriggerStatement node) =>
            Analyze(QualifiedName(node.Name), [], node.StatementList);

        public override void ExplicitVisit(AlterTriggerStatement node) =>
            Analyze(QualifiedName(node.Name), [], node.StatementList);

        public override void ExplicitVisit(CreateOrAlterTriggerStatement node) =>
            Analyze(QualifiedName(node.Name), [], node.StatementList);

        private void Analyze(string moduleName, IList<ProcedureParameter> parameters, StatementList? statementList)
        {
            // StatementList is null for an EXTERNAL NAME (CLR) body - nothing to walk.
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

            // The routine's own OUTERMOST statement list is never itself Accept()-ed (Unwrap
            // above extracts its raw .Statements to look past a single wrapping BEGIN...END, so
            // there is no StatementList fragment left to visit at the top level) - the collector's
            // own ExplicitVisit(StatementList) override only ever sees NESTED lists as a result,
            // so the adjacent-pair redundant-jump check must also run explicitly here for the
            // top-level sequence, or a redundant jump sitting directly in the routine body (not
            // inside any IF/WHILE/TRY block) would be silently missed.
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

            // An arbitrary GOTO/label target can make structurally-unreachable code actually
            // reachable - decline the whole routine's reachability walk rather than guess
            // (see DeadCodeFinding's own doc comment; the same discipline TransactionHygieneScanner
            // already applies).
            if (collector.Labels.Count == 0 && collector.GotoCount == 0)
            {
                var reachability = new ReachabilityWalker(moduleName, sourcePath);
                reachability.AnalyzeSequential(statements);
                Findings.AddRange(reachability.Findings);
            }
        }
    }

    /// <summary>Collects, in one pass over a single routine's own body, every declared local
    /// variable, every label/GOTO, every "real" (non-pure-write) variable reference, and every
    /// redundant-jump shape - see <see cref="DeadCodeFinding"/>'s doc comment for the exact
    /// "what counts as a use" precision guard.</summary>
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

                // The initializer expression (`DECLARE @x INT = @y + 1`) is a real read of
                // whatever IT references - visit it normally.
                element.Value?.Accept(this);
            }
        }

        public override void ExplicitVisit(SetVariableStatement node)
        {
            // A simple `SET @x = expr` is a pure write of @x - only visit the source expression
            // (and any other real sub-fragments), not the target itself, so @x's own occurrence
            // here never counts as a "use". A compound assignment (`SET @x += expr`) reads @x's
            // prior value too, so it counts - visit the target normally in that case.
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
            // Pure write of node.Variable, matching SET @x = expr's reasoning above - only the
            // source expression is a real "use". Deliberately does not call base.ExplicitVisit,
            // so node.Variable itself is never visited/counted here.
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

        /// <summary>A GOTO whose target label is the very next statement in this exact sequence -
        /// jumping to exactly where control flow would already go. Shared between the visitor's
        /// own nested-StatementList traversal above and the routine's outermost (unwrapped, never
        /// itself Accept()-ed) statement sequence in <see cref="RoutineVisitor.Analyze"/>.</summary>
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

    /// <summary>A sound (never-guess) terminality/reachability walk, the same CFG-walking
    /// discipline <see cref="TransactionHygieneScanner"/> already established, adapted from
    /// tracking "is a transaction open" to tracking "does this path always end the routine".
    /// Only invoked on a routine already confirmed to contain no GOTO/label anywhere (see the
    /// caller in <see cref="RoutineVisitor"/>).</summary>
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

                    // One finding per contiguous dead region, not one per statement in it -
                    // everything else in this list is trivially unreachable too.
                    return true;
                }

                terminal = AnalyzeStatement(statement);
            }

            return terminal;
        }

        /// <summary>Returns true iff control flow can NEVER fall through past this single
        /// statement on any path (also recurses to flag unreachable code nested inside it).</summary>
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

                    // No ELSE: the implicit else path always falls through, so the IF as a whole
                    // is never terminal regardless of the THEN branch.
                    if (ifStatement.ElseStatement is null)
                    {
                        return false;
                    }

                    var elseTerminal = AnalyzeSequential(ToStatementList(ifStatement.ElseStatement));
                    return thenTerminal && elseTerminal;

                case WhileStatement whileStatement:
                    // A WHILE loop may run zero times, so code after it is always potentially
                    // reachable regardless of the body's own terminality - conservative, matching
                    // TransactionHygieneScanner's identical WHILE-is-never-terminal reasoning.
                    // Still recurse into the body for its own internal unreachable-code findings.
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
