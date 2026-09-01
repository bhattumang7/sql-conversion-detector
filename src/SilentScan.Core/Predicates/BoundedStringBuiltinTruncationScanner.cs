using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Common;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class BoundedStringBuiltinTruncationScanner
{
    private const int NonUnicodeCapBytes = 8000;
    private const int UnicodeCapBytes = 4000;

    public static IReadOnlyList<BoundedStringBuiltinTruncationFinding> Scan(SqlParseResult parseResult)
    {
        var rule = CreateRule(parseResult.SourcePath);
        var walker = new ModuleWalker(parseResult.SourcePath, new DatabaseCatalog(), EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return Harvest(rule);
    }

    internal static Rule CreateRule(string sourcePath) => new(sourcePath);

    internal static IReadOnlyList<BoundedStringBuiltinTruncationFinding> Harvest(Rule rule) =>
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
        public List<BoundedStringBuiltinTruncationFinding> Findings { get; } = [];

        public void OnEnterFunctionCall(FunctionCall node, ModuleWalker walker)
        {
            var name = node.FunctionName?.Value;

            if (string.Equals(name, "REPLICATE", StringComparison.OrdinalIgnoreCase) && node.Parameters.Count == 2)
            {
                CheckReplicate(node);
            }
            else if (string.Equals(name, "REPLACE", StringComparison.OrdinalIgnoreCase) && node.Parameters.Count == 3)
            {
                CheckReplace(node);
            }
            else if (string.Equals(name, "SPACE", StringComparison.OrdinalIgnoreCase) && node.Parameters.Count == 1)
            {
                CheckSpace(node);
            }
        }

        private void CheckReplicate(FunctionCall node)
        {
            if (node.Parameters[0] is not StringLiteral source)
            {
                return;
            }

            if (LiteralComparisonFolder.TryFoldToNumeric(node.Parameters[1]) is not { } countValue
                || countValue <= 0
                || countValue != decimal.Truncate(countValue))
            {
                return;
            }

            var cap = source.IsNational ? UnicodeCapBytes : NonUnicodeCapBytes;
            if (source.Value.Length > cap)
            {
                return;
            }

            var computed = (long)source.Value.Length * (long)countValue;
            if (computed <= cap)
            {
                return;
            }

            Add(BoundedStringBuiltinTruncationFindingKind.ReplicateResultTruncated, "REPLICATE", computed, cap, node);
        }

        private void CheckReplace(FunctionCall node)
        {
            if (node.Parameters[0] is not StringLiteral input)
            {
                return;
            }

            var cap = input.IsNational ? UnicodeCapBytes : NonUnicodeCapBytes;
            if (input.Value.Length > cap)
            {
                return;
            }

            if (LiteralComparisonFolder.TryFoldToString(node.Parameters[1]) is not { Length: > 0 } from
                || LiteralComparisonFolder.TryFoldToString(node.Parameters[2]) is not { } to
                || to.Length <= from.Length)
            {
                return;
            }

            var replaced = input.Value.Replace(from, to, StringComparison.Ordinal);
            if (replaced.Length <= cap)
            {
                return;
            }

            Add(BoundedStringBuiltinTruncationFindingKind.ReplaceResultTruncated, "REPLACE", replaced.Length, cap, node);
        }

        private void CheckSpace(FunctionCall node)
        {
            if (LiteralComparisonFolder.TryFoldToNumeric(node.Parameters[0]) is not { } countValue
                || countValue <= NonUnicodeCapBytes
                || countValue != decimal.Truncate(countValue))
            {
                return;
            }

            Add(BoundedStringBuiltinTruncationFindingKind.SpaceResultTruncated, "SPACE", (long)countValue, NonUnicodeCapBytes, node);
        }

        private void Add(BoundedStringBuiltinTruncationFindingKind kind, string functionName, long computedLength, int cap, FunctionCall node) =>
            Findings.Add(new BoundedStringBuiltinTruncationFinding(
                kind, functionName, computedLength, cap,
                sourcePath, node.StartLine, node.StartColumn));
    }
}
