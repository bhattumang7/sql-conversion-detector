using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SilentScan.Core.Common;

internal static class NativelyCompiledModuleHelper
{
    public static bool IsNativelyCompiled(ProcedureStatementBodyBase node) => node switch
    {
        ProcedureStatementBody procedure => procedure.Options.Any(o => o.OptionKind == ProcedureOptionKind.NativeCompilation),
        FunctionStatementBody function => function.Options.Any(o => o.OptionKind == FunctionOptionKind.NativeCompilation),
        _ => false,
    };

    public static string? TryGetModuleQualifiedName(ProcedureStatementBodyBase node) => node switch
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
