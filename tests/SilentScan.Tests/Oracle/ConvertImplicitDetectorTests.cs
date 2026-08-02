using SilentScan.Verify.Oracle;

namespace SilentScan.Tests.Oracle;

public sealed class ConvertImplicitDetectorTests
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
    public void FindColumnConversions_ConvertOverRealColumn_IsReported()
    {
        // Real fragment captured from the Phase 0 spike oracle (varchar column vs nvarchar param).
        var xml = Wrap("""
            <ScalarOperator><Compare CompareOp="EQ"><ScalarOperator>
              <Convert DataType="nvarchar" Length="40" Style="0" Implicit="1">
                <ScalarOperator><Identifier><ColumnReference Database="[SilentScanSpike]" Schema="[dbo]" Table="[Orders]" Column="OrderCode" /></Identifier></ScalarOperator>
              </Convert>
            </ScalarOperator></Compare></ScalarOperator>
            """);

        var findings = ConvertImplicitDetector.FindColumnConversions(xml);

        var finding = Assert.Single(findings);
        Assert.Equal("Orders", finding.Table);
        Assert.Equal("OrderCode", finding.Column);
        Assert.Equal("SilentScanSpike", finding.Database);
        Assert.Equal("dbo", finding.Schema);
    }

    [Fact]
    public void FindColumnConversions_ImplicitAttributeAsLexicalTrue_IsReported()
    {
        // The showplan XSD types Implicit as xsd:boolean, which permits both "1"/"0" and
        // "true"/"false" lexical forms - not every SQL Server version/serialization path is
        // guaranteed to emit "1" specifically.
        var xml = Wrap("""
            <ScalarOperator><Compare CompareOp="EQ"><ScalarOperator>
              <Convert DataType="nvarchar" Length="40" Style="0" Implicit="true">
                <ScalarOperator><Identifier><ColumnReference Database="[SilentScanSpike]" Schema="[dbo]" Table="[Orders]" Column="OrderCode" /></Identifier></ScalarOperator>
              </Convert>
            </ScalarOperator></Compare></ScalarOperator>
            """);

        var findings = ConvertImplicitDetector.FindColumnConversions(xml);

        var finding = Assert.Single(findings);
        Assert.Equal("Orders", finding.Table);
    }

    [Fact]
    public void FindColumnConversions_ConvertOverParameterReference_IsNotReported()
    {
        // Real fragment captured investigating a Phase 4 corpus pilot false positive:
        // Showplan XML represents a local variable/parameter as a <ColumnReference> too, with
        // no Table attribute (Column="@p"). Must not be reported as a column-side conversion.
        var xml = Wrap("""
            <ScalarOperator><Compare CompareOp="EQ"><ScalarOperator>
              <Convert DataType="bit" Style="0" Implicit="1">
                <ScalarOperator><Identifier><ColumnReference Column="@p" /></Identifier></ScalarOperator>
              </Convert>
            </ScalarOperator></Compare></ScalarOperator>
            """);

        var findings = ConvertImplicitDetector.FindColumnConversions(xml);

        Assert.Empty(findings);
    }

    [Fact]
    public void FindColumnConversions_ExplicitConvert_IsNotReported()
    {
        var xml = Wrap("""
            <ScalarOperator><Compare CompareOp="EQ"><ScalarOperator>
              <Convert DataType="varchar" Length="10" Style="0" Implicit="0">
                <ScalarOperator><Identifier><ColumnReference Database="[T]" Schema="[dbo]" Table="[T]" Column="Id" /></Identifier></ScalarOperator>
              </Convert>
            </ScalarOperator></Compare></ScalarOperator>
            """);

        var findings = ConvertImplicitDetector.FindColumnConversions(xml);

        Assert.Empty(findings);
    }

    [Fact]
    public void FindColumnConversions_NoConvertAtAll_ReturnsEmpty()
    {
        var xml = Wrap("""<ScalarOperator><Compare CompareOp="EQ"><ScalarOperator><Identifier><ColumnReference Database="[T]" Schema="[dbo]" Table="[T]" Column="Id" /></Identifier></ScalarOperator></Compare></ScalarOperator>""");

        var findings = ConvertImplicitDetector.FindColumnConversions(xml);

        Assert.Empty(findings);
    }
}
