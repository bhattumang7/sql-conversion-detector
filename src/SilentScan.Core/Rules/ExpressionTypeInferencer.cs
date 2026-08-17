using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Rules;

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
/// for this tool's purposes: <see cref="VerdictClassifier"/> never consults precision/length in
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

        BinaryExpression binary => Combine(
            Resolve(binary.FirstExpression, resolveLeaf, typeAliases),
            Resolve(binary.SecondExpression, resolveLeaf, typeAliases)),

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
    /// identical coercibility gap <see cref="VerdictClassifier.ClassifySameCategory"/> already
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

        var winner = left.Category > right.Category ? left : right;
        return winner.IsStringFamily ? new SqlType(winner.Category, Collation: winner.Collation) : new SqlType(winner.Category);
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
