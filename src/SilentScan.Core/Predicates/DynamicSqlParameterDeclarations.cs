using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

/// <summary>
/// Parses sp_executesql's second argument - a comma-separated parameter list identical in
/// grammar to a stored procedure's parameter list (e.g. <c>@DisplayName nvarchar(40)</c>) - by
/// wrapping it in a throwaway CREATE PROCEDURE and reusing ScriptDOM's own parameter grammar
/// and <see cref="SqlTypeReferenceResolver"/>, rather than hand-rolling a second parser. This is
/// Tier B of CLAUDE.md's dynamic SQL policy: the declared parameter types are exact, better
/// type information than most static SQL gets, and the classic ORM-generated
/// varchar-column-vs-nvarchar-parameter bug shows up here more than anywhere else.
/// </summary>
public static class DynamicSqlParameterDeclarations
{
    public static IReadOnlyDictionary<string, SqlType?>? TryParse(string declarationText)
    {
        if (string.IsNullOrWhiteSpace(declarationText))
        {
            return new Dictionary<string, SqlType?>();
        }

        var wrapped = $"CREATE PROCEDURE dbo.__silentscan_dynamic_params ({declarationText}) AS SELECT 1;";
        var result = new SqlScriptParser().ParseText("dynamic-sql-params", wrapped);

        if (result.HasErrors
            || result.Fragment is not TSqlScript { Batches: [{ Statements: [CreateProcedureStatement createProcedure] }] })
        {
            return null;
        }

        var declared = new Dictionary<string, SqlType?>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in createProcedure.Parameters)
        {
            declared[parameter.VariableName.Value] = SqlTypeReferenceResolver.Resolve(parameter.DataType, columnCollation: null);
        }

        return declared;
    }
}
