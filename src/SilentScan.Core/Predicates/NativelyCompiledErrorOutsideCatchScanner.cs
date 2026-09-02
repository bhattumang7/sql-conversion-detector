using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Common;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class NativelyCompiledErrorOutsideCatchScanner
{
    private static readonly HashSet<string> ErrorFunctionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ERROR_MESSAGE", "ERROR_NUMBER", "ERROR_SEVERITY", "ERROR_STATE", "ERROR_LINE", "ERROR_PROCEDURE",
    };

    public static IReadOnlyList<NativelyCompiledErrorOutsideCatchFinding> Scan(SqlParseResult parseResult)
    {
        var rule = CreateRule(parseResult.SourcePath);
        var walker = new ModuleWalker(parseResult.SourcePath, new DatabaseCatalog(), EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return Harvest(rule);
    }

    internal static Rule CreateRule(string sourcePath) => new(sourcePath);

    internal static IReadOnlyList<NativelyCompiledErrorOutsideCatchFinding> Harvest(Rule rule) =>
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
        private int _catchDepth;

        public List<NativelyCompiledErrorOutsideCatchFinding> Findings { get; } = [];

        public void OnEnterProcedureOrFunctionBody(ProcedureStatementBodyBase node, ModuleWalker walker) =>
            _currentNativeModuleName = IsNativelyCompiled(node) ? TryGetModuleQualifiedName(node) : null;

        public void OnLeaveProcedureOrFunctionBody(ProcedureStatementBodyBase node, ModuleWalker walker) =>
            _currentNativeModuleName = null;

        public void OnEnterCatchBlock(TryCatchStatement node, ModuleWalker walker) => _catchDepth++;

        public void OnLeaveCatchBlock(TryCatchStatement node, ModuleWalker walker) => _catchDepth--;

        public void OnEnterFunctionCall(FunctionCall node, ModuleWalker walker)
        {
            if (_currentNativeModuleName is not { } moduleName
                || _catchDepth > 0
                || node.FunctionName?.Value is not { } functionName
                || !ErrorFunctionNames.Contains(functionName))
            {
                return;
            }

            Findings.Add(new NativelyCompiledErrorOutsideCatchFinding(
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
