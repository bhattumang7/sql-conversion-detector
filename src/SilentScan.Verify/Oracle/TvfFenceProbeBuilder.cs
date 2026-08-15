using SilentScan.Core.Catalog;
using SilentScan.Core.Predicates;

namespace SilentScan.Verify.Oracle;

/// <summary>
/// Builds a self-authored, compile-only probe for a <see cref="TvfFenceFinding"/>
/// (docs/detection-checklist.md Tier 1 #2). Never reproduces the source predicate's own
/// arguments (a correlated APPLY's argument references an outer row the probe has no scope for)
/// - every function-call kind gets a fresh, dummy <c>CAST(NULL AS type)</c> argument list instead
/// (mirroring <see cref="CorpusFindingProbeBuilder"/>'s identical reasoning: a dummy value is
/// never itself compared against anything, only its ability to compile matters), which also
/// means the probe checks the underlying function's OWN fence-ness independent of how any one
/// call site happens to invoke it - exactly the property the finding claims.
/// </summary>
public static class TvfFenceProbeBuilder
{
    /// <summary>
    /// Builds <c>SELECT * FROM [schema].[fn](args...);</c> for every kind except
    /// <see cref="TvfFenceFindingKind.InsertExec"/> (see <see cref="BuildInsertExecProbe"/>) -
    /// <paramref name="parameterTypes"/> is the function's own resolved parameter list
    /// (<see cref="FunctionParameterReader"/>), null when it couldn't be resolved at all (an
    /// unrenderable parameter type, or the function no longer exists under this exact name).
    /// </summary>
    public static string? BuildFunctionProbe(TvfFenceFinding finding, IReadOnlyList<SqlType>? parameterTypes)
    {
        if (finding.Kind == TvfFenceFindingKind.InsertExec || finding.FunctionQualifiedName is not { } qualifiedName || parameterTypes is null)
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

        return $"SELECT * FROM {BracketQualifiedName(qualifiedName)}({string.Join(", ", arguments)});";
    }

    /// <summary>
    /// Builds <c>DECLARE @t TABLE(...); INSERT INTO @t EXEC [schema].[proc](args...);</c> for
    /// <see cref="TvfFenceFindingKind.InsertExec"/> - <paramref name="resultColumns"/> is the
    /// procedure's own described first result set (<see cref="ProcedureResultColumnReader"/>),
    /// which the receiving table variable's column COUNT must match at compile time regardless of
    /// what values ever flow through it; <paramref name="parameterTypes"/> is the procedure's own
    /// resolved parameter list (<see cref="ProcedureParameterReader"/>), same dummy-argument
    /// reasoning as <see cref="BuildFunctionProbe"/>. Either being null (unrenderable/undescribable)
    /// makes this unprobeable.
    /// </summary>
    public static string? BuildInsertExecProbe(TvfFenceFinding finding, IReadOnlyList<SqlType>? resultColumns, IReadOnlyList<SqlType>? parameterTypes)
    {
        if (finding.Kind != TvfFenceFindingKind.InsertExec
            || finding.ReferencedObjectQualifiedName is not { } procedureQualifiedName
            || resultColumns is null || resultColumns.Count == 0
            || parameterTypes is null)
        {
            return null;
        }

        var columnDeclarations = new List<string>(resultColumns.Count);
        for (var i = 0; i < resultColumns.Count; i++)
        {
            var typeSyntax = SqlTypeSyntaxFormatter.Format(resultColumns[i]);
            if (typeSyntax is null)
            {
                return null;
            }

            columnDeclarations.Add($"[c{i}] {typeSyntax}");
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

        var argumentList = arguments.Count == 0 ? string.Empty : " " + string.Join(", ", arguments);
        return $"""
            DECLARE @t TABLE({string.Join(", ", columnDeclarations)});
            INSERT INTO @t EXEC {BracketQualifiedName(procedureQualifiedName)}{argumentList};
            """;
    }

    /// <summary>
    /// The dummy, parameterless probe text <see cref="ProcedureResultColumnReader"/> describes -
    /// <c>EXEC [schema].[proc](args...);</c>, built once from the SAME resolved parameter types
    /// the real probe uses, so the described shape and the probe's own compiled shape can never
    /// disagree about how many arguments the procedure takes.
    /// </summary>
    public static string? BuildExecDescribeProbe(string procedureQualifiedName, IReadOnlyList<SqlType> parameterTypes)
    {
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

        var argumentList = arguments.Count == 0 ? string.Empty : " " + string.Join(", ", arguments);
        return $"EXEC {BracketQualifiedName(procedureQualifiedName)}{argumentList};";
    }

    private static string BracketQualifiedName(string qualifiedName)
    {
        var parts = qualifiedName.Split('.', 2);
        return parts.Length == 2 ? $"{Bracket(parts[0])}.{Bracket(parts[1])}" : Bracket(parts[0]);
    }

    private static string Bracket(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";
}
