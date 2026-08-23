using SilentScan.Core.Catalog;
using SilentScan.Core.Predicates;
using SilentScan.Core.TypeInference;

namespace SilentScan.Verify.Oracle;

public static class CorpusFindingProbeBuilder
{
public static string? Build(TypedPredicateFinding finding, IReadOnlyDictionary<string, IReadOnlyList<SqlType>>? functionArguments = null)
    {
        var table = FormatTableReference(finding.Column.ImmediateRelationQualifiedName ?? finding.Column.TableQualifiedName, functionArguments);
        var column = Bracket(finding.Column.ImmediateColumnName ?? finding.Column.ColumnName);
        var op = NormalizeOperatorForProbe(finding.Operator);

        if (table is null)
        {
            return null;
        }

        var probeBody = finding.OtherOperand switch
        {
            PredicateOperand.Value { Type: not null } value => BuildValueProbe(table, column, op, value),
            PredicateOperand.Column otherColumn => BuildColumnProbe(table, column, op, otherColumn, functionArguments),
            _ => null,
        };

        if (probeBody is null)
        {
            return null;
        }

        var scaffolding = BuildTempTableScaffolding(finding);
        return scaffolding is null ? probeBody : scaffolding + probeBody;
    }

private static string? FormatTableReference(string qualifiedName, IReadOnlyDictionary<string, IReadOnlyList<SqlType>>? functionArguments)
    {
        var bracketed = BracketQualifiedName(qualifiedName);
        if (functionArguments is null || !functionArguments.TryGetValue(qualifiedName, out var parameterTypes))
        {
            return bracketed;
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

        return $"{bracketed}({string.Join(", ", arguments)})";
    }

private static string? BuildTempTableScaffolding(TypedPredicateFinding finding)
    {
        var tables = new List<(string QualifiedName, List<(string ColumnName, SqlType Type)> Columns)>();

        AddTempTableColumn(
            finding.Column.ImmediateRelationQualifiedName ?? finding.Column.TableQualifiedName,
            finding.Column.ImmediateColumnName ?? finding.Column.ColumnName,
            finding.Column.Type, tables);

        if (finding.OtherOperand is PredicateOperand.Column otherColumn)
        {
            AddTempTableColumn(
                otherColumn.ImmediateRelationQualifiedName ?? otherColumn.TableQualifiedName,
                otherColumn.ImmediateColumnName ?? otherColumn.ColumnName,
                otherColumn.Type, tables);
        }

        if (tables.Count == 0)
        {
            return null;
        }

        var declarations = tables.Select(t =>
        {
            var columnDefinitions = string.Join(", ", t.Columns.Select(c => $"{Bracket(c.ColumnName)} {SqlTypeSyntaxFormatter.Format(c.Type)}"));
            return $"CREATE TABLE {BracketQualifiedName(t.QualifiedName)} ({columnDefinitions});{Environment.NewLine}";
        });

        return string.Concat(declarations);
    }

    private static void AddTempTableColumn(
        string qualifiedName, string columnName, SqlType? type, List<(string QualifiedName, List<(string ColumnName, SqlType Type)> Columns)> tables)
    {
        if (!qualifiedName.StartsWith('#') || type is null || SqlTypeSyntaxFormatter.Format(type) is null)
        {
            return;
        }

        var table = tables.FirstOrDefault(t => string.Equals(t.QualifiedName, qualifiedName, StringComparison.OrdinalIgnoreCase));
        if (table.Columns is null)
        {
            table = (qualifiedName, []);
            tables.Add(table);
        }

        if (!table.Columns.Any(c => string.Equals(c.ColumnName, columnName, StringComparison.OrdinalIgnoreCase)))
        {
            table.Columns.Add((columnName, type));
        }
    }

    private static string NormalizeOperatorForProbe(string op) => op == "IN" ? "=" : op;

    private static string? BuildValueProbe(string table, string column, string op, PredicateOperand.Value operand)
    {
        if (operand.IsLiteral)
        {
            return operand.LiteralText is { } literalText
                ? $"SELECT 1 FROM {table} WHERE {column} {op} {literalText};"
                : null;
        }

        var typeSyntax = SqlTypeSyntaxFormatter.Format(operand.Type!);
        if (typeSyntax is null)
        {
            return null;
        }

        var collateClause = SqlTypeSyntaxFormatter.FormatCollateClause(operand.Type!);

        return $"""
            DECLARE @p {typeSyntax};
            SELECT 1 FROM {table} WHERE {column} {op} @p{collateClause};
            """;
    }

    private static string? BuildColumnProbe(
        string table, string column, string op, PredicateOperand.Column otherColumn, IReadOnlyDictionary<string, IReadOnlyList<SqlType>>? functionArguments)
    {
        var otherTable = FormatTableReference(otherColumn.ImmediateRelationQualifiedName ?? otherColumn.TableQualifiedName, functionArguments);
        if (otherTable is null)
        {
            return null;
        }

        var otherColumnName = Bracket(otherColumn.ImmediateColumnName ?? otherColumn.ColumnName);

        return $"SELECT 1 FROM {table} AS t1 CROSS JOIN {otherTable} AS t2 WHERE t1.{column} {op} t2.{otherColumnName};";
    }

    private static string BracketQualifiedName(string qualifiedName)
    {
        var parts = qualifiedName.Split('.', 2);
        return parts.Length == 2 ? $"{Bracket(parts[0])}.{Bracket(parts[1])}" : Bracket(parts[0]);
    }

    private static string Bracket(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";
}
