using SilentScan.Verify.Deployment;

namespace SilentScan.Tests.Deployment;

/// <summary>
/// GoBatchSplitter had zero test coverage before this - a lexer-aware GO split is easy to get
/// wrong silently (a false split corrupts both adjacent batches), so every distinguishing case
/// from the audit gets a fixture here.
/// </summary>
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
        // 'it''s' is a single string literal containing an escaped quote - a naive scanner
        // that treats the middle '' as string-end/string-start would desynchronize state for
        // the rest of the script.
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
        // An apostrophe inside a bracketed identifier used to flip the lexer into InString (no
        // dedicated bracket state existed at all), swallowing the real GO separator that follows
        // into the same batch as the next statement.
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
        // [a]]b] is a single bracketed identifier containing a literal ']' (escaped as ']]') -
        // a naive scanner that treats the middle ']' as the identifier's end would desynchronize
        // state for the rest of the script, same failure shape as the string-escaping test above.
        var script = "SELECT [a]]b] FROM T;\nGO\nSELECT 1;";

        var batches = GoBatchSplitter.Split(script);

        Assert.Equal(2, batches.Count);
    }

    [Fact]
    public void Split_NestedBlockComment_DoesNotSplitOnInnerClose()
    {
        // T-SQL block comments nest - only the OUTERMOST */ actually closes the comment. A
        // scanner with no depth counter exits at the first inner */, treating the GO between it
        // and the real outer */ as a genuine separator and splitting mid-comment.
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
}
