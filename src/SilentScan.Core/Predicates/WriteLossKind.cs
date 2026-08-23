namespace SilentScan.Core.Predicates;

public enum WriteLossKind
{
    UnicodeToNonUnicodeReplacement,

    ApproximateToExactTruncation,

    NumericScaleNarrowing,

    TemporalPrecisionLoss,
}
