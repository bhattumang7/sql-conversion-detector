using SilentScan.Core.Catalog;
using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Reporting;

public sealed class TypedPredicateSummaryTests
{
    private static TypedPredicateFinding Finding(Verdict verdict, string table = "dbo.T", string column = "Col", int line = 1) => new(
        verdict,
        new PredicateOperand.Column(table, column, new SqlType(SqlTypeCategory.VarChar), Indexed: true, Depth: 0, Provenance: null!),
        new PredicateOperand.Value(new SqlType(SqlTypeCategory.NVarChar)),
        "=",
        "test.sql",
        line,
        1);

    [Fact]
    public void From_EmptyFindings_ReturnsZeroedSummary()
    {
        var summary = TypedPredicateSummary.From([]);

        Assert.Equal(0, summary.TotalClassified);
        Assert.Equal(0, summary.SeekPreservedCount);
        Assert.Equal(0, summary.RangeSeekCount);
        Assert.Equal(0, summary.ScanForcedCount);
        Assert.Equal(0, summary.UnknownCount);
        Assert.Equal(0, summary.OperandClashCount);
        Assert.Equal(0, summary.DistinctRangeSeekCount);
        Assert.Equal(0, summary.DistinctScanForcedCount);
        Assert.Equal(0, summary.DistinctTotalClassified);
    }

    [Fact]
    public void From_MixedVerdicts_CountsEachBucketIncludingSeekPreserved()
    {
        var findings = new[]
        {
            Finding(Verdict.SeekPreserved),
            Finding(Verdict.SeekPreserved),
            Finding(Verdict.SeekPreserved),
            Finding(Verdict.RangeSeek),
            Finding(Verdict.ScanForced),
            Finding(Verdict.ScanForced),
            Finding(Verdict.Unknown),
            Finding(Verdict.OperandClash),
        };

        var summary = TypedPredicateSummary.From(findings);

        Assert.Equal(8, summary.TotalClassified);
        Assert.Equal(3, summary.SeekPreservedCount);
        Assert.Equal(1, summary.RangeSeekCount);
        Assert.Equal(2, summary.ScanForcedCount);
        Assert.Equal(1, summary.UnknownCount);
        Assert.Equal(1, summary.OperandClashCount);
    }

    [Fact]
    public void From_RepeatedIdenticalScanForcedFindings_DistinctCountCollapsesButRawCountDoesNot()
    {
        var findings = new[]
        {
            Finding(Verdict.ScanForced, "dbo.Documents", "CreatedByUser", line: 10),
            Finding(Verdict.ScanForced, "dbo.Documents", "CreatedByUser", line: 40),
            Finding(Verdict.ScanForced, "dbo.Documents", "CreatedByUser", line: 90),
            Finding(Verdict.ScanForced, "dbo.Discussion", "CreatedByUser", line: 12),
        };

        var summary = TypedPredicateSummary.From(findings);

        Assert.Equal(4, summary.ScanForcedCount);
        Assert.Equal(2, summary.DistinctScanForcedCount);
    }

    [Fact]
    public void From_RepeatedFindingsAcrossVerdicts_DistinctTotalClassifiedUsesSameBasisAsDistinctScanForced()
    {
        var findings = new[]
        {
            Finding(Verdict.ScanForced, "dbo.Documents", "CreatedByUser", line: 10),
            Finding(Verdict.ScanForced, "dbo.Documents", "CreatedByUser", line: 40),
            Finding(Verdict.SeekPreserved, "dbo.Orders", "OrderId", line: 1),
            Finding(Verdict.SeekPreserved, "dbo.Orders", "OrderId", line: 2),
            Finding(Verdict.Unknown, "dbo.Users", "Email", line: 3),
        };

        var summary = TypedPredicateSummary.From(findings);

        Assert.Equal(5, summary.TotalClassified);
        Assert.Equal(3, summary.DistinctTotalClassified);
    }
}
