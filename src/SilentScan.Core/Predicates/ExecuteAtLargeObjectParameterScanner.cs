using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
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
        private readonly Dictionary<string, SqlType> variableTypes = new(StringComparer.OrdinalIgnoreCase);

        public List<ExecuteAtLargeObjectParameterFinding> Findings { get; } = [];

        public void OnEnterTSqlBatch(TSqlBatch node, ModuleWalker walker) => variableTypes.Clear();

        public void OnEnterProcedureOrFunctionBody(ProcedureStatementBodyBase node, ModuleWalker walker)
        {
            variableTypes.Clear();
            foreach (var parameter in node.Parameters)
            {
                Track(parameter.VariableName.Value, parameter.DataType);
            }
        }

        public void OnEnterTriggerBody(TriggerStatementBody node, ModuleWalker walker) => variableTypes.Clear();

        public void OnEnterDeclareVariableStatement(DeclareVariableStatement node, ModuleWalker walker)
        {
            foreach (var declaration in node.Declarations)
            {
                Track(declaration.VariableName.Value, declaration.DataType);
            }
        }

        public void OnEnterExecuteStatement(ExecuteStatement node, ModuleWalker walker)
        {
            if (node.ExecuteSpecification is not { LinkedServer: not null, ExecutableEntity: ExecutableStringList stringList })
            {
                return;
            }

            foreach (var parameter in stringList.Parameters)
            {
                if (parameter.ParameterValue is not VariableReference variable
                    || !variableTypes.TryGetValue(variable.Name, out var type))
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

        private void Track(string variableName, DataTypeReference dataType)
        {
            var resolved = SqlTypeReferenceResolver.Resolve(dataType, columnCollation: null, catalog.TypeAliases);
            if (resolved is not null)
            {
                variableTypes[variableName] = resolved;
            }
        }
    }
}
