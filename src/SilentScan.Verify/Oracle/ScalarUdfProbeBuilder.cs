using SilentScan.Core.Catalog;
using SilentScan.Core.Predicates;
using SilentScan.Core.TypeInference;

namespace SilentScan.Verify.Oracle;

/// <summary>
/// Builds a self-authored, compile-only probe for a <see cref="ScalarUdfFinding"/>
/// (docs/detection-checklist.md Tier 1 #1). Always probes <see cref="ScalarUdfFinding.FunctionQualifiedName"/>
/// directly - even for <see cref="ScalarUdfFindingKind.NestedUnderViewOrTvf"/>, where that is the
/// underlying scalar UDF, NOT the view/iTVF actually named at the call site (mirroring
/// <see cref="TvfFenceProbeBuilder"/>'s identical choice for its own nested kind). This is
/// deliberate, not a shortcut: oracle-verified directly against the local Docker instance, a
/// scalar UDF called INSIDE a view expands and folds away under
/// <c>OPTION (USE HINT('DISABLE_TSQL_SCALAR_UDF_INLINING'))</c> even though the identical call
/// made directly at the top level does NOT - the hint does not propagate into a view's own
/// algebrized definition. Probing the function directly sidesteps that entirely; the lineage
/// pass that attributes the call to the view (<c>SilentScan.Core.Lineage.ScalarUdfMap</c>) is a
/// deterministic AST/catalog walk, already covered by its own unit tests, not something the
/// oracle needs to re-confirm.
/// Never reproduces the source call's own arguments - every argument gets a fresh, dummy
/// <c>CAST(NULL AS type)</c> instead (identical reasoning to <see cref="TvfFenceProbeBuilder"/>).
/// </summary>
public static class ScalarUdfProbeBuilder
{
    /// <summary>
    /// Builds <c>SELECT [schema].[fn](args...)[ OPTION (USE HINT('DISABLE_TSQL_SCALAR_UDF_INLINING'))];</c>
    /// - null for <see cref="ScalarUdfFindingKind.SchemaDependency"/> (never probed; see
    /// <see cref="ScalarUdfVerifier"/>) or when <paramref name="parameterTypes"/> couldn't be
    /// resolved/rendered.
    /// </summary>
    public static string? BuildInvocationProbe(ScalarUdfFinding finding, IReadOnlyList<SqlType>? parameterTypes, bool pinInlining)
    {
        if (finding.Kind == ScalarUdfFindingKind.SchemaDependency || parameterTypes is null)
        {
            return null;
        }

        var arguments = new List<string>(parameterTypes.Count);
        foreach (var type in parameterTypes)
        {
            var typeSyntax = SqlTypeSyntaxFormatter.Format(type);
            if (typeSyntax is null)
            {
                return null;
            }

            arguments.Add($"CAST(NULL AS {typeSyntax})");
        }

        var hint = pinInlining ? " OPTION (USE HINT('DISABLE_TSQL_SCALAR_UDF_INLINING'))" : string.Empty;
        return $"SELECT {BracketQualifiedName(finding.FunctionQualifiedName)}({string.Join(", ", arguments)}){hint};";
    }

    private static string BracketQualifiedName(string qualifiedName)
    {
        var parts = qualifiedName.Split('.', 2);
        return parts.Length == 2 ? $"{Bracket(parts[0])}.{Bracket(parts[1])}" : Bracket(parts[0]);
    }

    private static string Bracket(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";
}
