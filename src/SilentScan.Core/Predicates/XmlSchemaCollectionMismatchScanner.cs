using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Common;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class XmlSchemaCollectionMismatchScanner
{
    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    public static IReadOnlyList<XmlSchemaCollectionMismatchFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
    {
        var rule = CreateRule(parseResult.SourcePath);
        var walker = new ModuleWalker(parseResult.SourcePath, catalog, EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return Harvest(rule);
    }

    internal static Rule CreateRule(string sourcePath) => new(sourcePath);

    internal static IReadOnlyList<XmlSchemaCollectionMismatchFinding> Harvest(Rule rule) =>
        [
            .. rule.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];

    internal sealed class Rule(string sourcePath) : IModuleRule
    {
        private readonly Dictionary<string, string?> _variableSchemaCollections = new(StringComparer.OrdinalIgnoreCase);

        public List<XmlSchemaCollectionMismatchFinding> Findings { get; } = [];

        public void OnEnterTSqlBatch(TSqlBatch node, ModuleWalker walker) => _variableSchemaCollections.Clear();

        public void OnEnterTriggerBody(TriggerStatementBody node, ModuleWalker walker) => _variableSchemaCollections.Clear();

        public void OnEnterProcedureOrFunctionBody(ProcedureStatementBodyBase node, ModuleWalker walker)
        {
            _variableSchemaCollections.Clear();
            foreach (var parameter in node.Parameters)
            {
                _variableSchemaCollections[parameter.VariableName.Value] = SchemaCollectionNameOf(parameter.DataType);
            }
        }

        public void OnEnterDeclareVariableStatement(DeclareVariableStatement node, ModuleWalker walker)
        {
            foreach (var declaration in node.Declarations)
            {
                var targetSchemaCollection = SchemaCollectionNameOf(declaration.DataType);
                _variableSchemaCollections[declaration.VariableName.Value] = targetSchemaCollection;

                if (targetSchemaCollection is not null && declaration.Value is VariableReference sourceVariable)
                {
                    Inspect(declaration.VariableName.Value, targetSchemaCollection, sourceVariable, declaration.StartLine, declaration.StartColumn);
                }
            }
        }

        public void OnEnterSetVariableStatement(SetVariableStatement node, ModuleWalker walker)
        {
            if (node.Variable?.Name is not { } targetName
                || !_variableSchemaCollections.TryGetValue(targetName, out var targetSchemaCollection)
                || targetSchemaCollection is null
                || node.Expression is not VariableReference sourceVariable)
            {
                return;
            }

            Inspect(targetName, targetSchemaCollection, sourceVariable, node.StartLine, node.StartColumn);
        }

        private void Inspect(string targetName, string targetSchemaCollection, VariableReference sourceVariable, int line, int column)
        {
            if (!_variableSchemaCollections.TryGetValue(sourceVariable.Name, out var sourceSchemaCollection)
                || sourceSchemaCollection is null
                || string.Equals(sourceSchemaCollection, targetSchemaCollection, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Findings.Add(new XmlSchemaCollectionMismatchFinding(
                targetName, targetSchemaCollection, sourceVariable.Name, sourceSchemaCollection, sourcePath, line, column));
        }

        private static string? SchemaCollectionNameOf(DataTypeReference dataType) =>
            dataType is XmlDataTypeReference { XmlSchemaCollection: { } schemaCollection }
                ? SchemaObjectNameHelper.Qualify(schemaCollection)
                : null;
    }
}
