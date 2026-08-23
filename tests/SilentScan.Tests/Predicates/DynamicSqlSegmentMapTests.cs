using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class DynamicSqlSegmentMapTests
{
    [Fact]
    public void Map_SingleLineLiteral_ReturnsLiteralContentColumn()
    {
        var map = new DynamicSqlSegmentMap();

        map.AppendLiteral("test.sql", startLine: 1, startColumn: 6, prefixLength: 1, value: "SELECT 1");

        var span = map.Map(innerLine: 1, innerColumn: 8);

        Assert.Equal("test.sql", span.SourcePath);
        Assert.Equal(1, span.Line);
        Assert.Equal(14, span.Column);
    }

    [Fact]
    public void Map_MultiLineLiteral_ResetsColumnAfterEachNewline()
    {
        var map = new DynamicSqlSegmentMap();

        map.AppendLiteral("test.sql", startLine: 5, startColumn: 6, prefixLength: 1, value: "SELECT UserId\nFROM dbo.Users");

        var span = map.Map(innerLine: 2, innerColumn: 6);

        Assert.Equal(6, span.Line);
        Assert.Equal(6, span.Column);
    }

    [Fact]
    public void Map_EscapedQuoteBeforeTarget_AccountsForRawWidening()
    {
        var map = new DynamicSqlSegmentMap();

        const string value = "SELECT Name FROM T WHERE X = 'y' AND Z = 1";
        map.AppendLiteral("test.sql", startLine: 3, startColumn: 1, prefixLength: 1, value: value);

        var zIndex = value.IndexOf("Z = 1", StringComparison.Ordinal);
        var span = map.Map(innerLine: 1, innerColumn: zIndex + 1);

        var quotesBeforeTarget = value[..zIndex].Count(c => c == '\'');
        Assert.Equal(3, span.Line);
        Assert.Equal(2 + zIndex + quotesBeforeTarget, span.Column);
    }

    [Fact]
    public void Map_ConcatenatedLiterals_LocatesPositionInSecondSegment()
    {
        var map = new DynamicSqlSegmentMap();

        map.AppendLiteral("test.sql", startLine: 1, startColumn: 6, prefixLength: 1, value: "SELECT ");
        map.AppendLiteral("test.sql", startLine: 1, startColumn: 19, prefixLength: 1, value: "UserId FROM dbo.Users");

        Assert.Equal("SELECT UserId FROM dbo.Users", map.InnerText);

        var span = map.Map(innerLine: 1, innerColumn: 8);

        Assert.Equal(1, span.Line);
        Assert.Equal(20, span.Column);
    }

    [Fact]
    public void Map_ConcatenatedLiterals_LocatesPositionInFirstSegmentAmongThree()
    {
        var map = new DynamicSqlSegmentMap();

        map.AppendLiteral("test.sql", startLine: 1, startColumn: 6, prefixLength: 1, value: "SELECT ");
        map.AppendLiteral("test.sql", startLine: 1, startColumn: 19, prefixLength: 1, value: "UserId ");
        map.AppendLiteral("test.sql", startLine: 1, startColumn: 32, prefixLength: 1, value: "FROM dbo.Users");

        Assert.Equal("SELECT UserId FROM dbo.Users", map.InnerText);

        var span = map.Map(innerLine: 1, innerColumn: 1);

        Assert.Equal(1, span.Line);
        Assert.Equal(7, span.Column);
    }

    [Fact]
    public void Map_NationalPrefix_AccountsForTwoCharacterPrefix()
    {
        var map = new DynamicSqlSegmentMap();

        map.AppendLiteral("test.sql", startLine: 1, startColumn: 6, prefixLength: 2, value: "SELECT 1");

        var span = map.Map(innerLine: 1, innerColumn: 1);

        Assert.Equal(8, span.Column);
    }

    [Fact]
    public void Map_LineBeyondInnerText_ClampsToEndOfText()
    {
        var map = new DynamicSqlSegmentMap();
        map.AppendLiteral("test.sql", startLine: 1, startColumn: 6, prefixLength: 1, value: "SELECT 1");

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

    [Fact]
    public void AppendPlaceholder_ReturnsItsOwnInnerStartOffset()
    {
        var map = new DynamicSqlSegmentMap();
        map.AppendLiteral("test.sql", startLine: 1, startColumn: 6, prefixLength: 1, value: "SELECT ");

        var innerStart = map.AppendPlaceholder("test.sql", startLine: 9, startColumn: 3, value: "__silentscan_sym_L9C3__");

        Assert.Equal("SELECT ".Length, innerStart);
        Assert.Equal(innerStart, map.InnerText.IndexOf("__silentscan_sym_L9C3__", StringComparison.Ordinal));
    }

    [Fact]
    public void Map_PositionInsidePlaceholder_CollapsesToPlaceholderOrigin()
    {
        var map = new DynamicSqlSegmentMap();
        map.AppendLiteral("test.sql", startLine: 1, startColumn: 6, prefixLength: 1, value: "SELECT ");
        map.AppendPlaceholder("test.sql", startLine: 9, startColumn: 3, value: "__silentscan_sym_L9C3__");

        var start = map.Map(innerLine: 1, innerColumn: "SELECT ".Length + 1);
        var middle = map.Map(innerLine: 1, innerColumn: "SELECT ".Length + 10);
        var end = map.Map(innerLine: 1, innerColumn: "SELECT ".Length + "__silentscan_sym_L9C3__".Length);

        foreach (var span in new[] { start, middle, end })
        {
            Assert.Equal("test.sql", span.SourcePath);
            Assert.Equal(9, span.Line);
            Assert.Equal(3, span.Column);
        }
    }

    [Fact]
    public void Map_PositionInRealLiteralAfterPlaceholder_MapsToItsOwnOrigin_NotThePlaceholder()
    {
        var map = new DynamicSqlSegmentMap();

        map.AppendLiteral("test.sql", startLine: 1, startColumn: 6, prefixLength: 1, value: "SELECT ");
        map.AppendPlaceholder("test.sql", startLine: 4, startColumn: 12, value: "__silentscan_sym_L4C12__");
        map.AppendLiteral("test.sql", startLine: 1, startColumn: 40, prefixLength: 1, value: " FROM T");

        Assert.Equal("SELECT __silentscan_sym_L4C12__ FROM T", map.InnerText);

        var fIndex = map.InnerText.IndexOf("FROM", StringComparison.Ordinal);
        var span = map.Map(innerLine: 1, innerColumn: fIndex + 1);

        Assert.Equal("test.sql", span.SourcePath);
        Assert.Equal(1, span.Line);
        Assert.Equal(41 + 1, span.Column);
    }
}
