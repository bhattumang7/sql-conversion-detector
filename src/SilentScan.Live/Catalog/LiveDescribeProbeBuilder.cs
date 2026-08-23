using SilentScan.Core.Catalog;
using SilentScan.Verify.Catalog;
using SilentScan.Verify.Oracle;
using SilentScan.Core.TypeInference;

namespace SilentScan.Live.Catalog;

public static class LiveDescribeProbeBuilder
{
public static string BuildViewProbe(string qualifiedName) =>
        $"SELECT * FROM {BracketQualifiedName(qualifiedName)};";

public static (string? Probe, string? UnrenderableReason) BuildFunctionProbe(
        string qualifiedName, IReadOnlyList<FunctionParameterSpec> parameters)
    {
        var arguments = new List<string>(parameters.Count);
        foreach (var parameter in parameters)
        {
            if (parameter.IsTableType)
            {
                return (null, $"parameter '{parameter.Name}' is a table-valued parameter, which cannot be supplied as a typed NULL");
            }

            var typeSyntax = parameter.Type is { } type ? SqlTypeSyntaxFormatter.Format(type) : null;
            if (typeSyntax is null)
            {
                return (null, $"parameter '{parameter.Name}' has a type this tool cannot render as T-SQL syntax");
            }

            arguments.Add($"CAST(NULL AS {typeSyntax})");
        }

        return ($"SELECT * FROM {BracketQualifiedName(qualifiedName)}({string.Join(", ", arguments)});", null);
    }

public static (string? Probe, string? UnrenderableReason) BuildProcedureProbe(
        string qualifiedName, IReadOnlyList<ProcedureParameterSpec> parameters)
    {
        foreach (var parameter in parameters)
        {
            if (parameter.IsOutput)
            {
                return (null, $"parameter '{parameter.Name}' is an OUTPUT parameter, which this probe cannot supply a positional value for");
            }

            if (parameter.IsTableType)
            {
                return (null, $"parameter '{parameter.Name}' is a table-valued parameter, which has no positional literal form");
            }
        }

        var argumentText = parameters.Count > 0 ? " " + string.Join(", ", parameters.Select(_ => "NULL")) : string.Empty;
        return ($"EXEC {BracketQualifiedName(qualifiedName)}{argumentText};", null);
    }

    private static string BracketQualifiedName(string qualifiedName)
    {
        var parts = qualifiedName.Split('.', 2);
        return parts.Length == 2 ? $"{Bracket(parts[0])}.{Bracket(parts[1])}" : Bracket(parts[0]);
    }

    private static string Bracket(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";
}

public sealed record FunctionParameterSpec(string Name, SqlType? Type, bool IsTableType);

public sealed record ProcedureParameterSpec(string Name, bool IsTableType, bool IsOutput);
