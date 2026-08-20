using SilentScan.Core.Catalog;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md "DBA-script family sweep (2026-08-17)" §A "Identity/sequence range
/// exhaustion" - see <see cref="IdentityRangeFinding"/> for the full scope/precision story.
/// Catalog-only, no AST walk - both kinds are computed purely from <see cref="CatalogColumn"/>'s
/// own identity fields (all four populated in the same live catalog read every other column fact
/// comes from), live-mode only by construction since those fields default to <see langword="null"/>
/// in file mode.
/// </summary>
public static class IdentityRangeScanner
{
    /// <summary>
    /// <see cref="IdentityRangeFindingKind.IdentityRangeNearExhaustion"/> fires once the identity
    /// has consumed at least this fraction of its own type's representable range in the direction
    /// it is incrementing - a conservative, round number (90%) rather than a value calibrated
    /// against this project's own local test database, since (per this kind's own doc comment
    /// and the checklist's data-state-decidability framing) a development database's identity
    /// values are not representative of a production one and calibrating against them would be
    /// calibrating against the wrong population entirely.
    /// </summary>
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

    /// <summary>
    /// SCHEMA-decidable (docs/detection-checklist.md's design-time-decidability split) - a negative
    /// seed or a non-1 increment is a real, deliberate choice at least as often as an oversight, so
    /// this is reported informationally at <see cref="FindingConfidence.Low"/>, never as a defect.
    /// </summary>
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

    /// <summary>
    /// DATA-STATE-decidable, only meaningful against a production-shaped target (docs/detection-
    /// checklist.md's own explicit instruction) - only fires when genuinely close to the type's own
    /// ceiling; there is no corresponding "identity range OK" finding for a healthy column, since
    /// the absence of a finding on a low-value development database proves nothing about
    /// production and must never be read as a passing signal.
    /// </summary>
    private static void ScanNearExhaustion(CatalogTable table, CatalogColumn column, List<IdentityRangeFinding> findings)
    {
        if (column.IdentityCurrentValue is not { } current
            || column.IdentitySeed is not { } seed
            || column.Type is not { } type
            || TypeBound(type, ascending: column.IdentityIncrement is not { } inc || inc >= 0) is not { } bound)
        {
            // Never rows ever inserted (current value unknown), a seed this pass couldn't read, or
            // a column type whose bound this pass cannot compute confidently (anything but tinyint/
            // smallint/int/bigint/decimal(p,0)) - never guess at a ceiling.
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

    /// <summary>
    /// The type's own maximum representable value (<paramref name="ascending"/> true, the ordinary
    /// case) or minimum (a descending, negative-increment identity approaches its type's own lower
    /// bound instead) - <see langword="null"/> for any type this pass cannot bound confidently
    /// (anything but tinyint/smallint/int/bigint/decimal(p,0)), never a guessed number.
    /// </summary>
    private static decimal? TypeBound(SqlType type, bool ascending) => type.Category switch
    {
        SqlTypeCategory.TinyInt => ascending ? 255m : 0m,
        SqlTypeCategory.SmallInt => ascending ? 32_767m : -32_768m,
        SqlTypeCategory.Int => ascending ? 2_147_483_647m : -2_147_483_648m,
        SqlTypeCategory.BigInt => ascending ? 9_223_372_036_854_775_807m : -9_223_372_036_854_775_808m,
        // A decimal/numeric identity column always has scale 0 (the engine rejects a nonzero
        // scale on an IDENTITY column at CREATE TABLE time) - the bound is +/-(10^precision - 1).
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
