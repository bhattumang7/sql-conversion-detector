using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class FormattingScannerTests
{
    private static IReadOnlyList<FormattingFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return FormattingScanner.Scan(result);
    }

    [Fact]
    public void TabCharacter_Fires()
    {
        var findings = Scan("SELECT\t1;");

        Assert.Contains(findings, f => f.Kind == FormattingFindingKind.TabCharacterUsed);
    }

    [Fact]
    public void NoTabCharacter_NeverFires()
    {
        var findings = Scan("SELECT 1;");

        Assert.DoesNotContain(findings, f => f.Kind == FormattingFindingKind.TabCharacterUsed);
    }

    [Fact]
    public void TwoStatementsOnSameLine_Fires()
    {
        var sql = "CREATE PROCEDURE dbo.P AS BEGIN SELECT 1; SELECT 2; END";
        var findings = Scan(sql);

        Assert.Contains(findings, f => f.Kind == FormattingFindingKind.MultipleStatementsOnSameLine);
    }

    [Fact]
    public void OneStatementPerLine_NeverFires()
    {
        var sql = "CREATE PROCEDURE dbo.P AS\nBEGIN\nSELECT 1;\nSELECT 2;\nEND";
        var findings = Scan(sql);

        Assert.DoesNotContain(findings, f => f.Kind == FormattingFindingKind.MultipleStatementsOnSameLine);
    }

    [Fact]
    public void TwoDeclarationsOnSameLine_Fires()
    {
        var sql = "DECLARE @a INT, @b INT;";
        var findings = Scan(sql);

        var finding = Assert.Single(findings, f => f.Kind == FormattingFindingKind.MultipleDeclarationsOnSameLine);
        Assert.Equal("@b", finding.DetailText);
    }

    [Fact]
    public void DeclarationsOnSeparateLines_NeverFires()
    {
        var sql = "DECLARE @a INT,\n@b INT;";
        var findings = Scan(sql);

        Assert.DoesNotContain(findings, f => f.Kind == FormattingFindingKind.MultipleDeclarationsOnSameLine);
    }

    [Fact]
    public void SingleDeclaration_NeverFires()
    {
        var findings = Scan("DECLARE @a INT;");

        Assert.DoesNotContain(findings, f => f.Kind == FormattingFindingKind.MultipleDeclarationsOnSameLine);
    }

    [Fact]
    public void UnbracedIfBodyOnNextLine_FiresMissingBeginEnd()
    {
        var sql = "IF 1 = 1\n    SELECT 1;";
        var findings = Scan(sql);

        Assert.Contains(findings, f => f.Kind == FormattingFindingKind.MissingBeginEndBlock);
        Assert.DoesNotContain(findings, f => f.Kind == FormattingFindingKind.SingleLineConditionalBody);
    }

    [Fact]
    public void UnbracedIfBodySameLine_FiresSingleLineConditionalBody()
    {
        var sql = "IF 1 = 1 SELECT 1;";
        var findings = Scan(sql);

        Assert.Contains(findings, f => f.Kind == FormattingFindingKind.SingleLineConditionalBody);
        Assert.DoesNotContain(findings, f => f.Kind == FormattingFindingKind.MissingBeginEndBlock);
    }

    [Fact]
    public void BracedIfBody_NeverFires()
    {
        var sql = "IF 1 = 1\nBEGIN\n    SELECT 1;\nEND";
        var findings = Scan(sql);

        Assert.DoesNotContain(findings, f => f.Kind is FormattingFindingKind.MissingBeginEndBlock or FormattingFindingKind.SingleLineConditionalBody);
    }

    [Fact]
    public void ElseIfChain_NeverFiresOnTheChainItself()
    {
        var sql = "IF 1 = 1\n    SELECT 1;\nELSE IF 2 = 2\n    SELECT 2;";
        var findings = Scan(sql);

        Assert.DoesNotContain(findings, f => f.Kind == FormattingFindingKind.SingleLineConditionalBody && f.Line == 3);
    }

    [Fact]
    public void UnbracedWhileBody_Fires()
    {
        var sql = "WHILE 1 = 1\n    SELECT 1;";
        var findings = Scan(sql);

        Assert.Contains(findings, f => f.Kind == FormattingFindingKind.MissingBeginEndBlock);
    }

    [Fact]
    public void DanglingStatementAfterUnbracedIf_Fires()
    {
        var sql = "CREATE PROCEDURE dbo.P AS\nBEGIN\nIF 1 = 1\n    SELECT 1;\n    SELECT 2;\nEND";
        var findings = Scan(sql);

        Assert.Contains(findings, f => f.Kind == FormattingFindingKind.DanglingStatementAfterUnbracedBody);
    }

    [Fact]
    public void StatementAtLowerIndentAfterUnbracedIf_NeverFiresDangling()
    {
        var sql = "CREATE PROCEDURE dbo.P AS\nBEGIN\nIF 1 = 1\n    SELECT 1;\nSELECT 2;\nEND";
        var findings = Scan(sql);

        Assert.DoesNotContain(findings, f => f.Kind == FormattingFindingKind.DanglingStatementAfterUnbracedBody);
    }

    [Fact]
    public void ChainedUnbracedIfsAtSameIndentation_NeverFiresDangling()
    {

        var sql = "CREATE PROCEDURE dbo.P AS\nBEGIN\nIF @a = 1\n    SELECT 1;\nIF @b = 1\n    SELECT 2;\nEND";
        var findings = Scan(sql);

        Assert.DoesNotContain(findings, f => f.Kind == FormattingFindingKind.DanglingStatementAfterUnbracedBody);
    }

    [Fact]
    public void BracedIfFollowedByUnrelatedStatement_NeverFiresDangling()
    {
        var sql = "CREATE PROCEDURE dbo.P AS\nBEGIN\nIF 1 = 1\nBEGIN\n    SELECT 1;\nEND\n    SELECT 2;\nEND";
        var findings = Scan(sql);

        Assert.DoesNotContain(findings, f => f.Kind == FormattingFindingKind.DanglingStatementAfterUnbracedBody);
    }

    [Fact]
    public void IfImmediatelyAfterPriorBlockEndSameLine_Fires()
    {
        var sql = "CREATE PROCEDURE dbo.P AS\nBEGIN\nIF 1 = 1\nBEGIN\n    SELECT 1;\nEND IF 2 = 2\nBEGIN\n    SELECT 2;\nEND\nEND";
        var findings = Scan(sql);

        Assert.Contains(findings, f => f.Kind == FormattingFindingKind.IfImmediatelyFollowingPriorBlockEnd);
    }

    [Fact]
    public void IfOnItsOwnLineAfterPriorBlockEnd_NeverFires()
    {
        var sql = "CREATE PROCEDURE dbo.P AS\nBEGIN\nIF 1 = 1\nBEGIN\n    SELECT 1;\nEND\nIF 2 = 2\nBEGIN\n    SELECT 2;\nEND\nEND";
        var findings = Scan(sql);

        Assert.DoesNotContain(findings, f => f.Kind == FormattingFindingKind.IfImmediatelyFollowingPriorBlockEnd);
    }

    [Fact]
    public void RealElseIfChain_NeverFiresIfFollowingBlockEnd()
    {
        var sql = "CREATE PROCEDURE dbo.P AS\nBEGIN\nIF 1 = 1\nBEGIN\n    SELECT 1;\nEND\nELSE IF 2 = 2\nBEGIN\n    SELECT 2;\nEND\nEND";
        var findings = Scan(sql);

        Assert.DoesNotContain(findings, f => f.Kind == FormattingFindingKind.IfImmediatelyFollowingPriorBlockEnd);
    }

    [Fact]
    public void ParenthesizedColumnReference_FiresRedundantParentheses()
    {
        var sql = "SELECT (Col1) FROM dbo.T;";
        var findings = Scan(sql);

        Assert.Contains(findings, f => f.Kind == FormattingFindingKind.RedundantParentheses);
    }

    [Fact]
    public void ParenthesizedArithmeticExpression_NeverFiresRedundant()
    {
        var sql = "SELECT (Col1 + Col2) * 2 FROM dbo.T;";
        var findings = Scan(sql);

        Assert.DoesNotContain(findings, f => f.Kind == FormattingFindingKind.RedundantParentheses);
    }

    [Fact]
    public void DoubleWrappedBooleanExpression_FiresRedundant()
    {
        var sql = "SELECT 1 WHERE ((Col1 = 1));";
        var findings = Scan(sql);

        Assert.Contains(findings, f => f.Kind == FormattingFindingKind.RedundantParentheses);
    }

    [Fact]
    public void ModuleWithNoLeadingComment_FiresMissingFileHeader()
    {
        var findings = Scan("SELECT 1;");

        Assert.Contains(findings, f => f.Kind == FormattingFindingKind.MissingFileHeaderComment);
    }

    [Fact]
    public void ModuleWithLeadingLineComment_NeverFiresMissingFileHeader()
    {
        var findings = Scan("-- header\nSELECT 1;");

        Assert.DoesNotContain(findings, f => f.Kind == FormattingFindingKind.MissingFileHeaderComment);
    }

    [Fact]
    public void ModuleWithLeadingBlockComment_NeverFiresMissingFileHeader()
    {
        var findings = Scan("/* header */\nSELECT 1;");

        Assert.DoesNotContain(findings, f => f.Kind == FormattingFindingKind.MissingFileHeaderComment);
    }
}
