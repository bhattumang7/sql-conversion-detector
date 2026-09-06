using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

public static class LegacyLobConversionTargetScanner
{
    public static IReadOnlyList<LegacyLobConversionTargetFinding> Scan(SqlParseResult parseResult)
    {
        var rule = CreateRule(parseResult.SourcePath);
        var walker = new ModuleWalker(parseResult.SourcePath, new DatabaseCatalog(), EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return Harvest(rule);
    }

    internal static Rule CreateRule(string sourcePath) => new(sourcePath);

    internal static IReadOnlyList<LegacyLobConversionTargetFinding> Harvest(Rule rule) =>
        [
            .. rule.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];

    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    internal sealed class Rule(string sourcePath) : IModuleRule
    {
        public List<LegacyLobConversionTargetFinding> Findings { get; } = [];

        public void OnEnterCastCall(CastCall node, ModuleWalker walker) => Inspect(node.DataType, node.Collation, node);

        public void OnEnterConvertCall(ConvertCall node, ModuleWalker walker) => Inspect(node.DataType, node.Collation, node);

        public void OnEnterTryCastCall(TryCastCall node, ModuleWalker walker) => Inspect(node.DataType, node.Collation, node);

        public void OnEnterTryConvertCall(TryConvertCall node, ModuleWalker walker) => Inspect(node.DataType, node.Collation, node);

        private void Inspect(DataTypeReference dataType, Identifier? collationClause, TSqlFragment node)
        {
            if (collationClause?.Value is not { } collationName)
            {
                return;
            }

            var collation = new Collation(collationName);
            if (!collation.IsUtf8 && !collation.IsSupplementaryCharacterAware)
            {
                return;
            }

            var targetType = SqlTypeReferenceResolver.Resolve(dataType, columnCollation: null);
            if (targetType is not { IsLegacyLob: true })
            {
                return;
            }

            Findings.Add(new LegacyLobConversionTargetFinding(targetType.ToString(), collationName, sourcePath, node.StartLine, node.StartColumn));
        }
    }
}
