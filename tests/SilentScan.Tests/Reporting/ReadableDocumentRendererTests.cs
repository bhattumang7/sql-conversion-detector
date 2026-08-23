using SilentScan.Core.Reporting.Readable;

namespace SilentScan.Tests.Reporting;

public sealed class ReadableDocumentRendererTests
{
    [Fact]
    public void Markdown_TableRows_HaveTheSameCellCountAsTheHeader()
    {
        var document = new ReadableDocument([
            new ReadableBlock.Table(
                ["Where", "Column", "Detail"],
                [
                    ["a.sql:1", "dbo.T.C", "plain"],
                    ["b.sql:2", "dbo.T|X.C", "has | a pipe"],
                ]),
        ]);

        var lines = Lines(ReadableDocumentRenderer.Render(document, ReadableStyle.Markdown));

        Assert.All(lines, line => Assert.Equal(4, UnescapedPipes(line)));
        Assert.Equal("| Where | Column | Detail |", lines[0]);
        Assert.Equal("| --- | --- | --- |", lines[1]);
        Assert.Equal(@"| b.sql:2 | dbo.T\|X.C | has \| a pipe |", lines[3]);
    }

    [Fact]
    public void Markdown_NewlineInCell_StaysOnOneRow()
    {
        var document = new ReadableDocument([
            new ReadableBlock.Table(["A", "B"], [["first\nsecond", "x"]]),
        ]);

        var lines = Lines(ReadableDocumentRenderer.Render(document, ReadableStyle.Markdown));

        Assert.Equal(3, lines.Length);
        Assert.Equal("| first second | x |", lines[2]);
    }

    [Fact]
    public void Text_TableColumns_AreAlignedAndNotRightPadded()
    {
        var document = new ReadableDocument([
            new ReadableBlock.Table(["Where", "Detail"], [["a.sql:1", "short"], ["much-longer.sql:2", "x"]]),
        ]);

        var lines = Lines(ReadableDocumentRenderer.Render(document, ReadableStyle.Text));

        const int SecondColumn = 2 + 17 + 2;
        Assert.Equal(["Detail", "------", "short", "x"], lines.Select(line => line[SecondColumn..]));

        Assert.All(lines, line => Assert.Equal(line.TrimEnd(), line));
    }

    [Fact]
    public void Text_HeadingLevels_OnlyRuleTheOutermostTwo()
    {
        var document = new ReadableDocument([
            new ReadableBlock.Heading(1, "Title"),
            new ReadableBlock.Heading(2, "Section"),
            new ReadableBlock.Heading(3, "Subsection"),
        ]);

        var rendered = ReadableDocumentRenderer.Render(document, ReadableStyle.Text);

        Assert.Contains("Title\n=====", rendered.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("Section\n-------", rendered.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.DoesNotContain("Subsection\n-", rendered.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_HeadingLevels_MapToHashes()
    {
        var document = new ReadableDocument([
            new ReadableBlock.Heading(1, "Title"),
            new ReadableBlock.Heading(3, "Deep"),
            new ReadableBlock.Bullets(["one", "two"]),
        ]);

        var rendered = ReadableDocumentRenderer.Render(document, ReadableStyle.Markdown).ReplaceLineEndings("\n");

        Assert.Contains("# Title", rendered, StringComparison.Ordinal);
        Assert.Contains("### Deep", rendered, StringComparison.Ordinal);
        Assert.Contains("- one\n- two", rendered, StringComparison.Ordinal);
    }

    private static int UnescapedPipes(string line) =>
        line.Where((c, i) => c == '|' && (i == 0 || line[i - 1] != '\\')).Count();

    private static string[] Lines(string rendered) =>
        rendered.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
}
