using SilentScan.Core.Rules;
using SilentScan.Verify;
using SilentScan.Verify.Oracle;

namespace SilentScan.Tests.Integration;

/// <summary>
/// Closes the last link in VerdictClassifier's authority chain back to the real engine:
/// VerdictClassifier is a pure lookup over the checked-in <c>TypePairMatrix.json</c>
/// (<see cref="Rules.VerdictClassifierTests.Classify_NeverDisagreesWithItsOwnOracleProbedMatrix"/>
/// already pins that), and that JSON was itself oracle-probed - but only once, at generation
/// time, by <c>silentscan-verify generate-type-matrix</c>. Nothing previously re-checked that
/// the checked-in file still matches what THIS Docker SQL Server actually does today, so a
/// stale matrix (a new server image, a compat-level or CE change, or a hand-edit) could drift
/// silently and every downstream test would keep agreeing with itself forever. This regenerates
/// the FULL matrix - the exact same category/collation lists <c>GenerateTypeMatrixCommand</c>
/// uses - against the live oracle and diffs it cell-by-cell against the checked-in file, so a
/// real disagreement fails loudly here instead of shipping a wrong verdict.
/// Slow by nature (deploys and drops several disposable databases across ~200 probed cells);
/// this is the one test in the suite whose whole job is to be exhaustive rather than fast.
/// </summary>
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
