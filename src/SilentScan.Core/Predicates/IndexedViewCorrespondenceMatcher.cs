using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

internal enum IndexedViewCorrespondence
{
    Unknown,
    Matches,
    DoesNotMatch,
}

internal static class IndexedViewCorrespondenceMatcher
{
    public static IndexedViewCorrespondence Resolve(DatabaseCatalog catalog, string sourceViewQualifiedName, string targetViewQualifiedName)
    {
        if (!TryParseSimpleView(catalog, sourceViewQualifiedName, out var sourceQuery)
            || !TryParseSimpleView(catalog, targetViewQualifiedName, out var targetQuery))
        {
            return IndexedViewCorrespondence.Unknown;
        }

        if (sourceQuery.SelectElements.Count != targetQuery.SelectElements.Count)
        {
            return IndexedViewCorrespondence.DoesNotMatch;
        }

        for (var i = 0; i < sourceQuery.SelectElements.Count; i++)
        {
            if (sourceQuery.SelectElements[i] is not SelectScalarExpression sourceScalar
                || targetQuery.SelectElements[i] is not SelectScalarExpression targetScalar)
            {
                return IndexedViewCorrespondence.Unknown;
            }

            var sourceAlias = ResolveAlias(sourceScalar);
            var targetAlias = ResolveAlias(targetScalar);
            if (sourceAlias is null || targetAlias is null)
            {
                return IndexedViewCorrespondence.Unknown;
            }

            if (!catalog.IdentifierComparer.Equals(sourceAlias, targetAlias))
            {
                return IndexedViewCorrespondence.DoesNotMatch;
            }

            var scalarEqual = ScalarEquals(sourceScalar.Expression, targetScalar.Expression, catalog);
            if (scalarEqual is null)
            {
                return IndexedViewCorrespondence.Unknown;
            }

            if (!scalarEqual.Value)
            {
                return IndexedViewCorrespondence.DoesNotMatch;
            }
        }

        return CompareWhere(sourceQuery.WhereClause, targetQuery.WhereClause, catalog) switch
        {
            null => IndexedViewCorrespondence.Unknown,
            true => IndexedViewCorrespondence.Matches,
            false => IndexedViewCorrespondence.DoesNotMatch,
        };
    }

    private static bool? CompareWhere(WhereClause? source, WhereClause? target, DatabaseCatalog catalog)
    {
        if (source is null && target is null)
        {
            return true;
        }

        if (source is null || target is null)
        {
            return false;
        }

        return BooleanEquals(source.SearchCondition, target.SearchCondition, catalog);
    }

    private static bool? BooleanEquals(BooleanExpression left, BooleanExpression right, DatabaseCatalog catalog)
    {
        if (left is BooleanParenthesisExpression leftParen)
        {
            return BooleanEquals(leftParen.Expression, right, catalog);
        }

        if (right is BooleanParenthesisExpression rightParen)
        {
            return BooleanEquals(left, rightParen.Expression, catalog);
        }

        switch (left, right)
        {
            case (BooleanComparisonExpression lc, BooleanComparisonExpression rc):
                if (lc.ComparisonType != rc.ComparisonType)
                {
                    return false;
                }

                var firstEqual = ScalarEquals(lc.FirstExpression, rc.FirstExpression, catalog);
                var secondEqual = ScalarEquals(lc.SecondExpression, rc.SecondExpression, catalog);
                return firstEqual is null || secondEqual is null ? null : firstEqual.Value && secondEqual.Value;

            case (BooleanBinaryExpression lb, BooleanBinaryExpression rb):
                if (lb.BinaryExpressionType != rb.BinaryExpressionType)
                {
                    return false;
                }

                var leftEqual = BooleanEquals(lb.FirstExpression, rb.FirstExpression, catalog);
                var rightEqual = BooleanEquals(lb.SecondExpression, rb.SecondExpression, catalog);
                return leftEqual is null || rightEqual is null ? null : leftEqual.Value && rightEqual.Value;

            case (BooleanIsNullExpression li, BooleanIsNullExpression ri):
                return li.IsNot != ri.IsNot ? false : ScalarEquals(li.Expression, ri.Expression, catalog);

            default:
                return null;
        }
    }

    private static bool? ScalarEquals(ScalarExpression left, ScalarExpression right, DatabaseCatalog catalog)
    {
        if (left is ParenthesisExpression leftParen)
        {
            return ScalarEquals(leftParen.Expression, right, catalog);
        }

        if (right is ParenthesisExpression rightParen)
        {
            return ScalarEquals(left, rightParen.Expression, catalog);
        }

        switch (left, right)
        {
            case (ColumnReferenceExpression lc, ColumnReferenceExpression rc):
                return catalog.IdentifierComparer.Equals(
                    lc.MultiPartIdentifier.Identifiers[^1].Value, rc.MultiPartIdentifier.Identifiers[^1].Value);

            case (Literal ll, Literal rl):
                return ll.LiteralType == rl.LiteralType && string.Equals(ll.Value, rl.Value, StringComparison.Ordinal);

            default:
                return null;
        }
    }

    private static string? ResolveAlias(SelectScalarExpression scalar) =>
        scalar.ColumnName?.Value
        ?? (scalar.Expression is ColumnReferenceExpression direct ? direct.MultiPartIdentifier.Identifiers[^1].Value : null);

    private static bool TryParseSimpleView(DatabaseCatalog catalog, string viewQualifiedName, out QuerySpecification query)
    {
        query = null!;
        if (!catalog.TryGetViewDefinitionText(viewQualifiedName, out var definitionText))
        {
            return false;
        }

        var result = SqlScriptParser.ParseText("indexed-view-definition.sql", definitionText, initialQuotedIdentifiers: true, catalog.CompatibilityLevel);
        if (result.HasErrors
            || result.Fragment is not TSqlScript { Batches: [{ Statements: [CreateViewStatement createView] }] }
            || createView.SelectStatement.QueryExpression is not QuerySpecification
            {
                FromClause.TableReferences: [NamedTableReference],
                GroupByClause: null,
                HavingClause: null,
                TopRowFilter: null,
                UniqueRowFilter: UniqueRowFilter.NotSpecified,
                OrderByClause: null,
            } querySpec)
        {
            return false;
        }

        query = querySpec;
        return true;
    }
}
