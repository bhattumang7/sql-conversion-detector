using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Reporting;

namespace SilentScan.Core.Predicates;

/// <summary>
/// CLAUDE.md's dynamic SQL policy: reparses the provably-constant inner SQL of
/// EXEC('...')/sp_executesql N'...' call sites (see <see cref="DynamicSqlScanner"/>) through the
/// normal catalog/lineage/predicate pipeline, then remaps every finding it produces back to
/// where that piece of text actually lives in the original source file - not the call site's
/// line, which for a multi-line folded string would make the finding's location useless.
/// Recurses into dynamic SQL found *inside* a reparsed script (nesting), up to
/// <see cref="MaxNestingDepth"/> levels deep; beyond that, remaining candidates are reported
/// unanalyzable with a specific reason rather than silently dropped.
/// </summary>
public static class DynamicSqlPipeline
{
    /// <summary>
    /// Real-world dynamic SQL nesting rarely exceeds one or two levels; this is a backstop
    /// against runaway/adversarial input, not a tuned-for-recall limit.
    /// </summary>
    private const int MaxNestingDepth = 5;

    private static readonly IReadOnlyDictionary<string, SqlType?> NoDeclaredParameters = new Dictionary<string, SqlType?>();

    public static DynamicSqlPipelineResult Analyze(IReadOnlyList<DynamicSqlScript> scripts, DatabaseCatalog catalog, LineageCatalog lineage) =>
        Analyze(scripts, catalog, lineage, depth: 1, seeds: null);

    /// <summary>
    /// <paramref name="seeds"/> supplies, for a nested script whose own declared-parameter text
    /// can't type one of its parameters, the enclosing script's type for that same parameter -
    /// but ONLY for a parameter this exact script bound to a bare variable reference of the
    /// enclosing script's own declared parameter (<see cref="DynamicSqlScript.ArgumentBindings"/>).
    /// Never a blanket name-scope match: dynamic SQL runs in a fresh variable scope, so guessing
    /// from name alone risks a false ScanForced from an unrelated same-named variable.
    /// </summary>
    private static DynamicSqlPipelineResult Analyze(
        IReadOnlyList<DynamicSqlScript> scripts,
        DatabaseCatalog catalog,
        LineageCatalog lineage,
        int depth,
        Dictionary<DynamicSqlScript, IReadOnlyDictionary<string, SqlType?>>? seeds)
    {
        var accumulator = new ResultAccumulator();

        // Branch-fold coverage (roadmap "trace dynamic SQL across IF/ELSE/TRY-CATCH branches")
        // can turn ONE call site into several DynamicSqlScripts, one per possible constant
        // assembly - grouping by CallSite (already identical across every assembly of the same
        // site, and scripts already arrive call-site-contiguous from DynamicSqlScanner's own
        // visitation order, so this never reorders anything observably) lets each call site's
        // own substantive findings dedupe against EACH OTHER before joining the overall result,
        // without ever merging two genuinely different call sites' findings together.
        foreach (var group in scripts.GroupBy(s => s.CallSite))
        {
            var perCallSite = new ResultAccumulator();
            foreach (var script in group)
            {
                ProcessScript(script, catalog, lineage, depth, seeds, perCallSite);
            }

            accumulator.Findings.AddRange(perCallSite.Findings);
            accumulator.Skipped.AddRange(perCallSite.Skipped);
            accumulator.Tier1.AddRange(DedupeTier1(PreferBestConfidencePerKey(perCallSite.Tier1, Tier1Key, f => f.Confidence)));
            accumulator.Typed.AddRange(TypedFindingDeduplicator.Dedupe(PreferBestConfidencePerKey(perCallSite.Typed, TypedKey, f => f.Confidence)));
            accumulator.ExpressionDerived.AddRange(DedupeExpressionDerived(PreferBestConfidencePerKey(perCallSite.ExpressionDerived, ExpressionDerivedKey, f => f.Confidence)));
            accumulator.CollationConflicts.AddRange(DedupeCollationConflicts(PreferBestConfidencePerKey(perCallSite.CollationConflicts, CollationConflictKey, f => f.Confidence)));
            accumulator.WriteLoss.AddRange(DedupeWriteLoss(PreferBestConfidencePerKey(perCallSite.WriteLoss, WriteLossKey, f => f.Confidence)));
        }

        return accumulator.ToResult();
    }

    /// <summary>
    /// A syntactic Tier-1 finding's identity, position-independent (unlike <see
    /// cref="SargabilityFinding.SourcePath"/>/<see cref="SargabilityFinding.Line"/>/<see
    /// cref="SargabilityFinding.Column"/>, which legitimately differ across two assemblies of the
    /// same call site whenever an earlier branch's appended text shifts everything after it) -
    /// the same defect (e.g. <c>UPPER(Code)</c> on the same table) surfacing in more than one
    /// assembly is one finding, not one per assembly.
    /// </summary>
    private static List<SargabilityFinding> DedupeTier1(List<SargabilityFinding> findings)
    {
        var seen = new HashSet<(SargabilityFindingKind Kind, string ColumnName, string? Detail, string? TableQualifiedName)>();
        return findings.Where(finding => seen.Add(Tier1Key(finding))).ToList();
    }

    private static (SargabilityFindingKind Kind, string ColumnName, string? Detail, string? TableQualifiedName) Tier1Key(SargabilityFinding finding) =>
        (finding.Kind, finding.ColumnName, finding.Detail, finding.TableQualifiedName);

    private static string TypedKey(TypedPredicateFinding finding) =>
        TypedPredicateFindingIdentity.ComputeKey(finding.Column, finding.OtherOperand, finding.Operator);

    /// <summary>
    /// Keys on <see cref="TransformationSite.Description"/> only, never its own SourcePath/Line -
    /// those describe where the CAST/CONVERT layer lives in the ORIGINAL file, which is identical
    /// across every assembly of the same call site regardless of which assembly produced the
    /// finding, so including them would never actually cause a false collapse - but the finding's
    /// own SourcePath/Line/ColumnPosition (excluded here entirely) DO legitimately differ per
    /// assembly, which is the reason this key exists at all.
    /// </summary>
    private static List<ExpressionDerivedFinding> DedupeExpressionDerived(List<ExpressionDerivedFinding> findings)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return findings.Where(finding => seen.Add(ExpressionDerivedKey(finding))).ToList();
    }

    private static string ExpressionDerivedKey(ExpressionDerivedFinding finding) => string.Join(
        '\u0001',
        finding.ColumnName,
        string.Join(',', finding.TransformationChain.Select(t => t.Description)),
        string.Join(',', finding.UnderlyingBaseColumns.Select(b => $"{b.TableQualifiedName}.{b.ColumnName}:{b.Indexed}")));

    private static List<CollationConflictFinding> DedupeCollationConflicts(List<CollationConflictFinding> findings)
    {
        var seen = new HashSet<(string, string, string, string, string, string, string)>();
        return findings.Where(finding => seen.Add(CollationConflictKey(finding))).ToList();
    }

    private static (string, string, string, string, string, string, string) CollationConflictKey(CollationConflictFinding finding) => (
        finding.FirstTableQualifiedName, finding.FirstColumnName, finding.FirstCollationName,
        finding.SecondTableQualifiedName, finding.SecondColumnName, finding.SecondCollationName, finding.Operator);

    private static List<WriteLossFinding> DedupeWriteLoss(List<WriteLossFinding> findings)
    {
        var seen = new HashSet<(string, string, WriteLossKind, SqlType, SqlType)>();
        return findings.Where(finding => seen.Add(WriteLossKey(finding))).ToList();
    }

    private static (string, string, WriteLossKind, SqlType, SqlType) WriteLossKey(WriteLossFinding finding) =>
        (finding.TableQualifiedName, finding.ColumnName, finding.Kind, finding.TargetType, finding.SourceType);

    /// <summary>
    /// Reorders <paramref name="findings"/> so that, within any set sharing the same
    /// <paramref name="key"/>, the BEST (numerically lowest) <see cref="FindingConfidence"/> sorts
    /// first - every Dedupe* helper above keeps the first occurrence per key, so this makes "the
    /// same defect proven at High on one assembly and Medium on another survives as High" a
    /// property of ordering rather than requiring each Dedupe* to grow its own confidence-aware
    /// merge logic. <see cref="Enumerable.GroupBy{TSource,TKey}(IEnumerable{TSource},Func{TSource,TKey})"/>
    /// and <see cref="Enumerable.OrderBy{TSource,TKey}(IEnumerable{TSource},Func{TSource,TKey})"/>
    /// are both stable, so a caller whose findings are all the same confidence (every caller
    /// today) gets byte-identical order.
    /// </summary>
    private static List<T> PreferBestConfidencePerKey<T, TKey>(List<T> findings, Func<T, TKey> key, Func<T, FindingConfidence> confidence)
        where TKey : notnull =>
        [.. findings.GroupBy(key).SelectMany(group => group.OrderBy(confidence))];

    /// <summary>Mutable accumulator for one <see cref="ProcessScript"/> loop's worth of findings - a plain field bag rather than growing the caller's own local-variable count, which is most of what was driving its cognitive complexity over the line.</summary>
    private sealed class ResultAccumulator
    {
        public List<DynamicSqlFinding> Findings { get; } = [];

        public List<SargabilityFinding> Tier1 { get; } = [];

        public List<TypedPredicateFinding> Typed { get; } = [];

        public List<ExpressionDerivedFinding> ExpressionDerived { get; } = [];

        public List<CollationConflictFinding> CollationConflicts { get; } = [];

        public List<WriteLossFinding> WriteLoss { get; } = [];

        public List<SkippedConstruct> Skipped { get; } = [];

        public DynamicSqlPipelineResult ToResult() =>
            new(Findings, Tier1, Typed, ExpressionDerived, CollationConflicts, WriteLoss, Skipped);
    }

    private static void ProcessScript(
        DynamicSqlScript script,
        DatabaseCatalog catalog,
        LineageCatalog lineage,
        int depth,
        Dictionary<DynamicSqlScript, IReadOnlyDictionary<string, SqlType?>>? seeds,
        ResultAccumulator accumulator)
    {
        var virtualPath = $"{script.CallSite.SourcePath}::dynamic-sql@{script.CallSite.Line}";
        var innerParseResult = SqlScriptParser.ParseText(virtualPath, script.InnerText);

        if (innerParseResult.HasErrors)
        {
            var reason = innerParseResult.Errors[0].Message;
            accumulator.Findings.Add(new DynamicSqlFinding(
                script.CallSite.SourcePath, script.CallSite.Line, script.CallSite.Column, DynamicSqlOutcome.InnerParseFailed, reason));
            return;
        }

        accumulator.Findings.Add(new DynamicSqlFinding(
            script.CallSite.SourcePath, script.CallSite.Line, script.CallSite.Column, DynamicSqlOutcome.AnalyzedLiteral, Reason: null));

        var tier1Ledger = new SkipLedger();
        foreach (var tier1Finding in NonSargablePredicateScanner.Scan(innerParseResult, catalog, lineage, script.Scope, tier1Ledger))
        {
            accumulator.Tier1.Add(Remap(tier1Finding, script));
        }

        foreach (var tier1Skipped in tier1Ledger.Entries)
        {
            accumulator.Skipped.Add(Remap(tier1Skipped, script));
        }

        var ownDeclaredParameters = script.ParameterDeclarationText is { } declarationText
            ? DynamicSqlParameterDeclarations.TryParse(declarationText, catalog.TypeAliases) ?? NoDeclaredParameters
            : NoDeclaredParameters;
        var declaredParameters = seeds is not null && seeds.TryGetValue(script, out var seed)
            ? MergeSeededParameters(ownDeclaredParameters, seed)
            : ownDeclaredParameters;
        var extraction = TypedPredicateExtractor.Extract(innerParseResult, catalog, lineage, declaredParameters, script.Scope);
        foreach (var typedFinding in extraction.TypedFindings)
        {
            accumulator.Typed.Add(Remap(typedFinding, script));
        }

        foreach (var expressionFinding in extraction.ExpressionDerivedFindings)
        {
            accumulator.ExpressionDerived.Add(Remap(expressionFinding, script));
        }

        foreach (var collationConflict in extraction.CollationConflictFindings)
        {
            accumulator.CollationConflicts.Add(Remap(collationConflict, script));
        }

        foreach (var writeLoss in extraction.WriteLossFindings)
        {
            accumulator.WriteLoss.Add(Remap(writeLoss, script));
        }

        foreach (var skippedConstruct in extraction.SkippedConstructs)
        {
            accumulator.Skipped.Add(Remap(skippedConstruct, script));
        }

        var nested = AnalyzeNested(innerParseResult, script, declaredParameters, catalog, lineage, depth);
        accumulator.Findings.AddRange(nested.Findings);
        accumulator.Tier1.AddRange(nested.Tier1Findings);
        accumulator.Typed.AddRange(nested.TypedFindings);
        accumulator.ExpressionDerived.AddRange(nested.ExpressionDerivedFindings);
        accumulator.CollationConflicts.AddRange(nested.CollationConflictFindings);
        accumulator.WriteLoss.AddRange(nested.WriteLossFindings);
        accumulator.Skipped.AddRange(nested.SkippedConstructs);
    }

    private static DynamicSqlPipelineResult AnalyzeNested(
        SqlParseResult innerParseResult,
        DynamicSqlScript script,
        IReadOnlyDictionary<string, SqlType?> outerDeclaredParameters,
        DatabaseCatalog catalog,
        LineageCatalog lineage,
        int depth)
    {
        // Propagates the outer script's own scope into the nested scanner - the reparsed inner
        // text has no CREATE PROCEDURE wrapper for it to discover the scope from itself, so
        // without this, propagation would silently die at nesting depth 2.
        var nestedExtraction = DynamicSqlScanner.Scan(innerParseResult, script.Scope);
        var findings = nestedExtraction.Findings.Select(f => RemapFinding(f, script)).ToList();

        if (nestedExtraction.AnalyzableScripts.Count == 0)
        {
            return new DynamicSqlPipelineResult(findings, [], [], [], [], [], []);
        }

        if (depth >= MaxNestingDepth)
        {
            // Never silently drop these (CLAUDE.md) - report exactly how far analysis got and
            // why it stopped, remapped to the real call site that would have been recursed into.
            findings.AddRange(nestedExtraction.AnalyzableScripts
                .Select(nestedScript => script.SegmentMap.Map(nestedScript.CallSite.Line, nestedScript.CallSite.Column))
                .Select(callSite => new DynamicSqlFinding(callSite.SourcePath, callSite.Line, callSite.Column, DynamicSqlOutcome.Unanalyzable, "max-nesting-depth-exceeded")));

            return new DynamicSqlPipelineResult(findings, [], [], [], [], [], []);
        }

        var seeds = BuildArgumentBindingSeeds(nestedExtraction.AnalyzableScripts, outerDeclaredParameters);
        var nestedResult = Analyze(nestedExtraction.AnalyzableScripts, catalog, lineage, depth + 1, seeds);
        findings.AddRange(nestedResult.Findings.Select(f => RemapFinding(f, script)));

        return new DynamicSqlPipelineResult(
            findings,
            [.. nestedResult.Tier1Findings.Select(f => RemapNested(f, script))],
            [.. nestedResult.TypedFindings.Select(f => RemapNested(f, script))],
            [.. nestedResult.ExpressionDerivedFindings.Select(f => RemapNested(f, script))],
            [.. nestedResult.CollationConflictFindings.Select(f => RemapNested(f, script))],
            [.. nestedResult.WriteLossFindings.Select(f => RemapNested(f, script))],
            [.. nestedResult.SkippedConstructs.Select(s => Remap(s, script))]);
    }

    /// <summary>
    /// For each nested script, seeds only the formal parameters it bound to a bare variable
    /// reference (<see cref="DynamicSqlScript.ArgumentBindings"/>) that matches, by name, one of
    /// the ENCLOSING script's own declared parameters - the one case CLAUDE.md's dynamic SQL
    /// policy allows an enclosing script's type to stand in for a nested one's, since it's an
    /// explicit value hand-off at the call site rather than a guess from name alone.
    /// </summary>
    private static Dictionary<DynamicSqlScript, IReadOnlyDictionary<string, SqlType?>>? BuildArgumentBindingSeeds(
        IReadOnlyList<DynamicSqlScript> nestedScripts, IReadOnlyDictionary<string, SqlType?> outerDeclaredParameters)
    {
        Dictionary<DynamicSqlScript, IReadOnlyDictionary<string, SqlType?>>? seeds = null;
        foreach (var nestedScript in nestedScripts)
        {
            if (nestedScript.ArgumentBindings is not { Count: > 0 } bindings)
            {
                continue;
            }

            Dictionary<string, SqlType?>? seed = null;
            foreach (var (formalName, boundVariableName) in bindings)
            {
                if (outerDeclaredParameters.TryGetValue(boundVariableName, out var outerType))
                {
                    seed ??= new Dictionary<string, SqlType?>(StringComparer.OrdinalIgnoreCase);
                    seed[formalName] = outerType;
                }
            }

            if (seed is not null)
            {
                seeds ??= [];
                seeds[nestedScript] = seed;
            }
        }

        return seeds;
    }

    /// <summary>
    /// The nested script's OWN declaration always wins when it resolved a concrete type - the
    /// seed only fills a parameter the nested declaration left missing or null.
    /// </summary>
    private static Dictionary<string, SqlType?> MergeSeededParameters(
        IReadOnlyDictionary<string, SqlType?> ownDeclaredParameters, IReadOnlyDictionary<string, SqlType?> seed)
    {
        var merged = new Dictionary<string, SqlType?>(ownDeclaredParameters, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, type) in seed)
        {
            if (!merged.TryGetValue(name, out var existing) || existing is null)
            {
                merged[name] = type;
            }
        }

        return merged;
    }

    private static DynamicSqlFinding RemapFinding(DynamicSqlFinding finding, DynamicSqlScript outerScript)
    {
        var span = outerScript.SegmentMap.Map(finding.Line, finding.Column);
        return finding with { SourcePath = span.SourcePath, Line = span.Line, Column = span.Column };
    }

    private static SourceSpan? RemapCallSite(SourceSpan? callSite, DynamicSqlScript outerScript) =>
        callSite is { } span ? outerScript.SegmentMap.Map(span.Line, span.Column) : null;

    /// <summary>The worse (numerically higher) of two <see cref="FindingConfidence"/> values - a finding nested inside a script that itself rested on an assumption is never MORE trustworthy than that assumption.</summary>
    private static FindingConfidence Worse(FindingConfidence a, FindingConfidence b) => (FindingConfidence)Math.Max((int)a, (int)b);

    private static SargabilityFinding Remap(SargabilityFinding finding, DynamicSqlScript script)
    {
        var span = script.SegmentMap.Map(finding.Line, finding.Column);
        return finding with { SourcePath = span.SourcePath, Line = span.Line, Column = span.Column, DynamicSqlCallSite = script.CallSite, Confidence = script.Confidence };
    }

    private static TypedPredicateFinding Remap(TypedPredicateFinding finding, DynamicSqlScript script)
    {
        var span = script.SegmentMap.Map(finding.Line, finding.ColumnPosition);
        return finding with { SourcePath = span.SourcePath, Line = span.Line, ColumnPosition = span.Column, DynamicSqlCallSite = script.CallSite, Confidence = script.Confidence };
    }

    private static ExpressionDerivedFinding Remap(ExpressionDerivedFinding finding, DynamicSqlScript script)
    {
        var span = script.SegmentMap.Map(finding.Line, finding.ColumnPosition);
        return finding with { SourcePath = span.SourcePath, Line = span.Line, ColumnPosition = span.Column, DynamicSqlCallSite = script.CallSite, Confidence = script.Confidence };
    }

    private static CollationConflictFinding Remap(CollationConflictFinding finding, DynamicSqlScript script)
    {
        var span = script.SegmentMap.Map(finding.Line, finding.ColumnPosition);
        return finding with { SourcePath = span.SourcePath, Line = span.Line, ColumnPosition = span.Column, DynamicSqlCallSite = script.CallSite, Confidence = script.Confidence };
    }

    private static WriteLossFinding Remap(WriteLossFinding finding, DynamicSqlScript script)
    {
        var span = script.SegmentMap.Map(finding.Line, finding.ColumnPosition);
        return finding with { SourcePath = span.SourcePath, Line = span.Line, ColumnPosition = span.Column, DynamicSqlCallSite = script.CallSite, Confidence = script.Confidence };
    }

    private static SkippedConstruct Remap(SkippedConstruct entry, DynamicSqlScript script)
    {
        var span = script.SegmentMap.Map(entry.Line, entry.Column);
        return entry with { SourcePath = span.SourcePath, Line = span.Line, Column = span.Column };
    }

    /// <summary>
    /// A finding produced from a nested script already has its own SourcePath/Line/Column and
    /// DynamicSqlCallSite - but expressed in the coordinates of the *outer* script's reparsed
    /// text (that's what the nested <see cref="DynamicSqlScanner"/> was actually parsing).
    /// One more hop through <paramref name="outerScript"/>'s segment map resolves both to real
    /// source coordinates, chaining however many nesting levels deep this finding came from.
    /// </summary>
    private static SargabilityFinding RemapNested(SargabilityFinding finding, DynamicSqlScript outerScript)
    {
        var span = outerScript.SegmentMap.Map(finding.Line, finding.Column);
        return finding with { SourcePath = span.SourcePath, Line = span.Line, Column = span.Column, DynamicSqlCallSite = RemapCallSite(finding.DynamicSqlCallSite, outerScript), Confidence = Worse(finding.Confidence, outerScript.Confidence) };
    }

    private static TypedPredicateFinding RemapNested(TypedPredicateFinding finding, DynamicSqlScript outerScript)
    {
        var span = outerScript.SegmentMap.Map(finding.Line, finding.ColumnPosition);
        return finding with { SourcePath = span.SourcePath, Line = span.Line, ColumnPosition = span.Column, DynamicSqlCallSite = RemapCallSite(finding.DynamicSqlCallSite, outerScript), Confidence = Worse(finding.Confidence, outerScript.Confidence) };
    }

    private static ExpressionDerivedFinding RemapNested(ExpressionDerivedFinding finding, DynamicSqlScript outerScript)
    {
        var span = outerScript.SegmentMap.Map(finding.Line, finding.ColumnPosition);
        return finding with { SourcePath = span.SourcePath, Line = span.Line, ColumnPosition = span.Column, DynamicSqlCallSite = RemapCallSite(finding.DynamicSqlCallSite, outerScript), Confidence = Worse(finding.Confidence, outerScript.Confidence) };
    }

    private static CollationConflictFinding RemapNested(CollationConflictFinding finding, DynamicSqlScript outerScript)
    {
        var span = outerScript.SegmentMap.Map(finding.Line, finding.ColumnPosition);
        return finding with { SourcePath = span.SourcePath, Line = span.Line, ColumnPosition = span.Column, DynamicSqlCallSite = RemapCallSite(finding.DynamicSqlCallSite, outerScript), Confidence = Worse(finding.Confidence, outerScript.Confidence) };
    }

    private static WriteLossFinding RemapNested(WriteLossFinding finding, DynamicSqlScript outerScript)
    {
        var span = outerScript.SegmentMap.Map(finding.Line, finding.ColumnPosition);
        return finding with { SourcePath = span.SourcePath, Line = span.Line, ColumnPosition = span.Column, DynamicSqlCallSite = RemapCallSite(finding.DynamicSqlCallSite, outerScript), Confidence = Worse(finding.Confidence, outerScript.Confidence) };
    }
}

/// <summary>Findings produced by reparsing and analyzing the dynamic SQL scripts of one scan, including any found nested inside them.</summary>
public sealed record DynamicSqlPipelineResult(
    IReadOnlyList<DynamicSqlFinding> Findings,
    IReadOnlyList<SargabilityFinding> Tier1Findings,
    IReadOnlyList<TypedPredicateFinding> TypedFindings,
    IReadOnlyList<ExpressionDerivedFinding> ExpressionDerivedFindings,
    IReadOnlyList<CollationConflictFinding> CollationConflictFindings,
    IReadOnlyList<WriteLossFinding> WriteLossFindings,
    IReadOnlyList<SkippedConstruct> SkippedConstructs);
