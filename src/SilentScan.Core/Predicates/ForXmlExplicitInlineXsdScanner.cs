using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class ForXmlExplicitInlineXsdScanner
{
    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    public static IReadOnlyList<ForXmlExplicitInlineXsdFinding> Scan(SqlParseResult parseResult, DatabaseCatalog? catalog = null)
    {
        var rule = CreateRule(parseResult.SourcePath);
        var walker = new ModuleWalker(parseResult.SourcePath, catalog ?? new DatabaseCatalog(), EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return Harvest(rule);
    }

    internal static Rule CreateRule(string sourcePath) => new(sourcePath);

    internal static IReadOnlyList<ForXmlExplicitInlineXsdFinding> Harvest(Rule rule) =>
        [
            .. rule.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];

    internal sealed class Rule(string sourcePath) : IModuleRule
    {
        public List<ForXmlExplicitInlineXsdFinding> Findings { get; } = [];

        public void OnEnterSelectStatementScope(SelectStatement node, ModuleWalker walker)
        {
            var options = node.QueryExpression?.ForClause is XmlForClause xmlForClause
                ? xmlForClause.Options
                : null;

            if (options is null)
            {
                return;
            }

            var hasExplicit = false;
            var hasInlineXsd = false;

            foreach (var option in options)
            {
                hasExplicit |= option.OptionKind == XmlForClauseOptions.Explicit;
                hasInlineXsd |= option.OptionKind == XmlForClauseOptions.XmlSchema;
            }

            if (!hasExplicit || !hasInlineXsd)
            {
                return;
            }

            Findings.Add(new ForXmlExplicitInlineXsdFinding(sourcePath, node.StartLine, node.StartColumn));
        }
    }
}
