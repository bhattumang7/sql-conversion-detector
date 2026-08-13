using SilentScan.Core.Catalog;
using SilentScan.Verify.Catalog;
using SilentScan.Verify.Oracle;

namespace SilentScan.Live.Catalog;

/// <summary>
/// Builds the compile-only probe text <c>sys.dm_exec_describe_first_result_set</c> describes to
/// get an inline table-valued function's LIVE column shape (see
/// <see cref="LiveDescribedColumnReader"/>) - a bare <c>SELECT * FROM dbo.Fn</c> is rejected by
/// the engine ("Parameters were not supplied"), so a dummy, type-matched argument list has to be
/// synthesized, the same problem <c>SilentScan.Verify.Oracle.CorpusFindingProbeBuilder</c> solves
/// for corpus oracle probes and <see cref="FunctionParameterSpec"/>'s naming mirrors. Pure and
/// I/O-free: every input is already-read catalog metadata, so this is unit-testable without a
/// database.
/// </summary>
public static class LiveDescribeProbeBuilder
{
    /// <summary>Bare <c>SELECT * FROM [schema].[object];</c> - views need no arguments.</summary>
    public static string BuildViewProbe(string qualifiedName) =>
        $"SELECT * FROM {BracketQualifiedName(qualifiedName)};";

    /// <summary>
    /// Returns the probe text for an inline TVF, or null with a reason when at least one
    /// parameter can't be rendered as a typed <c>NULL</c> - a table-valued parameter (would need
    /// a multi-statement <c>DECLARE</c> batch, out of scope for a bare SELECT probe) or a type
    /// with no fixed T-SQL spelling (xml, CLR UDT, ...). A dummy argument is never compared
    /// against anything, so only its ability to compile matters, not its value - mirrors
    /// <c>CorpusFindingProbeBuilder.FormatTableReference</c>'s identical reasoning.
    /// </summary>
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

    private static string BracketQualifiedName(string qualifiedName)
    {
        var parts = qualifiedName.Split('.', 2);
        return parts.Length == 2 ? $"{Bracket(parts[0])}.{Bracket(parts[1])}" : Bracket(parts[0]);
    }

    private static string Bracket(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";
}

/// <summary>One inline-TVF parameter's name plus its resolved type, or null when the type has no rendering; <see cref="IsTableType"/> marks a TVP, which has no typed-NULL form at all.</summary>
public sealed record FunctionParameterSpec(string Name, SqlType? Type, bool IsTableType);
