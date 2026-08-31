using SilentScan.Core.Catalog;
using SilentScan.Core.Rules;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

public static class AlterColumnSafetyScanner
{
    public static IReadOnlyList<AlterColumnSafetyFinding> Scan(DatabaseCatalog catalog)
    {
        var findings = new List<AlterColumnSafetyFinding>();

        foreach (var alter in catalog.AlterColumnEvents)
        {
            if (alter.PreviousType is not { } previous || alter.NewType is not { } next)
            {
                continue;
            }

            var kind = Classify(previous, next);
            if (kind is null)
            {
                continue;
            }

            findings.Add(new AlterColumnSafetyFinding(
                alter.TableQualifiedName, alter.ColumnName, kind.Value, previous, next, alter.SourcePath, alter.SourceLine));
        }

        return
        [
            .. findings
                .OrderBy(f => f.TableQualifiedName, StringComparer.Ordinal)
                .ThenBy(f => f.ColumnName, StringComparer.Ordinal),
        ];
    }

    private static AlterColumnSafetyKind? Classify(SqlType previous, SqlType next)
    {
        if (IsIncompatibleFamilyConversion(previous, next))
        {
            return AlterColumnSafetyKind.IncompatibleFamilyConversion;
        }

        if (IsPrecisionOrScaleNarrowing(previous, next))
        {
            return AlterColumnSafetyKind.PrecisionOrScaleNarrowing;
        }

        if (WriteLossClassifier.IsTemporalOffsetDroppedRisk(next, previous))
        {
            return AlterColumnSafetyKind.TemporalOffsetDropped;
        }

        return null;
    }

    private static bool IsCharFamily(SqlTypeCategory category) => category is
        SqlTypeCategory.Char or SqlTypeCategory.VarChar or SqlTypeCategory.NChar or SqlTypeCategory.NVarChar;

    private static bool IsIncompatibleFamilyConversion(SqlType previous, SqlType next) =>
        IsCharFamily(previous.Category) && next.IsBinaryFamily;

    private static bool IsPrecisionOrScaleNarrowing(SqlType previous, SqlType next)
    {
        if (NumericFamilyNarrowing.IsDecimalPrecisionNarrowed(next, previous))
        {
            return true;
        }

        if (NumericFamilyNarrowing.IsDecimalIntegerDigitCapacityNarrowed(next, previous))
        {
            return true;
        }

        if (WriteLossClassifier.IsTemporalScaleNarrowingRisk(next, previous))
        {
            return true;
        }

        return NumericFamilyNarrowing.Classify(next, previous) is not null;
    }
}
