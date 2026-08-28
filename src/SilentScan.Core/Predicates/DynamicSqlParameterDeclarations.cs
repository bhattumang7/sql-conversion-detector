using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

public static class DynamicSqlParameterDeclarations
{
    public static IReadOnlyDictionary<string, SqlType?>? TryParse(
        string declarationText, IReadOnlyDictionary<string, SqlType>? typeAliases = null, int? compatibilityLevel = null)
    {
        if (string.IsNullOrWhiteSpace(declarationText))
        {
            return new Dictionary<string, SqlType?>();
        }

        var wrapped = $"CREATE PROCEDURE dbo.__silentscan_dynamic_params ({declarationText}) AS SELECT 1;";
        var result = SqlScriptParser.ParseText("dynamic-sql-params", wrapped, initialQuotedIdentifiers: true, compatibilityLevel);

        if (result.HasErrors
            || result.Fragment is not TSqlScript { Batches: [{ Statements: [CreateProcedureStatement createProcedure] }] })
        {
            return null;
        }

        var declared = new Dictionary<string, SqlType?>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in createProcedure.Parameters)
        {
            declared[parameter.VariableName.Value] = SqlTypeReferenceResolver.Resolve(parameter.DataType, columnCollation: null, typeAliases);
        }

        return declared;
    }
}
