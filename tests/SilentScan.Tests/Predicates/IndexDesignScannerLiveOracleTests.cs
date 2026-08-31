using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class IndexDesignScannerLiveOracleTests
{
    [Fact]
    public async Task LiveDeployment_ClusteredCompositeVarcharSumOverLimit_Fires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE TABLE dbo.CompositeKeyOverLimit (
                CodeA VARCHAR(500) NOT NULL,
                CodeB VARCHAR(500) NOT NULL,
                CONSTRAINT PK_CompositeKeyOverLimit PRIMARY KEY CLUSTERED (CodeA, CodeB)
            );
            """,
            minimumConfidence: FindingConfidence.Low);

        var finding = Assert.Single(
            report.Find<IndexDesignFinding>("IndexDesignScanner"),
            f => f.Kind == IndexDesignFindingKind.VariableLengthKeyColumnExceedsKeyLimit);
        Assert.Equal("dbo.CompositeKeyOverLimit", finding.TableQualifiedName);
    }

    [Fact]
    public async Task LiveDeployment_ClusteredCompositeVarcharSumUnderLimit_NeverFires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE TABLE dbo.CompositeKeyUnderLimit (
                CodeA VARCHAR(400) NOT NULL,
                CodeB VARCHAR(400) NOT NULL,
                CONSTRAINT PK_CompositeKeyUnderLimit PRIMARY KEY CLUSTERED (CodeA, CodeB)
            );
            """,
            minimumConfidence: FindingConfidence.Low);

        Assert.DoesNotContain(
            report.Find<IndexDesignFinding>("IndexDesignScanner"),
            f => f.Kind == IndexDesignFindingKind.VariableLengthKeyColumnExceedsKeyLimit);
    }
}
