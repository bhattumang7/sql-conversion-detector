using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.TypeInference;

/// <summary>
/// Roadmap Phase B: the single shared authority for typing the scalar-expression shapes that
/// are NOT pass-specific - arithmetic, parenthesis/unary wrapping, CAST/CONVERT, and CASE/
/// COALESCE/NULLIF/IIF result typing. Before this, three passes (<see
/// cref="Catalog.ComputedColumnTypeResolver"/>, <see cref="Predicates.TypedPredicateExtractor"/>'s
/// operand dispatch, and <see cref="Lineage.ScalarExpressionResolver"/>'s generic fallback) each
/// either duplicated a slice of this logic or fell straight to Unknown for CASE/COALESCE/NULLIF/
/// IIF/arithmetic - CLAUDE.md's own named hard cases, previously unimplemented everywhere at
/// once. A caller-supplied <c>resolveLeaf</c> callback handles anything genuinely pass-specific
/// (column references needing scope resolution, variables, ordinary function calls needing a
/// builtin/UDF registry lookup, subqueries) - this class recurses back into itself for every
/// sub-expression of a shape it does own, and defers to the callback for everything else.
///
/// CASE/COALESCE/IIF result typing and NULLIF's are NOT the same rule, verified against the real
/// Docker oracle (not assumed from documentation) before being encoded here: CASE/COALESCE/IIF
/// merge every branch by T-SQL data type precedence (SqlTypeCategory's ordinal), while NULLIF
/// always returns its FIRST expression's own type, unmodified - probed with
/// <c>CASE WHEN 1=1 THEN IntCol ELSE DecCol END</c> / <c>COALESCE(IntCol, DecCol)</c> /
/// <c>IIF(1=1, IntCol, DecCol)</c> all resolving DECIMAL, versus <c>NULLIF(IntCol, DecCol)</c>
/// resolving INT - expr1's own type, not the precedence winner.
///
/// Facet (length/precision/scale) computation for a merged result is a KNOWN approximation: SQL
/// Server actually widens precision further for a merged numeric result (probed: <c>CASE WHEN
/// 1=1 THEN IntCol ELSE Decimal(9,2)Col END</c> resolves DECIMAL(12,2), not DECIMAL(9,2)) - this
/// class returns the precedence winner's OWN declared facets unchanged, matching the identical
/// simplification arithmetic combination already made before this class existed. This is safe
/// for this tool's purposes: <see cref="Rules.VerdictClassifier"/> never consults precision/length in
/// a cross-category decision, only category and collation, so an imprecise facet never produces
/// a wrong verdict - only a wrong facet display if one were ever surfaced literally (not
/// currently done anywhere).
/// </summary>
public static class ExpressionTypeInferencer
{
    /// <param name="expression">The expression to type.</param>
    /// <param name="resolveLeaf">
    /// Called for any expression kind this class doesn't own: column references, variables,
    /// global variables, ordinary function calls, scalar subqueries, etc. Recursion loops back
    /// into <see cref="Resolve"/> for sub-expressions of a shape this class DOES own (e.g. a
    /// CASE branch that is itself a nested CASE), never back through <paramref name="resolveLeaf"/>
    /// for those.
    /// </param>
    /// <param name="typeAliases">CREATE TYPE ... FROM aliases, for CAST/CONVERT target resolution.</param>
    public static SqlType? Resolve(
        ScalarExpression expression, Func<ScalarExpression, SqlType?> resolveLeaf, IReadOnlyDictionary<string, SqlType>? typeAliases) => expression switch
    {
        Literal literal => LiteralTypeResolver.Resolve(literal),

        ParenthesisExpression parenthesis => Resolve(parenthesis.Expression, resolveLeaf, typeAliases),

        UnaryExpression unary => Resolve(unary.Expression, resolveLeaf, typeAliases),

        // An unsized CAST/CONVERT to a string/binary-family type silently means 30 characters
        // (oracle-confirmed - docs/detection-checklist.md "Small precise adds", "Explicit-length
        // audit of CAST/CONVERT to a string type"), a materially different default than a bare
        // DECLARE's own length-1 default - see SqlTypeReferenceResolver.Resolve's own doc comment.
        CastCall castCall => SqlTypeReferenceResolver.Resolve(castCall.DataType, columnCollation: null, typeAliases, unsizedStringOrBinaryDefaultLength: 30),

        ConvertCall convertCall => SqlTypeReferenceResolver.Resolve(convertCall.DataType, columnCollation: null, typeAliases, unsizedStringOrBinaryDefaultLength: 30),

        // TRY_CAST/TRY_CONVERT are distinct ScriptDom node types (TryCastCall/TryConvertCall),
        // not CastCall/ConvertCall with a flag - confirmed directly by parsing TRY_CAST(x AS
        // DATE) and inspecting the resulting fragment's own runtime type. The DECLARED result
        // type is identical to the non-TRY form either way (TRY_CAST returns either that exact
        // type or NULL, never a different type) - this is purely a type-inference completeness
        // fix (docs/detection-checklist.md "Second full-archive practitioner sweep" §G's
        // TRY_CAST computed-column item needs a real resolved type for the column to ever
        // resolve through FromScopeResolver as a genuine BaseColumn), not a determinism claim -
        // TryCastComputedColumnPredicateFinding's own non-determinism/non-indexability claim is
        // unaffected either way.
        TryCastCall tryCastCall => SqlTypeReferenceResolver.Resolve(tryCastCall.DataType, columnCollation: null, typeAliases, unsizedStringOrBinaryDefaultLength: 30),

        TryConvertCall tryConvertCall => SqlTypeReferenceResolver.Resolve(tryConvertCall.DataType, columnCollation: null, typeAliases, unsizedStringOrBinaryDefaultLength: 30),

        BinaryExpression binary => ResolveBinary(binary, resolveLeaf, typeAliases),

        // NULLIF is NOT a precedence merge (oracle-verified - see class remarks): always
        // expr1's own type, regardless of what expr2 is. expr2 is still walked for a nested
        // leaf's sake (e.g. a variable inside it might matter to a caller's own bookkeeping),
        // but never combined into the result type.
        NullIfExpression nullIf => Resolve(nullIf.FirstExpression, resolveLeaf, typeAliases),

        CoalesceExpression coalesce => CombineBranches(coalesce.Expressions, resolveLeaf, typeAliases),

        IIfCall iif => CombineBranches([iif.ThenExpression, iif.ElseExpression], resolveLeaf, typeAliases),

        SearchedCaseExpression searched => CombineCase(searched.WhenClauses, searched.ElseExpression, resolveLeaf, typeAliases),

        SimpleCaseExpression simple => CombineCase(simple.WhenClauses, simple.ElseExpression, resolveLeaf, typeAliases),

        _ => resolveLeaf(expression),
    };

    /// <summary>
    /// String concatenation (<c>+</c> where both sides are string-family) is NOT the same rule
    /// as CASE/COALESCE's own precedence-branch merge, oracle-verified directly (Docker,
    /// sys.columns.max_length off a SELECT ... INTO probe): <c>varchar(10) + varchar(15)</c>
    /// resolves <c>varchar(25)</c> - the SUM of the two lengths, not <c>Math.Max</c> of
    /// them (which is CASE/COALESCE's own rule, <see cref="CombineSameCategoryStrings"/>). Every
    /// other binary-expression shape (arithmetic +/-/*, or a `+` where at least one side isn't
    /// string-family) still uses the general precedence <see cref="Combine"/> path unchanged.
    /// </summary>
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

    /// <summary>
    /// Sums both operands' lengths, capped at the category's own hard maximum width (8000 for
    /// char/varchar, 4000 for nchar/nvarchar - <c>Length</c> is a character count throughout this
    /// codebase, so nvarchar's 4000-character cap is the same 8000-BYTE limit varchar's 8000-
    /// character cap is) - oracle-verified directly: <c>varchar(5000) + varchar(5000)</c> (sum
    /// 10000) resolves <c>varchar(8000)</c>, never auto-promoting to MAX the way an explicit
    /// CAST/CONCAT would. Either side already MAX makes the result MAX (also oracle-verified:
    /// <c>varchar(max) + varchar(10)</c> resolves <c>varchar(max)</c>), same rule
    /// <see cref="CombineSameCategoryStrings"/> already uses. A mismatched string CATEGORY
    /// (char + varchar) falls back to the general precedence <see cref="Combine"/> rule instead -
    /// this method's own sum rule was verified for same-category concatenation only.
    /// </summary>
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

    /// <summary>
    /// Merges every branch by precedence, EXCEPT a bare <c>NULL</c> literal branch, which is
    /// skipped entirely rather than resolved and folded in - oracle-verified: <c>CASE WHEN 1=1
    /// THEN NULL ELSE IntCol END</c> resolves INT against the real server, not Unknown. An
    /// untyped NULL has no type of its own to contribute to the merge (unlike a branch this pass
    /// merely couldn't type, e.g. an unresolvable column reference, which must still null the
    /// whole result - CombineAll's existing "one unresolvable branch nulls everything" rule is
    /// otherwise unchanged).
    /// </summary>
    private static SqlType? CombineBranches(
        IEnumerable<ScalarExpression> branches, Func<ScalarExpression, SqlType?> resolveLeaf, IReadOnlyDictionary<string, SqlType>? typeAliases) =>
        CombineAll(branches.Where(e => e is not NullLiteral).Select(e => Resolve(e, resolveLeaf, typeAliases)));

    /// <summary>
    /// Folds every branch by precedence. A single unresolvable branch nulls the whole result
    /// (Unknown) rather than guessing from only the branches this pass COULD type - the branch
    /// it couldn't type might be the actual precedence winner.
    /// </summary>
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

    /// <summary>
    /// T-SQL data type precedence for a binary operator's/CASE-family result: the LOWER-
    /// precedence operand converts to the higher one's category (the same direction <see
    /// cref="SqlTypeCategory"/>'s ordinal already encodes). Same category with differing,
    /// both-resolved string collations is left null (Unknown) rather than guessed - the
    /// identical coercibility gap <see cref="Rules.VerdictClassifier.ClassifySameCategory"/> already
    /// declines to resolve.
    /// </summary>
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

        // The merged result's true length is genuinely unknown here (SQL Server widens precision
        // further than either branch's own declared facet, per this class's own remarks) -
        // LengthKnown: false so a caller like ParameterLengthClassifier never reads this null
        // Length as "declared with no explicit length," a fabricated cause for a length this
        // pass never actually inferred.
        var winner = left.Category > right.Category ? left : right;
        return winner.IsStringFamily ? new SqlType(winner.Category, Collation: winner.Collation, LengthKnown: false) : new SqlType(winner.Category);
    }

    /// <summary>
    /// Oracle-verified (sys.dm_exec_describe_first_result_set): a CASE/COALESCE-family result
    /// combining two same-category string branches of DIFFERING length takes the WIDER of the
    /// two (varchar(10) vs varchar(20) -&gt; varchar(20)), MAX whenever either side is MAX
    /// regardless of position - never just one branch's own length picked arbitrarily, which is
    /// what this used to do (a real bug: a view column like DNN Platform's
    /// vw_Profile.PropertyValue - <c>CASE WHEN PropertyText IS NULL THEN PropertyValue ELSE
    /// PropertyText END</c>, mixing nvarchar(3750) with nvarchar(MAX) - inferred the narrower
    /// branch's own length instead of MAX, a genuine lineage-parity mismatch against the real
    /// deployed column).
    /// </summary>
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
