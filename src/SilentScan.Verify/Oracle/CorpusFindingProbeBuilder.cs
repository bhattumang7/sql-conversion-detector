using SilentScan.Core.Catalog;
using SilentScan.Core.Predicates;

namespace SilentScan.Verify.Oracle;

/// <summary>
/// Builds a self-authored, compile-only probe statement for a <see cref="TypedPredicateFinding"/>
/// against the corpus repo's own deployed DDL (CLAUDE.md Verify: "for each SCAN_FORCED
/// finding, execute a parameterized probe of the predicate and confirm CONVERT_IMPLICIT-on-
/// column"). The probe never runs the repo's own SQL - it reconstructs an equivalent minimal
/// comparison from the finding's resolved column and operand types, so only tables/columns
/// are borrowed from the corpus, never its logic.
///
/// Queries the column's <see cref="PredicateOperand.Column.ImmediateRelationQualifiedName"/>
/// (the view/TVF the source predicate was actually written against) when one is set, rather
/// than always the ultimate base table - a depth&gt;=1 finding probed straight against the base
/// table never exercises the view layer it claims to be inherited through at all, which made
/// the oracle structurally unable to confirm the tool's own core differentiator. Both views
/// and the underlying base table produce the SAME plan-level signal once the optimizer inlines
/// the view (CONVERT_IMPLICIT still appears against the base column, not the view's), so <see
/// cref="CorpusFindingVerifier"/>'s confirmation matching (against TableQualifiedName/
/// ColumnName) needs no change. Falls back to the base table when there's no immediate relation
/// (Depth 0) or it isn't queryable bare - a resulting compile failure surfaces honestly as
/// ProbeFailed rather than silently substituting a guess.
/// </summary>
public static class CorpusFindingProbeBuilder
{
    /// <summary>Returns the probe SQL for <paramref name="finding"/>, or null if the finding lacks enough type information to synthesize one (reported as not-probeable, never guessed).</summary>
    public static string? Build(TypedPredicateFinding finding)
    {
        var table = BracketQualifiedName(finding.Column.ImmediateRelationQualifiedName ?? finding.Column.TableQualifiedName);
        var column = Bracket(finding.Column.ImmediateColumnName ?? finding.Column.ColumnName);
        var op = NormalizeOperatorForProbe(finding.Operator);

        var probeBody = finding.OtherOperand switch
        {
            PredicateOperand.Value { Type: not null } value => BuildValueProbe(table, column, op, value),
            PredicateOperand.Column otherColumn => BuildColumnProbe(table, column, op, otherColumn),
            _ => null,
        };

        if (probeBody is null)
        {
            return null;
        }

        var scaffolding = BuildTempTableScaffolding(finding);
        return scaffolding is null ? probeBody : scaffolding + probeBody;
    }

    /// <summary>
    /// A finding's own column, or its "other" side when that's a column too, can name a real
    /// object this probe's session never deployed to standalone at all: a <c>#temp</c>/
    /// <c>##global temp</c> table CLAUDE.md already tracks the shape of (parser-derived catalog
    /// data the engine itself can't expose, since the table only ever existed transiently inside
    /// the ORIGINAL proc's own execution). Without this, every finding against one failed
    /// outright with "Invalid object name" - not a soundness gap (the finding's own classification
    /// never depended on the probe), but a real, avoidable loss of oracle coverage for a genuinely
    /// common corpus shape. Oracle-verified (SET SHOWPLAN_XML ON compiles a <c>CREATE TABLE
    /// #T(...); SELECT ... FROM #T ...;</c> batch as one unit, establishing the temp table's
    /// schema for the second statement's own compilation) that this is sound under compile-only
    /// SHOWPLAN_XML, never touching real data. Declared using ONLY the type this scanner already
    /// resolved for the referenced column - the temp table's OTHER columns (never touched by this
    /// probe) are never synthesized, since inventing types for them this scanner never actually
    /// saw would be a guess.
    /// </summary>
    private static string? BuildTempTableScaffolding(TypedPredicateFinding finding)
    {
        // An ordered list, not a Dictionary (CLAUDE.md: "deterministic output ordering") - a
        // self-join probe (BuildColumnProbe aliasing the SAME temp table as both t1 and t2)
        // needs every column either side references declared on the ONE synthesized object, not
        // two colliding CREATE TABLEs or a table missing whichever column only the second
        // reference named, so tables are grouped by qualified name here rather than emitted as
        // two independent declarations.
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
        // A temp table name is always a single bare identifier (T-SQL has no schema-qualified
        // #temp table syntax) - '#'/'##' is checked on the name itself, never split apart. A
        // type this scanner couldn't render as T-SQL syntax (SqlTypeSyntaxFormatter.Format
        // returning null) drops just this ONE column's declaration rather than the whole probe -
        // Build's own caller already declines the whole probe if scaffolding alone can't make it
        // compile, via the ordinary "still fails, ProbeFailed" path.
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

    // IN-list findings collapse the whole list to one effective "other type" for classification
    // (docs/audit-remediation-plan.md Phase 4.3) - `Col IN (@p)` isn't valid syntax for a single
    // scalar operand, but `Col = @p` exercises the identical CONVERT_IMPLICIT behavior the
    // classifier actually reasoned about, so it stands in for probing purposes.
    private static string NormalizeOperatorForProbe(string op) => op == "IN" ? "=" : op;

    private static string? BuildValueProbe(string table, string column, string op, PredicateOperand.Value operand)
    {
        if (operand.IsLiteral)
        {
            // Reconstructs the literal exactly rather than substituting a same-typed variable
            // (docs/audit-remediation-plan.md Phase 5.2, audit finding C2) - verified against
            // the real engine that these are NOT always equivalent (a bare string literal like
            // N'x' types as nvarchar(8000), not the parameterized probe's content-length
            // nvarchar(n)). A literal kind LiteralTextRenderer doesn't cover fails closed (null)
            // instead of silently falling back to a variable, which would misrepresent fidelity.
            return operand.LiteralText is { } literalText
                ? $"SELECT 1 FROM {table} WHERE {column} {op} {literalText};"
                : null;
        }

        var typeSyntax = SqlTypeSyntaxFormatter.Format(operand.Type!);
        if (typeSyntax is null)
        {
            return null;
        }

        // COLLATE belongs on the operand's use site, not its DECLARE - T-SQL rejects
        // `DECLARE @p VARCHAR(n) COLLATE ...` outright (verified against the oracle).
        var collateClause = SqlTypeSyntaxFormatter.FormatCollateClause(operand.Type!);

        return $"""
            DECLARE @p {typeSyntax};
            SELECT 1 FROM {table} WHERE {column} {op} @p{collateClause};
            """;
    }

    private static string? BuildColumnProbe(string table, string column, string op, PredicateOperand.Column otherColumn)
    {
        var otherTable = BracketQualifiedName(otherColumn.ImmediateRelationQualifiedName ?? otherColumn.TableQualifiedName);
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
