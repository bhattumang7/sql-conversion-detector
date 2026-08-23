using SilentScan.Core.Catalog;
using SilentScan.Core.Predicates;
using SilentScan.Core.TypeInference;

namespace SilentScan.Verify.Oracle;

public static class ScalarUdfProbeBuilder
{
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
