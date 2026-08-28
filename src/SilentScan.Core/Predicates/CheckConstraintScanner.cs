using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class CheckConstraintScanner
{
    public static IReadOnlyList<CheckConstraintFinding> Scan(DatabaseCatalog catalog)
    {
        var findings = new List<CheckConstraintFinding>();

        foreach (var check in catalog.CheckConstraints)
        {
            AnalyzeCheckConstraint(catalog, check, findings);
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

    private static void AnalyzeCheckConstraint(
        DatabaseCatalog catalog, CatalogCheckConstraint check, List<CheckConstraintFinding> findings)
    {
        if (check.IsDisabled || string.IsNullOrWhiteSpace(check.DefinitionText))
        {
            return;
        }

        var table = catalog.Find(check.TableQualifiedName);
        if (table is null)
        {
            return;
        }

        var searchCondition = TryParse(check.DefinitionText, catalog.CompatibilityLevel);
        if (searchCondition is null)
        {
            return;
        }

        var referencedColumnNames = new HashSet<string>(catalog.IdentifierComparer);
        var columnRefVisitor = new ColumnNameCollector(referencedColumnNames);
        searchCondition.Accept(columnRefVisitor);

        var nullGuardedColumnNames = new HashSet<string>(catalog.IdentifierComparer);
        var nullGuardVisitor = new NullGuardCollector(nullGuardedColumnNames);
        searchCondition.Accept(nullGuardVisitor);

        var sourcePath = table.SourcePath;
        var line = table.SourceLine;

        foreach (var columnName in referencedColumnNames)
        {
            var catalogColumn = table.FindColumn(columnName, catalog.IdentifierComparer);
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

    private static BooleanExpression? TryParse(string definitionText, int? compatibilityLevel)
    {
        var wrapped = $"SELECT 1 WHERE {definitionText};";
        var result = SqlScriptParser.ParseText("check-constraint.sql", wrapped, initialQuotedIdentifiers: true, compatibilityLevel);
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
