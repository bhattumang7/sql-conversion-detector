using SilentScan.Verify.Oracle;

namespace SilentScan.Tests.Oracle;

public sealed class BinderParityDetectorTests
{
    private const string ShowPlanNs = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    private static string Wrap(string scalarOperatorXml) => $"""
        <ShowPlanXML xmlns="{ShowPlanNs}">
          <BatchSequence><Batch><Statements><StmtSimple>
            <QueryPlan><RelOp><Filter><Predicate>{scalarOperatorXml}</Predicate></Filter></RelOp></QueryPlan>
          </StmtSimple></Statements></Batch></BatchSequence>
        </ShowPlanXML>
        """;

    [Fact]
    public void FindAllColumnReferences_RealTableColumn_IsReported()
    {
        var xml = Wrap("""
            <ScalarOperator><Compare CompareOp="EQ"><ScalarOperator>
              <Identifier><ColumnReference Database="[SilentScanSpike]" Schema="[dbo]" Table="[Orders]" Column="OrderCode" /></Identifier>
            </ScalarOperator></Compare></ScalarOperator>
            """);

        var found = BinderParityDetector.FindAllColumnReferences(xml);

        var reference = Assert.Single(found);
        Assert.Equal("SilentScanSpike", reference.Database);
        Assert.Equal("dbo", reference.Schema);
        Assert.Equal("Orders", reference.Table);
        Assert.Equal("OrderCode", reference.Column);
    }

    [Fact]
    public void FindAllColumnReferences_LocalVariableOrParameter_IsExcluded()
    {
        var xml = Wrap("""<ScalarOperator><Identifier><ColumnReference Column="@p" /></Identifier></ScalarOperator>""");

        var found = BinderParityDetector.FindAllColumnReferences(xml);

        Assert.Empty(found);
    }

    [Fact]
    public void FindAllColumnReferences_NotJustUnderConvert_UnlikeConvertImplicitDetector()
    {
        var xml = Wrap("""
            <ScalarOperator><Identifier><ColumnReference Database="[T]" Schema="[dbo]" Table="[Orders]" Column="OrderId" /></Identifier></ScalarOperator>
            """);

        var found = BinderParityDetector.FindAllColumnReferences(xml);

        var reference = Assert.Single(found);
        Assert.Equal("OrderId", reference.Column);
        Assert.Empty(ConvertImplicitDetector.FindColumnConversions(xml));
    }

    [Fact]
    public void FindAllColumnReferences_MultipleReferences_ReportsAll()
    {
        var xml = Wrap("""
            <ScalarOperator><Compare CompareOp="EQ">
              <ScalarOperator><Identifier><ColumnReference Database="[T]" Schema="[dbo]" Table="[Orders]" Column="OrderCode" /></Identifier></ScalarOperator>
              <ScalarOperator><Identifier><ColumnReference Database="[T]" Schema="[dbo]" Table="[Orders]" Column="OrderId" /></Identifier></ScalarOperator>
            </Compare></ScalarOperator>
            """);

        var found = BinderParityDetector.FindAllColumnReferences(xml);

        Assert.Equal(2, found.Count);
        Assert.Contains(found, r => r.Column == "OrderCode");
        Assert.Contains(found, r => r.Column == "OrderId");
    }
}
