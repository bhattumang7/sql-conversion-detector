using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates.DynamicSqlValue;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

public static partial class DynamicSqlPipeline
{
private const int MaxNestingDepth = 5;

    private static readonly IReadOnlyDictionary<string, SqlType?> NoDeclaredParameters = new Dictionary<string, SqlType?>();

[GeneratedRegex(@"\$[A-Za-z_][A-Za-z0-9_]*\$")]
    private static partial Regex TemplatePlaceholderRegex();

    private static readonly IReadOnlyDictionary<string, TvfFenceOrigin> NoTvfFenceMap = new Dictionary<string, TvfFenceOrigin>();

    private static readonly IReadOnlyDictionary<string, ScalarUdfOrigin> NoScalarUdfMap = new Dictionary<string, ScalarUdfOrigin>();

private readonly record struct PipelineContext(
        DatabaseCatalog Catalog,
        LineageCatalog Lineage,
        IReadOnlyDictionary<string, TvfFenceOrigin> TvfFenceMap,
        IReadOnlyDictionary<string, ScalarUdfOrigin> ScalarUdfMap,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? CallerScopeByCalleeScope);

    public static DynamicSqlPipelineResult Analyze(
        IReadOnlyList<DynamicSqlScript> scripts, DatabaseCatalog catalog, LineageCatalog lineage, IReadOnlyDictionary<string, IReadOnlyList<string>>? callerScopeByCalleeScope = null) =>
        Analyze(scripts, new PipelineContext(catalog, lineage, NoTvfFenceMap, NoScalarUdfMap, callerScopeByCalleeScope), depth: 1, seeds: null);

public static DynamicSqlPipelineResult Analyze(
        IReadOnlyList<DynamicSqlScript> scripts, DatabaseCatalog catalog, LineageCatalog lineage, IReadOnlyDictionary<string, TvfFenceOrigin> tvfFenceMap, IReadOnlyDictionary<string, IReadOnlyList<string>>? callerScopeByCalleeScope = null) =>
        Analyze(scripts, catalog, lineage, tvfFenceMap, NoScalarUdfMap, callerScopeByCalleeScope);

public static DynamicSqlPipelineResult Analyze(
        IReadOnlyList<DynamicSqlScript> scripts, DatabaseCatalog catalog, LineageCatalog lineage, IReadOnlyDictionary<string, TvfFenceOrigin> tvfFenceMap, IReadOnlyDictionary<string, ScalarUdfOrigin> scalarUdfMap, IReadOnlyDictionary<string, IReadOnlyList<string>>? callerScopeByCalleeScope = null) =>
        Analyze(scripts, new PipelineContext(catalog, lineage, tvfFenceMap, scalarUdfMap, callerScopeByCalleeScope), depth: 1, seeds: null);

private static DynamicSqlPipelineResult Analyze(
        IReadOnlyList<DynamicSqlScript> scripts,
        PipelineContext context,
        int depth,
        Dictionary<DynamicSqlScript, IReadOnlyDictionary<string, SqlType?>>? seeds)
    {
        var accumulator = new ResultAccumulator();

        foreach (var group in scripts.GroupBy(s => s.CallSite))
        {
            var perCallSite = new ResultAccumulator();
            foreach (var script in group)
            {
                ProcessScript(script, context, depth, seeds, perCallSite);
            }

            accumulator.Findings.AddRange(perCallSite.Findings);
            accumulator.Skipped.AddRange(perCallSite.Skipped);
            accumulator.Tier1.AddRange(DedupeTier1(PreferBestConfidencePerKey(perCallSite.Tier1, Tier1Key, f => f.Confidence)));
            accumulator.Typed.AddRange(TypedFindingDeduplicator.Dedupe(PreferBestConfidencePerKey(perCallSite.Typed, TypedKey, f => f.Confidence)));
            accumulator.ExpressionDerived.AddRange(DedupeExpressionDerived(PreferBestConfidencePerKey(perCallSite.ExpressionDerived, ExpressionDerivedKey, f => f.Confidence)));
            accumulator.CollationConflicts.AddRange(DedupeCollationConflicts(PreferBestConfidencePerKey(perCallSite.CollationConflicts, CollationConflictKey, f => f.Confidence)));
            accumulator.Unparameterized.AddRange(DedupeUnparameterized(PreferBestConfidencePerKey(perCallSite.Unparameterized, UnparameterizedKey, f => f.Confidence)));
            accumulator.WriteLoss.AddRange(DedupeWriteLoss(PreferBestConfidencePerKey(perCallSite.WriteLoss, WriteLossKey, f => f.Confidence)));
            accumulator.TvfFence.AddRange(DedupeTvfFence(PreferBestConfidencePerKey(perCallSite.TvfFence, TvfFenceKey, f => f.Confidence)));
            accumulator.ScalarUdf.AddRange(DedupeScalarUdf(PreferBestConfidencePerKey(perCallSite.ScalarUdf, ScalarUdfKey, f => f.Confidence)));
        }

        return accumulator.ToResult();
    }

private static List<SargabilityFinding> DedupeTier1(List<SargabilityFinding> findings)
    {
        var seen = new HashSet<(SargabilityFindingKind Kind, string ColumnName, string? Detail, string? TableQualifiedName)>();
        return findings.Where(finding => seen.Add(Tier1Key(finding))).ToList();
    }

    private static (SargabilityFindingKind Kind, string ColumnName, string? Detail, string? TableQualifiedName) Tier1Key(SargabilityFinding finding) =>
        (finding.Kind, finding.ColumnName, finding.Detail, finding.TableQualifiedName);

    private static string TypedKey(TypedPredicateFinding finding) =>
        TypedPredicateFindingIdentity.ComputeKey(finding.Column, finding.OtherOperand, finding.Operator);

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

    private static List<UnparameterizedDynamicSqlFinding> DedupeUnparameterized(List<UnparameterizedDynamicSqlFinding> findings)
    {
        var seen = new HashSet<(string, int, int, UnparameterizedDynamicSqlFindingKind)>();
        return findings.Where(finding => seen.Add(UnparameterizedKey(finding))).ToList();
    }

    private static (string, int, int, UnparameterizedDynamicSqlFindingKind) UnparameterizedKey(UnparameterizedDynamicSqlFinding finding) =>
        (finding.SourcePath, finding.Line, finding.Column, finding.Kind);

    private static List<WriteLossFinding> DedupeWriteLoss(List<WriteLossFinding> findings)
    {
        var seen = new HashSet<(string?, string, WriteLossKind, SqlType, SqlType)>();
        return findings.Where(finding => seen.Add(WriteLossKey(finding))).ToList();
    }

    private static (string?, string, WriteLossKind, SqlType, SqlType) WriteLossKey(WriteLossFinding finding) =>
        (finding.TableQualifiedName, finding.ColumnName, finding.Kind, finding.TargetType, finding.SourceType);

    private static List<TvfFenceFinding> DedupeTvfFence(List<TvfFenceFinding> findings)
    {
        var seen = new HashSet<(TvfFenceFindingKind, string?, string?)>();
        return findings.Where(finding => seen.Add(TvfFenceKey(finding))).ToList();
    }

    private static (TvfFenceFindingKind, string?, string?) TvfFenceKey(TvfFenceFinding finding) =>
        (finding.Kind, finding.FunctionQualifiedName, finding.ReferencedObjectQualifiedName);

    private static List<ScalarUdfFinding> DedupeScalarUdf(List<ScalarUdfFinding> findings)
    {
        var seen = new HashSet<(ScalarUdfFindingKind, string, string, ScalarUdfContext)>();
        return findings.Where(finding => seen.Add(ScalarUdfKey(finding))).ToList();
    }

    private static (ScalarUdfFindingKind, string, string, ScalarUdfContext) ScalarUdfKey(ScalarUdfFinding finding) =>
        (finding.Kind, finding.FunctionQualifiedName, finding.ReferencedObjectQualifiedName, finding.Context);

private static List<T> PreferBestConfidencePerKey<T, TKey>(List<T> findings, Func<T, TKey> key, Func<T, FindingConfidence> confidence)
        where TKey : notnull =>
        [.. findings.GroupBy(key).SelectMany(group => group.OrderBy(confidence))];

private sealed class ResultAccumulator
    {
        public List<DynamicSqlFinding> Findings { get; } = [];

        public List<SargabilityFinding> Tier1 { get; } = [];

        public List<TypedPredicateFinding> Typed { get; } = [];

        public List<ExpressionDerivedFinding> ExpressionDerived { get; } = [];

        public List<CollationConflictFinding> CollationConflicts { get; } = [];

        public List<WriteLossFinding> WriteLoss { get; } = [];

        public List<TvfFenceFinding> TvfFence { get; } = [];

        public List<ScalarUdfFinding> ScalarUdf { get; } = [];

        public List<UnparameterizedDynamicSqlFinding> Unparameterized { get; } = [];

        public List<SkippedConstruct> Skipped { get; } = [];

        public DynamicSqlPipelineResult ToResult() =>
            new(Findings, Tier1, Typed, ExpressionDerived, CollationConflicts, WriteLoss, TvfFence, ScalarUdf, Unparameterized, Skipped);
    }

private static bool TryParseAndClassify(
        DynamicSqlScript script, IReadOnlyList<PlaceholderOccurrence>? placeholders, ResultAccumulator accumulator,
        [NotNullWhen(true)] out SqlParseResult? innerParseResult,
        out Func<int, int, SourceSpan>? elisionMap)
    {
        innerParseResult = null;
        elisionMap = null;

        if (placeholders is { Count: > 0 } && IsEntirelyPlaceholder(script.InnerText, placeholders))
        {
            accumulator.Findings.Add(new DynamicSqlFinding(
                script.CallSite.SourcePath, script.CallSite.Line, script.CallSite.Column,
                DynamicSqlOutcome.Unanalyzable, "symbolic-value-not-positionable:whole-statement"));
            return false;
        }

        var virtualPath = $"{script.CallSite.SourcePath}::dynamic-sql@{script.CallSite.Line}";
        var parseResult = SqlScriptParser.ParseText(virtualPath, script.InnerText);

        if (parseResult.HasErrors)
        {
            if (placeholders is { Count: > 0 })
            {
                if (TryReparseWithTargetedElision(script, placeholders, parseResult.Errors, out var elidedParseResult, out var map))
                {
                    innerParseResult = elidedParseResult;
                    elisionMap = map;
                    return true;
                }

                accumulator.Findings.Add(new DynamicSqlFinding(
                    script.CallSite.SourcePath, script.CallSite.Line, script.CallSite.Column,
                    DynamicSqlOutcome.Unanalyzable, "symbolic-value-broke-parse"));
            }
            else if (TemplatePlaceholderRegex().IsMatch(script.InnerText))
            {
                accumulator.Findings.Add(new DynamicSqlFinding(
                    script.CallSite.SourcePath, script.CallSite.Line, script.CallSite.Column,
                    DynamicSqlOutcome.Unanalyzable, "template-placeholder-not-instantiated"));
            }
            else
            {
                var reason = parseResult.Errors[0].Message;
                accumulator.Findings.Add(new DynamicSqlFinding(
                    script.CallSite.SourcePath, script.CallSite.Line, script.CallSite.Column, DynamicSqlOutcome.InnerParseFailed, reason));
            }

            return false;
        }

        innerParseResult = parseResult;
        return true;
    }

private static string PlaceholderToken(PlaceholderOccurrence occurrence) =>
        $"__silentscan_sym_L{occurrence.Origin.Line}C{occurrence.Origin.Column}__";

[GeneratedRegex(@"__silentscan_sym_L\d+C\d+__")]
    private static partial Regex PlaceholderTokenRegex();

private static readonly string[] ElisionFillerCandidates = [" ", "1=1", "NULL", "(SELECT 1)"];

    private static bool TryReparseWithTargetedElision(
        DynamicSqlScript script, IReadOnlyList<PlaceholderOccurrence> placeholders, IReadOnlyList<ParseError> originalErrors,
        [NotNullWhen(true)] out SqlParseResult? elidedParseResult,
        [NotNullWhen(true)] out Func<int, int, SourceSpan>? map)
    {
        foreach (var filler in ElisionFillerCandidates)
        {
            if (TryReparseWithTargetedElision(script, placeholders, originalErrors, filler, out elidedParseResult, out map))
            {
                return true;
            }
        }

        elidedParseResult = null;
        map = null;
        return false;
    }

    private static bool TryReparseWithTargetedElision(
        DynamicSqlScript script, IReadOnlyList<PlaceholderOccurrence> placeholders, IReadOnlyList<ParseError> originalErrors, string filler,
        [NotNullWhen(true)] out SqlParseResult? elidedParseResult,
        [NotNullWhen(true)] out Func<int, int, SourceSpan>? map)
    {
        var virtualPath = $"{script.CallSite.SourcePath}::dynamic-sql@{script.CallSite.Line}::elided";
        var toElide = new HashSet<string>(StringComparer.Ordinal);
        var errors = originalErrors;

        for (var round = 0; round <= placeholders.Count; round++)
        {
            var blamed = errors.SelectMany(e => PlaceholderTokenRegex().Matches(e.Message).Select(m => m.Value));
            var addedAny = false;
            foreach (var token in blamed)
            {
                addedAny |= toElide.Add(token);
            }

            if (!addedAny)
            {
                break;
            }

            var toElideNow = placeholders.Where(p => toElide.Contains(PlaceholderToken(p))).ToList();
            var variant = NeutralElisionVariant.Build(script.InnerText, toElideNow, filler);
            var parseResult = SqlScriptParser.ParseText(virtualPath, variant.Text);
            if (!parseResult.HasErrors)
            {
                elidedParseResult = parseResult;
                map = (line, column) => variant.Map(line, column, script.SegmentMap);
                return true;
            }

            errors = parseResult.Errors;
        }

        elidedParseResult = null;
        map = null;
        return false;
    }

private sealed class NeutralElisionVariant
    {
        private readonly string _innerText;
        private readonly int[] _neutralOffsetToInnerOffset;
        private readonly Dictionary<int, SourceSpan> _fillerOriginByNeutralOffset;

        private NeutralElisionVariant(string text, string innerText, int[] neutralOffsetToInnerOffset, Dictionary<int, SourceSpan> fillerOriginByNeutralOffset)
        {
            Text = text;
            _innerText = innerText;
            _neutralOffsetToInnerOffset = neutralOffsetToInnerOffset;
            _fillerOriginByNeutralOffset = fillerOriginByNeutralOffset;
        }

        public string Text { get; }

        public static NeutralElisionVariant Build(string innerText, IReadOnlyList<PlaceholderOccurrence> occurrences, string filler = " ")
        {
            var sorted = occurrences.OrderBy(o => o.InnerStartOffset).ToList();
            var text = new StringBuilder();
            var innerOffsets = new List<int>();
            var fillerOrigins = new Dictionary<int, SourceSpan>();
            var cursor = 0;

            foreach (var occurrence in sorted)
            {
                for (var i = cursor; i < occurrence.InnerStartOffset; i++)
                {
                    innerOffsets.Add(i);
                    text.Append(innerText[i]);
                }

                for (var i = 0; i < filler.Length; i++)
                {
                    fillerOrigins[text.Length + i] = occurrence.Origin;
                    innerOffsets.Add(occurrence.InnerStartOffset);
                }

                text.Append(filler);

                cursor = occurrence.InnerStartOffset + occurrence.Length;
            }

            for (var i = cursor; i < innerText.Length; i++)
            {
                innerOffsets.Add(i);
                text.Append(innerText[i]);
            }

            innerOffsets.Add(innerText.Length);

            return new NeutralElisionVariant(text.ToString(), innerText, [.. innerOffsets], fillerOrigins);
        }

        public SourceSpan Map(int neutralLine, int neutralColumn, DynamicSqlSegmentMap originalMap)
        {
            var neutralOffset = LineColToOffset(Text, neutralLine, neutralColumn);

            if (_fillerOriginByNeutralOffset.TryGetValue(neutralOffset, out var fillerOrigin))
            {
                return fillerOrigin;
            }

            var boundedOffset = Math.Clamp(neutralOffset, 0, _neutralOffsetToInnerOffset.Length - 1);
            var innerOffset = _neutralOffsetToInnerOffset[boundedOffset];
            var (innerLine, innerColumn) = OffsetToLineCol(_innerText, innerOffset);
            return originalMap.Map(innerLine, innerColumn);
        }

        private static int LineColToOffset(string text, int line, int column)
        {
            var offset = 0;
            var currentLine = 1;
            while (currentLine < line)
            {
                var newlineIndex = text.IndexOf('\n', offset);
                if (newlineIndex < 0)
                {
                    return text.Length;
                }

                offset = newlineIndex + 1;
                currentLine++;
            }

            return Math.Min(offset + column - 1, text.Length);
        }

        private static (int Line, int Column) OffsetToLineCol(string text, int offset)
        {
            var line = 1;
            var lastNewline = -1;
            for (var i = 0; i < offset; i++)
            {
                if (text[i] == '\n')
                {
                    line++;
                    lastNewline = i;
                }
            }

            return (line, offset - lastNewline);
        }
    }

    private static void ProcessScript(
        DynamicSqlScript script,
        PipelineContext context,
        int depth,
        Dictionary<DynamicSqlScript, IReadOnlyDictionary<string, SqlType?>>? seeds,
        ResultAccumulator accumulator)
    {
        var placeholders = script.PlaceholderOccurrences;
        if (!TryParseAndClassify(script, placeholders, accumulator, out var innerParseResult, out var elisionMap))
        {
            return;
        }

        var map = elisionMap ?? script.SegmentMap.Map;
        var outcome = elisionMap is null ? DynamicSqlOutcome.AnalyzedLiteral : DynamicSqlOutcome.PartiallyAnalyzed;
        var reason = elisionMap is null ? null : "optional-fragment-elided";
        accumulator.Findings.Add(new DynamicSqlFinding(
            script.CallSite.SourcePath, script.CallSite.Line, script.CallSite.Column, outcome, reason));

        DetectUnparameterizedConcatenation(script, innerParseResult, accumulator);

        var tier1Ledger = new SkipLedger();
        foreach (var tier1Finding in NonSargablePredicateScanner.Scan(innerParseResult, context.Catalog, context.Lineage, script.Scope, tier1Ledger, context.CallerScopeByCalleeScope))
        {
            accumulator.Tier1.Add(Remap(tier1Finding, script, map));
        }

        foreach (var tier1Skipped in tier1Ledger.Entries)
        {
            accumulator.Skipped.Add(Remap(tier1Skipped, map));
        }

        FoldFenceAndScalarUdfFindings(innerParseResult, context, script, map, accumulator);

        var ownDeclaredParameters = script.ParameterDeclarationText is { } declarationText
            ? DynamicSqlParameterDeclarations.TryParse(declarationText, context.Catalog.TypeAliases) ?? NoDeclaredParameters
            : NoDeclaredParameters;
        var declaredParameters = seeds is not null && seeds.TryGetValue(script, out var seed)
            ? MergeSeededParameters(ownDeclaredParameters, seed)
            : ownDeclaredParameters;
        var extraction = TypedPredicateExtractor.Extract(innerParseResult, context.Catalog, context.Lineage, declaredParameters, script.Scope, context.CallerScopeByCalleeScope);
        foreach (var typedFinding in extraction.TypedFindings)
        {
            accumulator.Typed.Add(Remap(typedFinding, script, map));
        }

        foreach (var expressionFinding in extraction.ExpressionDerivedFindings)
        {
            accumulator.ExpressionDerived.Add(Remap(expressionFinding, script, map));
        }

        foreach (var collationConflict in extraction.CollationConflictFindings)
        {
            accumulator.CollationConflicts.Add(Remap(collationConflict, script, map));
        }

        foreach (var writeLoss in extraction.WriteLossFindings)
        {
            accumulator.WriteLoss.Add(Remap(writeLoss, script, map));
        }

        foreach (var skippedConstruct in extraction.SkippedConstructs)
        {
            accumulator.Skipped.Add(Remap(skippedConstruct, map));
        }

        var nested = placeholders is { Count: > 0 }
            ? RefuseNestedCandidates(innerParseResult, script, map)
            : AnalyzeNested(innerParseResult, script, declaredParameters, context, depth);
        accumulator.Findings.AddRange(nested.Findings);
        accumulator.Tier1.AddRange(nested.Tier1Findings);
        accumulator.Typed.AddRange(nested.TypedFindings);
        accumulator.ExpressionDerived.AddRange(nested.ExpressionDerivedFindings);
        accumulator.CollationConflicts.AddRange(nested.CollationConflictFindings);
        accumulator.WriteLoss.AddRange(nested.WriteLossFindings);
        accumulator.TvfFence.AddRange(nested.TvfFenceFindings);
        accumulator.ScalarUdf.AddRange(nested.ScalarUdfFindings);
        accumulator.Unparameterized.AddRange(nested.UnparameterizedFindings);
        accumulator.Skipped.AddRange(nested.SkippedConstructs);
    }

private static void DetectUnparameterizedConcatenation(DynamicSqlScript script, SqlParseResult innerParseResult, ResultAccumulator accumulator)
    {
        var boundaries = script.SegmentMap.ConcatenationBoundaryOffsets;
        if (boundaries.Count == 0)
        {
            return;
        }

        var sawValueSplice = boundaries.Any(offset =>
            DynamicSqlOperandPositionClassifier.Classify(innerParseResult.Fragment, offset) == DynamicSqlOperandPosition.Value);

        if (!sawValueSplice)
        {
            return;
        }

        accumulator.Unparameterized.Add(new UnparameterizedDynamicSqlFinding(
            script.CallSite.SourcePath, script.CallSite.Line, script.CallSite.Column,
            UnparameterizedDynamicSqlFindingKind.ConcatenatedValueInConstantSql, script.Confidence));

        if (script.IsExecString)
        {
            accumulator.Unparameterized.Add(new UnparameterizedDynamicSqlFinding(
                script.CallSite.SourcePath, script.CallSite.Line, script.CallSite.Column,
                UnparameterizedDynamicSqlFindingKind.ExecStringConcatenatesParameterizableValue, script.Confidence));
        }
    }

private static void FoldFenceAndScalarUdfFindings(
        SqlParseResult innerParseResult, PipelineContext context, DynamicSqlScript script, Func<int, int, SourceSpan> map, ResultAccumulator accumulator)
    {
        foreach (var tvfFenceFinding in TvfFenceScanner.Scan(innerParseResult, context.Catalog, context.TvfFenceMap))
        {
            accumulator.TvfFence.Add(Remap(tvfFenceFinding, script, map));
        }

        foreach (var scalarUdfFinding in ScalarUdfScanner.Scan(innerParseResult, context.Catalog, context.ScalarUdfMap))
        {
            accumulator.ScalarUdf.Add(Remap(scalarUdfFinding, script, map));
        }
    }

    private static DynamicSqlPipelineResult AnalyzeNested(
        SqlParseResult innerParseResult,
        DynamicSqlScript script,
        IReadOnlyDictionary<string, SqlType?> outerDeclaredParameters,
        PipelineContext context,
        int depth)
    {
        var nestedExtraction = DynamicSqlScannerV2.Scan(innerParseResult, script.Scope, catalog: context.Catalog);
        var findings = nestedExtraction.Findings.Select(f => RemapFinding(f, script)).ToList();

        if (nestedExtraction.AnalyzableScripts.Count == 0)
        {
            return new DynamicSqlPipelineResult(findings, [], [], [], [], [], [], [], [], []);
        }

        if (depth >= MaxNestingDepth)
        {
            findings.AddRange(nestedExtraction.AnalyzableScripts
                .Select(nestedScript => script.SegmentMap.Map(nestedScript.CallSite.Line, nestedScript.CallSite.Column))
                .Select(callSite => new DynamicSqlFinding(callSite.SourcePath, callSite.Line, callSite.Column, DynamicSqlOutcome.Unanalyzable, "max-nesting-depth-exceeded")));

            return new DynamicSqlPipelineResult(findings, [], [], [], [], [], [], [], [], []);
        }

        var seeds = BuildArgumentBindingSeeds(nestedExtraction.AnalyzableScripts, outerDeclaredParameters);
        var nestedResult = Analyze(nestedExtraction.AnalyzableScripts, context, depth + 1, seeds);
        findings.AddRange(nestedResult.Findings.Select(f => RemapFinding(f, script)));

        return new DynamicSqlPipelineResult(
            findings,
            [.. nestedResult.Tier1Findings.Select(f => RemapNested(f, script))],
            [.. nestedResult.TypedFindings.Select(f => RemapNested(f, script))],
            [.. nestedResult.ExpressionDerivedFindings.Select(f => RemapNested(f, script))],
            [.. nestedResult.CollationConflictFindings.Select(f => RemapNested(f, script))],
            [.. nestedResult.WriteLossFindings.Select(f => RemapNested(f, script))],
            [.. nestedResult.TvfFenceFindings.Select(f => RemapNested(f, script))],
            [.. nestedResult.ScalarUdfFindings.Select(f => RemapNested(f, script))],
            [.. nestedResult.UnparameterizedFindings.Select(f => RemapNested(f, script))],
            [.. nestedResult.SkippedConstructs.Select(s => Remap(s, script))]);
    }

private static DynamicSqlPipelineResult RefuseNestedCandidates(SqlParseResult innerParseResult, DynamicSqlScript script, Func<int, int, SourceSpan> map)
    {
        var nestedExtraction = DynamicSqlScannerV2.Scan(innerParseResult, script.Scope);
        var findings = nestedExtraction.Findings.Select(f => RemapFinding(f, map)).ToList();
        findings.AddRange(nestedExtraction.AnalyzableScripts
            .Select(nestedScript => map(nestedScript.CallSite.Line, nestedScript.CallSite.Column))
            .Select(callSite => new DynamicSqlFinding(callSite.SourcePath, callSite.Line, callSite.Column, DynamicSqlOutcome.Unanalyzable, "nested-dynamic-sql-inside-symbolic-value")));

        return new DynamicSqlPipelineResult(findings, [], [], [], [], [], [], [], [], []);
    }

private static bool IsEntirelyPlaceholder(string innerText, IReadOnlyList<PlaceholderOccurrence> occurrences)
    {
        var remaining = innerText;
        foreach (var occurrence in occurrences.OrderByDescending(o => o.InnerStartOffset))
        {
            remaining = remaining.Remove(occurrence.InnerStartOffset, occurrence.Length);
        }

        return string.IsNullOrWhiteSpace(remaining);
    }

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

    private static DynamicSqlFinding RemapFinding(DynamicSqlFinding finding, DynamicSqlScript outerScript) =>
        RemapFinding(finding, outerScript.SegmentMap.Map);

    private static DynamicSqlFinding RemapFinding(DynamicSqlFinding finding, Func<int, int, SourceSpan> map)
    {
        var span = map(finding.Line, finding.Column);
        return finding with { SourcePath = span.SourcePath, Line = span.Line, Column = span.Column };
    }

    private static SourceSpan? RemapCallSite(SourceSpan? callSite, DynamicSqlScript outerScript) =>
        callSite is { } span ? outerScript.SegmentMap.Map(span.Line, span.Column) : null;

private static FindingConfidence Worse(FindingConfidence a, FindingConfidence b) => (FindingConfidence)Math.Max((int)a, (int)b);

private static TFinding Remap<TFinding>(TFinding finding, DynamicSqlScript script, Func<int, int, SourceSpan> map)
        where TFinding : IRelocatableFinding<TFinding>
    {
        var span = map(finding.Line, finding.PositionColumn);
        return finding.Relocated(span, script.CallSite, script.Confidence);
    }

    private static SkippedConstruct Remap(SkippedConstruct entry, DynamicSqlScript script) =>
        Remap(entry, script.SegmentMap.Map);

    private static SkippedConstruct Remap(SkippedConstruct entry, Func<int, int, SourceSpan> map)
    {
        var span = map(entry.Line, entry.Column);
        return entry with { SourcePath = span.SourcePath, Line = span.Line, Column = span.Column };
    }

private static TFinding RemapNested<TFinding>(TFinding finding, DynamicSqlScript outerScript)
        where TFinding : IRelocatableFinding<TFinding>
    {
        var span = outerScript.SegmentMap.Map(finding.Line, finding.PositionColumn);
        return finding.Relocated(span, RemapCallSite(finding.DynamicSqlCallSite, outerScript), Worse(finding.Confidence, outerScript.Confidence));
    }

private static UnparameterizedDynamicSqlFinding RemapNested(UnparameterizedDynamicSqlFinding finding, DynamicSqlScript outerScript)
    {
        var span = outerScript.SegmentMap.Map(finding.Line, finding.Column);
        return finding with { SourcePath = span.SourcePath, Line = span.Line, Column = span.Column };
    }
}

public sealed record DynamicSqlPipelineResult(
    IReadOnlyList<DynamicSqlFinding> Findings,
    IReadOnlyList<SargabilityFinding> Tier1Findings,
    IReadOnlyList<TypedPredicateFinding> TypedFindings,
    IReadOnlyList<ExpressionDerivedFinding> ExpressionDerivedFindings,
    IReadOnlyList<CollationConflictFinding> CollationConflictFindings,
    IReadOnlyList<WriteLossFinding> WriteLossFindings,
    IReadOnlyList<TvfFenceFinding> TvfFenceFindings,
    IReadOnlyList<ScalarUdfFinding> ScalarUdfFindings,
    IReadOnlyList<UnparameterizedDynamicSqlFinding> UnparameterizedFindings,
    IReadOnlyList<SkippedConstruct> SkippedConstructs);
