using SilentScan.Verify.Oracle;

namespace SilentScan.Tests.Oracle;

public sealed class IndexAccessDetectorTests
{
    private const string ShowPlanNs = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    private static string Wrap(string relOpXml) => $"""
        <ShowPlanXML xmlns="{ShowPlanNs}">
          <BatchSequence><Batch><Statements><StmtSimple>
            <QueryPlan>{relOpXml}</QueryPlan>
          </StmtSimple></Statements></Batch></BatchSequence>
        </ShowPlanXML>
        """;

    [Fact]
    public void HasIndexSeek_RealSeekPlanFragment_ReturnsTrue()
    {
        var xml = Wrap("""
            <RelOp PhysicalOp="Index Seek" LogicalOp="Index Seek">
              <IndexScan>
                <Object Database="[IdxProbe]" Schema="[dbo]" Table="[T]" Index="[IX_T_Col]" />
              </IndexScan>
            </RelOp>
            """);

        Assert.True(IndexAccessDetector.HasIndexSeek(xml, "IX_T_Col"));
    }

    [Fact]
    public void HasIndexSeek_RealScanPlanFragmentAgainstSameIndex_ReturnsFalse()
    {
        var xml = Wrap("""
            <RelOp PhysicalOp="Index Scan" LogicalOp="Index Scan">
              <IndexScan Ordered="0">
                <Object Database="[IdxProbe]" Schema="[dbo]" Table="[T]" Index="[IX_T_Col]" />
              </IndexScan>
            </RelOp>
            """);

        Assert.False(IndexAccessDetector.HasIndexSeek(xml, "IX_T_Col"));
    }

    [Fact]
    public void HasIndexSeek_SeekOnADifferentIndex_ReturnsFalseForTheOneAsked()
    {
        var xml = Wrap("""
            <RelOp PhysicalOp="Index Seek" LogicalOp="Index Seek">
              <IndexScan>
                <Object Table="[T]" Index="[IX_T_Other]" />
              </IndexScan>
            </RelOp>
            """);

        Assert.False(IndexAccessDetector.HasIndexSeek(xml, "IX_T_Col"));
    }

    [Fact]
    public void HasIndexSeek_NoRelOpAtAll_ReturnsFalse()
    {
        var xml = Wrap(string.Empty);

        Assert.False(IndexAccessDetector.HasIndexSeek(xml, "IX_T_Col"));
    }
}
