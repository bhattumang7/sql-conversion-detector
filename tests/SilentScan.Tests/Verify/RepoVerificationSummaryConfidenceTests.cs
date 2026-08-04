using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;
using SilentScan.Verify.Commands;
using SilentScan.Verify.Oracle;

namespace SilentScan.Tests.Verify;

public sealed class RepoVerificationSummaryConfidenceTests
{
    private static readonly ColumnProvenance.BaseColumn Provenance =
        new("dbo.T", "Code", new SqlType(SqlTypeCategory.VarChar, Length: 10), Depth: 0);

    private static TypedPredicateFinding TypedFinding(FindingConfidence confidence)
    {
        var column = new PredicateOperand.Column("dbo.T", "Code", new SqlType(SqlTypeCategory.VarChar, Length: 10), Indexed: true, Depth: 0, Provenance);
        var other = new PredicateOperand.Value(new SqlType(SqlTypeCategory.NVarChar, Length: 10));
        return new TypedPredicateFinding(Verdict.ScanForced, column, other, "=", "file.sql", 1, 1, Confidence: confidence);
    }

    private static RepoVerificationSummary Summary(List<CorpusFindingResult> confirmed) => new(
        TotalDdlFiles: 0,
        DeploymentErrors: [],
        LineageParityMismatches: [],
        ProbeWorthyFindingCount: confirmed.Count,
        DistinctProbeWorthyFindingCount: confirmed.Count,
        Confirmed: confirmed,
        NotConfirmed: [],
        NotProbeable: [],
        ProbeFailed: [],
        ConfirmedUnindexed: [],
        ConfirmedViaScratchIndex: [],
        CollationConflictConfirmed: [],
        CollationConflictNotConfirmed: [],
        CollationConflictProbeFailed: [],
        Tier1Confirmed: [],
        Tier1NotConfirmed: [],
        Tier1NotProbeable: [],
        Tier1ProbeFailed: [],
        Tier1ConfirmedUnindexed: [],
        Tier1ConfirmedViaScratchIndex: [],
        ExpressionDerivedConfirmed: [],
        ExpressionDerivedNotConfirmed: [],
        ExpressionDerivedNotProbeable: [],
        ExpressionDerivedProbeFailed: [],
        ExpressionDerivedConfirmedUnindexed: [],
        DynamicSql: new DynamicSqlSummary(0, 0, 0, 0, new Dictionary<string, int>()),
        PassesDialectSniffing: true,
        ParseSuccessRate: 1.0);

    [Fact]
    public void ConfirmedByConfidence_MixedConfidenceConfirmations_SegregatesRatherThanSums()
    {
        var confirmed = new List<CorpusFindingResult>
        {
            new(TypedFinding(FindingConfidence.High), CorpusFindingOutcome.Confirmed, Detail: null),
            new(TypedFinding(FindingConfidence.Medium), CorpusFindingOutcome.Confirmed, Detail: null),
        };
        var summary = Summary(confirmed);

        // The raw list still legitimately reports 2 - that's not what must never appear. What
        // must never appear is a *combined confidence* total standing in for the two distinct
        // claims a High and a Medium confirmation actually make.
        Assert.Equal(2, summary.Confirmed.Count);
        Assert.Equal(new ConfidenceTally(High: 1, Medium: 1, Low: 0), summary.ConfirmedByConfidence);
        Assert.Equal(1, summary.ConfirmedByConfidence.High);
        Assert.Equal(1, summary.ConfirmedByConfidence.Medium);
    }

    [Fact]
    public void ConfirmedByConfidence_AllHigh_MediumAndLowAreZero()
    {
        var confirmed = new List<CorpusFindingResult>
        {
            new(TypedFinding(FindingConfidence.High), CorpusFindingOutcome.Confirmed, Detail: null),
            new(TypedFinding(FindingConfidence.High), CorpusFindingOutcome.Confirmed, Detail: null),
        };
        var tally = Summary(confirmed).ConfirmedByConfidence;

        Assert.Equal(2, tally.High);
        Assert.Equal(0, tally.Medium);
        Assert.Equal(0, tally.Low);
        Assert.Equal(2, tally.Total);
    }

    [Fact]
    public void ConfirmedByConfidence_EmptyBucket_IsZeroEverywhere()
    {
        var tally = Summary([]).ConfirmedByConfidence;

        Assert.Equal(default, tally);
        Assert.Equal(0, tally.Total);
    }
}
