using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SilentScan.Core.Lineage;

/// <summary>
/// Resolves which syntactic region a scalar-UDF invocation sits inside, given the regions a
/// visitor claimed on its way down (a WHERE clause, a JOIN condition, a SELECT list, and so on).
/// Innermost wins: regions nest, and the smallest one containing the call is the one that actually
/// describes how the engine evaluates it.
///
/// Lives beside <see cref="ScalarUdfContext"/> rather than in either caller because both
/// <c>Lineage.ScalarUdfMap</c> and <c>Predicates.ScalarUdfScanner</c> claim regions the same way
/// and had byte-identical copies of this lookup. Predicates already depends on Lineage, never the
/// reverse, so this is the direction CLAUDE.md's pass ordering allows the shared code to sit.
/// </summary>
internal static class ScalarUdfContextRegions
{
    /// <summary>
    /// The context of the smallest claimed region containing <paramref name="node"/>, or
    /// <see cref="ScalarUdfContext.Other"/> when no region claims it. A region is half-open
    /// (<c>Start</c> inclusive, <c>End</c> exclusive), matching how the callers record it from a
    /// fragment's own offset and length.
    /// </summary>
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
