using SilentScan.Core.Parsing;

namespace SilentScan.Tests.Parsing;

public sealed class DroppedBatchObjectSnifferTests
{
    [Theory]
    [InlineData("CREATE PROCEDURE dbo.usp_Foo AS SELECT 1 FROM WHERE;", UnanalyzedObjectKind.Procedure, "dbo.usp_Foo")]
    [InlineData("CREATE PROC dbo.usp_Foo AS SELECT 1 FROM WHERE;", UnanalyzedObjectKind.Procedure, "dbo.usp_Foo")]
    [InlineData("CREATE VIEW dbo.vw_Foo AS SELECT 1 FROM WHERE;", UnanalyzedObjectKind.View, "dbo.vw_Foo")]
    [InlineData("CREATE FUNCTION dbo.fn_Foo() RETURNS INT AS BEGIN RETURN 1 FROM WHERE END", UnanalyzedObjectKind.Function, "dbo.fn_Foo")]
    [InlineData("CREATE TRIGGER dbo.trg_Foo ON dbo.T AFTER INSERT AS SELECT 1 FROM WHERE", UnanalyzedObjectKind.Trigger, "dbo.trg_Foo")]
    [InlineData("CREATE TABLE dbo.T (Id INT NOT NUL)", UnanalyzedObjectKind.Table, "dbo.T")]
    [InlineData("ALTER PROCEDURE dbo.usp_Foo AS SELECT 1 FROM WHERE;", UnanalyzedObjectKind.Procedure, "dbo.usp_Foo")]
    [InlineData("CREATE OR ALTER PROCEDURE dbo.usp_Foo AS SELECT 1 FROM WHERE;", UnanalyzedObjectKind.Procedure, "dbo.usp_Foo")]
    [InlineData("create procedure dbo.usp_Foo as select 1 from where;", UnanalyzedObjectKind.Procedure, "dbo.usp_Foo")]
    [InlineData("CREATE PROCEDURE [dbo].[usp_Foo] AS SELECT 1 FROM WHERE;", UnanalyzedObjectKind.Procedure, "[dbo].[usp_Foo]")]
    [InlineData("CREATE PROCEDURE usp_Foo AS SELECT 1 FROM WHERE;", UnanalyzedObjectKind.Procedure, "usp_Foo")]
    public void Sniff_WellFormedHeader_IdentifiesKindAndName(string batchText, UnanalyzedObjectKind expectedKind, string expectedName)
    {
        var (kind, name) = DroppedBatchObjectSniffer.Sniff(batchText);

        Assert.Equal(expectedKind, kind);
        Assert.Equal(expectedName, name);
    }

    [Fact]
    public void Sniff_LeadingLineComment_StillIdentifiesTheHeader()
    {
        var (kind, name) = DroppedBatchObjectSniffer.Sniff(
            "-- header comment\nCREATE PROCEDURE dbo.usp_Foo AS SELECT 1 FROM WHERE;");

        Assert.Equal(UnanalyzedObjectKind.Procedure, kind);
        Assert.Equal("dbo.usp_Foo", name);
    }

    [Fact]
    public void Sniff_LeadingBlockComment_StillIdentifiesTheHeader()
    {
        var (kind, name) = DroppedBatchObjectSniffer.Sniff(
            "/* header\n   comment */\n  CREATE PROCEDURE dbo.usp_Foo AS SELECT 1 FROM WHERE;");

        Assert.Equal(UnanalyzedObjectKind.Procedure, kind);
        Assert.Equal("dbo.usp_Foo", name);
    }

    [Theory]
    [InlineData("INSERT INTO dbo.T VALUES (1, FROM WHERE);")]
    [InlineData("SELECT 1 FORM T;")]
    [InlineData("BEGIN TRANSACTION FOO BAR")]
    [InlineData("")]
    [InlineData("   \n\t  ")]
    [InlineData("CREATE FOOBAR dbo.Thing AS SELECT 1 FROM WHERE;")]
    public void Sniff_NotAConfidentCreateOrAlterHeader_DegradesToUnidentified(string batchText)
    {
        var (kind, name) = DroppedBatchObjectSniffer.Sniff(batchText);

        Assert.Equal(UnanalyzedObjectKind.Unidentified, kind);
        Assert.Null(name);
    }

    [Fact]
    public void Sniff_CreateWithNoNameFollowing_DegradesToUnidentified()
    {

        var (kind, name) = DroppedBatchObjectSniffer.Sniff("CREATE PROCEDURE");

        Assert.Equal(UnanalyzedObjectKind.Unidentified, kind);
        Assert.Null(name);
    }
}
