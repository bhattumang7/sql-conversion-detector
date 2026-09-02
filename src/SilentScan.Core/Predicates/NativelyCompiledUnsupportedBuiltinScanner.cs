using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Common;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class NativelyCompiledUnsupportedBuiltinScanner
{
    private static readonly HashSet<string> UnsupportedFunctionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "UPPER", "LOWER", "REPLACE", "CHARINDEX", "STUFF", "REVERSE", "PATINDEX", "QUOTENAME",
        "DATALENGTH", "ISNUMERIC", "ISDATE", "HASHBYTES", "CONCAT", "FORMAT", "SOUNDEX",
        "STDEV", "STDEVP", "VAR", "VARP", "STRING_AGG", "STRING_SPLIT",
    };

    public static IReadOnlyList<NativelyCompiledUnsupportedBuiltinFinding> Scan(SqlParseResult parseResult)
    {
        var rule = CreateRule(parseResult.SourcePath);
        var walker = new ModuleWalker(parseResult.SourcePath, new DatabaseCatalog(), EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return Harvest(rule);
    }

    internal static Rule CreateRule(string sourcePath) => new(sourcePath);

    internal static IReadOnlyList<NativelyCompiledUnsupportedBuiltinFinding> Harvest(Rule rule) =>
        [
            .. rule.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];

    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    internal sealed class Rule(string sourcePath) : IModuleRule
    {
        private string? _currentNativeModuleName;

        public List<NativelyCompiledUnsupportedBuiltinFinding> Findings { get; } = [];

        public void OnEnterProcedureOrFunctionBody(ProcedureStatementBodyBase node, ModuleWalker walker) =>
            _currentNativeModuleName = IsNativelyCompiled(node) ? TryGetModuleQualifiedName(node) : null;

        public void OnLeaveProcedureOrFunctionBody(ProcedureStatementBodyBase node, ModuleWalker walker) =>
            _currentNativeModuleName = null;

        public void OnEnterFunctionCall(FunctionCall node, ModuleWalker walker)
        {
            if (_currentNativeModuleName is not { } moduleName
                || node.FunctionName?.Value is not { } functionName
                || !UnsupportedFunctionNames.Contains(functionName))
            {
                return;
            }

            Findings.Add(new NativelyCompiledUnsupportedBuiltinFinding(
                moduleName, functionName.ToUpperInvariant(), sourcePath, node.StartLine, node.StartColumn));
        }

        private static bool IsNativelyCompiled(ProcedureStatementBodyBase node) => node switch
        {
            ProcedureStatementBody procedure => procedure.Options.Any(o => o.OptionKind == ProcedureOptionKind.NativeCompilation),
            FunctionStatementBody function => function.Options.Any(o => o.OptionKind == FunctionOptionKind.NativeCompilation),
            _ => false,
        };

        private static string? TryGetModuleQualifiedName(ProcedureStatementBodyBase node) => node switch
        {
            CreateProcedureStatement p => SchemaObjectNameHelper.Qualify(p.ProcedureReference.Name),
            AlterProcedureStatement p => SchemaObjectNameHelper.Qualify(p.ProcedureReference.Name),
            CreateOrAlterProcedureStatement p => SchemaObjectNameHelper.Qualify(p.ProcedureReference.Name),
            CreateFunctionStatement f => SchemaObjectNameHelper.Qualify(f.Name),
            AlterFunctionStatement f => SchemaObjectNameHelper.Qualify(f.Name),
            CreateOrAlterFunctionStatement f => SchemaObjectNameHelper.Qualify(f.Name),
            _ => null,
        };
    }
}
