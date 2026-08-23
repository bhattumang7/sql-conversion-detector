using SilentScan.Core.Rules;
using SilentScan.Verify;
using SilentScan.Verify.Oracle;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class TypePairMatrixLiveRegenerationTests
{
    [Fact]
    public async Task LiveMatrix_MatchesCheckedInMatrix_ForEveryProbedCell()
    {
        var generator = new TypeMatrixGenerator(SqlServerOptions.LocalDocker);
        var (liveEntries, _) = await generator.GenerateAsync(
            TypeMatrixGenerator.NumericFamily,
            TypeMatrixGenerator.DateTimeFamily,
            TypeMatrixGenerator.StringFamily,
            TypeMatrixGenerator.Collations,
            TypeMatrixGenerator.CrossFamilyOther,
            TypeMatrixGenerator.BinaryFamily);

        var checkedIn = TypePairMatrix.Instance;
        var mismatches = new List<string>();

        foreach (var live in liveEntries)
        {
            var pinned = checkedIn.TryGetOutcome(live.ColumnCategory, live.OtherCategory, live.CollationName);
            if (pinned is null)
            {
                mismatches.Add($"{live.ColumnCategory} vs {live.OtherCategory} (collation {live.CollationName ?? "n/a"}): live oracle probed this cell but it is MISSING from the checked-in matrix.");
                continue;
            }

            if (pinned.ColumnConverts != live.ColumnConverts
                || pinned.CompileFailed != live.CompileFailed
                || pinned.DynamicRangeSeekAvailable != live.DynamicRangeSeekAvailable)
            {
                mismatches.Add(
                    $"{live.ColumnCategory} vs {live.OtherCategory} (collation {live.CollationName ?? "n/a"}): "
                    + $"checked-in [ColumnConverts={pinned.ColumnConverts}, CompileFailed={pinned.CompileFailed}, DynamicRangeSeekAvailable={pinned.DynamicRangeSeekAvailable}] "
                    + $"vs live [ColumnConverts={live.ColumnConverts}, CompileFailed={live.CompileFailed}, DynamicRangeSeekAvailable={live.DynamicRangeSeekAvailable}].");
            }
        }

        foreach (var pinned in checkedIn.Entries)
        {
            var stillProbed = liveEntries.Any(e =>
                e.ColumnCategory == pinned.ColumnCategory && e.OtherCategory == pinned.OtherCategory && e.CollationName == pinned.CollationName);
            if (!stillProbed)
            {
                mismatches.Add($"{pinned.ColumnCategory} vs {pinned.OtherCategory} (collation {pinned.CollationName ?? "n/a"}): checked-in cell was NOT reproduced by this run's live probe (family/collation list drifted).");
            }
        }

        Assert.True(mismatches.Count == 0, "TypePairMatrix.json has drifted from the live oracle:\n" + string.Join("\n", mismatches));
        Assert.Equal(checkedIn.Entries.Count, liveEntries.Count);
    }
}
