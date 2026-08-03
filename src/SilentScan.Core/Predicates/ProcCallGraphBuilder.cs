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
/// excluded - that's <see cref="DynamicSqlScanner"/>'s own concern, with its own argument-binding
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
            _currentScope = SchemaObjectNameHelper.Qualify(name);

            // StatementList is null for an EXTERNAL NAME (CLR) body - nothing to walk.
            statementList?.AcceptChildren(this);
            _currentScope = previous;
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

            var arguments = MatchArguments(procedureReference.Parameters, formalParameters);
            Edges.Add(new ProcCallEdge(_currentScope, qualifiedName, new SourceSpan(sourcePath, node.StartLine, node.StartColumn), arguments));
        }

        /// <summary>
        /// A named argument (<c>@P = value</c>) matches its formal by name, wherever it appears;
        /// every other argument matches by position among the REMAINING (not-yet-named) formals,
        /// in the order it appears - T-SQL itself requires every positional argument before any
        /// named one, so a simple left-to-right position counter over the un-named formals is
        /// exact, not an approximation.
        /// </summary>
        private static List<ProcCallArgument> MatchArguments(IList<ExecuteParameter> actualParameters, IReadOnlyList<ProcedureParameterInfo> formalParameters)
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
                matched.Add(new ProcCallArgument(formal.Name, formal.Type, formal.IsOutput, callerVariableName, actual.ParameterValue is Literal));
            }

            return matched;
        }
    }
}
