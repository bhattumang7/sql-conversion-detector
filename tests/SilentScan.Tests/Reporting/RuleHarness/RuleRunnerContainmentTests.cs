using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.RuleHarness;

namespace SilentScan.Tests.Reporting.RuleHarness;

public sealed class RuleRunnerContainmentTests
{
    private sealed record FakeFinding(SourceSpan Location, FindingConfidence Confidence) : IFinding;

    private sealed class ThrowingRule : IPerFileRule
    {
        public string Id => "ThrowingTestRule";

        public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class BenignRule : IPerFileRule
    {
        public string Id => "BenignTestRule";

        public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) =>
            [new FakeFinding(new SourceSpan(parseResult.SourcePath, 1, 1), FindingConfidence.High)];
    }

    private static RuleContext BuildEmptyContext()
    {
        var catalog = CatalogBuilder.Build([]);
        var lineage = LineageResolver.Resolve(catalog, []);
        return new RuleContext(
            catalog, lineage, new SkipLedger(), new ProcCallGraph([]),
            new Dictionary<string, TvfFenceOrigin>(), new Dictionary<string, ScalarUdfOrigin>(),
            new Dictionary<string, ViewExpansionOrigin>(), [],
            new Dictionary<string, SelectStarViewCandidate>(), new Dictionary<string, IReadOnlyList<string>>());
    }

    [Fact]
    public void RuleThatThrows_IsContainedAndOtherRulesStillRun()
    {
        var parseResult = SqlScriptParser.ParseText("test.sql", "SELECT 1;");
        var context = BuildEmptyContext();

        var result = RuleRunner.Run(
            [new ThrowingRule(), new BenignRule()], [parseResult], context, FindingConfidence.Low, NullScanProgress.Instance);

        Assert.Empty(result.For<FakeFinding>("ThrowingTestRule"));
        Assert.Single(result.For<FakeFinding>("BenignTestRule"));
        Assert.Contains(result.Crashes, c => c.ConstructKind == "RuleCrash" && c.Reason.Contains("ThrowingTestRule", StringComparison.Ordinal));
    }
}
