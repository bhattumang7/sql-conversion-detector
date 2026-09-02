using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Common;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class NativelyCompiledClrTypeScanner
{
    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    public static IReadOnlyList<NativelyCompiledClrTypeFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
    {
        var rule = CreateRule(parseResult.SourcePath, catalog);
        var walker = new ModuleWalker(parseResult.SourcePath, catalog, EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return Harvest(rule);
    }

    internal static Rule CreateRule(string sourcePath, DatabaseCatalog catalog) => new(sourcePath, catalog);

    internal static IReadOnlyList<NativelyCompiledClrTypeFinding> Harvest(Rule rule) =>
        [
            .. rule.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];

    internal sealed class Rule(string sourcePath, DatabaseCatalog catalog) : IModuleRule
    {
        private string? _currentNativeModuleName;

        public List<NativelyCompiledClrTypeFinding> Findings { get; } = [];

        public void OnEnterProcedureOrFunctionBody(ProcedureStatementBodyBase node, ModuleWalker walker)
        {
            if (!IsNativelyCompiled(node))
            {
                _currentNativeModuleName = null;
                return;
            }

            _currentNativeModuleName = TryGetModuleQualifiedName(node);
            if (_currentNativeModuleName is not { } moduleName)
            {
                return;
            }

            foreach (var parameter in node.Parameters)
            {
                if (TryGetClrTypeName(parameter.DataType) is { } typeName)
                {
                    Findings.Add(new NativelyCompiledClrTypeFinding(
                        moduleName, NativelyCompiledClrTypeKind.Parameter, parameter.VariableName.Value, typeName,
                        sourcePath, parameter.StartLine, parameter.StartColumn));
                }
            }
        }

        public void OnLeaveProcedureOrFunctionBody(ProcedureStatementBodyBase node, ModuleWalker walker) =>
            _currentNativeModuleName = null;

        public void OnEnterDeclareVariableStatement(DeclareVariableStatement node, ModuleWalker walker)
        {
            if (_currentNativeModuleName is not { } moduleName)
            {
                return;
            }

            foreach (var declaration in node.Declarations)
            {
                if (TryGetClrTypeName(declaration.DataType) is { } typeName)
                {
                    Findings.Add(new NativelyCompiledClrTypeFinding(
                        moduleName, NativelyCompiledClrTypeKind.LocalVariable, declaration.VariableName.Value, typeName,
                        sourcePath, declaration.StartLine, declaration.StartColumn));
                }
            }
        }

        private string? TryGetClrTypeName(DataTypeReference? dataType)
        {
            if (dataType is not UserDataTypeReference userType)
            {
                return null;
            }

            var qualifiedName = SchemaObjectNameHelper.Qualify(userType.Name);
            return catalog.IsClrUserDefinedType(qualifiedName) ? qualifiedName : null;
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
