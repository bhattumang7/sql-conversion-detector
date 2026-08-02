using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SilentScan.Core.Rules;

/// <summary>
/// Renders any ScriptDOM fragment - a bare scalar expression, a whole boolean predicate,
/// whatever the caller has in hand - back to valid, re-parseable T-SQL text, using ScriptDOM's
/// own <see cref="Sql160ScriptGenerator"/> rather than a hand-rolled renderer (which
/// <see cref="LiteralTextRenderer"/> uses for exactly the one narrow shape - a bare literal -
/// where reimplementing was simpler than pulling in the generator; a general expression shape
/// like <c>UPPER(Code)</c> or <c>Code LIKE '%x'</c> has far too many forms to hand-roll safely,
/// and the built-in generator is exactly what re-emits a fragment ScriptDOM itself just parsed).
/// 160 matches <see cref="Parsing.SqlScriptParser"/>'s own <c>TSql160Parser</c> - the version the
/// fragment was originally parsed with, kept in sync deliberately.
/// </summary>
public static class FragmentTextRenderer
{
    public static string Render(TSqlFragment fragment)
    {
        var generator = new Sql160ScriptGenerator();
        generator.GenerateScript(fragment, out var script);
        return script.Trim();
    }
}
