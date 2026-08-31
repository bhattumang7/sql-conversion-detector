using SilentScan.Core.Diagnostics;
using SilentScan.Core.Lineage;
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

    public IReadOnlyDictionary<string, IReadOnlyList<IFinding>> AllFindings => findingsByRuleId;

    public IReadOnlyList<TFinding> For<TFinding>(string ruleId)
        where TFinding : IFinding =>
        findingsByRuleId.TryGetValue(ruleId, out var findings)
            ? [.. findings.OfType<TFinding>()]
            : [];
}

public static class RuleRunner
{
    private const string RuleCrashKind = "RuleCrash";

    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    public static RuleRunResult Run(
        IReadOnlyList<IRule> rules,
        IReadOnlyList<SqlParseResult> parseResults,
        RuleContext context,
        FindingConfidence minimumConfidence,
        IScanProgress progress)
    {
        var resultsByRuleId = new Dictionary<string, IReadOnlyList<IFinding>>(StringComparer.Ordinal);
        var crashes = new List<SkippedConstruct>();

        var perFileRules = rules.OfType<IPerFileRule>().ToList();
        var rawPerFileFindings = RunPerFileRules(perFileRules, parseResults, context, progress, crashes);

        var crossModuleRules = rules.OfType<ICrossModuleRule>().ToList();
        var rawCrossModuleFindings = RunCrossModuleRules(crossModuleRules, parseResults, context, crashes);

        foreach (var rule in rules)
        {
            var comparer = DefaultLocationComparer.Instance as IComparer<IFinding>;

            IReadOnlyList<IFinding> raw = rule switch
            {
                IPerFileRule perFileRule => WithComparer(perFileRule, rawPerFileFindings[perFileRule.Id], out comparer),
                ICatalogRule catalogRule => RunCatalogRule(catalogRule, context, crashes),
                ICrossModuleRule crossModuleRule => rawCrossModuleFindings[crossModuleRule.Id],
                _ => throw new InvalidOperationException($"Rule '{rule.Id}' does not implement IPerFileRule, ICatalogRule, or ICrossModuleRule."),
            };

            var filtered = rule.ApplyConfidenceFilter ? raw.Where(f => f.Confidence <= minimumConfidence) : raw;
            resultsByRuleId[rule.Id] = [.. filtered.OrderBy(f => f, comparer)];
        }

        return new RuleRunResult(resultsByRuleId, crashes);
    }

    private static List<IFinding> WithComparer(IPerFileRule rule, List<IFinding> findings, out IComparer<IFinding> comparer)
    {
        comparer = rule.Comparer ?? DefaultLocationComparer.Instance;
        return findings;
    }

    private static Dictionary<string, List<IFinding>> RunPerFileRules(
        IReadOnlyList<IPerFileRule> rules,
        IReadOnlyList<SqlParseResult> parseResults,
        RuleContext context,
        IScanProgress progress,
        List<SkippedConstruct> crashes)
    {
        var findingsByRuleId = new Dictionary<string, List<IFinding>>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            findingsByRuleId[rule.Id] = [];
        }

        var stateByRule = new Dictionary<IPerFileRule, object?>();
        var preparedRules = new List<IPerFileRule>();
        foreach (var rule in rules)
        {
            try
            {
                stateByRule[rule] = rule.Prepare(context);
                preparedRules.Add(rule);
            }
            catch (Exception ex)
            {
                crashes.Add(new SkippedConstruct(AnalysisPass.Predicates, string.Empty, 0, 0, RuleCrashKind, $"{rule.Id}: {ex.Message}"));
            }
        }

        var stages = preparedRules.ToDictionary(rule => rule, rule => progress.Begin(rule.Id, parseResults.Count));
        try
        {
            var perFileResults = parseResults
                .AsParallel()
                .Select(parseResult => ScanOneFile(preparedRules, parseResult, context, stateByRule, stages, crashes))
                .ToList();

            foreach (var perFile in perFileResults)
            {
                foreach (var (ruleId, findings) in perFile)
                {
                    findingsByRuleId[ruleId].AddRange(findings);
                }
            }
        }
        finally
        {
            foreach (var stage in stages.Values)
            {
                stage.Dispose();
            }
        }

        foreach (var rule in preparedRules)
        {
            try
            {
                findingsByRuleId[rule.Id].AddRange(rule.ScanCatalogOnce(context));
            }
            catch (Exception ex)
            {
                crashes.Add(new SkippedConstruct(AnalysisPass.Predicates, string.Empty, 0, 0, RuleCrashKind, $"{rule.Id}: {ex.Message}"));
            }
        }

        return findingsByRuleId;
    }

    private static List<(string RuleId, IReadOnlyList<IFinding> Findings)> ScanOneFile(
        List<IPerFileRule> rules,
        SqlParseResult parseResult,
        RuleContext context,
        Dictionary<IPerFileRule, object?> stateByRule,
        Dictionary<IPerFileRule, IScanStage> stages,
        List<SkippedConstruct> crashes)
    {
        var results = new List<(string, IReadOnlyList<IFinding>)>(rules.Count);
        var moduleRules = new List<IModuleRule>();
        var moduleRuleOwner = new Dictionary<IModuleRule, IPerFileRule>();

        foreach (var rule in rules)
        {
            stages[rule].Advance(currentItem: parseResult.SourcePath);

            IModuleRule? moduleRule;
            try
            {
                moduleRule = rule.CreateModuleRule(parseResult, context, stateByRule[rule]);
            }
            catch (Exception ex)
            {
                RecordCrash(crashes, rule.Id, parseResult.SourcePath, ex);
                results.Add((rule.Id, []));
                continue;
            }

            if (moduleRule is null)
            {
                IReadOnlyList<IFinding> legacyFindings;
                try
                {
                    legacyFindings = rule.Scan(parseResult, context, stateByRule[rule]);
                }
                catch (Exception ex)
                {
                    RecordCrash(crashes, rule.Id, parseResult.SourcePath, ex);
                    legacyFindings = [];
                }

                results.Add((rule.Id, legacyFindings));
                continue;
            }

            moduleRules.Add(moduleRule);
            moduleRuleOwner[moduleRule] = rule;
        }

        if (moduleRules.Count == 0)
        {
            return results;
        }

        var walker = new ModuleWalker(
            parseResult.SourcePath, context.Catalog, EmptyResolvedViews, ledger: null,
            currentProcScope: null, callerScopeByCalleeScope: null, rules: moduleRules);
        parseResult.Fragment.Accept(walker);

        foreach (var moduleRule in moduleRules)
        {
            var rule = moduleRuleOwner[moduleRule];
            stages[rule].Advance(currentItem: parseResult.SourcePath);

            if (walker.CrashedRules.TryGetValue(moduleRule, out var crashException))
            {
                RecordCrash(crashes, rule.Id, parseResult.SourcePath, crashException);
                results.Add((rule.Id, []));
                continue;
            }

            IReadOnlyList<IFinding> harvested;
            try
            {
                harvested = rule.HarvestFindings(parseResult, context, stateByRule[rule], moduleRule);
            }
            catch (Exception ex)
            {
                RecordCrash(crashes, rule.Id, parseResult.SourcePath, ex);
                harvested = [];
            }

            results.Add((rule.Id, harvested));
        }

        return results;
    }

    private static void RecordCrash(List<SkippedConstruct> crashes, string ruleId, string sourcePath, Exception ex)
    {
        lock (crashes)
        {
            crashes.Add(new SkippedConstruct(AnalysisPass.Predicates, sourcePath, 0, 0, RuleCrashKind, $"{ruleId}: {ex.Message}"));
        }
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

    private static Dictionary<string, IReadOnlyList<IFinding>> RunCrossModuleRules(
        IReadOnlyList<ICrossModuleRule> rules,
        IReadOnlyList<SqlParseResult> parseResults,
        RuleContext context,
        List<SkippedConstruct> crashes)
    {
        var moduleRulesByRule = rules.ToDictionary(rule => rule, _ => new List<IModuleRule>());

        var perFileResults = parseResults
            .AsParallel()
            .Select(parseResult => ScanOneFileForCrossModuleRules(rules, parseResult, context, crashes))
            .ToList();

        foreach (var perFile in perFileResults)
        {
            foreach (var (rule, moduleRule) in perFile)
            {
                moduleRulesByRule[rule].Add(moduleRule);
            }
        }

        var findingsByRuleId = new Dictionary<string, IReadOnlyList<IFinding>>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            var moduleRules = moduleRulesByRule[rule];
            try
            {
                findingsByRuleId[rule.Id] = moduleRules.Count > 0
                    ? rule.Aggregate(context, moduleRules)
                    : rule.Scan(parseResults, context);
            }
            catch (Exception ex)
            {
                crashes.Add(new SkippedConstruct(AnalysisPass.Predicates, string.Empty, 0, 0, RuleCrashKind, $"{rule.Id}: {ex.Message}"));
                findingsByRuleId[rule.Id] = [];
            }
        }

        return findingsByRuleId;
    }

    private static List<(ICrossModuleRule Rule, IModuleRule ModuleRule)> ScanOneFileForCrossModuleRules(
        IReadOnlyList<ICrossModuleRule> rules,
        SqlParseResult parseResult,
        RuleContext context,
        List<SkippedConstruct> crashes)
    {
        var moduleRules = new List<IModuleRule>();
        var moduleRuleOwner = new Dictionary<IModuleRule, ICrossModuleRule>();

        foreach (var rule in rules)
        {
            IModuleRule? moduleRule;
            try
            {
                moduleRule = rule.CreateModuleRule(parseResult, context);
            }
            catch (Exception ex)
            {
                RecordCrash(crashes, rule.Id, parseResult.SourcePath, ex);
                continue;
            }

            if (moduleRule is null)
            {
                continue;
            }

            moduleRules.Add(moduleRule);
            moduleRuleOwner[moduleRule] = rule;
        }

        var results = new List<(ICrossModuleRule, IModuleRule)>(moduleRules.Count);
        if (moduleRules.Count == 0)
        {
            return results;
        }

        var walker = new ModuleWalker(
            parseResult.SourcePath, context.Catalog, EmptyResolvedViews, ledger: null,
            currentProcScope: null, callerScopeByCalleeScope: null, rules: moduleRules);
        parseResult.Fragment.Accept(walker);

        foreach (var moduleRule in moduleRules)
        {
            var rule = moduleRuleOwner[moduleRule];
            if (walker.CrashedRules.TryGetValue(moduleRule, out var crashException))
            {
                RecordCrash(crashes, rule.Id, parseResult.SourcePath, crashException);
                continue;
            }

            results.Add((rule, moduleRule));
        }

        return results;
    }
}
