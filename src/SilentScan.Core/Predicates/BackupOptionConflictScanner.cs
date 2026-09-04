using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class BackupOptionConflictScanner
{
    public static IReadOnlyList<BackupOptionConflictFinding> Scan(SqlParseResult parseResult)
    {
        var rule = CreateRule(parseResult.SourcePath);
        var walker = new ModuleWalker(parseResult.SourcePath, new DatabaseCatalog(), EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return Harvest(rule);
    }

    internal static Rule CreateRule(string sourcePath) => new(sourcePath);

    internal static IReadOnlyList<BackupOptionConflictFinding> Harvest(Rule rule) =>
        [
            .. rule.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];

    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    internal sealed class Rule(string sourcePath) : IModuleRule
    {
        public List<BackupOptionConflictFinding> Findings { get; } = [];

        public void OnEnterBackupDatabaseStatement(BackupDatabaseStatement node, ModuleWalker walker)
        {
            var hasDifferential = node.Options.Any(o => o.OptionKind == BackupOptionKind.Differential);
            var hasCopyOnly = node.Options.Any(o => o.OptionKind == BackupOptionKind.CopyOnly);

            if (hasDifferential && hasCopyOnly)
            {
                Findings.Add(new BackupOptionConflictFinding(sourcePath, node.StartLine, node.StartColumn));
            }
        }
    }
}
