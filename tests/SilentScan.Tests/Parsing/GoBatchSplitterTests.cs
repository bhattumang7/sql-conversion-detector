using SilentScan.Core.Parsing;

namespace SilentScan.Tests.Parsing;

public sealed class GoBatchSplitterTests
{
    [Fact]
    public void Split_SimpleTwoBatches_SplitsOnGo()
    {
        var batches = GoBatchSplitter.Split("CREATE TABLE T1 (Id INT);\nGO\nCREATE TABLE T2 (Id INT);");

        Assert.Equal(2, batches.Count);
        Assert.Contains("CREATE TABLE T1", batches[0]);
        Assert.Contains("CREATE TABLE T2", batches[1]);
    }

    [Fact]
    public void Split_GoInsideStringLiteral_DoesNotSplit()
    {
        var script = "INSERT INTO T (Note) VALUES ('line one\nGO\nline two');\nGO\nSELECT 1;";

        var batches = GoBatchSplitter.Split(script);

        Assert.Equal(2, batches.Count);
        Assert.Contains("line one", batches[0]);
        Assert.Contains("line two", batches[0]);
        Assert.DoesNotContain("SELECT 1", batches[0]);
    }

    [Fact]
    public void Split_GoInsideBlockComment_DoesNotSplit()
    {
        var script = "/* disabled:\nGO\n*/\nSELECT 1;\nGO\nSELECT 2;";

        var batches = GoBatchSplitter.Split(script);

        Assert.Equal(2, batches.Count);
        Assert.Contains("SELECT 1", batches[0]);
        Assert.Contains("SELECT 2", batches[1]);
    }

    [Fact]
    public void Split_GoWithTrailingLineComment_Splits()
    {
        var batches = GoBatchSplitter.Split("SELECT 1;\nGO -- separator\nSELECT 2;");

        Assert.Equal(2, batches.Count);
    }

    [Fact]
    public void Split_GoWithRepeatCount_RepeatsThePrecedingBatch()
    {
        var batches = GoBatchSplitter.Split("SELECT 1;\nGO 3");

        Assert.Equal(3, batches.Count);
        Assert.All(batches, b => Assert.Contains("SELECT 1", b));
    }

    [Fact]
    public void Split_CaseInsensitiveGo_Splits()
    {
        var batches = GoBatchSplitter.Split("SELECT 1;\ngo\nSELECT 2;");

        Assert.Equal(2, batches.Count);
    }

    [Fact]
    public void Split_EscapedQuoteInsideString_StaysInsideTheStringState()
    {

        var script = "INSERT INTO T (Note) VALUES ('it''s a GO test');\nGO\nSELECT 1;";

        var batches = GoBatchSplitter.Split(script);

        Assert.Equal(2, batches.Count);
    }

    [Fact]
    public void Split_LineCommentContainingGo_DoesNotSplit()
    {
        var script = "SELECT 1;\n-- GO\nSELECT 2;\nGO\nSELECT 3;";

        var batches = GoBatchSplitter.Split(script);

        Assert.Equal(2, batches.Count);
        Assert.Contains("SELECT 1", batches[0]);
        Assert.Contains("SELECT 2", batches[0]);
        Assert.Contains("SELECT 3", batches[1]);
    }

    [Fact]
    public void Split_EmptyBatchesAreDropped()
    {
        var batches = GoBatchSplitter.Split("GO\nGO\nSELECT 1;\nGO\nGO");

        Assert.Single(batches);
        Assert.Contains("SELECT 1", batches[0]);
    }

    [Fact]
    public void Split_NoGoSeparators_ReturnsSingleBatch()
    {
        var batches = GoBatchSplitter.Split("SELECT 1;\nSELECT 2;");

        Assert.Single(batches);
    }

    [Fact]
    public void Split_CarriageReturnLineFeed_HandledLikeLineFeed()
    {
        var batches = GoBatchSplitter.Split("SELECT 1;\r\nGO\r\nSELECT 2;");

        Assert.Equal(2, batches.Count);
    }

    [Fact]
    public void Split_GoInsideBracketedIdentifier_DoesNotSplit()
    {

        var script = "SELECT [Customer's\nGO\nOrder] FROM T;\nGO\nSELECT 2;";

        var batches = GoBatchSplitter.Split(script);

        Assert.Equal(2, batches.Count);
        Assert.Contains("Customer's", batches[0]);
        Assert.Contains("Order] FROM T", batches[0]);
        Assert.Contains("SELECT 2", batches[1]);
    }

    [Fact]
    public void Split_DoubledClosingBracketInsideIdentifier_StaysInsideTheBracketState()
    {

        var script = "SELECT [a]]b] FROM T;\nGO\nSELECT 1;";

        var batches = GoBatchSplitter.Split(script);

        Assert.Equal(2, batches.Count);
    }

    [Fact]
    public void Split_NestedBlockComment_DoesNotSplitOnInnerClose()
    {

        var script = "/* outer /* inner */ still commented\nGO\nstill commented */\nSELECT 1;\nGO\nSELECT 2;";

        var batches = GoBatchSplitter.Split(script);

        Assert.Equal(2, batches.Count);
        Assert.Contains("SELECT 1", batches[0]);
        Assert.Contains("SELECT 2", batches[1]);
    }

    [Fact]
    public void Split_GoWithTrailingBlockComment_Splits()
    {
        var batches = GoBatchSplitter.Split("SELECT 1;\nGO /* deploy step */\nSELECT 2;");

        Assert.Equal(2, batches.Count);
    }

    [Fact]
    public void Split_GoInsideQuotedIdentifier_DoesNotSplit()
    {
        var script = "SELECT \"Customer\nGO\nOrder\" FROM T;\nGO\nSELECT 2;";

        var batches = GoBatchSplitter.Split(script);

        Assert.Equal(2, batches.Count);
        Assert.Contains("SELECT 2", batches[1]);
    }

    [Fact]
    public void SplitWithSpans_SimpleTwoBatches_ReturnsSpansMatchingExactText()
    {
        var script = "CREATE TABLE T1 (Id INT);\nGO\nCREATE TABLE T2 (Id INT);";

        var spans = GoBatchSplitter.SplitWithSpans(script);

        Assert.Equal(2, spans.Count);
        foreach (var (start, length, text) in spans)
        {
            Assert.Equal(text, script.Substring(start, length));
        }

        Assert.Contains("CREATE TABLE T1", spans[0].Text);
        Assert.Contains("CREATE TABLE T2", spans[1].Text);
    }

    [Fact]
    public void SplitWithSpans_GoWithRepeatCount_ReturnsTheSegmentOnce()
    {

        var spans = GoBatchSplitter.SplitWithSpans("SELECT 1;\nGO 3");

        Assert.Single(spans);
    }

    [Fact]
    public void SplitWithSpans_EmptyBatchesAreDropped()
    {
        var script = "GO\nGO\nSELECT 1;\nGO\nGO";

        var spans = GoBatchSplitter.SplitWithSpans(script);

        Assert.Single(spans);
        Assert.Equal("SELECT 1;", spans[0].Text);
        Assert.Equal(spans[0].Text, script.Substring(spans[0].Start, spans[0].Length));
    }

    [Fact]
    public void SplitWithSpans_LeadingAndTrailingWhitespaceAroundABatch_IsExcludedFromItsSpan()
    {
        var script = "\n\n  SELECT 1;  \n\nGO\nSELECT 2;";

        var spans = GoBatchSplitter.SplitWithSpans(script);

        Assert.Equal(2, spans.Count);
        Assert.Equal("SELECT 1;", spans[0].Text);
        Assert.Equal(spans[0].Text, script.Substring(spans[0].Start, spans[0].Length));
    }

    [Fact]
    public void SplitWithSpans_GoInsideStringLiteral_DoesNotSplit()
    {
        var script = "INSERT INTO T (Note) VALUES ('line one\nGO\nline two');\nGO\nSELECT 1;";

        var spans = GoBatchSplitter.SplitWithSpans(script);

        Assert.Equal(2, spans.Count);
        Assert.Contains("line one", spans[0].Text);
        Assert.Contains("line two", spans[0].Text);
        Assert.DoesNotContain("SELECT 1", spans[0].Text);
    }

    [Fact]
    public void SplitWithSpans_NoGoSeparators_ReturnsSingleSpanCoveringWholeTrimmedScript()
    {
        var script = "SELECT 1;\nSELECT 2;";

        var spans = GoBatchSplitter.SplitWithSpans(script);

        Assert.Single(spans);
        Assert.Equal(script, spans[0].Text);
        Assert.Equal(0, spans[0].Start);
        Assert.Equal(script.Length, spans[0].Length);
    }
}
