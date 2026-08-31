using System.Numerics;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

public static class IdentityRangeScanner
{
    public const decimal NearExhaustionRemainingFraction = 0.9m;

    private static readonly BigInteger ThresholdDenominator = ScaleFactor(NearExhaustionRemainingFraction);
    private static readonly BigInteger ThresholdNumerator = new(NearExhaustionRemainingFraction * (decimal)ScaleFactor(NearExhaustionRemainingFraction));

    private static BigInteger ScaleFactor(decimal value)
    {
        var bits = decimal.GetBits(value);
        var scale = (bits[3] >> 16) & 0xFF;
        return BigInteger.Pow(10, scale);
    }

    public static IReadOnlyList<IdentityRangeFinding> Scan(DatabaseCatalog catalog, IScanStage? stage = null)
    {
        var findings = new List<IdentityRangeFinding>();

        foreach (var table in catalog.Tables)
        {
            stage?.Advance(currentItem: table.QualifiedName);

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

    private static void ScanNearExhaustion(CatalogTable table, CatalogColumn column, List<IdentityRangeFinding> findings)
    {
        if (column.IdentityCurrentValue is not { } current
            || column.IdentitySeed is not { } seed
            || column.Type is not { } type
            || TypeBound(type, ascending: column.IdentityIncrement is not { } inc || inc >= 0) is not { } bound)
        {

            return;
        }

        var seedInteger = new BigInteger(seed);
        var currentInteger = new BigInteger(current);

        var totalRange = BigInteger.Abs(bound - seedInteger);
        if (totalRange.IsZero)
        {
            return;
        }

        var consumed = BigInteger.Abs(currentInteger - seedInteger);
        if (consumed * ThresholdDenominator < totalRange * ThresholdNumerator)
        {
            return;
        }

        var fractionConsumed = (double)consumed / (double)totalRange;

        findings.Add(new IdentityRangeFinding(
            IdentityRangeFindingKind.IdentityRangeNearExhaustion,
            table.QualifiedName,
            column.Name,
            $"'{table.QualifiedName}.{column.Name}' (IDENTITY, current value {current}, type {type}) has consumed {fractionConsumed:P0} of its representable range toward {bound} - once exhausted, every subsequent INSERT raises a hard arithmetic-overflow error (Msg 8115) until the column is widened or reseeded. MEANINGFUL ONLY AGAINST A PRODUCTION-SHAPED TARGET: this check reads the live current value, which on a low-value development database reflects nothing about production's own accumulated inserts.",
            table.SourcePath,
            table.SourceLine));
    }

    private static BigInteger? TypeBound(SqlType type, bool ascending) => type.Category switch
    {
        SqlTypeCategory.TinyInt => ascending ? 255 : 0,
        SqlTypeCategory.SmallInt => ascending ? 32_767 : -32_768,
        SqlTypeCategory.Int => ascending ? 2_147_483_647 : -2_147_483_648,
        SqlTypeCategory.BigInt => ascending ? 9_223_372_036_854_775_807 : -9_223_372_036_854_775_808,

        SqlTypeCategory.Decimal when type is { Precision: >= 1 and <= 38 and { } precision, Scale: 0 } =>
            ascending ? DecimalMax(precision) : -DecimalMax(precision),
        _ => null,
    };

    private static BigInteger DecimalMax(int precision) => BigInteger.Pow(10, precision) - BigInteger.One;
}
