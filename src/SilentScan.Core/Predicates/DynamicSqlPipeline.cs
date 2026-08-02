using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

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
        var findings = new List<DynamicSqlFinding>();
        var tier1 = new List<SargabilityFinding>();
        var typed = new List<TypedPredicateFinding>();
        var expressionDerived = new List<ExpressionDerivedFinding>();
        var collationConflicts = new List<CollationConflictFinding>();
        var skipped = new List<SkippedConstruct>();

        foreach (var script in scripts)
        {
            var virtualPath = $"{script.CallSite.SourcePath}::dynamic-sql@{script.CallSite.Line}";
            var innerParseResult = SqlScriptParser.ParseText(virtualPath, script.InnerText);

            if (innerParseResult.HasErrors)
            {
                var reason = innerParseResult.Errors[0].Message;
                findings.Add(new DynamicSqlFinding(
                    script.CallSite.SourcePath, script.CallSite.Line, script.CallSite.Column, DynamicSqlOutcome.InnerParseFailed, reason));
                continue;
            }

            findings.Add(new DynamicSqlFinding(
                script.CallSite.SourcePath, script.CallSite.Line, script.CallSite.Column, DynamicSqlOutcome.AnalyzedLiteral, Reason: null));

            foreach (var tier1Finding in NonSargablePredicateScanner.Scan(innerParseResult, catalog, lineage, script.Scope))
            {
                tier1.Add(Remap(tier1Finding, script));
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
                typed.Add(Remap(typedFinding, script));
            }

            foreach (var expressionFinding in extraction.ExpressionDerivedFindings)
            {
                expressionDerived.Add(Remap(expressionFinding, script));
            }

            foreach (var collationConflict in extraction.CollationConflictFindings)
            {
                collationConflicts.Add(Remap(collationConflict, script));
            }

            foreach (var skippedConstruct in extraction.SkippedConstructs)
            {
                skipped.Add(Remap(skippedConstruct, script));
            }

            var nested = AnalyzeNested(innerParseResult, script, declaredParameters, catalog, lineage, depth);
            findings.AddRange(nested.Findings);
            tier1.AddRange(nested.Tier1Findings);
            typed.AddRange(nested.TypedFindings);
            expressionDerived.AddRange(nested.ExpressionDerivedFindings);
            collationConflicts.AddRange(nested.CollationConflictFindings);
            skipped.AddRange(nested.SkippedConstructs);
        }

        return new DynamicSqlPipelineResult(findings, tier1, typed, expressionDerived, collationConflicts, skipped);
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
            return new DynamicSqlPipelineResult(findings, [], [], [], [], []);
        }

        if (depth >= MaxNestingDepth)
        {
            // Never silently drop these (CLAUDE.md) - report exactly how far analysis got and
            // why it stopped, remapped to the real call site that would have been recursed into.
            findings.AddRange(nestedExtraction.AnalyzableScripts
                .Select(nestedScript => script.SegmentMap.Map(nestedScript.CallSite.Line, nestedScript.CallSite.Column))
                .Select(callSite => new DynamicSqlFinding(callSite.SourcePath, callSite.Line, callSite.Column, DynamicSqlOutcome.Unanalyzable, "max-nesting-depth-exceeded")));

            return new DynamicSqlPipelineResult(findings, [], [], [], [], []);
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
                seeds ??= new Dictionary<DynamicSqlScript, IReadOnlyDictionary<string, SqlType?>>();
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

    private static SargabilityFinding Remap(SargabilityFinding finding, DynamicSqlScript script)
    {
        var span = script.SegmentMap.Map(finding.Line, finding.Column);
        return finding with { SourcePath = span.SourcePath, Line = span.Line, Column = span.Column, DynamicSqlCallSite = script.CallSite };
    }

    private static TypedPredicateFinding Remap(TypedPredicateFinding finding, DynamicSqlScript script)
    {
        var span = script.SegmentMap.Map(finding.Line, finding.ColumnPosition);
        return finding with { SourcePath = span.SourcePath, Line = span.Line, ColumnPosition = span.Column, DynamicSqlCallSite = script.CallSite };
    }

    private static ExpressionDerivedFinding Remap(ExpressionDerivedFinding finding, DynamicSqlScript script)
    {
        var span = script.SegmentMap.Map(finding.Line, finding.ColumnPosition);
        return finding with { SourcePath = span.SourcePath, Line = span.Line, ColumnPosition = span.Column, DynamicSqlCallSite = script.CallSite };
    }

    private static CollationConflictFinding Remap(CollationConflictFinding finding, DynamicSqlScript script)
    {
        var span = script.SegmentMap.Map(finding.Line, finding.ColumnPosition);
        return finding with { SourcePath = span.SourcePath, Line = span.Line, ColumnPosition = span.Column, DynamicSqlCallSite = script.CallSite };
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
        return finding with { SourcePath = span.SourcePath, Line = span.Line, Column = span.Column, DynamicSqlCallSite = RemapCallSite(finding.DynamicSqlCallSite, outerScript) };
    }

    private static TypedPredicateFinding RemapNested(TypedPredicateFinding finding, DynamicSqlScript outerScript)
    {
        var span = outerScript.SegmentMap.Map(finding.Line, finding.ColumnPosition);
        return finding with { SourcePath = span.SourcePath, Line = span.Line, ColumnPosition = span.Column, DynamicSqlCallSite = RemapCallSite(finding.DynamicSqlCallSite, outerScript) };
    }

    private static ExpressionDerivedFinding RemapNested(ExpressionDerivedFinding finding, DynamicSqlScript outerScript)
    {
        var span = outerScript.SegmentMap.Map(finding.Line, finding.ColumnPosition);
        return finding with { SourcePath = span.SourcePath, Line = span.Line, ColumnPosition = span.Column, DynamicSqlCallSite = RemapCallSite(finding.DynamicSqlCallSite, outerScript) };
    }

    private static CollationConflictFinding RemapNested(CollationConflictFinding finding, DynamicSqlScript outerScript)
    {
        var span = outerScript.SegmentMap.Map(finding.Line, finding.ColumnPosition);
        return finding with { SourcePath = span.SourcePath, Line = span.Line, ColumnPosition = span.Column, DynamicSqlCallSite = RemapCallSite(finding.DynamicSqlCallSite, outerScript) };
    }
}

/// <summary>Findings produced by reparsing and analyzing the dynamic SQL scripts of one scan, including any found nested inside them.</summary>
public sealed record DynamicSqlPipelineResult(
    IReadOnlyList<DynamicSqlFinding> Findings,
    IReadOnlyList<SargabilityFinding> Tier1Findings,
    IReadOnlyList<TypedPredicateFinding> TypedFindings,
    IReadOnlyList<ExpressionDerivedFinding> ExpressionDerivedFindings,
    IReadOnlyList<CollationConflictFinding> CollationConflictFindings,
    IReadOnlyList<SkippedConstruct> SkippedConstructs);
