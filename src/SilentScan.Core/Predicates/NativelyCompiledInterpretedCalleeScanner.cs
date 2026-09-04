using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Common;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class NativelyCompiledInterpretedCalleeScanner
{
    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    public static IReadOnlyList<NativelyCompiledInterpretedCalleeFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
    {
        var rule = CreateRule(parseResult.SourcePath, catalog);
        var walker = new ModuleWalker(parseResult.SourcePath, catalog, EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return Harvest(rule);
    }

    internal static Rule CreateRule(string sourcePath, DatabaseCatalog catalog) => new(sourcePath, catalog);

    internal static IReadOnlyList<NativelyCompiledInterpretedCalleeFinding> Harvest(Rule rule) =>
        [
            .. rule.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];

    internal sealed class Rule(string sourcePath, DatabaseCatalog catalog) : IModuleRule
    {
        private string? _currentNativeModuleName;

        public List<NativelyCompiledInterpretedCalleeFinding> Findings { get; } = [];

        public void OnEnterProcedureOrFunctionBody(ProcedureStatementBodyBase node, ModuleWalker walker) =>
            _currentNativeModuleName = NativelyCompiledModuleHelper.IsNativelyCompiled(node) ? NativelyCompiledModuleHelper.TryGetModuleQualifiedName(node) : null;

        public void OnLeaveProcedureOrFunctionBody(ProcedureStatementBodyBase node, ModuleWalker walker) =>
            _currentNativeModuleName = null;

        public void OnEnterExecutableProcedureReference(ExecutableProcedureReference node, ModuleWalker walker)
        {
            if (_currentNativeModuleName is not { } moduleName
                || node.ProcedureReference?.ProcedureReference?.Name is not { } name)
            {
                return;
            }

            var calleeQualifiedName = SchemaObjectNameHelper.Qualify(name);
            if (IsKnownInterpreted(calleeQualifiedName))
            {
                Findings.Add(new NativelyCompiledInterpretedCalleeFinding(
                    moduleName, NativelyCompiledInterpretedCalleeKind.ExecutedProcedure, calleeQualifiedName,
                    sourcePath, node.StartLine, node.StartColumn));
            }
        }

        public void OnEnterFunctionCall(FunctionCall node, ModuleWalker walker)
        {
            if (_currentNativeModuleName is not { } moduleName)
            {
                return;
            }

            var calleeQualifiedName = SchemaObjectNameHelper.QualifyFunctionCall(node);
            if (IsKnownInterpreted(calleeQualifiedName))
            {
                Findings.Add(new NativelyCompiledInterpretedCalleeFinding(
                    moduleName, NativelyCompiledInterpretedCalleeKind.CalledFunction, calleeQualifiedName,
                    sourcePath, node.StartLine, node.StartColumn));
            }
        }

        private bool IsKnownInterpreted(string calleeQualifiedName) =>
            catalog.TryGetRoutineIsNativelyCompiled(calleeQualifiedName, out var calleeIsNativelyCompiled)
            && !calleeIsNativelyCompiled;
    }
}
