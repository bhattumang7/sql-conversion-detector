using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Common;
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
        private readonly VariableTypeTracker _variableTypes = new(catalog);

        public List<RestrictedImplicitAssignmentFinding> Findings { get; } = [];

        public void OnEnterTSqlBatch(TSqlBatch node, ModuleWalker walker) => _variableTypes.Clear();

        public void OnEnterProcedureOrFunctionBody(ProcedureStatementBodyBase node, ModuleWalker walker) =>
            _variableTypes.TrackParameters(node);

        public void OnEnterTriggerBody(TriggerStatementBody node, ModuleWalker walker) => _variableTypes.Clear();

        public void OnEnterDeclareVariableStatement(DeclareVariableStatement node, ModuleWalker walker) =>
            _variableTypes.TrackDeclarations(node);

        public void OnEnterSetVariableStatement(SetVariableStatement node, ModuleWalker walker)
        {
            if (node.AssignmentKind != AssignmentKind.Equals
                || node.Expression is not VariableReference source
                || !_variableTypes.TryGetValue(node.Variable.Name, out var targetType)
                || !_variableTypes.TryGetValue(source.Name, out var sourceType)
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
