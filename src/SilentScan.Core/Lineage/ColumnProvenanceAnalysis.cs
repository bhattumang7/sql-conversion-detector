using SilentScan.Core.Catalog;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Lineage;

/// <summary>
/// Walks a <see cref="ColumnProvenance"/> chain to answer "is this actually a real column, or
/// a computed expression wearing a column's name?" A predicate can never seek through a Cast
/// or Expression layer regardless of what it's compared against - that's a different failure
/// mode from the type-precedence mismatches <see cref="Rules.VerdictClassifier"/> handles, and
/// it can be introduced arbitrarily many view/TVF layers upstream of the predicate that
/// actually references the column.
/// </summary>
public static class ColumnProvenanceAnalysis
{
    /// <summary>
    /// True if the value at this point is a computed expression (CAST/CONVERT or any other
    /// scalar expression), not a passthrough of a real column. A UNION branch counts if ANY
    /// branch is expression-derived - CLAUDE.md's "mixed-branch case is itself a finding," and
    /// a predicate against the merged column can't seek through the expression-derived branch
    /// regardless of what the other branches look like.
    /// </summary>
    public static bool IsExpressionDerived(ColumnProvenance provenance) => provenance switch
    {
        ColumnProvenance.Cast or ColumnProvenance.Expression => true,
        ColumnProvenance.Union union => union.Branches.Any(IsExpressionDerived),
        _ => false,
    };

    /// <summary>
    /// The single scalar type at this point in a provenance chain, if one is knowable - shared
    /// by Pass 3/4's own operand typing (<see cref="Predicates.TypedPredicateExtractor"/>) and
    /// the Verify oracle's environment-parity gate (<c>LineageParityChecker</c>), which both
    /// need the identical "what type does this column resolve to" answer. Never guesses: a
    /// Union only yields a type when every branch agrees, and an Expression only when Pass 2
    /// already inferred one - anything else (Declared with no type, a disagreeing Union, plain
    /// Unknown) returns null rather than a guess.
    /// </summary>
    public static SqlType? TryGetScalarType(ColumnProvenance provenance) => provenance switch
    {
        ColumnProvenance.BaseColumn baseColumn => baseColumn.Type,
        ColumnProvenance.Declared declared => declared.Type,
        ColumnProvenance.Cast cast => cast.ExplicitType,
        ColumnProvenance.Expression expression => expression.InferredType,
        ColumnProvenance.Union union => AllBranchesAgree(union.Branches, out var agreedType) ? agreedType : null,
        // The compiler's pattern-exhaustiveness check does not treat this sealed-subtype set as
        // closed even with every concrete case listed - ColumnProvenanceSubtypeCoverageTests is
        // the real forcing function: it reflects over every nested ColumnProvenance subtype and
        // fails if one appears here uncovered.
        _ => null,
    };

    /// <summary>
    /// SQL Server's own decimal max precision (documented, and the ceiling
    /// <see cref="WidenDecimalFacets"/>'s own formula must never exceed).
    /// </summary>
    private const int MaxDecimalPrecision = 38;

    private static bool AllBranchesAgree(IReadOnlyList<ColumnProvenance> branches, out SqlType? agreedType)
    {
        agreedType = null;
        foreach (var branch in branches)
        {
            var branchType = TryGetScalarType(branch);
            if (branchType is null)
            {
                return false;
            }

            if (agreedType is null)
            {
                agreedType = branchType;
            }
            else if (agreedType.Category != branchType.Category)
            {
                return false;
            }
            else if (agreedType.IsStringFamily
                && !string.Equals(agreedType.Collation?.Name, branchType.Collation?.Name, StringComparison.OrdinalIgnoreCase))
            {
                // Categories agree but the branches' collations genuinely differ: real SQL
                // Server resolves collation-mixed unions by coercibility precedence rules (or
                // raises a conflict error) this pass does not implement - silently keeping the
                // first branch's collation would be a guess about exactly the fact (SQL_* vs
                // Windows) that decides ScanForced vs RangeSeek. Null the collation so the
                // verdict engine sees collation-unknown, not a wrong one.
                agreedType = agreedType with { Collation = null, Length = null, LengthKnown = false };
            }
            else if (agreedType.IsStringFamily)
            {
                agreedType = WidenStringFacets(agreedType, branchType);
            }
            else if (agreedType.Category == SqlTypeCategory.Decimal)
            {
                agreedType = WidenDecimalFacets(agreedType, branchType);
            }
        }

        return agreedType is not null;
    }

    /// <summary>
    /// A UNION of same-category, same-collation-status string branches of differing length takes
    /// the WIDER of the two (oracle-verified: <c>sys.dm_exec_describe_first_result_set</c> off a
    /// real deployed <c>varchar(10) UNION ALL varchar(200)</c> view reports <c>max_length 200</c>) -
    /// the identical rule <see cref="TypeInference.ExpressionTypeInferencer"/>'s own CASE/COALESCE merge
    /// already uses (<c>Math.Max</c>), previously applied only there; a union column silently kept
    /// whichever branch happened to be first instead. MAX-ness widens the same way a CASE/COALESCE
    /// merge's own MAX handling already does.
    /// </summary>
    private static SqlType WidenStringFacets(SqlType agreedType, SqlType branchType)
    {
        if (agreedType.IsMax || branchType.IsMax)
        {
            return agreedType with { Length = null, IsMax = true };
        }

        if (agreedType.Length is not { } l || branchType.Length is not { } r)
        {
            return agreedType with { Length = null, LengthKnown = false };
        }

        return agreedType with { Length = Math.Max(l, r) };
    }

    /// <summary>
    /// A UNION of two DECIMAL branches widens scale to the wider of the two, and precision to
    /// the wider INTEGER-digit count (precision minus scale) plus that widened scale - oracle-
    /// verified directly (<c>sys.dm_exec_describe_first_result_set</c>): <c>DECIMAL(5,3) UNION ALL
    /// DECIMAL(10,1)</c> resolves <c>DECIMAL(12,3)</c> (scale = MAX(3,1) = 3; integer digits =
    /// MAX(5-3, 10-1) = MAX(2,9) = 9; precision = 9+3 = 12) - NOT simply <c>Math.Max</c> of each
    /// facet independently (that would give the wrong DECIMAL(10,3)), and not the "keep the
    /// winning branch's own declared facets" approximation
    /// <see cref="TypeInference.ExpressionTypeInferencer"/>'s own remarks document as accepted there for
    /// CASE/COALESCE - this is a real widening, not an approximation, because a union's own
    /// column-parity check (<c>LiveLineageParityChecker</c>) compares directly against the live
    /// engine's own answer for this exact shape.
    /// </summary>
    private static SqlType WidenDecimalFacets(SqlType agreedType, SqlType branchType)
    {
        if (agreedType.Precision is not { } p1 || agreedType.Scale is not { } s1
            || branchType.Precision is not { } p2 || branchType.Scale is not { } s2)
        {
            return agreedType with { Precision = null, Scale = null };
        }

        var scale = Math.Max(s1, s2);
        var integerDigits = Math.Max(p1 - s1, p2 - s2);
        var precision = Math.Min(integerDigits + scale, MaxDecimalPrecision);
        return agreedType with { Precision = precision, Scale = scale };
    }

    /// <summary>Every base table column reachable underneath this provenance (0, 1, or many - an expression, or a UNION of several branches, can combine several columns).</summary>
    public static IReadOnlyList<ColumnProvenance.BaseColumn> FindUnderlyingBaseColumns(ColumnProvenance provenance) => provenance switch
    {
        ColumnProvenance.BaseColumn baseColumn => [baseColumn],
        ColumnProvenance.Cast cast => FindUnderlyingBaseColumns(cast.Inner),
        ColumnProvenance.Expression expression => [.. expression.Inputs.SelectMany(FindUnderlyingBaseColumns)],
        ColumnProvenance.Union union => [.. union.Branches.SelectMany(FindUnderlyingBaseColumns)],
        _ => [],
    };

    /// <summary>
    /// Every layer (outermost first) that introduced a CAST/CONVERT or other expression
    /// between the predicate and the base column(s) - CLAUDE.md's "origin: file/line of the
    /// layer that introduced the mismatch," extended across the whole chain rather than just
    /// the nearest one, since the user-facing question is "which view changed it, and where."
    /// </summary>
    public static IReadOnlyList<TransformationSite> DescribeTransformationChain(ColumnProvenance provenance)
    {
        var sites = new List<TransformationSite>();
        Walk(provenance, sites);
        return sites;
    }

    private static void Walk(ColumnProvenance provenance, List<TransformationSite> sites)
    {
        switch (provenance)
        {
            case ColumnProvenance.Cast cast:
                sites.Add(new TransformationSite(cast.OriginSourcePath, cast.OriginLine, $"CAST/CONVERT to {cast.ExplicitType}"));
                Walk(cast.Inner, sites);
                break;

            case ColumnProvenance.Expression expression:
                sites.Add(new TransformationSite(expression.OriginSourcePath, expression.OriginLine, "expression"));
                foreach (var input in expression.Inputs)
                {
                    Walk(input, sites);
                }

                break;

            case ColumnProvenance.Union union:
                // The Union node itself isn't a transformation site - each branch's own
                // Cast/Expression sites (if any) already carry their own file/line, so a
                // reader can tell which UNION branch introduced the mismatch without this
                // adding a synthetic "UNION" entry with no origin of its own.
                foreach (var branch in union.Branches)
                {
                    Walk(branch, sites);
                }

                break;
        }
    }
}

/// <summary>One layer of a transformation chain: where it was introduced and what kind of transformation it was.</summary>
public sealed record TransformationSite(string? SourcePath, int Line, string Description);
