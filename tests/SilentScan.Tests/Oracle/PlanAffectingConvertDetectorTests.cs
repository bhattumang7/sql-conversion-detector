using SilentScan.Verify.Oracle;

namespace SilentScan.Tests.Oracle;

public sealed class PlanAffectingConvertDetectorTests
{
    private const string ShowPlanNs = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    private static string Wrap(string queryPlanBody) => $"""
        <ShowPlanXML xmlns="{ShowPlanNs}">
          <BatchSequence><Batch><Statements><StmtSimple>
            <QueryPlan>{queryPlanBody}</QueryPlan>
          </StmtSimple></Statements></Batch></BatchSequence>
        </ShowPlanXML>
        """;

    [Fact]
    public void FindWarnings_SingleCardinalityEstimateWarning_IsReported()
    {
        var xml = Wrap("""
            <Warnings><PlanAffectingConvert ConvertIssue="Cardinality Estimate" Expression="CONVERT_IMPLICIT(int,[db].[dbo].[T].[C],0)"></PlanAffectingConvert></Warnings>
            <RelOp></RelOp>
            """);

        var warnings = PlanAffectingConvertDetector.FindWarnings(xml);

        var warning = Assert.Single(warnings);
        Assert.Equal("Cardinality Estimate", warning.ConvertIssue);
        Assert.Equal("CONVERT_IMPLICIT(int,[db].[dbo].[T].[C],0)", warning.Expression);
    }

    [Fact]
    public void FindWarnings_CardinalityEstimateAndSeekPlanBothPresent_ReportsBoth()
    {
        var xml = Wrap("""
            <Warnings>
              <PlanAffectingConvert ConvertIssue="Cardinality Estimate" Expression="CONVERT_IMPLICIT(int,[db].[dbo].[T].[C],0)"></PlanAffectingConvert>
              <PlanAffectingConvert ConvertIssue="Seek Plan" Expression="CONVERT_IMPLICIT(int,[db].[dbo].[T].[C],0)=CONVERT_IMPLICIT(int,[@1],0)"></PlanAffectingConvert>
            </Warnings>
            <RelOp></RelOp>
            """);

        var warnings = PlanAffectingConvertDetector.FindWarnings(xml);

        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.ConvertIssue == "Cardinality Estimate");
        Assert.Contains(warnings, w => w.ConvertIssue == "Seek Plan");
    }

    [Fact]
    public void FindWarnings_NoWarningsElementAtAll_ReturnsEmpty()
    {
        var xml = Wrap("<RelOp></RelOp>");

        var warnings = PlanAffectingConvertDetector.FindWarnings(xml);

        Assert.Empty(warnings);
    }

    [Fact]
    public void FindWarnings_WarningsElementPresentButNotPlanAffectingConvert_ReturnsEmpty()
    {
        var xml = Wrap("""
            <Warnings><ColumnsWithNoStatistics><ColumnReference Column="X"></ColumnReference></ColumnsWithNoStatistics></Warnings>
            <RelOp></RelOp>
            """);

        var warnings = PlanAffectingConvertDetector.FindWarnings(xml);

        Assert.Empty(warnings);
    }

    [Fact]
    public void FindWarnings_MissingConvertIssueOrExpressionAttribute_FallsBackToEmptyStringNotNull()
    {
        var xml = Wrap("""
            <Warnings><PlanAffectingConvert></PlanAffectingConvert></Warnings>
            <RelOp></RelOp>
            """);

        var warning = Assert.Single(PlanAffectingConvertDetector.FindWarnings(xml));

        Assert.Equal(string.Empty, warning.ConvertIssue);
        Assert.Equal(string.Empty, warning.Expression);
    }
}
