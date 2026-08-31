using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class ForcedSerialScanner
{
    private static readonly HashSet<string> NonParallelizableIntrinsicFunctionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "OBJECT_ID", "IDENT_CURRENT",
        "ERROR_NUMBER", "ERROR_MESSAGE", "ERROR_LINE", "ERROR_SEVERITY", "ERROR_STATE", "ERROR_PROCEDURE",
    };

    public static IReadOnlyList<ForcedSerialFinding> Scan(SqlParseResult parseResult)
    {
        var rule = CreateRule(parseResult.SourcePath);
        var walker = new ModuleWalker(parseResult.SourcePath, new DatabaseCatalog(), EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
    return Harvest(rule);
    }
    internal static Rule CreateRule(string sourcePath) => new(sourcePath);

    internal static IReadOnlyList<ForcedSerialFinding> Harvest(Rule rule) =>
            [
            .. rule.Findings
                .OrderBy(f => f.Kind)
                .ThenBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];


    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    internal sealed class Rule(string sourcePath) : IModuleRule
    {
        public List<ForcedSerialFinding> Findings { get; } = [];

        private readonly HashSet<string> _tableVariableNames = new(StringComparer.OrdinalIgnoreCase);

        private int _queryWithFromDepth;

        public void OnEnterTSqlBatch(TSqlBatch node, ModuleWalker walker) => _tableVariableNames.Clear();

        public void OnEnterDeclareTableVariableStatement(DeclareTableVariableStatement node, ModuleWalker walker) =>
            _tableVariableNames.Add(node.Body.VariableName.Value);

        public void OnEnterInsertStatementScope(InsertStatement node, ModuleWalker walker) =>
            InspectDataModification(node.InsertSpecification);

        public void OnEnterUpdateStatementScope(UpdateStatement node, ScopeChain scopeChain, ModuleWalker walker) =>
            InspectDataModification(node.UpdateSpecification);

        public void OnEnterDeleteStatementScope(DeleteStatement node, ScopeChain scopeChain, ModuleWalker walker) =>
            InspectDataModification(node.DeleteSpecification);

        public void OnEnterMergeStatementScope(MergeStatement node, ScopeChain scopeChain, ModuleWalker walker) =>
            InspectDataModification(node.MergeSpecification);

        public void OnEnterDeclareCursorStatement(DeclareCursorStatement node, ModuleWalker walker) =>
            InspectCursorDefinition(node.CursorDefinition, node.Name.Value);

        public void OnEnterSetVariableStatement(SetVariableStatement node, ModuleWalker walker)
        {
            if (node.CursorDefinition is not null)
            {
                InspectCursorDefinition(node.CursorDefinition, node.Variable.Name);
            }
        }

        public void OnEnterQuerySpecificationScope(QuerySpecification node, ScopeChain scopeChain, ModuleWalker walker)
        {
            if (node.FromClause is not null)
            {
                _queryWithFromDepth++;
            }
        }

        public void OnLeaveQuerySpecificationScope(QuerySpecification node, ScopeChain scopeChain, ModuleWalker walker)
        {
            if (node.FromClause is not null)
            {
                _queryWithFromDepth--;
            }
        }

        public void OnEnterFunctionCall(FunctionCall node, ModuleWalker walker)
        {
            if (_queryWithFromDepth > 0 && NonParallelizableIntrinsicFunctionNames.Contains(node.FunctionName.Value))
            {
                Findings.Add(new ForcedSerialFinding(
                    ForcedSerialFindingKind.NonParallelizableIntrinsic, sourcePath, sourcePath,
                    node.StartLine, node.StartColumn, DetailText: node.FunctionName.Value));
            }
        }

        public void OnEnterGlobalVariableExpression(GlobalVariableExpression node, ModuleWalker walker)
        {
            if (_queryWithFromDepth > 0 && string.Equals(node.Name, "@@TRANCOUNT", StringComparison.OrdinalIgnoreCase))
            {
                Findings.Add(new ForcedSerialFinding(
                    ForcedSerialFindingKind.NonParallelizableIntrinsic, sourcePath, sourcePath,
                    node.StartLine, node.StartColumn, DetailText: "@@TRANCOUNT"));
            }
        }

        private void InspectDataModification(DataModificationSpecification spec)
        {
            var targetVariable = TableVariableName(spec.Target);
            var outputVariable = TableVariableName(spec.OutputIntoClause?.IntoTable);
            var variableName = targetVariable ?? outputVariable;
            if (variableName is null)
            {
                return;
            }

            Findings.Add(new ForcedSerialFinding(
                ForcedSerialFindingKind.TableVariableModification, sourcePath, sourcePath,
                spec.StartLine, spec.StartColumn, DetailText: variableName));
        }

        private string? TableVariableName(TableReference? tableReference) =>
            tableReference is VariableTableReference variableRef && _tableVariableNames.Contains(variableRef.Variable.Name)
                ? variableRef.Variable.Name
                : null;

        private void InspectCursorDefinition(CursorDefinition definition, string cursorName)
        {
            var kinds = definition.Options.Select(o => o.OptionKind).ToHashSet();

            var hasExplicitType = kinds.Contains(CursorOptionKind.Static) || kinds.Contains(CursorOptionKind.Keyset) || kinds.Contains(CursorOptionKind.Dynamic);
            var fires = kinds.Contains(CursorOptionKind.FastForward)
                || (kinds.Contains(CursorOptionKind.ForwardOnly) && kinds.Contains(CursorOptionKind.ReadOnly) && !hasExplicitType);

            if (!fires)
            {
                return;
            }

            Findings.Add(new ForcedSerialFinding(
                ForcedSerialFindingKind.FastForwardCursor, sourcePath, sourcePath,
                definition.StartLine, definition.StartColumn, DetailText: cursorName));
        }
    }
}
