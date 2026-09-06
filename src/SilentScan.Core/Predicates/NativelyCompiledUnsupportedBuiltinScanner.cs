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
        "STDEV", "STDEVP", "VAR", "VARP", "STRING_SPLIT",

        "COMPRESS", "DECOMPRESS", "CHECKSUM", "BINARY_CHECKSUM", "PARSENAME", "FORMATMESSAGE",
        "APP_NAME", "TYPE_NAME", "COL_NAME", "OBJECT_ID", "OBJECT_NAME", "DB_ID", "DB_NAME",
        "SCHEMA_ID", "SCHEMA_NAME", "PERMISSIONS", "HAS_PERMS_BY_NAME", "CURRENT_TIMEZONE",
        "IDENT_CURRENT", "STATS_DATE", "OBJECTPROPERTY", "COLLATIONPROPERTY", "FILE_ID",
        "INDEXPROPERTY",
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
            _currentNativeModuleName = NativelyCompiledModuleHelper.IsNativelyCompiled(node) ? NativelyCompiledModuleHelper.TryGetModuleQualifiedName(node) : null;

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

        public void OnEnterLeftFunctionCall(LeftFunctionCall node, ModuleWalker walker)
        {
            if (_currentNativeModuleName is not { } moduleName)
            {
                return;
            }

            Findings.Add(new NativelyCompiledUnsupportedBuiltinFinding(
                moduleName, "LEFT", sourcePath, node.StartLine, node.StartColumn));
        }

        public void OnEnterRightFunctionCall(RightFunctionCall node, ModuleWalker walker)
        {
            if (_currentNativeModuleName is not { } moduleName)
            {
                return;
            }

            Findings.Add(new NativelyCompiledUnsupportedBuiltinFinding(
                moduleName, "RIGHT", sourcePath, node.StartLine, node.StartColumn));
        }
    }
}
