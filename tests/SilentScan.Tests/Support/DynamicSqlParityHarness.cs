using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Predicates.DynamicSqlValue;

namespace SilentScan.Tests.Support;

/// <summary>
/// Runs the same input through <see cref="DynamicSqlScanner.Scan"/> (old) and
/// <see cref="DynamicSqlScannerV2.Scan"/> (new) and reports every difference -
/// docs/dynamic-sql-rebuild-plan.md Phase 3's exit gate. Divergence policy: V2 must be equal or
/// STRICTLY BETTER per scenario. Allowed: declined-in-old -> analyzed-in-new (or Medium instead
/// of a decline), a Tainted-equivalent becoming a typed hole. Forbidden: analyzed-in-old ->
/// declined-in-new, any High-confidence result in new that wasn't ALSO High in old. This harness
/// only DETECTS and REPORTS differences - callers decide, scenario by scenario, whether an
/// observed one is an accepted improvement or a regression (see docs/dynamic-sql-rebuild-plan.md
/// Phase 3 §"Divergence policy").
/// </summary>
public static class DynamicSqlParityHarness
{
    public sealed record ParityReport(
        IReadOnlyList<string> ScriptsOnlyInOld,
        IReadOnlyList<string> ScriptsOnlyInNew,
        IReadOnlyList<string> FindingsOnlyInOld,
        IReadOnlyList<string> FindingsOnlyInNew,
        IReadOnlyList<string> OutputSummariesOnlyInOld,
        IReadOnlyList<string> OutputSummariesOnlyInNew)
    {
        public bool IsIdentical =>
            ScriptsOnlyInOld.Count == 0 && ScriptsOnlyInNew.Count == 0
            && FindingsOnlyInOld.Count == 0 && FindingsOnlyInNew.Count == 0
            && OutputSummariesOnlyInOld.Count == 0 && OutputSummariesOnlyInNew.Count == 0;

        /// <summary>True under the divergence policy: every difference is old-declined/new-analyzed (or a confidence downgrade removed), never the reverse, and no NEW High-confidence script that wasn't already High in old.</summary>
        public bool IsAcceptableUnderPolicy(out string violation)
        {
            if (ScriptsOnlyInOld.Count > 0)
            {
                violation = $"old analyzed a script new did not: {ScriptsOnlyInOld[0]}";
                return false;
            }

            if (OutputSummariesOnlyInOld.Count > 0)
            {
                violation = $"old proved an OUTPUT summary new did not: {OutputSummariesOnlyInOld[0]}";
                return false;
            }

            violation = string.Empty;
            return true;
        }
    }

    public static ParityReport Compare(
        SqlParseResult parseResult,
        DynamicSqlScope? scope = null,
        ProcCallGraph? callGraph = null,
        IReadOnlyDictionary<(string, string), IReadOnlyList<string>>? outputSummaryIndex = null,
        DatabaseCatalog? catalog = null)
    {
        var old = DynamicSqlScanner.Scan(parseResult, scope, callGraph, outputSummaryIndex, catalog);
        var @new = DynamicSqlScannerV2.Scan(parseResult, scope, callGraph, outputSummaryIndex, catalog);

        var oldScripts = Multiset(old.AnalyzableScripts, ScriptKey);
        var newScripts = Multiset(@new.AnalyzableScripts, ScriptKey);
        var oldFindings = Multiset(old.Findings, FindingKey);
        var newFindings = Multiset(@new.Findings, FindingKey);
        var oldSummaries = Multiset(old.OutputSummaries, SummaryKey);
        var newSummaries = Multiset(@new.OutputSummaries, SummaryKey);

        return new ParityReport(
            OnlyIn(oldScripts, newScripts), OnlyIn(newScripts, oldScripts),
            OnlyIn(oldFindings, newFindings), OnlyIn(newFindings, oldFindings),
            OnlyIn(oldSummaries, newSummaries), OnlyIn(newSummaries, oldSummaries));
    }

    private static string ScriptKey(DynamicSqlScript s) =>
        $"{s.CallSite.SourcePath}:{s.CallSite.Line}:{s.CallSite.Column} text=[{s.InnerText}] conf={s.Confidence} paramDecl=[{s.ParameterDeclarationText}] scope=[{s.Scope.ProcScope}]";

    private static string FindingKey(DynamicSqlFinding f) =>
        $"{f.SourcePath}:{f.Line}:{f.Column} outcome={f.Outcome} reason=[{f.Reason}]";

    private static string SummaryKey(ProcedureOutputSummary s) =>
        $"{s.QualifiedName}.{s.ParameterName}=[{string.Join(',', s.PossibleValues.OrderBy(v => v, StringComparer.Ordinal))}]";

    private static Dictionary<string, int> Multiset<T>(IReadOnlyList<T> items, Func<T, string> key)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var k = key(item);
            counts[k] = counts.GetValueOrDefault(k) + 1;
        }

        return counts;
    }

    private static List<string> OnlyIn(Dictionary<string, int> a, Dictionary<string, int> b)
    {
        var result = new List<string>();
        foreach (var (key, countA) in a)
        {
            var countB = b.GetValueOrDefault(key);
            if (countA > countB)
            {
                result.Add(countA - countB == 1 ? key : $"x{countA - countB}: {key}");
            }
        }

        return result;
    }
}
