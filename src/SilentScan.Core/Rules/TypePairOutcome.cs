using SilentScan.Core.Catalog;

namespace SilentScan.Core.Rules;

/// <summary>
/// One oracle-probed cell of <see cref="TypePairMatrix"/>: for a column of
/// <see cref="ColumnCategory"/> compared against a value/parameter of <see cref="OtherCategory"/>
/// (and, for string-family pairs, under <see cref="CollationName"/>), what the real SQL Server
/// optimizer actually did - not what the official precedence list alone would predict. Direction
/// matters: this is the outcome for the column specifically on the <see cref="ColumnCategory"/>
/// side, not a symmetric fact about the pair.
/// </summary>
public sealed record TypePairOutcome(
    SqlTypeCategory ColumnCategory,
    SqlTypeCategory OtherCategory,
    string? CollationName,
    bool ColumnConverts,
    bool CompileFailed,
    bool DynamicRangeSeekAvailable);
