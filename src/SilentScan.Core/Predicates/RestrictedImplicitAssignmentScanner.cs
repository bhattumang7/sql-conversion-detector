using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

public static class RestrictedImplicitAssignmentScanner
{
    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    public static IReadOnlyList<RestrictedImplicitAssignmentFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
    {
        var rule = CreateRule(parseResult.SourcePath, catalog);
        var walker = new ModuleWalker(parseResult.SourcePath, catalog, EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return Harvest(rule);
    }

    internal static Rule CreateRule(string sourcePath, DatabaseCatalog catalog) => new(sourcePath, catalog);

    internal static IReadOnlyList<RestrictedImplicitAssignmentFinding> Harvest(Rule rule) =>
        [
            .. rule.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];

    internal sealed class Rule(string sourcePath, DatabaseCatalog catalog) : IModuleRule
    {
        private readonly Dictionary<string, SqlType> variableTypes = new(StringComparer.OrdinalIgnoreCase);

        public List<RestrictedImplicitAssignmentFinding> Findings { get; } = [];

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

        public void OnEnterSetVariableStatement(SetVariableStatement node, ModuleWalker walker)
        {
            if (node.AssignmentKind != AssignmentKind.Equals
                || node.Expression is not VariableReference source
                || !variableTypes.TryGetValue(node.Variable.Name, out var targetType)
                || !variableTypes.TryGetValue(source.Name, out var sourceType)
                || !IsIllegalPair(targetType, sourceType))
            {
                return;
            }

            Findings.Add(new RestrictedImplicitAssignmentFinding(
                node.Variable.Name,
                targetType.ToString(),
                source.Name,
                sourceType.ToString(),
                sourcePath,
                node.StartLine,
                node.StartColumn));
        }

        private void Track(string variableName, DataTypeReference dataType)
        {
            var resolved = SqlTypeReferenceResolver.Resolve(dataType, columnCollation: null, catalog.TypeAliases);
            if (resolved is not null)
            {
                variableTypes[variableName] = resolved;
            }
        }

        private static bool IsIllegalPair(SqlType target, SqlType source)
        {
            if (source.Category == SqlTypeCategory.Xml)
            {
                return target.Category != SqlTypeCategory.Xml;
            }

            if (target.Category == SqlTypeCategory.Xml)
            {
                return source.Category != SqlTypeCategory.Xml && !source.IsStringFamily && !source.IsBinaryFamily;
            }

            return source.Category == SqlTypeCategory.SqlVariant && target.Category != SqlTypeCategory.SqlVariant;
        }
    }
}
