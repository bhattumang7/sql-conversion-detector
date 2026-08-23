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

    [Fact]
    public void FindColumnConversions_TwoConversionsInSamePlanOnlyOneRangeBound_AttributesPerNodeNotPlanWide()
    {
        var xml = $"""
            <ShowPlanXML xmlns="{ShowPlanNs}">
              <BatchSequence><Batch><Statements><StmtSimple>
                <QueryPlan><RelOp PhysicalOp="Concatenation">
                  <RelOp PhysicalOp="Index Seek">
                    <IndexScan>
                      <SeekPredicates>
                        <SeekPredicateNew>
                          <SeekKeys>
                            <Prefix ScanType="EQ">
                              <RangeColumns>
                                <ColumnReference Database="[T]" Schema="[dbo]" Table="[Probe]" Column="WindowsColCol" />
                              </RangeColumns>
                            </Prefix>
                          </SeekKeys>
                        </SeekPredicateNew>
                      </SeekPredicates>
                      <Predicate>
                        <ScalarOperator><Compare CompareOp="EQ"><ScalarOperator>
                          <Convert DataType="nvarchar" Length="50" Style="0" Implicit="1">
                            <ScalarOperator><Identifier><ColumnReference Database="[T]" Schema="[dbo]" Table="[Probe]" Column="WindowsColCol" /></Identifier></ScalarOperator>
                          </Convert>
                        </ScalarOperator></Compare></ScalarOperator>
                      </Predicate>
                    </IndexScan>
                  </RelOp>
                  <RelOp PhysicalOp="Index Scan">
                    <IndexScan>
                      <Predicate>
                        <ScalarOperator><Compare CompareOp="EQ"><ScalarOperator>
                          <Convert DataType="nvarchar" Length="50" Style="0" Implicit="1">
                            <ScalarOperator><Identifier><ColumnReference Database="[T]" Schema="[dbo]" Table="[Probe]" Column="SqlColCol" /></Identifier></ScalarOperator>
                          </Convert>
                        </ScalarOperator></Compare></ScalarOperator>
                      </Predicate>
                    </IndexScan>
                  </RelOp>
                </RelOp></QueryPlan>
              </StmtSimple></Statements></Batch></BatchSequence>
            </ShowPlanXML>
            """;

        var findings = ConvertImplicitDetector.FindColumnConversions(xml);

        Assert.Equal(2, findings.Count);
        Assert.True(Assert.Single(findings, f => f.Column == "WindowsColCol").RangeSeekBound);
        Assert.False(Assert.Single(findings, f => f.Column == "SqlColCol").RangeSeekBound);
    }
}
