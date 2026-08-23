using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Parsing;
using SilentScan.Core.TypeInference;
using SilentScan.Core.Common;

namespace SilentScan.Core.Predicates;

public static class ProcCallGraphBuilder
{
    public static ProcCallGraph Build(IEnumerable<SqlParseResult> parseResults, DatabaseCatalog catalog, SkipLedger ledger)
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

        private readonly Dictionary<string, SqlType?> _variableTypes = new(StringComparer.OrdinalIgnoreCase);

        public List<ProcCallEdge> Edges { get; } = [];

        public override void ExplicitVisit(DeclareVariableStatement node)
        {
            foreach (var declaration in node.Declarations)
            {
                _variableTypes[declaration.VariableName.Value] =
                    Parsing.SqlTypeReferenceResolver.Resolve(declaration.DataType, columnCollation: null, catalog.TypeAliases);
            }

            node.AcceptChildren(this);
        }

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
            var previousVariableTypes = new Dictionary<string, SqlType?>(_variableTypes, StringComparer.OrdinalIgnoreCase);
            _currentScope = SchemaObjectNameHelper.Qualify(name);

            _variableTypes.Clear();
            if (catalog.TryGetProcedureParameters(_currentScope, out var ownFormalParameters))
            {
                foreach (var parameter in ownFormalParameters)
                {
                    _variableTypes[parameter.Name] = parameter.Type;
                }
            }

            _currentScopeStatements = statementList?.Statements is [BeginEndBlockStatement singleBlock]
                ? singleBlock.StatementList.Statements
                : statementList?.Statements;

            statementList?.AcceptChildren(this);
            _currentScope = previous;
            _currentScopeStatements = previousStatements;
            _variableTypes.Clear();
            foreach (var (variableName, variableType) in previousVariableTypes)
            {
                _variableTypes[variableName] = variableType;
            }
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

            var arguments = MatchArguments(procedureReference.Parameters, formalParameters, sourcePath, _currentScopeStatements, node, _variableTypes);
            Edges.Add(new ProcCallEdge(_currentScope, qualifiedName, new SourceSpan(sourcePath, node.StartLine, node.StartColumn), arguments));
        }

private static List<ProcCallArgument> MatchArguments(
            IList<ExecuteParameter> actualParameters, IReadOnlyList<ProcedureParameterInfo> formalParameters, string sourcePath,
            IList<TSqlStatement>? currentScopeStatements, ExecuteStatement callSite, IReadOnlyDictionary<string, SqlType?> variableTypes)
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
                var literalArgument = TryGetDirectLiteralArgument(actual.ParameterValue, sourcePath)
                    ?? ResolvePropagatedLiteral(callerVariableName, currentScopeStatements, sourcePath, callSite);
                var callerArgumentType = callerVariableName is not null
                    ? variableTypes.GetValueOrDefault(callerVariableName)
                    : null;
                matched.Add(new ProcCallArgument(
                    formal.Name, formal.Type, formal.IsOutput, callerVariableName, actual.ParameterValue is Literal, literalArgument,
                    callerArgumentType));
            }

            return matched;
        }

private static ProcCallLiteralArgument? TryGetDirectLiteralArgument(ScalarExpression parameterValue, string sourcePath) => parameterValue switch
        {
            StringLiteral stringLiteral => ToLiteralArgument(stringLiteral, sourcePath),
            IntegerLiteral integerLiteral => ToIntegerLiteralArgument(integerLiteral, sourcePath),
            _ => null,
        };

        private static ProcCallLiteralArgument? ResolvePropagatedLiteral(
            string? callerVariableName, IList<TSqlStatement>? currentScopeStatements, string sourcePath, ExecuteStatement callSite) =>
            callerVariableName is not null && currentScopeStatements is not null
                ? TryResolveTopLevelLiteral(currentScopeStatements, callerVariableName, sourcePath, callSite)
                : null;

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

                everAssigned = true;
                current = literal;
            }

            return everAssigned ? current : null;
        }

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

                    literal = element.Value is null ? null : TryGetDirectLiteralArgument(element.Value, sourcePath);
                    return true;

                case SetVariableStatement set when string.Equals(set.Variable.Name, variableName, StringComparison.OrdinalIgnoreCase):
                    literal = set.AssignmentKind == AssignmentKind.Equals && set.Expression is not null
                        ? TryGetDirectLiteralArgument(set.Expression, sourcePath)
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

private static ProcCallLiteralArgument ToIntegerLiteralArgument(IntegerLiteral integerLiteral, string sourcePath) =>
            new(integerLiteral.Value, sourcePath, integerLiteral.StartLine, integerLiteral.StartColumn, PrefixLength: 0);

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
