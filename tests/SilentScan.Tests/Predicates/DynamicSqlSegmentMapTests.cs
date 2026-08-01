using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// Exercises <see cref="DynamicSqlSegmentMap"/> directly with hand-computed expected
/// positions - the two hard cases this class exists for are a literal segment spanning
/// multiple source lines, and a `''`-escaped quote shrinking the unescaped text relative to
/// the raw source, both of which break a naive index-for-index mapping.
/// </summary>
public sealed class DynamicSqlSegmentMapTests
{
    [Fact]
    public void Map_SingleLineLiteral_ReturnsLiteralContentColumn()
    {
        var map = new DynamicSqlSegmentMap();

        // EXEC('SELECT 1') - the literal token starts at column 6 (the opening quote),
        // prefixLength 1 (no N prefix), so content ('S') starts at column 7.
        map.AppendLiteral("test.sql", startLine: 1, startColumn: 6, prefixLength: 1, value: "SELECT 1");

        var span = map.Map(innerLine: 1, innerColumn: 8);

        Assert.Equal("test.sql", span.SourcePath);
        Assert.Equal(1, span.Line);
        Assert.Equal(14, span.Column); // 7 (content start) + 7 (0-based offset of the '1')
    }

    [Fact]
    public void Map_MultiLineLiteral_ResetsColumnAfterEachNewline()
    {
        var map = new DynamicSqlSegmentMap();

        // A literal starting at line 5, column 6, spanning two source lines:
        //   'SELECT UserId
        //    FROM dbo.Users'
        map.AppendLiteral("test.sql", startLine: 5, startColumn: 6, prefixLength: 1, value: "SELECT UserId\nFROM dbo.Users");

        // "FROM dbo.Users" begins right after the newline in the inner text, which is at
        // inner-text line 2 (SELECT UserId is one line, FROM dbo.Users is line 2).
        var span = map.Map(innerLine: 2, innerColumn: 6);

        Assert.Equal(6, span.Line); // startLine 5 + 1 line of delta
        Assert.Equal(6, span.Column); // "FROM " is 5 chars, so column 6 is 'd' of dbo
    }

    [Fact]
    public void Map_EscapedQuoteBeforeTarget_AccountsForRawWidening()
    {
        var map = new DynamicSqlSegmentMap();

        // Raw source: 'SELECT Name FROM T WHERE X = ''y'' AND Z = 1'
        // Unescaped value has "''" collapsed to "'", so Value offsets drift from raw offsets
        // by one character for every escaped quote already consumed.
        const string value = "SELECT Name FROM T WHERE X = 'y' AND Z = 1";
        map.AppendLiteral("test.sql", startLine: 3, startColumn: 1, prefixLength: 1, value: value);

        var zIndex = value.IndexOf("Z = 1", StringComparison.Ordinal);
        var span = map.Map(innerLine: 1, innerColumn: zIndex + 1);

        // Content starts at column 2 (startColumn 1 + prefixLength 1). Every `'` still
        // present in the unescaped Value before the target represents one raw `''` pair
        // collapsed to a single character, so it independently recomputes (not just trusts)
        // the widening the segment map must apply to land on the right raw column.
        var quotesBeforeTarget = value[..zIndex].Count(c => c == '\'');
        Assert.Equal(3, span.Line);
        Assert.Equal(2 + zIndex + quotesBeforeTarget, span.Column);
    }

    [Fact]
    public void Map_ConcatenatedLiterals_LocatesPositionInSecondSegment()
    {
        var map = new DynamicSqlSegmentMap();

        // EXEC('SELECT ' + 'UserId FROM dbo.Users') - two literals on the same line.
        map.AppendLiteral("test.sql", startLine: 1, startColumn: 6, prefixLength: 1, value: "SELECT ");
        map.AppendLiteral("test.sql", startLine: 1, startColumn: 19, prefixLength: 1, value: "UserId FROM dbo.Users");

        Assert.Equal("SELECT UserId FROM dbo.Users", map.InnerText);

        // Position of 'U' in "UserId" within the folded text (offset 7, 1-based column 8).
        var span = map.Map(innerLine: 1, innerColumn: 8);

        Assert.Equal(1, span.Line);
        Assert.Equal(20, span.Column); // second literal's content starts at column 19 + 1 = 20
    }

    [Fact]
    public void Map_ConcatenatedLiterals_LocatesPositionInFirstSegmentAmongThree()
    {
        var map = new DynamicSqlSegmentMap();

        // Three literals concatenated - the target position falls in the FIRST one, exercising
        // the search path that must skip past later segments rather than default to the last.
        map.AppendLiteral("test.sql", startLine: 1, startColumn: 6, prefixLength: 1, value: "SELECT ");
        map.AppendLiteral("test.sql", startLine: 1, startColumn: 19, prefixLength: 1, value: "UserId ");
        map.AppendLiteral("test.sql", startLine: 1, startColumn: 32, prefixLength: 1, value: "FROM dbo.Users");

        Assert.Equal("SELECT UserId FROM dbo.Users", map.InnerText);

        // Position of 'S' at the very start of the folded text (offset 0, column 1).
        var span = map.Map(innerLine: 1, innerColumn: 1);

        Assert.Equal(1, span.Line);
        Assert.Equal(7, span.Column); // first literal's content starts at column 6 + 1 = 7
    }

    [Fact]
    public void Map_NationalPrefix_AccountsForTwoCharacterPrefix()
    {
        var map = new DynamicSqlSegmentMap();

        // EXEC(N'SELECT 1') - startColumn points at 'N', prefixLength 2 for "N'".
        map.AppendLiteral("test.sql", startLine: 1, startColumn: 6, prefixLength: 2, value: "SELECT 1");

        var span = map.Map(innerLine: 1, innerColumn: 1);

        Assert.Equal(8, span.Column); // 6 (N) + 2 (prefix) + 0 (offset)
    }

    [Fact]
    public void Map_LineBeyondInnerText_ClampsToEndOfText()
    {
        var map = new DynamicSqlSegmentMap();
        map.AppendLiteral("test.sql", startLine: 1, startColumn: 6, prefixLength: 1, value: "SELECT 1");

        // Defensive clamp: a caller asking for a line number the inner text doesn't have
        // (shouldn't happen for a position ScriptDOM actually reported against this same
        // text) lands at the end of the text rather than throwing.
        var span = map.Map(innerLine: 5, innerColumn: 1);

        Assert.Equal(1, span.Line);
        Assert.Equal(6 + 1 + "SELECT 1".Length, span.Column);
    }

    [Fact]
    public void Map_BeforeAnyLiteralAppended_Throws()
    {
        var map = new DynamicSqlSegmentMap();

        Assert.Throws<InvalidOperationException>(() => map.Map(1, 1));
    }
}
