using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Common;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

public static class RevertCookieTypeMismatchScanner
{
    private const int MinimumCookieLength = 50;

    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    public static IReadOnlyList<RevertCookieTypeMismatchFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
    {
        var rule = CreateRule(parseResult.SourcePath, catalog);
        var walker = new ModuleWalker(parseResult.SourcePath, catalog, EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return Harvest(rule);
    }

    internal static Rule CreateRule(string sourcePath, DatabaseCatalog catalog) => new(sourcePath, catalog);

    internal static IReadOnlyList<RevertCookieTypeMismatchFinding> Harvest(Rule rule) =>
        [
            .. rule.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];

    internal sealed class Rule(string sourcePath, DatabaseCatalog catalog) : IModuleRule
    {
        private readonly VariableTypeTracker _variableTypes = new(catalog);

        public List<RevertCookieTypeMismatchFinding> Findings { get; } = [];

        public void OnEnterTSqlBatch(TSqlBatch node, ModuleWalker walker) => _variableTypes.Clear();

        public void OnEnterProcedureOrFunctionBody(ProcedureStatementBodyBase node, ModuleWalker walker) =>
            _variableTypes.TrackParameters(node);

        public void OnEnterTriggerBody(TriggerStatementBody node, ModuleWalker walker) => _variableTypes.Clear();

        public void OnEnterDeclareVariableStatement(DeclareVariableStatement node, ModuleWalker walker) =>
            _variableTypes.TrackDeclarations(node);

        public void OnEnterRevertStatement(RevertStatement node, ModuleWalker walker)
        {
            if (node.Cookie is not VariableReference cookie
                || !_variableTypes.TryGetValue(cookie.Name, out var cookieType)
                || IsValidCookieType(cookieType))
            {
                return;
            }

            Findings.Add(new RevertCookieTypeMismatchFinding(
                cookie.Name,
                cookieType.ToString(),
                sourcePath,
                node.StartLine,
                node.StartColumn));
        }

        private static bool IsValidCookieType(SqlType type) =>
            type.Category == SqlTypeCategory.VarBinary && !type.IsMax && type.Length is { } length && length >= MinimumCookieLength;
    }
}
