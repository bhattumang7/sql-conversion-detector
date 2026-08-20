using SilentScan.Core.Catalog;
using SilentScan.Verify.Catalog;
using SilentScan.Verify.Oracle;
using SilentScan.Core.TypeInference;

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

    /// <summary>
    /// Returns the probe text for a stored procedure's <c>INSERT ... EXEC</c> shape (docs/
    /// detection-checklist.md Tier 2 "Dynamic SQL quality" item 3, temp-table shape mismatch
    /// across a proc-call boundary) - a bare, positional <c>EXEC [schema].[proc] NULL, ...;</c>.
    /// Unlike <see cref="BuildFunctionProbe"/>'s inline-TVF-call argument list, T-SQL's own
    /// <c>EXECUTE</c> grammar accepts only a constant or a variable as an argument value, never an
    /// arbitrary expression - <c>CAST(NULL AS type)</c> is a real parse error here (oracle-
    /// confirmed against the Docker instance: Msg 156, "Incorrect syntax near the keyword
    /// 'NULL'"), so a bare, untyped <c>NULL</c> literal is used instead (oracle-confirmed to
    /// compile and implicitly convert to the parameter's own declared type, whatever it is) -
    /// simpler than the function-probe path AND correct, since a probe argument is never compared
    /// against anything here, only its ability to compile matters. Declines (returns null) only
    /// for a case that never arises for a table-valued function: an <c>OUTPUT</c> parameter - a
    /// plain positional value is not valid T-SQL for one, and silently omitting it would risk
    /// either a parse error on a required parameter or a misleading probe for one with a default -
    /// "report, don't guess" applies equally here. A table-valued parameter is declined for the
    /// same reason <see cref="BuildFunctionProbe"/> declines one: no positional literal form exists
    /// for a TVP at all.
    /// </summary>
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

/// <summary>One inline-TVF parameter's name plus its resolved type, or null when the type has no rendering; <see cref="IsTableType"/> marks a TVP, which has no typed-NULL form at all.</summary>
public sealed record FunctionParameterSpec(string Name, SqlType? Type, bool IsTableType);

/// <summary>
/// One stored-procedure parameter's name and whether it's table-valued/declared OUTPUT - no
/// resolved type, unlike <see cref="FunctionParameterSpec"/>: <see cref="LiveDescribeProbeBuilder.BuildProcedureProbe"/>
/// renders a bare, untyped <c>NULL</c> for every non-output, non-table-valued parameter (EXECUTE's
/// own grammar only accepts a constant or a variable, never a typed <c>CAST</c> expression), so
/// nothing here ever needs the parameter's own declared type.
/// </summary>
public sealed record ProcedureParameterSpec(string Name, bool IsTableType, bool IsOutput);
