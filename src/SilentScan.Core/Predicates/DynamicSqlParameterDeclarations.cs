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
    /// <summary>
    /// <paramref name="typeAliases"/> lets a sp_executesql parameter declared with a
    /// <c>CREATE TYPE ... FROM</c> alias (e.g. <c>@Code dbo.CodeType</c>) resolve to that
    /// alias's real underlying type instead of null - this is called from
    /// <see cref="DynamicSqlPipeline"/>, where <c>DatabaseCatalog</c> (and therefore
    /// <c>TypeAliases</c>) already exists, unlike the dynamic SQL engine (this
    /// method's ORIGINAL caller, back when the catalog didn't exist yet at scan time - see
    /// <see cref="DynamicSqlScript.ParameterDeclarationText"/>'s own doc comment for why the
    /// parsing moved). Null (the default) still resolves only <c>sysname</c>, same as before.
    /// </summary>
    public static IReadOnlyDictionary<string, SqlType?>? TryParse(string declarationText, IReadOnlyDictionary<string, SqlType>? typeAliases = null)
    {
        if (string.IsNullOrWhiteSpace(declarationText))
        {
            return new Dictionary<string, SqlType?>();
        }

        var wrapped = $"CREATE PROCEDURE dbo.__silentscan_dynamic_params ({declarationText}) AS SELECT 1;";
        var result = SqlScriptParser.ParseText("dynamic-sql-params", wrapped);

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
