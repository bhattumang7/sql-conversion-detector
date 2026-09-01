using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Common;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class StringSplitArgumentScanner
{
    public static IReadOnlyList<StringSplitArgumentFinding> Scan(SqlParseResult parseResult)
    {
        var rule = CreateRule(parseResult.SourcePath);
        var walker = new ModuleWalker(parseResult.SourcePath, new DatabaseCatalog(), EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return Harvest(rule);
    }

    internal static Rule CreateRule(string sourcePath) => new(sourcePath);

    internal static IReadOnlyList<StringSplitArgumentFinding> Harvest(Rule rule) =>
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
        public List<StringSplitArgumentFinding> Findings { get; } = [];

        public void OnEnterGlobalFunctionTableReference(GlobalFunctionTableReference node, ModuleWalker walker)
        {
            var name = node.Name?.Value;

            if (!string.Equals(name, "STRING_SPLIT", StringComparison.OrdinalIgnoreCase) || node.Parameters.Count < 2)
            {
                return;
            }

            var separator = node.Parameters[1];

            if (separator is NullLiteral)
            {
                Add(separator, "NULL");
                return;
            }

            if (LiteralComparisonFolder.TryFoldToString(separator) is { } folded && folded.Length != 1)
            {
                Add(separator, FragmentTextRenderer.Render(separator));
            }
        }

        private void Add(ScalarExpression separator, string separatorText) =>
            Findings.Add(new StringSplitArgumentFinding(
                StringSplitArgumentFindingKind.SeparatorNotSingleCharacter, separatorText,
                sourcePath, separator.StartLine, separator.StartColumn));
    }
}
