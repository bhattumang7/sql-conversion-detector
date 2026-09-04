using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Common;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

public static class ExecuteAtLargeObjectParameterScanner
{
    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    public static IReadOnlyList<ExecuteAtLargeObjectParameterFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
    {
        var rule = CreateRule(parseResult.SourcePath, catalog);
        var walker = new ModuleWalker(parseResult.SourcePath, catalog, EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return Harvest(rule);
    }

    internal static Rule CreateRule(string sourcePath, DatabaseCatalog catalog) => new(sourcePath, catalog);

    internal static IReadOnlyList<ExecuteAtLargeObjectParameterFinding> Harvest(Rule rule) =>
        [
            .. rule.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];

    internal sealed class Rule(string sourcePath, DatabaseCatalog catalog) : IModuleRule
    {
        private readonly VariableTypeTracker _variableTypes = new(catalog);

        public List<ExecuteAtLargeObjectParameterFinding> Findings { get; } = [];

        public void OnEnterTSqlBatch(TSqlBatch node, ModuleWalker walker) => _variableTypes.Clear();

        public void OnEnterProcedureOrFunctionBody(ProcedureStatementBodyBase node, ModuleWalker walker) =>
            _variableTypes.TrackParameters(node);

        public void OnEnterTriggerBody(TriggerStatementBody node, ModuleWalker walker) => _variableTypes.Clear();

        public void OnEnterDeclareVariableStatement(DeclareVariableStatement node, ModuleWalker walker) =>
            _variableTypes.TrackDeclarations(node);

        public void OnEnterExecuteStatement(ExecuteStatement node, ModuleWalker walker)
        {
            if (node.ExecuteSpecification is not { LinkedServer: not null, ExecutableEntity: ExecutableStringList stringList })
            {
                return;
            }

            foreach (var parameter in stringList.Parameters)
            {
                if (parameter.ParameterValue is not VariableReference variable
                    || !_variableTypes.TryGetValue(variable.Name, out var type))
                {
                    continue;
                }

                if (type.IsMax && type.Category is SqlTypeCategory.VarChar or SqlTypeCategory.NVarChar or SqlTypeCategory.VarBinary)
                {
                    Findings.Add(new ExecuteAtLargeObjectParameterFinding(
                        variable.Name.TrimStart('@'), type.ToString(), ExecuteAtLargeObjectParameterFindingKind.CrashesSession,
                        sourcePath, variable.StartLine, variable.StartColumn));
                }
                else if (type.Category == SqlTypeCategory.Xml)
                {
                    Findings.Add(new ExecuteAtLargeObjectParameterFinding(
                        variable.Name.TrimStart('@'), type.ToString(), ExecuteAtLargeObjectParameterFindingKind.XmlRejected,
                        sourcePath, variable.StartLine, variable.StartColumn));
                }
            }
        }
    }
}
