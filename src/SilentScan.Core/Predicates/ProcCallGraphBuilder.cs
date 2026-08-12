using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

/// <summary>
/// Builds the procedure call graph: every <c>EXEC dbo.SomeProc @arg1, @P2 = @arg2</c> call site
/// whose target resolved to a procedure this scan actually saw a <c>CREATE/ALTER PROCEDURE</c>
/// for (<see cref="DatabaseCatalog.TryGetProcedureParameters"/>), with each actual argument
/// matched to the callee's own declared formal parameter. <c>sp_executesql</c> is deliberately
/// excluded - that's the dynamic SQL engine's own concern, with its own argument-binding
/// mechanism (<see cref="DynamicSqlScript.ArgumentBindings"/>). A call site whose target can't be
/// resolved to a known procedure (a system proc, a synonym pointing nowhere cataloged, or a name
/// this scan never saw declared) is ledgered rather than silently producing no edge - CLAUDE.md's
/// "never silently counted as clean" applies to a call graph exactly as much as to a predicate.
/// This pass only supplies the raw graph; using it to seed a callee's own parameter types or to
/// trace a constant value across a call edge are separate, later concerns.
/// </summary>
public static class ProcCallGraphBuilder
{
    public static ProcCallGraph Build(IReadOnlyList<SqlParseResult> parseResults, DatabaseCatalog catalog, SkipLedger ledger)
    {
        var edges = new List<ProcCallEdge>();
        foreach (var result in parseResults)
        {
            var visitor = new Visitor(catalog, ledger, result.SourcePath);
            result.Fragment.Accept(visitor);
            edges.AddRange(visitor.Edges);
        }

        return new ProcCallGraph(edges);
    }

    private const string CallGraphConstructKind = "procedure call graph edge";

    private sealed class Visitor(DatabaseCatalog catalog, SkipLedger ledger, string sourcePath) : TSqlFragmentVisitor
    {
        private string? _currentScope;
        private IList<TSqlStatement>? _currentScopeStatements;

        public List<ProcCallEdge> Edges { get; } = [];

        public override void ExplicitVisit(CreateProcedureStatement node) => VisitScopedBody(node.ProcedureReference.Name, node.StatementList);

        public override void ExplicitVisit(AlterProcedureStatement node) => VisitScopedBody(node.ProcedureReference.Name, node.StatementList);

        public override void ExplicitVisit(CreateOrAlterProcedureStatement node) => VisitScopedBody(node.ProcedureReference.Name, node.StatementList);

        public override void ExplicitVisit(CreateFunctionStatement node) => VisitScopedBody(node.Name, node.StatementList);

        public override void ExplicitVisit(AlterFunctionStatement node) => VisitScopedBody(node.Name, node.StatementList);

        public override void ExplicitVisit(CreateOrAlterFunctionStatement node) => VisitScopedBody(node.Name, node.StatementList);

        public override void ExplicitVisit(CreateTriggerStatement node) => VisitScopedBody(node.Name, node.StatementList);

        public override void ExplicitVisit(AlterTriggerStatement node) => VisitScopedBody(node.Name, node.StatementList);

        public override void ExplicitVisit(CreateOrAlterTriggerStatement node) => VisitScopedBody(node.Name, node.StatementList);

        public override void ExplicitVisit(ExecuteStatement node)
        {
            if (node.ExecuteSpecification.ExecutableEntity is ExecutableProcedureReference
                {
                    ProcedureReference.ProcedureReference.Name: { } calleeName,
                } procedureReference
                && !string.Equals(calleeName.BaseIdentifier.Value, "sp_executesql", StringComparison.OrdinalIgnoreCase))
            {
                VisitProcedureCall(node, procedureReference, calleeName);
            }

            node.AcceptChildren(this);
        }

        private void VisitScopedBody(SchemaObjectName name, StatementList? statementList)
        {
            var previous = _currentScope;
            var previousStatements = _currentScopeStatements;
            _currentScope = SchemaObjectNameHelper.Qualify(name);

            // The overwhelmingly common `AS BEGIN ... END` shape wraps the WHOLE body in a
            // single BeginEndBlockStatement - without unwrapping it, "the scope's own top-level
            // statements" would be a one-element list containing just that wrapper, and every
            // DECLARE/SET/EXEC actually in the body (all one level deeper) would look like it
            // sits inside a nested block, never straight-line, to TryResolveTopLevelLiteral.
            _currentScopeStatements = statementList?.Statements is [BeginEndBlockStatement singleBlock]
                ? singleBlock.StatementList.Statements
                : statementList?.Statements;

            // StatementList is null for an EXTERNAL NAME (CLR) body - nothing to walk.
            statementList?.AcceptChildren(this);
            _currentScope = previous;
            _currentScopeStatements = previousStatements;
        }

        private void VisitProcedureCall(ExecuteStatement node, ExecutableProcedureReference procedureReference, SchemaObjectName calleeName)
        {
            var qualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(calleeName));
            if (!catalog.TryGetProcedureParameters(qualifiedName, out var formalParameters))
            {
                ledger.Record(
                    AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, CallGraphConstructKind,
                    $"EXEC target '{qualifiedName}' is not a procedure this scan saw declared - call graph edge not recorded");
                return;
            }

            var arguments = MatchArguments(procedureReference.Parameters, formalParameters, sourcePath, _currentScopeStatements, node);
            Edges.Add(new ProcCallEdge(_currentScope, qualifiedName, new SourceSpan(sourcePath, node.StartLine, node.StartColumn), arguments));
        }

        /// <summary>
        /// A named argument (<c>@P = value</c>) matches its formal by name, wherever it appears;
        /// every other argument matches by position among the REMAINING (not-yet-named) formals,
        /// in the order it appears - T-SQL itself requires every positional argument before any
        /// named one, so a simple left-to-right position counter over the un-named formals is
        /// exact, not an approximation.
        /// </summary>
        private static List<ProcCallArgument> MatchArguments(
            IList<ExecuteParameter> actualParameters, IReadOnlyList<ProcedureParameterInfo> formalParameters, string sourcePath,
            IList<TSqlStatement>? currentScopeStatements, ExecuteStatement callSite)
        {
            var byName = formalParameters.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
            var positionalCursor = 0;
            var matched = new List<ProcCallArgument>(actualParameters.Count);

            foreach (var actual in actualParameters)
            {
                ProcedureParameterInfo? formal = null;
                if (actual.Variable is { } namedFormal && byName.TryGetValue(namedFormal.Name, out var byNameFormal))
                {
                    formal = byNameFormal;
                }
                else if (actual.Variable is null && positionalCursor < formalParameters.Count)
                {
                    formal = formalParameters[positionalCursor];
                    positionalCursor++;
                }

                if (formal is null)
                {
                    continue;
                }

                var callerVariableName = actual.ParameterValue is VariableReference variableRef ? variableRef.Name : null;
                var literalArgument = actual.ParameterValue is StringLiteral stringLiteral
                    ? ToLiteralArgument(stringLiteral, sourcePath)
                    : ResolvePropagatedLiteral(callerVariableName, currentScopeStatements, sourcePath, callSite);
                matched.Add(new ProcCallArgument(
                    formal.Name, formal.Type, formal.IsOutput, callerVariableName, actual.ParameterValue is Literal, literalArgument));
            }

            return matched;
        }

        private static ProcCallLiteralArgument? ResolvePropagatedLiteral(
            string? callerVariableName, IList<TSqlStatement>? currentScopeStatements, string sourcePath, ExecuteStatement callSite) =>
            callerVariableName is not null && currentScopeStatements is not null
                ? TryResolveTopLevelLiteral(currentScopeStatements, callerVariableName, sourcePath, callSite)
                : null;

        /// <summary>
        /// One-level constant propagation (CLAUDE.md roadmap: trace a caller variable's own
        /// literal assignment back through a proc-call edge) for a bare variable argument -
        /// <c>DECLARE @v NVARCHAR(20) = N'Active'; EXEC callee @v;</c> resolves to 'Active' the
        /// same way passing the literal directly would. Deliberately conservative on one front,
        /// required for this to be a proven fact rather than a guess: <paramref
        /// name="variableName"/> must never be written inside ANY nested IF/WHILE/TRY anywhere in
        /// this scope (<see cref="ConditionallyWrittenVariableCollector"/>) - a conditional write
        /// means the value at any given point depends on which branch ran, which this pass has no
        /// fold-state tracking to resolve. Beyond that, this walks the scope's OWN top-level
        /// statements in program order UP TO (not including) the top-level statement that itself
        /// contains <paramref name="callSite"/> (<see cref="FindTopLevelStatementIndex"/>) and
        /// takes the LAST literal assignment found there - T-SQL executes top-to-bottom, so a
        /// later top-level SET always overwrites an earlier one by the time the call actually
        /// runs, exactly like two agreeing (or disagreeing) literals aren't actually ambiguous
        /// once WHERE the call sits relative to them is known. A non-literal assignment anywhere
        /// in that prefix still poisons everything at-or-before it (the variable's value there is
        /// genuinely unknown, not just unproven) unless a LATER literal assignment in the same
        /// prefix overwrites it again.
        /// </summary>
        private static ProcCallLiteralArgument? TryResolveTopLevelLiteral(
            IList<TSqlStatement> scopeStatements, string variableName, string sourcePath, ExecuteStatement callSite)
        {
            if (IsWrittenInsideConditional(scopeStatements, variableName))
            {
                return null;
            }

            var callIndex = FindTopLevelStatementIndex(scopeStatements, callSite);
            return FindLastLiteralAssignmentBeforeCall(scopeStatements, variableName, sourcePath, callIndex);
        }

        /// <summary>
        /// The index of the top-level statement in <paramref name="scopeStatements"/> that either
        /// IS <paramref name="target"/> or contains it as a descendant (identified by (line,
        /// column) span containment - ScriptDOM guarantees a nested fragment's own start position
        /// falls strictly within its container's, T-SQL having no non-linear text layout) - or
        /// <paramref name="scopeStatements"/>.Count if none contains it (defensive: should not
        /// happen for a call site genuinely reached via this same scope's own traversal).
        /// </summary>
        private static int FindTopLevelStatementIndex(IList<TSqlStatement> scopeStatements, TSqlFragment target)
        {
            for (var i = 0; i < scopeStatements.Count; i++)
            {
                if (Contains(scopeStatements[i], target))
                {
                    return i;
                }
            }

            return scopeStatements.Count;
        }

        private static bool Contains(TSqlFragment container, TSqlFragment target)
        {
            var containerEnd = EndPosition(container);
            var targetStart = (target.StartLine, target.StartColumn);
            return (container.StartLine, container.StartColumn).CompareTo(targetStart) <= 0
                && targetStart.CompareTo(containerEnd) <= 0;
        }

        private static (int Line, int Column) EndPosition(TSqlFragment fragment)
        {
            if (fragment.ScriptTokenStream is null || fragment.LastTokenIndex < 0 || fragment.LastTokenIndex >= fragment.ScriptTokenStream.Count)
            {
                return (fragment.StartLine, fragment.StartColumn);
            }

            var lastToken = fragment.ScriptTokenStream[fragment.LastTokenIndex];
            return (lastToken.Line, lastToken.Column);
        }

        private static bool IsWrittenInsideConditional(IList<TSqlStatement> scopeStatements, string variableName)
        {
            var poisoned = new ConditionallyWrittenVariableCollector();
            foreach (var statement in scopeStatements)
            {
                statement.Accept(poisoned);
            }

            return poisoned.Names.Contains(variableName);
        }

        /// <summary>
        /// One pass over the scope's own top-level statements STRICTLY BEFORE <paramref
        /// name="callIndex"/>, tracking the LAST DECLARE/SET that targets <paramref
        /// name="variableName"/> - T-SQL executes top-to-bottom, so a later assignment always
        /// overwrites an earlier one by the time the call itself runs; earlier assignments (agreeing
        /// or not) are no longer ambiguous once WHERE the call sits relative to them is known.
        /// </summary>
        private static ProcCallLiteralArgument? FindLastLiteralAssignmentBeforeCall(
            IList<TSqlStatement> scopeStatements, string variableName, string sourcePath, int callIndex)
        {
            ProcCallLiteralArgument? current = null;
            var everAssigned = false;

            for (var i = 0; i < callIndex && i < scopeStatements.Count; i++)
            {
                if (!TryGetAssignmentToVariable(scopeStatements[i], variableName, sourcePath, out var literal))
                {
                    continue;
                }

                // A real assignment (DECLARE's own initializer, or a SET) whose RHS this pass
                // can't fold to a literal makes the variable's value genuinely unknown from here
                // on - `current` becomes null (literal is null in that case) exactly like the
                // fold-declined case, but a LATER literal assignment before the call can still
                // overwrite it again, so this keeps walking rather than giving up outright.
                everAssigned = true;
                current = literal;
            }

            return everAssigned ? current : null;
        }

        /// <summary>
        /// True (with <paramref name="literal"/> set, possibly to null) when <paramref
        /// name="statement"/> is a DECLARE or SET that targets <paramref name="variableName"/> -
        /// null literal means the assignment exists but isn't a foldable string literal (a
        /// non-literal RHS, or a <c>+=</c>/other non-plain assignment kind), which the caller
        /// treats identically to "found a real assignment, but not a literal one".
        /// </summary>
        private static bool TryGetAssignmentToVariable(TSqlStatement statement, string variableName, string sourcePath, out ProcCallLiteralArgument? literal)
        {
            literal = null;
            switch (statement)
            {
                case DeclareVariableStatement declare:
                    var element = declare.Declarations.FirstOrDefault(
                        e => string.Equals(e.VariableName.Value, variableName, StringComparison.OrdinalIgnoreCase));
                    if (element is null)
                    {
                        return false;
                    }

                    literal = element.Value is StringLiteral stringLiteral ? ToLiteralArgument(stringLiteral, sourcePath) : null;
                    return true;

                case SetVariableStatement set when string.Equals(set.Variable.Name, variableName, StringComparison.OrdinalIgnoreCase):
                    literal = set.AssignmentKind == AssignmentKind.Equals && set.Expression is StringLiteral setLiteral
                        ? ToLiteralArgument(setLiteral, sourcePath)
                        : null;
                    return true;

                default:
                    return false;
            }
        }

        private static ProcCallLiteralArgument ToLiteralArgument(StringLiteral stringLiteral, string sourcePath)
        {
            var prefixLength = stringLiteral.IsNational ? 2 : 1;
            return new ProcCallLiteralArgument(stringLiteral.Value, sourcePath, stringLiteral.StartLine, stringLiteral.StartColumn, prefixLength);
        }

        /// <summary>
        /// Every variable name written (DECLARE-with-value or SET) anywhere inside an IF/WHILE/
        /// TRY-CATCH subtree, at any nesting depth - the set <see cref="TryResolveTopLevelLiteral"/>
        /// refuses to trust, since a write inside a conditional construct means the value at any
        /// given later point depends on which branch actually ran, something this single-pass
        /// scan (no fold-state tracking the way the dynamic SQL engine's own reaching-definitions
        /// analysis has) cannot determine. A bare BEGIN/END block is NOT conditional and does not
        /// bump depth - only IF/WHILE/TRY genuinely branch.
        /// </summary>
        private sealed class ConditionallyWrittenVariableCollector : TSqlFragmentVisitor
        {
            private int _conditionalDepth;

            public HashSet<string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);

            public override void ExplicitVisit(IfStatement node)
            {
                _conditionalDepth++;
                node.AcceptChildren(this);
                _conditionalDepth--;
            }

            public override void ExplicitVisit(WhileStatement node)
            {
                _conditionalDepth++;
                node.AcceptChildren(this);
                _conditionalDepth--;
            }

            public override void ExplicitVisit(TryCatchStatement node)
            {
                _conditionalDepth++;
                node.AcceptChildren(this);
                _conditionalDepth--;
            }

            public override void ExplicitVisit(DeclareVariableStatement node)
            {
                if (_conditionalDepth == 0)
                {
                    return;
                }

                foreach (var element in node.Declarations)
                {
                    Names.Add(element.VariableName.Value);
                }
            }

            public override void ExplicitVisit(SetVariableStatement node)
            {
                if (_conditionalDepth > 0)
                {
                    Names.Add(node.Variable.Name);
                }
            }
        }
    }
}
