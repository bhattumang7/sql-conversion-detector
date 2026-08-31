using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Common;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class OutputParameterScanner
{
    public static IReadOnlyList<OutputParameterFinding> Scan(SqlParseResult parseResult)
    {
        var rule = CreateRule(parseResult.SourcePath);
        var walker = new ModuleWalker(parseResult.SourcePath, new DatabaseCatalog(), EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
    return Harvest(rule);
    }
    internal static Rule CreateRule(string sourcePath) => new(sourcePath);

    internal static IReadOnlyList<OutputParameterFinding> Harvest(Rule rule) =>
            [
            .. rule.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.ProcedureLine)
                .ThenBy(f => f.ParameterName, StringComparer.OrdinalIgnoreCase),
        ];


    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    internal readonly record struct FlowState(HashSet<string>? Unassigned, bool Declined)
    {
        public static FlowState Declined_() => new(null, true);
    }

    internal sealed class Rule(string sourcePath) : IModuleRule, IStatementFlowPolicy<FlowState>
    {
        private int _procedureLine;
        private int _procedureColumn;

        public List<OutputParameterFinding> Findings { get; } = [];

        public void OnEnterCreateProcedureStatement(CreateProcedureStatement node, ModuleWalker walker) =>
            AnalyzeProcedure(node.Parameters, node.StatementList, node.ProcedureReference.Name);

        public void OnEnterAlterProcedureStatement(AlterProcedureStatement node, ModuleWalker walker) =>
            AnalyzeProcedure(node.Parameters, node.StatementList, node.ProcedureReference.Name);

        public void OnEnterCreateOrAlterProcedureStatement(CreateOrAlterProcedureStatement node, ModuleWalker walker) =>
            AnalyzeProcedure(node.Parameters, node.StatementList, node.ProcedureReference.Name);

        private void AnalyzeProcedure(
            IList<ProcedureParameter> parameters, StatementList? statementList, SchemaObjectName procedureName)
        {
            var outputNames = parameters
                .Where(p => p.Modifier == ParameterModifier.Output)
                .Select(p => p.VariableName.Value)
                .ToList();

            if (outputNames.Count == 0 || statementList is null)
            {

                return;
            }

            var statements = statementList.Statements is [BeginEndBlockStatement singleBlock]
                ? singleBlock.StatementList.Statements
                : statementList.Statements;

            _procedureLine = procedureName.BaseIdentifier.StartLine;
            _procedureColumn = procedureName.BaseIdentifier.StartColumn;

            var entryState = new FlowState(new HashSet<string>(outputNames, StringComparer.OrdinalIgnoreCase), false);
            var finalState = ProcedureBodyFlowWalker.Walk(statements, entryState, this);

            if (finalState is { Declined: false, Unassigned.Count: > 0 })
            {
                var exitAnchor = statements.Count > 0 ? statements[^1] : (TSqlFragment)procedureName;
                foreach (var name in finalState.Unassigned.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                {
                    Findings.Add(new OutputParameterFinding(
                        OutputParameterFindingKind.UnassignedOnSomePath,
                        sourcePath,
                        name,
                        procedureName.BaseIdentifier.StartLine,
                        procedureName.BaseIdentifier.StartColumn,
                        exitAnchor.StartLine,
                        exitAnchor.StartColumn));
                }
            }
        }

        public bool IsDeclined(FlowState state) => state.Declined;

        public bool IsDone(FlowState state) => state.Unassigned!.Count == 0;

        public FlowState PerStatement(TSqlStatement statement, FlowState state)
        {
            foreach (var (name, _) in VariableWriteSites.InStatement(statement))
            {
                state.Unassigned!.Remove(name);
            }

            return state;
        }

        public FlowState OnReturn(FlowState state, TSqlStatement statement)
        {
            EmitUnassignedFindings(state.Unassigned!, statement);
            return state with { Unassigned = [] };
        }

        public FlowState OnThrow(FlowState state) => state with { Unassigned = [] };

        public FlowState OnGoTo(FlowState state) => FlowState.Declined_();

        public FlowState CloneForBranch(FlowState state) =>
            state.Declined ? state : new FlowState([.. state.Unassigned!], false);

        public FlowState Merge(FlowState a, FlowState b) => MergeBranches(a, b);

        private void EmitUnassignedFindings(HashSet<string> unassigned, TSqlStatement statement)
        {
            foreach (var name in unassigned)
            {
                Findings.Add(new OutputParameterFinding(
                    OutputParameterFindingKind.UnassignedOnSomePath,
                    sourcePath,
                    name,
                    _procedureLine,
                    _procedureColumn,
                    statement.StartLine,
                    statement.StartColumn));
            }
        }

        private static FlowState MergeBranches(FlowState a, FlowState b)
        {
            if (a.Declined || b.Declined)
            {
                return FlowState.Declined_();
            }

            var merged = new HashSet<string>(a.Unassigned!, StringComparer.OrdinalIgnoreCase);
            merged.UnionWith(b.Unassigned!);
            return new FlowState(merged, false);
        }
    }
}
