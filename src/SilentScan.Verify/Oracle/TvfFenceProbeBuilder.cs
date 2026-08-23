using SilentScan.Core.Catalog;
using SilentScan.Core.Predicates;
using SilentScan.Core.TypeInference;

namespace SilentScan.Verify.Oracle;

public static class TvfFenceProbeBuilder
{
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
