using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class CreateDatabaseOptionConflictScanner
{
    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    public static IReadOnlyList<CreateDatabaseOptionConflictFinding> Scan(SqlParseResult parseResult)
    {
        var rule = CreateRule(parseResult.SourcePath);
        var walker = new ModuleWalker(parseResult.SourcePath, new DatabaseCatalog(), EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return Harvest(rule);
    }

    internal static Rule CreateRule(string sourcePath) => new(sourcePath);

    internal static IReadOnlyList<CreateDatabaseOptionConflictFinding> Harvest(Rule rule) =>
        [
            .. rule.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];

    internal sealed class Rule(string sourcePath) : IModuleRule
    {
        public List<CreateDatabaseOptionConflictFinding> Findings { get; } = [];

        public void OnEnterCreateDatabaseStatement(CreateDatabaseStatement node, ModuleWalker walker)
        {
            var hasContainmentPartial = node.Containment?.Value == ContainmentOptionKind.Partial;
            var hasCatalogCollation = node.Options.Any(o => o.OptionKind == DatabaseOptionKind.CatalogCollation);

            if (hasContainmentPartial && hasCatalogCollation)
            {
                Findings.Add(new CreateDatabaseOptionConflictFinding(
                    CreateDatabaseOptionConflictKind.ContainmentPartialAndCatalogCollation, sourcePath, node.StartLine, node.StartColumn));
            }
        }
    }
}
