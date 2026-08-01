using SilentScan.Core.Catalog;

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
                agreedType = agreedType with { Collation = null };
            }
        }

        return agreedType is not null;
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
