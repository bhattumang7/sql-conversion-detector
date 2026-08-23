using SilentScan.Core.Catalog;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Rules;

public sealed record TypePairOutcome(
    SqlTypeCategory ColumnCategory,
    SqlTypeCategory OtherCategory,
    string? CollationName,
    bool ColumnConverts,
    bool CompileFailed,
    bool DynamicRangeSeekAvailable);
