using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SilentScan.Core.Lineage;

internal static class ScalarUdfContextRegions
{
    public static ScalarUdfContext Resolve(
        IEnumerable<(int Start, int End, ScalarUdfContext Context)> regions, TSqlFragment node)
    {
        var best = default((int Start, int End, ScalarUdfContext Context)?);
        foreach (var region in regions)
        {
            if (node.StartOffset < region.Start || node.StartOffset >= region.End)
            {
                continue;
            }

            if (best is null || region.End - region.Start < best.Value.End - best.Value.Start)
            {
                best = region;
            }
        }

        return best?.Context ?? ScalarUdfContext.Other;
    }
}
