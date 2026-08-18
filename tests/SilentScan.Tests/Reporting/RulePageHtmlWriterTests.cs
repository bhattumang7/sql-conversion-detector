using SilentScan.Core.Reporting;

namespace SilentScan.Tests.Reporting;

public sealed class RulePageHtmlWriterTests
{
    [Fact]
    public void Write_RuleWithNoFixGuidanceOrExamples_OmitsHowToFixItSection()
    {
        var rule = new RuleDefinition("silentscan/test/no-extras", "Some rationale.");

        var html = RulePageHtmlWriter.Write(rule, []);

        Assert.DoesNotContain("How can I fix it?", html);
        Assert.DoesNotContain("Verified by an automated test", html);
        Assert.Contains("Some rationale.", html);
    }

    [Fact]
    public void Write_RuleWithFixGuidance_RendersHowToFixItSection()
    {
        var rule = new RuleDefinition("silentscan/test/with-fix", "Some rationale.", "Do the fix instead.");

        var html = RulePageHtmlWriter.Write(rule, []);

        Assert.Contains("How can I fix it?", html);
        Assert.Contains("Do the fix instead.", html);
    }

    [Fact]
    public void Write_VerifiedExampleWithNoCleanCounterpart_OmitsCleanBlock()
    {
        var rule = new RuleDefinition("silentscan/test/fires-only", "Some rationale.");
        var example = new RuleExample("path/fires.sql", "SELECT 1;", CleanPath: null, CleanSql: null);

        var html = RulePageHtmlWriter.Write(rule, [example]);

        Assert.Contains("Verified by an automated test", html);
        Assert.Contains("SELECT 1;", html);
        Assert.DoesNotContain("path/clean.sql", html);
    }

    [Fact]
    public void Write_VerifiedExampleWithCleanCounterpart_RendersBothBlocks()
    {
        var rule = new RuleDefinition("silentscan/test/fires-and-clean", "Some rationale.");
        var example = new RuleExample("path/fires.sql", "SELECT bad;", "path/clean.sql", "SELECT good;");

        var html = RulePageHtmlWriter.Write(rule, [example]);

        Assert.Contains("SELECT bad;", html);
        Assert.Contains("SELECT good;", html);
        Assert.Contains("path/clean.sql", html);
    }

    [Fact]
    public void Write_RationaleAndSqlAreHtmlEncoded()
    {
        var rule = new RuleDefinition("silentscan/test/encoding", "A <script> tag & an ampersand.");

        var html = RulePageHtmlWriter.Write(rule, []);

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void Write_TitleIsHumanizedNotRawId()
    {
        var rule = new RuleDefinition("silentscan/control-flow/trigger-emits-output", "Some rationale.");

        var html = RulePageHtmlWriter.Write(rule, []);

        Assert.Contains("<h1>Trigger Emits Output</h1>", html);
        Assert.Contains("silentscan/control-flow/trigger-emits-output", html);
    }
}
