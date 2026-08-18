using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

/// <summary>
/// Catalog-plus-text pass over every live-read CHECK constraint (docs/detection-checklist.md Tier 2
/// §A: "CHECK constraint that doesn't account for NULL" / "CHECK constraint accidentally placed on
/// an IDENTITY column") - see <see cref="CheckConstraintFinding"/> for the full precision story and
/// oracle evidence for both kinds. Mirrors <see cref="UntrustedConstraintScanner"/>'s own shape: a
/// catalog-only entry point invoked once per scan, always empty in file mode since <see
/// cref="CatalogCheckConstraint.DefinitionText"/> is only ever populated by <c>LiveCatalogReader</c>.
/// </summary>
public static class CheckConstraintScanner
{
    public static IReadOnlyList<CheckConstraintFinding> Scan(DatabaseCatalog catalog)
    {
        var findings = new List<CheckConstraintFinding>();

        foreach (var check in catalog.CheckConstraints)
        {
            if (check.IsDisabled || string.IsNullOrWhiteSpace(check.DefinitionText))
            {
                continue;
            }

            var table = catalog.Find(check.TableQualifiedName);
            if (table is null)
            {
                continue;
            }

            var searchCondition = TryParse(check.DefinitionText);
            if (searchCondition is null)
            {
                continue;
            }

            var referencedColumnNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var columnRefVisitor = new ColumnNameCollector(referencedColumnNames);
            searchCondition.Accept(columnRefVisitor);

            // Liberal, OR-branches-included collection - deliberately the inverse use of the
            // "AND-only-reachable" discipline other scanners in this codebase apply: here the
            // ABSENCE of a NULL guard is what triggers a finding, so a guard reachable ANYWHERE
            // (including inside an OR branch, the textbook `Col IS NULL OR Col > 0` fix) must
            // count as "handled" - see CheckConstraintFinding.NullNotHandled's own doc comment.
            var nullGuardedColumnNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var nullGuardVisitor = new NullGuardCollector(nullGuardedColumnNames);
            searchCondition.Accept(nullGuardVisitor);

            var sourcePath = table.SourcePath;
            var line = table.SourceLine;

            foreach (var columnName in referencedColumnNames)
            {
                var catalogColumn = table.FindColumn(columnName);
                if (catalogColumn is null)
                {
                    continue;
                }

                if (catalogColumn.IsNullable && !nullGuardedColumnNames.Contains(columnName))
                {
                    findings.Add(new CheckConstraintFinding(
                        CheckConstraintFindingKind.NullNotHandled, check.ConstraintName, check.TableQualifiedName,
                        catalogColumn.Name, sourcePath, line));
                }

                if (catalogColumn.IsIdentity)
                {
                    findings.Add(new CheckConstraintFinding(
                        CheckConstraintFindingKind.ConstraintOnIdentityColumn, check.ConstraintName, check.TableQualifiedName,
                        catalogColumn.Name, sourcePath, line));
                }
            }
        }

        return
        [
            .. findings
                .OrderBy(f => f.Kind)
                .ThenBy(f => f.TableQualifiedName, StringComparer.Ordinal)
                .ThenBy(f => f.ConstraintName, StringComparer.Ordinal)
                .ThenBy(f => f.ColumnName, StringComparer.Ordinal),
        ];
    }

    /// <summary>A CHECK constraint's definition is a boolean predicate (not valid as a bare SELECT
    /// list expression), so it wraps under WHERE exactly like <see
    /// cref="SchemaDependencyScanner"/>'s own identical reparse of the same text - the only shape
    /// both file mode and live mode's plain <c>sys.check_constraints.definition</c> string can
    /// share. A CHECK constraint can never itself contain a subquery (the engine rejects that at
    /// DDL time), so there is no cross-scope column reference to worry about here.</summary>
    private static BooleanExpression? TryParse(string definitionText)
    {
        var wrapped = $"SELECT 1 WHERE {definitionText};";
        var result = SqlScriptParser.ParseText("check-constraint.sql", wrapped);
        if (result.HasErrors || result.Fragment is not TSqlScript script)
        {
            return null;
        }

        return script.Batches
            .SelectMany(b => b.Statements)
            .OfType<SelectStatement>()
            .Select(s => s.QueryExpression)
            .OfType<QuerySpecification>()
            .Select(q => q.WhereClause?.SearchCondition)
            .FirstOrDefault(c => c is not null);
    }

    /// <summary>Collects every column name referenced anywhere in the predicate - a CHECK
    /// constraint's own definition never carries a table alias (it can only ever reference columns
    /// of the one table it's declared on), so the last identifier is always the bare column name.</summary>
    private sealed class ColumnNameCollector(HashSet<string> names) : TSqlFragmentVisitor
    {
        public override void ExplicitVisit(ColumnReferenceExpression node)
        {
            var identifiers = node.MultiPartIdentifier?.Identifiers;
            if (identifiers is { Count: > 0 })
            {
                names.Add(identifiers[^1].Value);
            }

            base.ExplicitVisit(node);
        }
    }

    /// <summary>Collects the column name of every bare <c>IS NULL</c>/<c>IS NOT NULL</c> test
    /// reachable anywhere in the predicate tree, deliberately not gated by AND/OR nesting - see
    /// <see cref="Scan"/>'s own comment for why liberal collection is the correct, safe direction
    /// for this specific kind. Only a direct <c>col IS [NOT] NULL</c> shape counts, matching
    /// <see cref="CheckConstraintFindingKind.NullNotHandled"/>'s own doc comment - an
    /// <c>ISNULL(col, ...)</c>/<c>COALESCE(col, ...)</c> function call is a materially different AST
    /// shape (a scalar function, not a boolean test) and is not treated as an equivalent guard.</summary>
    private sealed class NullGuardCollector(HashSet<string> names) : TSqlFragmentVisitor
    {
        public override void ExplicitVisit(BooleanIsNullExpression node)
        {
            if (node.Expression is ColumnReferenceExpression { MultiPartIdentifier.Identifiers: { Count: > 0 } identifiers })
            {
                names.Add(identifiers[^1].Value);
            }

            base.ExplicitVisit(node);
        }
    }
}
