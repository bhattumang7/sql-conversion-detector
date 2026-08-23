using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.TypeInference;

public static class ExpressionTypeInferencer
{
public static SqlType? Resolve(
        ScalarExpression expression, Func<ScalarExpression, SqlType?> resolveLeaf, IReadOnlyDictionary<string, SqlType>? typeAliases) => expression switch
    {
        Literal literal => LiteralTypeResolver.Resolve(literal),

        ParenthesisExpression parenthesis => Resolve(parenthesis.Expression, resolveLeaf, typeAliases),

        UnaryExpression unary => Resolve(unary.Expression, resolveLeaf, typeAliases),

        CastCall castCall => SqlTypeReferenceResolver.Resolve(castCall.DataType, columnCollation: null, typeAliases, unsizedStringOrBinaryDefaultLength: 30),

        ConvertCall convertCall => SqlTypeReferenceResolver.Resolve(convertCall.DataType, columnCollation: null, typeAliases, unsizedStringOrBinaryDefaultLength: 30),

        TryCastCall tryCastCall => SqlTypeReferenceResolver.Resolve(tryCastCall.DataType, columnCollation: null, typeAliases, unsizedStringOrBinaryDefaultLength: 30),

        TryConvertCall tryConvertCall => SqlTypeReferenceResolver.Resolve(tryConvertCall.DataType, columnCollation: null, typeAliases, unsizedStringOrBinaryDefaultLength: 30),

        BinaryExpression binary => ResolveBinary(binary, resolveLeaf, typeAliases),

        NullIfExpression nullIf => Resolve(nullIf.FirstExpression, resolveLeaf, typeAliases),

        CoalesceExpression coalesce => CombineBranches(coalesce.Expressions, resolveLeaf, typeAliases),

        IIfCall iif => CombineBranches([iif.ThenExpression, iif.ElseExpression], resolveLeaf, typeAliases),

        SearchedCaseExpression searched => CombineCase(searched.WhenClauses, searched.ElseExpression, resolveLeaf, typeAliases),

        SimpleCaseExpression simple => CombineCase(simple.WhenClauses, simple.ElseExpression, resolveLeaf, typeAliases),

        _ => resolveLeaf(expression),
    };

private static SqlType? ResolveBinary(BinaryExpression binary, Func<ScalarExpression, SqlType?> resolveLeaf, IReadOnlyDictionary<string, SqlType>? typeAliases)
    {
        var left = Resolve(binary.FirstExpression, resolveLeaf, typeAliases);
        var right = Resolve(binary.SecondExpression, resolveLeaf, typeAliases);

        if (binary.BinaryExpressionType == BinaryExpressionType.Add && left is { IsStringFamily: true } && right is { IsStringFamily: true })
        {
            return CombineStringConcat(left, right);
        }

        return ResolveExactArithmetic(binary.BinaryExpressionType, left, right) ?? Combine(left, right);
    }

    private static SqlType? ResolveExactArithmetic(BinaryExpressionType op, SqlType? left, SqlType? right)
    {
        if (op is not (BinaryExpressionType.Add or BinaryExpressionType.Subtract or BinaryExpressionType.Multiply or BinaryExpressionType.Divide))
        {
            return null;
        }

        if (left?.Category != SqlTypeCategory.Decimal && right?.Category != SqlTypeCategory.Decimal)
        {
            return null;
        }

        if (ExactNumericPrecisionScale(left) is not { } l || ExactNumericPrecisionScale(right) is not { } r)
        {
            return null;
        }

        var (p1, s1) = l;
        var (p2, s2) = r;

        int unboundedPrecision;
        int unboundedScale;
        switch (op)
        {
            case BinaryExpressionType.Add:
            case BinaryExpressionType.Subtract:
                unboundedScale = Math.Max(s1, s2);
                unboundedPrecision = unboundedScale + Math.Max(p1 - s1, p2 - s2) + 1;
                break;
            case BinaryExpressionType.Multiply:
                unboundedPrecision = p1 + p2 + 1;
                unboundedScale = s1 + s2;
                break;
            default:
                unboundedScale = Math.Max(6, s1 + p2 + 1);
                unboundedPrecision = p1 - s1 + s2 + unboundedScale;
                break;
        }

        if (unboundedPrecision <= 38)
        {
            return new SqlType(SqlTypeCategory.Decimal, Precision: unboundedPrecision, Scale: unboundedScale);
        }

        var integralDigits = unboundedPrecision - unboundedScale;
        var finalScale = op is BinaryExpressionType.Add or BinaryExpressionType.Subtract
            ? Math.Min(unboundedScale, Math.Max(0, 39 - integralDigits))
            : Math.Min(unboundedScale, Math.Max(6, 38 - integralDigits));

        return new SqlType(SqlTypeCategory.Decimal, Precision: 38, Scale: finalScale);
    }

    private static (int Precision, int Scale)? ExactNumericPrecisionScale(SqlType? type) => type?.Category switch
    {
        SqlTypeCategory.TinyInt => (3, 0),
        SqlTypeCategory.SmallInt => (5, 0),
        SqlTypeCategory.Int => (10, 0),
        SqlTypeCategory.BigInt => (19, 0),
        SqlTypeCategory.SmallMoney => (10, 4),
        SqlTypeCategory.Money => (19, 4),
        SqlTypeCategory.Decimal when type.Precision is { } p && type.Scale is { } s => (p, s),
        _ => null,
    };

private static SqlType? CombineStringConcat(SqlType left, SqlType right)
    {
        if (left.Category != right.Category)
        {
            return Combine(left, right);
        }

        if (left.Collation is not null && right.Collation is not null && left.Collation.Name != right.Collation.Name)
        {
            return null;
        }

        var collation = left.Collation ?? right.Collation;
        if (left.IsMax || right.IsMax)
        {
            return new SqlType(left.Category, Collation: collation, IsMax: true);
        }

        if (left.Length is not { } l || right.Length is not { } r)
        {
            return new SqlType(left.Category, Collation: collation);
        }

        var maxWidth = left.Category is SqlTypeCategory.NChar or SqlTypeCategory.NVarChar ? 4000 : 8000;
        return new SqlType(left.Category, Length: Math.Min(l + r, maxWidth), Collation: collation);
    }

    private static SqlType? CombineCase(
        IEnumerable<WhenClause> whenClauses, ScalarExpression? elseExpression, Func<ScalarExpression, SqlType?> resolveLeaf, IReadOnlyDictionary<string, SqlType>? typeAliases)
    {
        var branches = whenClauses.Select(w => w.ThenExpression);
        if (elseExpression is not null)
        {
            branches = branches.Append(elseExpression);
        }

        return CombineBranches(branches, resolveLeaf, typeAliases);
    }

private static SqlType? CombineBranches(
        IEnumerable<ScalarExpression> branches, Func<ScalarExpression, SqlType?> resolveLeaf, IReadOnlyDictionary<string, SqlType>? typeAliases) =>
        CombineAll(branches.Where(e => e is not NullLiteral).Select(e => Resolve(e, resolveLeaf, typeAliases)));

private static SqlType? CombineAll(IEnumerable<SqlType?> branchTypes)
    {
        SqlType? result = null;
        var first = true;

        foreach (var branchType in branchTypes)
        {
            if (branchType is null)
            {
                return null;
            }

            result = first ? branchType : Combine(result, branchType);
            first = false;
        }

        return result;
    }

private static SqlType? Combine(SqlType? left, SqlType? right)
    {
        if (left is null || right is null)
        {
            return null;
        }

        if (left.Category == right.Category)
        {
            return left.IsStringFamily ? CombineSameCategoryStrings(left, right) : left;
        }

        var winner = left.Category > right.Category ? left : right;
        return winner.IsStringFamily ? new SqlType(winner.Category, Collation: winner.Collation, LengthKnown: false) : new SqlType(winner.Category);
    }

private static SqlType? CombineSameCategoryStrings(SqlType left, SqlType right)
    {
        if (left.Collation is not null && right.Collation is not null && left.Collation.Name != right.Collation.Name)
        {
            return null;
        }

        var collation = left.Collation ?? right.Collation;
        if (left.IsMax || right.IsMax)
        {
            return new SqlType(left.Category, Collation: collation, IsMax: true);
        }

        var length = left.Length is { } l && right.Length is { } r ? Math.Max(l, r) : left.Length ?? right.Length;
        return new SqlType(left.Category, Length: length, Collation: collation);
    }
}
