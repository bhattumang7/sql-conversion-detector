using SilentScan.Core.Diagnostics;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Core.Reporting.RuleHarness;

public sealed class DefaultLocationComparer : IComparer<IFinding>
{
    public static readonly DefaultLocationComparer Instance = new();

    public int Compare(IFinding? x, IFinding? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        var pathCompare = string.CompareOrdinal(x.Location.SourcePath, y.Location.SourcePath);
        if (pathCompare != 0)
        {
            return pathCompare;
        }

        var lineCompare = x.Location.Line.CompareTo(y.Location.Line);
        return lineCompare != 0 ? lineCompare : x.Location.Column.CompareTo(y.Location.Column);
    }
}

public sealed class RuleRunResult(
    IReadOnlyDictionary<string, IReadOnlyList<IFinding>> findingsByRuleId,
    IReadOnlyList<SkippedConstruct> crashes)
{
    public IReadOnlyList<SkippedConstruct> Crashes { get; } = crashes;

    public IReadOnlyList<TFinding> For<TFinding>(string ruleId)
        where TFinding : IFinding =>
        findingsByRuleId.TryGetValue(ruleId, out var findings)
            ? [.. findings.Cast<TFinding>()]
            : [];
}

public static class RuleRunner
{
    private const string RuleCrashKind = "RuleCrash";

    public static RuleRunResult Run(
        IReadOnlyList<IRule> rules,
        IReadOnlyList<SqlParseResult> parseResults,
        RuleContext context,
        FindingConfidence minimumConfidence,
        IScanProgress progress)
    {
        var resultsByRuleId = new Dictionary<string, IReadOnlyList<IFinding>>(StringComparer.Ordinal);
        var crashes = new List<SkippedConstruct>();

        foreach (var rule in rules)
        {
            var comparer = DefaultLocationComparer.Instance as IComparer<IFinding>;

            IReadOnlyList<IFinding> raw = rule switch
            {
                IPerFileRule perFileRule => RunPerFileRule(perFileRule, parseResults, context, progress, crashes, out comparer),
                ICatalogRule catalogRule => RunCatalogRule(catalogRule, context, crashes),
                ICrossModuleRule crossModuleRule => RunCrossModuleRule(crossModuleRule, parseResults, context, crashes),
                _ => throw new InvalidOperationException($"Rule '{rule.Id}' does not implement IPerFileRule, ICatalogRule, or ICrossModuleRule."),
            };

            var filtered = rule.ApplyConfidenceFilter ? raw.Where(f => f.Confidence <= minimumConfidence) : raw;
            resultsByRuleId[rule.Id] = [.. filtered.OrderBy(f => f, comparer)];
        }

        return new RuleRunResult(resultsByRuleId, crashes);
    }

    private static List<IFinding> RunPerFileRule(
        IPerFileRule rule,
        IReadOnlyList<SqlParseResult> parseResults,
        RuleContext context,
        IScanProgress progress,
        List<SkippedConstruct> crashes,
        out IComparer<IFinding> comparer)
    {
        comparer = rule.Comparer ?? DefaultLocationComparer.Instance;

        object? state;
        try
        {
            state = rule.Prepare(context);
        }
        catch (Exception ex)
        {
            lock (crashes)
            {
                crashes.Add(new SkippedConstruct(AnalysisPass.Predicates, string.Empty, 0, 0, RuleCrashKind, $"{rule.Id}: {ex.Message}"));
            }
            return [];
        }

        using var stage = progress.Begin(rule.Id, parseResults.Count);

        var findings = parseResults
            .AsParallel()
            .SelectMany(parseResult =>
            {
                IReadOnlyList<IFinding> perFileFindings;
                try
                {
                    perFileFindings = rule.Scan(parseResult, context, state);
                }
                catch (Exception ex)
                {
                    lock (crashes)
                    {
                        crashes.Add(new SkippedConstruct(AnalysisPass.Predicates, parseResult.SourcePath, 0, 0, RuleCrashKind, $"{rule.Id}: {ex.Message}"));
                    }
                    perFileFindings = [];
                }

                stage.Advance();
                return perFileFindings;
            })
            .ToList();

        try
        {
            findings.AddRange(rule.ScanCatalogOnce(context));
        }
        catch (Exception ex)
        {
            crashes.Add(new SkippedConstruct(AnalysisPass.Predicates, string.Empty, 0, 0, RuleCrashKind, $"{rule.Id}: {ex.Message}"));
        }

        return findings;
    }

    private static IReadOnlyList<IFinding> RunCatalogRule(ICatalogRule rule, RuleContext context, List<SkippedConstruct> crashes)
    {
        try
        {
            return rule.Scan(context);
        }
        catch (Exception ex)
        {
            crashes.Add(new SkippedConstruct(AnalysisPass.Predicates, string.Empty, 0, 0, RuleCrashKind, $"{rule.Id}: {ex.Message}"));
            return [];
        }
    }

    private static IReadOnlyList<IFinding> RunCrossModuleRule(ICrossModuleRule rule, IReadOnlyList<SqlParseResult> parseResults, RuleContext context, List<SkippedConstruct> crashes)
    {
        try
        {
            return rule.Scan(parseResults, context);
        }
        catch (Exception ex)
        {
            crashes.Add(new SkippedConstruct(AnalysisPass.Predicates, string.Empty, 0, 0, RuleCrashKind, $"{rule.Id}: {ex.Message}"));
            return [];
        }
    }
}
