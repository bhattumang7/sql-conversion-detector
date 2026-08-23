using SilentScan.Core.Catalog;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

public static class IdentityRangeScanner
{
public const decimal NearExhaustionRemainingFraction = 0.9m;

    public static IReadOnlyList<IdentityRangeFinding> Scan(DatabaseCatalog catalog)
    {
        var findings = new List<IdentityRangeFinding>();

        foreach (var table in catalog.Tables)
        {
            if (table.Kind != CatalogTableKind.Table)
            {
                continue;
            }

            foreach (var column in table.Columns)
            {
                if (!column.IsIdentity)
                {
                    continue;
                }

                ScanSeedOrIncrementAnomaly(table, column, findings);
                ScanNearExhaustion(table, column, findings);
            }
        }

        return
        [
            .. findings
                .OrderBy(f => f.TableQualifiedName, StringComparer.Ordinal)
                .ThenBy(f => f.ColumnName, StringComparer.Ordinal)
                .ThenBy(f => f.Kind),
        ];
    }

private static void ScanSeedOrIncrementAnomaly(CatalogTable table, CatalogColumn column, List<IdentityRangeFinding> findings)
    {
        var seed = column.IdentitySeed;
        var increment = column.IdentityIncrement;

        var seedIsNegative = seed is { } s && s < 0;
        var incrementIsNotOne = increment is { } i && i != 1;
        if (!seedIsNegative && !incrementIsNotOne)
        {
            return;
        }

        findings.Add(new IdentityRangeFinding(
            IdentityRangeFindingKind.IdentitySeedOrIncrementAnomaly,
            table.QualifiedName,
            column.Name,
            $"'{table.QualifiedName}.{column.Name}' is IDENTITY(seed={FormatValue(seed)}, increment={FormatValue(increment)}) - a negative seed or a non-1 increment is an unusual data-modeling choice worth a second look (a reversed-numbering scheme, an interleaved-writer scheme, or similar are all legitimate deliberate reasons this could be intentional).",
            table.SourcePath,
            table.SourceLine,
            FindingConfidence.Low));
    }

private static void ScanNearExhaustion(CatalogTable table, CatalogColumn column, List<IdentityRangeFinding> findings)
    {
        if (column.IdentityCurrentValue is not { } current
            || column.IdentitySeed is not { } seed
            || column.Type is not { } type
            || TypeBound(type, ascending: column.IdentityIncrement is not { } inc || inc >= 0) is not { } bound)
        {
            return;
        }

        var totalRange = Math.Abs(bound - seed);
        if (totalRange == 0)
        {
            return;
        }

        var consumed = Math.Abs(current - seed);
        var fractionConsumed = consumed / totalRange;
        if (fractionConsumed < NearExhaustionRemainingFraction)
        {
            return;
        }

        findings.Add(new IdentityRangeFinding(
            IdentityRangeFindingKind.IdentityRangeNearExhaustion,
            table.QualifiedName,
            column.Name,
            $"'{table.QualifiedName}.{column.Name}' (IDENTITY, current value {current}, type {type}) has consumed {fractionConsumed:P0} of its representable range toward {bound} - once exhausted, every subsequent INSERT raises a hard arithmetic-overflow error (Msg 8115) until the column is widened or reseeded. MEANINGFUL ONLY AGAINST A PRODUCTION-SHAPED TARGET: this check reads the live current value, which on a low-value development database reflects nothing about production's own accumulated inserts.",
            table.SourcePath,
            table.SourceLine));
    }

private static decimal? TypeBound(SqlType type, bool ascending) => type.Category switch
    {
        SqlTypeCategory.TinyInt => ascending ? 255m : 0m,
        SqlTypeCategory.SmallInt => ascending ? 32_767m : -32_768m,
        SqlTypeCategory.Int => ascending ? 2_147_483_647m : -2_147_483_648m,
        SqlTypeCategory.BigInt => ascending ? 9_223_372_036_854_775_807m : -9_223_372_036_854_775_808m,
        SqlTypeCategory.Decimal when type is { Precision: { } precision, Scale: 0 } =>
            ascending ? DecimalMax(precision) : -DecimalMax(precision),
        _ => null,
    };

    private static decimal DecimalMax(int precision)
    {
        var max = 1m;
        for (var i = 0; i < precision; i++)
        {
            max *= 10m;
        }

        return max - 1m;
    }

    private static string FormatValue(decimal? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
}
