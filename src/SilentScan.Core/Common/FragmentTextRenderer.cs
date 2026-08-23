using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SilentScan.Core.Common;

public static class FragmentTextRenderer
{
    public static string Render(TSqlFragment fragment)
    {
        var generator = new Sql160ScriptGenerator();
        generator.GenerateScript(fragment, out var script);
        return script.Trim();
    }
}
