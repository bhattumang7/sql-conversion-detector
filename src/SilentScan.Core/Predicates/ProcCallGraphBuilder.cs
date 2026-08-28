using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates.DynamicSqlValue;
using SilentScan.Core.TypeInference;
using SilentScan.Core.Common;

namespace SilentScan.Core.Predicates;

public static class ProcCallGraphBuilder
{
    public static ProcCallGraph Build(IEnumerable<SqlParseResult> parseResults, DatabaseCatalog catalog, SkipLedger ledger)
    {
        var edges = new List<ProcCallEdge>();
        var spExecuteSqlCallSites = new List<SpExecuteSqlCallSite>();
        foreach (var result in parseResults)
        {
            var visitor = new Visitor(catalog, ledger, result.SourcePath);
            result.Fragment.Accept(visitor);
            edges.AddRange(visitor.Edges);
            spExecuteSqlCallSites.AddRange(visitor.SpExecuteSqlCallSites);
        }

        return new ProcCallGraph(edges, spExecuteSqlCallSites);
    }

    private const string CallGraphConstructKind = "procedure call graph edge";
    private const string SpExecuteSqlName = "sp_executesql";

    private sealed class Visitor(DatabaseCatalog catalog, SkipLedger ledger, string sourcePath) : TSqlFragmentVisitor
    {
        private string? _currentScope;
        private IList<TSqlStatement>? _currentScopeStatements;

        private readonly Dictionary<string, SqlType?> _variableTypes = new(StringComparer.OrdinalIgnoreCase);

        public List<ProcCallEdge> Edges { get; } = [];

        public List<SpExecuteSqlCallSite> SpExecuteSqlCallSites { get; } = [];

        public override void ExplicitVisit(DeclareVariableStatement node)
        {
            foreach (var declaration in node.Declarations)
            {
                _variableTypes[declaration.VariableName.Value] =
                    Parsing.SqlTypeReferenceResolver.Resolve(declaration.DataType, columnCollation: null, catalog.TypeAliases);
            }

            node.AcceptChildren(this);
        }

        public override void ExplicitVisit(TSqlBatch node)
        {
            var previousStatements = _currentScopeStatements;
            _currentScopeStatements = FlattenBeginEndBlocks(node.Statements);
            _variableTypes.Clear();
            node.AcceptChildren(this);
            _currentScopeStatements = previousStatements;
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
            switch (node.ExecuteSpecification.ExecutableEntity)
            {
                case ExecutableProcedureReference
                {
                    ProcedureReference.ProcedureReference.Name: { } calleeName,
                } procedureReference
                    when !string.Equals(calleeName.BaseIdentifier.Value, SpExecuteSqlName, StringComparison.OrdinalIgnoreCase):
                    VisitProcedureCall(node, procedureReference, calleeName);
                    break;

                case ExecutableProcedureReference { ProcedureReference.ProcedureVariable: { } }:
                    RecordUnresolvableCall(node, "EXEC target is a variable holding the procedure name - resolved dynamically, not statically");
                    break;

                case ExecutableProcedureReference
                {
                    ProcedureReference.ProcedureReference.Name.BaseIdentifier.Value: var spExecutesqlName,
                    Parameters: [{ ParameterValue: { } statementExpression }, ..],
                } spExecuteSqlCall when string.Equals(spExecutesqlName, SpExecuteSqlName, StringComparison.OrdinalIgnoreCase):
                    TryFoldDynamicCall(node, [statementExpression]);
                    TryRecordSpExecuteSqlParameterBindings(node, spExecuteSqlCall.Parameters);
                    break;

                case ExecutableProcedureReference
                {
                    ProcedureReference.ProcedureReference.Name.BaseIdentifier.Value: var spExecutesqlName,
                } when string.Equals(spExecutesqlName, SpExecuteSqlName, StringComparison.OrdinalIgnoreCase):
                    RecordUnresolvableCall(node, "EXEC target is sp_executesql - the executed statement text is dynamic, not a statically known procedure");
                    break;

                case ExecutableStringList { Strings.Count: > 0 } stringList:
                    TryFoldDynamicCall(node, [.. stringList.Strings]);
                    break;

                case ExecutableStringList:
                    RecordUnresolvableCall(node, "EXEC target is a dynamic SQL string, not a statically known procedure");
                    break;
            }

            node.AcceptChildren(this);
        }

        private const int DynamicSqlFoldCap = 32;

        private void TryFoldDynamicCall(ExecuteStatement node, IReadOnlyList<ScalarExpression> statementExpressions)
        {
            var site = new SourceSpan(sourcePath, node.StartLine, node.StartColumn);
            var state = ResolveVariableStateAtCallSite(node);

            SqlTextValue combined = new SqlTextValue.Template([]);
            foreach (var expression in statementExpressions)
            {
                combined = SqlTextValue.Concat(combined, ExpressionEvaluator.Fold(expression, state, sourcePath, DynamicSqlFoldCap, catalog));
            }

            if (SqlTextValue.Widen(combined, DynamicSqlFoldCap, site) is not SqlTextValue.Template widenedTemplate)
            {
                RecordUnresolvableCall(node, "dynamic SQL statement text does not fold to a deterministic literal string - call graph edge not recorded");
                return;
            }

            var assemblies = SqlTextValue.Expand(widenedTemplate, DynamicSqlFoldCap);
            if (assemblies.Count != 1 || SqlTextValue.ContainsHole(assemblies[0]))
            {
                RecordUnresolvableCall(node, "dynamic SQL statement text does not fold to a single deterministic literal string - call graph edge not recorded");
                return;
            }

            var rendered = TemplateRenderer.Render(assemblies[0]);
            if (!TryResolveFoldedProcedureCall(rendered.InnerText, out var innerProcedureReference))
            {
                RecordUnresolvableCall(node, "dynamic SQL literal does not fold to a single statically named EXEC call");
                return;
            }

            var qualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(innerProcedureReference.ProcedureReference.ProcedureReference.Name));
            if (!catalog.TryGetProcedureParameters(qualifiedName, out var formalParameters))
            {
                ledger.Record(
                    AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, CallGraphConstructKind,
                    $"EXEC target '{qualifiedName}' folded from a dynamic SQL string is not a procedure this scan saw declared - call graph edge not recorded");
                return;
            }

            var matched = MatchFoldedArguments(innerProcedureReference.Parameters, formalParameters, rendered.SegmentMap);
            Edges.Add(new ProcCallEdge(_currentScope, qualifiedName, site, matched));
        }

        private bool TryResolveFoldedProcedureCall(string innerText, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ExecutableProcedureReference? procedureReference)
        {
            procedureReference = null;
            var parseResult = SqlScriptParser.ParseText(sourcePath, innerText);
            if (parseResult.Errors.Count > 0
                || parseResult.Fragment is not TSqlScript { Batches: [{ Statements: [ExecuteStatement innerExecute] }] }
                || innerExecute.ExecuteSpecification.ExecutableEntity is not ExecutableProcedureReference
                {
                    ProcedureReference.ProcedureReference.Name: { } innerCalleeName,
                    ProcedureReference.ProcedureVariable: null,
                } candidate
                || string.Equals(innerCalleeName.BaseIdentifier.Value, SpExecuteSqlName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            procedureReference = candidate;
            return true;
        }

        private const string SpExecuteSqlParameterConstructKind = "sp_executesql inline parameter type";
        private const string SpExecuteSqlSyntheticParameterListName = "dbo.__silentscan_sp_executesql_params";

        private void TryRecordSpExecuteSqlParameterBindings(ExecuteStatement node, IList<ExecuteParameter> parameters)
        {
            if (parameters.Count < 2)
            {
                return;
            }

            if (parameters[1].ParameterValue is not StringLiteral definitionLiteral)
            {
                ledger.Record(
                    AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, SpExecuteSqlParameterConstructKind,
                    "sp_executesql's parameter-definition argument is not a literal string - inline parameter types not statically decidable");
                return;
            }

            if (!TryParseParameterDefinitions(definitionLiteral.Value, out var declaredParameters))
            {
                ledger.Record(
                    AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, SpExecuteSqlParameterConstructKind,
                    "sp_executesql's parameter-definition string did not parse as a declared parameter list - inline parameter types not resolved");
                return;
            }

            var byName = declaredParameters.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
            var bindings = new List<SpExecuteSqlParameterBinding>();

            for (var i = 2; i < parameters.Count; i++)
            {
                var actual = parameters[i];
                if (actual.Variable is not { } namedFormal || !byName.TryGetValue(namedFormal.Name, out var declared))
                {
                    continue;
                }

                var callerVariableName = actual.ParameterValue is VariableReference variableRef ? variableRef.Name : null;
                var callerArgumentType = ScalarExpressionResolver.ResolveScalarType(
                    actual.ParameterValue, [], sourcePath,
                    new ScalarExpressionResolver.ScalarTypeContext(null, catalog.TypeAliases, catalog, _variableTypes));
                var assignmentFlow = callerVariableName is null
                    ? new ScopeVariableFlowTracker.AssignmentFlow(Assigned: true, Approximate: false)
                    : ScopeVariableFlowTracker.WasAssignedBeforeCall(_currentScopeStatements, callerVariableName, node);

                bindings.Add(new SpExecuteSqlParameterBinding(
                    declared.Name, declared.Type, declared.IsOutput, callerVariableName, callerArgumentType,
                    actual.IsOutput, actual.ParameterValue, assignmentFlow.Assigned, assignmentFlow.Approximate));
            }

            if (bindings.Count > 0)
            {
                SpExecuteSqlCallSites.Add(new SpExecuteSqlCallSite(_currentScope, new SourceSpan(sourcePath, node.StartLine, node.StartColumn), bindings));
            }
        }

        private bool TryParseParameterDefinitions(string definitionText, out List<ProcedureParameterInfo> parameters)
        {
            parameters = [];
            var syntheticText = $"CREATE PROCEDURE {SpExecuteSqlSyntheticParameterListName} ({definitionText}) AS SELECT 1;";
            var parseResult = SqlScriptParser.ParseText(sourcePath, syntheticText);
            if (parseResult.Errors.Count > 0 || parseResult.Fragment is not TSqlScript { Batches: [{ Statements: [CreateProcedureStatement createStatement] }] })
            {
                return false;
            }

            foreach (var parameter in createStatement.Parameters)
            {
                var resolvedType = SqlTypeReferenceResolver.Resolve(parameter.DataType, columnCollation: null, catalog.TypeAliases);
                parameters.Add(new ProcedureParameterInfo(parameter.VariableName.Value, resolvedType, parameter.Modifier == ParameterModifier.Output));
            }

            return true;
        }

        private List<ProcCallArgument> MatchFoldedArguments(
            IList<ExecuteParameter> actualParameters, IReadOnlyList<ProcedureParameterInfo> formalParameters, DynamicSqlSegmentMap segmentMap)
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

                var literalArgument = ScopeVariableFlowTracker.TryGetDirectLiteralArgument(actual.ParameterValue, sourcePath);
                if (literalArgument is { } rawLiteralArgument)
                {
                    var translated = segmentMap.Map(rawLiteralArgument.StartLine, rawLiteralArgument.StartColumn);
                    literalArgument = rawLiteralArgument with
                    {
                        SourcePath = translated.SourcePath, StartLine = translated.Line, StartColumn = translated.Column,
                    };
                }

                var callerArgumentType = ScalarExpressionResolver.ResolveScalarType(
                    actual.ParameterValue, [], sourcePath, new ScalarExpressionResolver.ScalarTypeContext(null, catalog.TypeAliases, catalog, _variableTypes));
                matched.Add(new ProcCallArgument(
                    formal.Name, formal.Type, formal.IsOutput, CallerVariableName: null, actual.ParameterValue is Literal, literalArgument,
                    callerArgumentType, actual.IsOutput, actual.ParameterValue, CallerVariableWasAssignedBeforeCall: true, CallerFlowApproximate: false));
            }

            return matched;
        }

        private Dictionary<string, SqlTextValue> ResolveVariableStateAtCallSite(ExecuteStatement callSite)
        {
            var emptyState = new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase);
            if (_currentScopeStatements is null)
            {
                return emptyState;
            }

            var declaredTypes = new Dictionary<string, SqlType>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, type) in _variableTypes)
            {
                if (type is not null)
                {
                    declaredTypes[name] = type;
                }
            }

            var context = new TransferContext(
                declaredTypes, sourcePath, DynamicSqlFoldCap, new DynamicSqlScope(_currentScope, TriggerTarget: null),
                Findings: [], Scripts: [], OutputSummaries: [], CallGraph: null, OutputSummaryIndex: null, Catalog: catalog);

            Dictionary<string, SqlTextValue>? capturedState = null;
            Action<Dictionary<string, SqlTextValue>, bool> CompileLeafCapturing(TSqlStatement statement, IReadOnlyList<int> activeGuards)
            {
                if (ReferenceEquals(statement, callSite))
                {
                    return (state, emit) =>
                    {
                        if (emit)
                        {
                            capturedState = new Dictionary<string, SqlTextValue>(state, StringComparer.OrdinalIgnoreCase);
                        }
                    };
                }

                return DynamicSqlTransfer.CompileLeaf(statement, activeGuards, context);
            }

            var seed = new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase);
            DynamicSqlTransfer.SeedBatchDeclaredVariables(_currentScopeStatements, context, seed);
            new DynamicSqlCfg(sourcePath, DynamicSqlFoldCap, CompileLeafCapturing).Solve(_currentScopeStatements, seed);

            return capturedState ?? emptyState;
        }

        private void RecordUnresolvableCall(ExecuteStatement node, string reason) =>
            ledger.Record(AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, CallGraphConstructKind, reason);

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

            _currentScopeStatements = FlattenBeginEndBlocks(statementList?.Statements);

            statementList?.AcceptChildren(this);
            _currentScope = previous;
            _currentScopeStatements = previousStatements;
            _variableTypes.Clear();
            foreach (var (variableName, variableType) in previousVariableTypes)
            {
                _variableTypes[variableName] = variableType;
            }
        }

        private static List<TSqlStatement>? FlattenBeginEndBlocks(IList<TSqlStatement>? statements)
        {
            if (statements is null)
            {
                return null;
            }

            var flattened = new List<TSqlStatement>(statements.Count);
            AppendFlattened(statements, flattened);
            return flattened;
        }

        private static void AppendFlattened(IList<TSqlStatement> statements, List<TSqlStatement> flattened)
        {
            foreach (var statement in statements)
            {
                if (statement is BeginEndBlockStatement block)
                {
                    AppendFlattened(block.StatementList.Statements, flattened);
                }
                else
                {
                    flattened.Add(statement);
                }
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

            var arguments = MatchArguments(
                procedureReference.Parameters, formalParameters,
                new MatchArgumentsContext(sourcePath, _currentScopeStatements, node, _variableTypes, catalog, qualifiedName, ledger));
            Edges.Add(new ProcCallEdge(_currentScope, qualifiedName, new SourceSpan(sourcePath, node.StartLine, node.StartColumn), arguments));
        }

        private readonly record struct MatchArgumentsContext(
            string SourcePath,
            IList<TSqlStatement>? CurrentScopeStatements,
            ExecuteStatement CallSite,
            IReadOnlyDictionary<string, SqlType?> VariableTypes,
            DatabaseCatalog Catalog,
            string QualifiedCalleeName,
            SkipLedger Ledger);

        private static List<ProcCallArgument> MatchArguments(
            IList<ExecuteParameter> actualParameters, IReadOnlyList<ProcedureParameterInfo> formalParameters, MatchArgumentsContext context)
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
                    var reason = actual.Variable is { } unmatchedName
                        ? $"argument '{unmatchedName.Name}' does not match any declared parameter of '{context.QualifiedCalleeName}' - argument not matched to a formal parameter"
                        : $"positional argument does not match any declared parameter of '{context.QualifiedCalleeName}' - argument not matched to a formal parameter";
                    context.Ledger.Record(AnalysisPass.Predicates, context.SourcePath, actual.StartLine, actual.StartColumn, CallGraphConstructKind, reason);
                    continue;
                }

                var callerVariableName = actual.ParameterValue is VariableReference variableRef ? variableRef.Name : null;
                var literalArgument = ScopeVariableFlowTracker.TryGetDirectLiteralArgument(actual.ParameterValue, context.SourcePath)
                    ?? ScopeVariableFlowTracker.ResolvePropagatedLiteral(callerVariableName, context.CurrentScopeStatements, context.SourcePath, context.CallSite);
                var callerArgumentType = ScalarExpressionResolver.ResolveScalarType(
                    actual.ParameterValue, [], context.SourcePath,
                    new ScalarExpressionResolver.ScalarTypeContext(null, context.Catalog.TypeAliases, context.Catalog, context.VariableTypes));
                var assignmentFlow = callerVariableName is null
                    ? new ScopeVariableFlowTracker.AssignmentFlow(Assigned: true, Approximate: false)
                    : ScopeVariableFlowTracker.WasAssignedBeforeCall(context.CurrentScopeStatements, callerVariableName, context.CallSite);
                matched.Add(new ProcCallArgument(
                    formal.Name, formal.Type, formal.IsOutput, callerVariableName, actual.ParameterValue is Literal, literalArgument,
                    callerArgumentType, actual.IsOutput, actual.ParameterValue, assignmentFlow.Assigned, assignmentFlow.Approximate));
            }

            return matched;
        }
    }
}
