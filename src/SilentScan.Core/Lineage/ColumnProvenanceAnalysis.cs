using SilentScan.Core.Catalog;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Lineage;

public static class ColumnProvenanceAnalysis
{
    public static bool IsExpressionDerived(ColumnProvenance provenance) => provenance switch
    {
        ColumnProvenance.Cast or ColumnProvenance.Expression => true,
        ColumnProvenance.Union union => union.Branches.Any(IsExpressionDerived),
        _ => false,
    };

    public static SqlType? TryGetScalarType(ColumnProvenance provenance) => provenance switch
    {
        ColumnProvenance.BaseColumn baseColumn => baseColumn.Type,
        ColumnProvenance.Declared declared => declared.Type,
        ColumnProvenance.Cast cast => cast.ExplicitType,
        ColumnProvenance.Expression expression => expression.InferredType,
        ColumnProvenance.Union union => AllBranchesAgree(union.Branches, out var agreedType) ? agreedType : null,

        _ => null,
    };

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

    public static IReadOnlyList<ColumnProvenance.BaseColumn> FindUnderlyingBaseColumns(ColumnProvenance provenance) => provenance switch
    {
        ColumnProvenance.BaseColumn baseColumn => [baseColumn],
        ColumnProvenance.Cast cast => FindUnderlyingBaseColumns(cast.Inner),
        ColumnProvenance.Expression expression => [.. expression.Inputs.SelectMany(FindUnderlyingBaseColumns)],
        ColumnProvenance.Union union => [.. union.Branches.SelectMany(FindUnderlyingBaseColumns)],
        _ => [],
    };

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

                foreach (var branch in union.Branches)
                {
                    Walk(branch, sites);
                }

                break;
        }
    }
}

public sealed record TransformationSite(string? SourcePath, int Line, string Description);
