using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
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
public static partial class DynamicSqlPipeline
{
    /// <summary>
    /// Real-world dynamic SQL nesting rarely exceeds one or two levels; this is a backstop
    /// against runaway/adversarial input, not a tuned-for-recall limit.
    /// </summary>
    private const int MaxNestingDepth = 5;

    private static readonly IReadOnlyDictionary<string, SqlType?> NoDeclaredParameters = new Dictionary<string, SqlType?>();

    /// <summary>
    /// A bare <c>$Name$</c> token, the templating convention this project already recognizes at
    /// the corpus-preprocessing layer (<see cref="Corpus.CorpusTemplatePreprocessor"/>) for
    /// whole source files - this is the same convention surviving INSIDE a literal that builds
    /// dynamic SQL, where no manifest substitution ever reaches it. Deliberately requires at
    /// least one identifier character between the two <c>$</c> delimiters (an empty <c>$$</c>
    /// is legal, ordinary T-SQL - the money-column default alias in some dialects - not a
    /// template artifact).
    /// </summary>
    [GeneratedRegex(@"\$[A-Za-z_][A-Za-z0-9_]*\$")]
    private static partial Regex TemplatePlaceholderRegex();

    public static DynamicSqlPipelineResult Analyze(
        IReadOnlyList<DynamicSqlScript> scripts, DatabaseCatalog catalog, LineageCatalog lineage, IReadOnlyDictionary<string, IReadOnlyList<string>>? callerScopeByCalleeScope = null) =>
        Analyze(scripts, catalog, lineage, depth: 1, seeds: null, callerScopeByCalleeScope);

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
        Dictionary<DynamicSqlScript, IReadOnlyDictionary<string, SqlType?>>? seeds,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? callerScopeByCalleeScope = null)
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
                ProcessScript(script, catalog, lineage, depth, seeds, callerScopeByCalleeScope, perCallSite);
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

    /// <summary>
    /// Handles everything before ordinary Tier-1/typed extraction can safely run: the
    /// whole-statement-is-a-placeholder pre-parse check, the parse itself, reclassifying a parse
    /// FAILURE as the scanner's own fault when a placeholder is present rather than the user's
    /// source, and (once parsing succeeds) the placeholder position classifier. Returns false the
    /// moment any of these already fully explains the site (a finding has been added, nothing
    /// left to do); true only when ordinary extraction should proceed against
    /// <paramref name="innerParseResult"/>.
    /// </summary>
    private static bool TryParseAndClassify(
        DynamicSqlScript script, IReadOnlyList<PlaceholderOccurrence>? placeholders, ResultAccumulator accumulator,
        [NotNullWhen(true)] out SqlParseResult? innerParseResult,
        out Func<int, int, SourceSpan>? elisionMap)
    {
        innerParseResult = null;
        elisionMap = null;

        if (placeholders is { Count: > 0 } && IsEntirelyPlaceholder(script.InnerText, placeholders))
        {
            // No real SQL text survives once every placeholder is removed - EXEC(@sym) itself,
            // or the equivalent after folding. There is nothing to reparse at all, so this must
            // be caught BEFORE parsing: parsing a bare synthesized token and reporting whatever
            // ScriptDOM makes of it would blame the user's source for a shape this scanner
            // invented.
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
                // A placeholder token can break the surrounding syntax in ways ordinary source
                // text wouldn't (e.g. sitting where only a keyword is legal) - reporting this as
                // InnerParseFailed would read as "the user's own SQL doesn't parse", which isn't
                // what happened: an ASSUMPTION this scanner made broke the parse, not the source.
                // Before giving up outright: a symbolic value standing for a whole optional
                // clause/fragment (rather than one scalar) can never fit an identifier-shaped
                // token, but a single space might - see TryReparseWithNeutralElision.
                if (TryReparseWithNeutralElision(script, placeholders, out var elidedParseResult, out var map))
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
                // A source-level templating convention (e.g. $Signature$) stamped a token into
                // this literal that was never substituted before it reached this call site -
                // ScriptDOM's parse error ("Incorrect syntax near '$Signature$'") is real, but
                // reporting it as InnerParseFailed would blame this scanner for not handling
                // ordinary T-SQL, when the actual cause is that the script was never fully
                // instantiated. A distinct, DIFFERENT reason from the placeholder-broke-parse
                // cases above: those are THIS scanner's own synthesized substitution breaking a
                // parse that would otherwise succeed; this is the source text itself still
                // carrying an un-instantiated template token, before this scanner touched it.
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

        if (placeholders is { Count: > 0 } && !AllPlaceholdersInSafePosition(parseResult.Fragment, placeholders))
        {
            accumulator.Findings.Add(new DynamicSqlFinding(
                script.CallSite.SourcePath, script.CallSite.Line, script.CallSite.Column,
                DynamicSqlOutcome.Unanalyzable, "symbolic-value-unsupported-position"));
            return false;
        }

        // A script with no placeholder, or one where every occurrence just proved itself safe
        // (inside a string literal - a genuine varchar/nvarchar literal comparison BY
        // CONSTRUCTION - or inside a table reference's own identifier parts, where a synthesized
        // __silentscan_sym_...__ token can never resolve against the real catalog so the ordinary
        // extractor below naturally finds nothing there), falls through to the same extraction
        // path uniformly: no special-cased early return, and no "one statement only" restriction
        // either - a sibling statement in the same multi-statement script that never touches a
        // placeholder gets full, ordinary extraction. Remap stamps script.Confidence (already
        // Medium whenever a placeholder exists at all) onto whatever it finds.
        innerParseResult = parseResult;
        return true;
    }

    /// <summary>
    /// The one fallback for a symbolic value that broke the parse outright: it may not stand for
    /// a single scalar at all, but for a whole optional clause/fragment (an appended filter, a
    /// cursor body) - no identifier-shaped token can ever sit legally in that position. Replacing
    /// every occurrence with a single space instead of its usual token either still fails to
    /// parse (the fragment wasn't actually optional in a "may be entirely absent" sense - e.g. a
    /// cursor's <c>DECLARE ... CURSOR FOR</c> body, which needs a real SELECT no matter what;
    /// declines exactly as before, no change) or reveals a valid statement missing only the part
    /// this scanner could never see anyway. A space, unlike deleting the span outright, can never
    /// fuse two adjacent literal fragments into a token that wasn't there in either the real
    /// runtime query OR the elided one (T-SQL treats whitespace as a pure token separator
    /// everywhere outside a quoted literal/identifier, and a placeholder inside one of those
    /// would already have classified as a SAFE position long before this ever runs) - so
    /// extraction against the result can only ever under-report relative to the true runtime
    /// query (the elided fragment's own content stays genuinely unknown), never fabricate a
    /// finding that depends on it.
    /// </summary>
    private static bool TryReparseWithNeutralElision(
        DynamicSqlScript script, IReadOnlyList<PlaceholderOccurrence> placeholders,
        [NotNullWhen(true)] out SqlParseResult? elidedParseResult,
        [NotNullWhen(true)] out Func<int, int, SourceSpan>? map)
    {
        var variant = NeutralElisionVariant.Build(script.InnerText, placeholders);
        var virtualPath = $"{script.CallSite.SourcePath}::dynamic-sql@{script.CallSite.Line}::elided";
        var parseResult = SqlScriptParser.ParseText(virtualPath, variant.Text);
        if (parseResult.HasErrors)
        {
            elidedParseResult = null;
            map = null;
            return false;
        }

        elidedParseResult = parseResult;
        map = (line, column) => variant.Map(line, column, script.SegmentMap);
        return true;
    }

    /// <summary>
    /// Rebuilds a dynamic SQL script's own inner text with every symbolic placeholder occurrence
    /// replaced by a single space instead of its usual identifier-shaped token, plus the position
    /// translation <see cref="Map"/> needs to resolve a finding inside the REBUILT text back to
    /// real source coordinates: convert the rebuilt text's own (line, column) to a flat offset,
    /// translate that back to the corresponding offset in the ORIGINAL <see
    /// cref="DynamicSqlScript.InnerText"/> (a position landing inside the inserted filler itself
    /// has no such original offset at all - it collapses to that placeholder occurrence's own
    /// <see cref="PlaceholderOccurrence.Origin"/>, mirroring <see cref="DynamicSqlSegmentMap"/>'s
    /// identical treatment of its own token-substitution case), then hand the translated position
    /// to the script's OWN already-correct <see cref="DynamicSqlSegmentMap.Map"/> for the final
    /// hop to real source coordinates - reusing it rather than re-deriving its quote-escaping/
    /// multi-line arithmetic here, which only stays correct when applied to the exact segment
    /// boundaries it was built from.
    /// </summary>
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

        public static NeutralElisionVariant Build(string innerText, IReadOnlyList<PlaceholderOccurrence> occurrences)
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

                fillerOrigins[text.Length] = occurrence.Origin;
                innerOffsets.Add(occurrence.InnerStartOffset);
                text.Append(' ');

                cursor = occurrence.InnerStartOffset + occurrence.Length;
            }

            for (var i = cursor; i < innerText.Length; i++)
            {
                innerOffsets.Add(i);
                text.Append(innerText[i]);
            }

            // One extra sentinel entry so a position exactly at end-of-text (offset ==
            // Text.Length, the count of positions ScriptDOM can legally report is one more than
            // the count of characters) still has a valid original-offset lookup.
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
        DatabaseCatalog catalog,
        LineageCatalog lineage,
        int depth,
        Dictionary<DynamicSqlScript, IReadOnlyDictionary<string, SqlType?>>? seeds,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? callerScopeByCalleeScope,
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

        var tier1Ledger = new SkipLedger();
        foreach (var tier1Finding in NonSargablePredicateScanner.Scan(innerParseResult, catalog, lineage, script.Scope, tier1Ledger, callerScopeByCalleeScope))
        {
            accumulator.Tier1.Add(Remap(tier1Finding, script, map));
        }

        foreach (var tier1Skipped in tier1Ledger.Entries)
        {
            accumulator.Skipped.Add(Remap(tier1Skipped, map));
        }

        var ownDeclaredParameters = script.ParameterDeclarationText is { } declarationText
            ? DynamicSqlParameterDeclarations.TryParse(declarationText, catalog.TypeAliases) ?? NoDeclaredParameters
            : NoDeclaredParameters;
        var declaredParameters = seeds is not null && seeds.TryGetValue(script, out var seed)
            ? MergeSeededParameters(ownDeclaredParameters, seed)
            : ownDeclaredParameters;
        var extraction = TypedPredicateExtractor.Extract(innerParseResult, catalog, lineage, declaredParameters, script.Scope, callerScopeByCalleeScope);
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

        // A script whose OWN identity rests on a placeholder never recurses into further
        // nested dynamic SQL - a real runtime value could reshape the surrounding text in ways
        // this scanner never modeled, so treating a nested candidate's findings as independently
        // trustworthy would launder that same unproven assumption one level deeper. A partially-
        // analyzed script (elisionMap non-null) always has placeholders too, so it already routes
        // through the same refusal - never AnalyzeNested, which assumes innerParseResult's
        // coordinates are the script's own untranslated InnerText.
        var nested = placeholders is { Count: > 0 }
            ? RefuseNestedCandidates(innerParseResult, script, map)
            : AnalyzeNested(innerParseResult, script, declaredParameters, catalog, lineage, depth, callerScopeByCalleeScope);
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
        int depth,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? callerScopeByCalleeScope)
    {
        // Propagates the outer script's own scope into the nested scanner - the reparsed inner
        // text has no CREATE PROCEDURE wrapper for it to discover the scope from itself, so
        // without this, propagation would silently die at nesting depth 2.
        var nestedExtraction = DynamicSqlScanner.Scan(innerParseResult, script.Scope, catalog: catalog);
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
        var nestedResult = Analyze(nestedExtraction.AnalyzableScripts, catalog, lineage, depth + 1, seeds, callerScopeByCalleeScope);
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
    /// The outer script itself rests on a placeholder - any dynamic SQL call site found INSIDE
    /// its reparsed text inherits that same unproven context, so every candidate is reported
    /// Unanalyzable rather than recursed into, remapped back to its real call site exactly like
    /// the max-nesting-depth-exceeded case above. Any finding the nested scan itself already
    /// produced (an unrelated Unanalyzable reason from ITS OWN folding) is remapped and kept too -
    /// never silently dropped, CLAUDE.md.
    /// </summary>
    private static DynamicSqlPipelineResult RefuseNestedCandidates(SqlParseResult innerParseResult, DynamicSqlScript script, Func<int, int, SourceSpan> map)
    {
        var nestedExtraction = DynamicSqlScanner.Scan(innerParseResult, script.Scope);
        var findings = nestedExtraction.Findings.Select(f => RemapFinding(f, map)).ToList();
        findings.AddRange(nestedExtraction.AnalyzableScripts
            .Select(nestedScript => map(nestedScript.CallSite.Line, nestedScript.CallSite.Column))
            .Select(callSite => new DynamicSqlFinding(callSite.SourcePath, callSite.Line, callSite.Column, DynamicSqlOutcome.Unanalyzable, "nested-dynamic-sql-inside-symbolic-value")));

        return new DynamicSqlPipelineResult(findings, [], [], [], [], [], []);
    }

    /// <summary>Whether every character of <paramref name="innerText"/> outside <paramref name="occurrences"/>' own spans is blank - <c>EXEC(@sym)</c> itself, or the equivalent after folding, where there is no real SQL text left to reparse at all.</summary>
    private static bool IsEntirelyPlaceholder(string innerText, IReadOnlyList<PlaceholderOccurrence> occurrences)
    {
        var remaining = innerText;
        foreach (var occurrence in occurrences.OrderByDescending(o => o.InnerStartOffset))
        {
            remaining = remaining.Remove(occurrence.InnerStartOffset, occurrence.Length);
        }

        return string.IsNullOrWhiteSpace(remaining);
    }

    /// <summary>
    /// Sound per-occurrence, not per-statement: EVERY placeholder occurrence anywhere in the
    /// reparsed script must independently sit inside a string literal (a genuine varchar/nvarchar
    /// literal comparison by construction) or inside a table reference's own identifier parts (a
    /// synthesized <c>__silentscan_sym_...__</c> token can never collide with a real deployed
    /// object, so nothing downstream is ever extracted against it) - never a bare value position
    /// (a WHERE predicate, a SELECT expression), which still refuses. A script can freely MIX
    /// both safe positions across multiple statements: a corpus-measured shape (First Responder
    /// Kit's output-to-table blocks) has an <c>IF EXISTS(...) INSERT db.schema.table ...</c> where
    /// one placeholder names the identifier and another sits quoted inside a literal in the SAME
    /// or a SIBLING statement. Since this proves EVERY occurrence safe rather than special-casing
    /// a whole-statement shape, there is no need to restrict which statement, or how many, the
    /// script contains - a sibling statement that never touches a placeholder at all gets full,
    /// ordinary extraction exactly as if the whole script were placeholder-free.
    /// </summary>
    private static bool AllPlaceholdersInSafePosition(TSqlFragment fragment, IReadOnlyList<PlaceholderOccurrence> occurrences)
    {
        var stringLiteralRanges = CollectStringLiteralRanges(fragment);
        var tableIdentifiers = CollectAllTableReferenceNames(fragment);
        return occurrences.All(o => IsWithinAnyRange(o, stringLiteralRanges) || tableIdentifiers.Any(name => IsWithinIdentifier(name, o)));
    }

    private static List<(int Start, int End)> CollectStringLiteralRanges(TSqlFragment fragment)
    {
        var collector = new StringLiteralRangeCollector();
        fragment.Accept(collector);
        return collector.Ranges;
    }

    private sealed class StringLiteralRangeCollector : TSqlFragmentVisitor
    {
        public List<(int Start, int End)> Ranges { get; } = [];

        public override void ExplicitVisit(StringLiteral node)
        {
            Ranges.Add((node.StartOffset, node.StartOffset + node.FragmentLength));
            base.ExplicitVisit(node);
        }
    }

    private static bool IsWithinAnyRange(PlaceholderOccurrence occurrence, List<(int Start, int End)> ranges)
    {
        var end = occurrence.InnerStartOffset + occurrence.Length;
        return ranges.Any(r => r.Start <= occurrence.InnerStartOffset && end <= r.End);
    }

    /// <summary>
    /// Every table any statement in <paramref name="fragment"/> reads or writes, or targets via
    /// DROP/TRUNCATE - a real corpus scan found the dominant shape is a full INSERT/SELECT with
    /// its own WHERE clause, dynamically naming the table it reads via QUOTENAME, e.g. First
    /// Responder Kit's <c>SET @sql = N'INSERT ... SELECT ... FROM ' + QUOTENAME(@Server) + N'.'
    /// + QUOTENAME(@Db) + N' WHERE ServerName IS NULL OR ...'</c>, alongside DROP TABLE/TRUNCATE
    /// TABLE's own distinct (non-<see cref="NamedTableReference"/>) target syntax. Scans the
    /// WHOLE fragment - not one statement - since <see cref="AllPlaceholdersInSafePosition"/>
    /// proves safety per OCCURRENCE, not per statement-shape, so there is no reason to restrict
    /// which table names are even eligible to match. Deliberately NOT a general SchemaObjectName
    /// collector - a CAST target's UserDataTypeReference or a scalar function call's own name
    /// also carry a SchemaObjectName, and a placeholder there is a TYPE or FUNCTION identity, not
    /// a table one; admitting those under this same "unresolvable ⇒ no downstream claim"
    /// reasoning would be a different (and unverified) argument.
    /// </summary>
    private static List<SchemaObjectName> CollectAllTableReferenceNames(TSqlFragment fragment)
    {
        var collector = new TableReferenceNameCollector();
        fragment.Accept(collector);
        return collector.Names;
    }

    private sealed class TableReferenceNameCollector : TSqlFragmentVisitor
    {
        public List<SchemaObjectName> Names { get; } = [];

        public override void ExplicitVisit(NamedTableReference node)
        {
            Names.Add(node.SchemaObject);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(DropTableStatement node)
        {
            Names.AddRange(node.Objects);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(TruncateTableStatement node)
        {
            Names.Add(node.TableName);
            base.ExplicitVisit(node);
        }

        // DROP FUNCTION/PROCEDURE/VIEW/TRIGGER/SYNONYM all share DropObjectsStatement's own
        // Objects shape, but ScriptDOM's visitor dispatches on each leaf type individually (there
        // is no single ExplicitVisit(DropObjectsStatement) hook) - a symbolic identifier here is
        // just as unresolvable against the real catalog as one in a FROM clause, for the same
        // reason: nothing downstream ever looks it up as a TABLE reference.
        public override void ExplicitVisit(DropFunctionStatement node)
        {
            Names.AddRange(node.Objects);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(DropProcedureStatement node)
        {
            Names.AddRange(node.Objects);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(DropViewStatement node)
        {
            Names.AddRange(node.Objects);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(DropTriggerStatement node)
        {
            Names.AddRange(node.Objects);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(DropSynonymStatement node)
        {
            Names.AddRange(node.Objects);
            base.ExplicitVisit(node);
        }
    }

    private static bool IsWithinIdentifier(SchemaObjectName name, PlaceholderOccurrence occurrence)
    {
        var end = occurrence.InnerStartOffset + occurrence.Length;
        return name.Identifiers.Any(id => id.StartOffset <= occurrence.InnerStartOffset && end <= id.StartOffset + id.FragmentLength);
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

    private static DynamicSqlFinding RemapFinding(DynamicSqlFinding finding, DynamicSqlScript outerScript) =>
        RemapFinding(finding, outerScript.SegmentMap.Map);

    private static DynamicSqlFinding RemapFinding(DynamicSqlFinding finding, Func<int, int, SourceSpan> map)
    {
        var span = map(finding.Line, finding.Column);
        return finding with { SourcePath = span.SourcePath, Line = span.Line, Column = span.Column };
    }

    private static SourceSpan? RemapCallSite(SourceSpan? callSite, DynamicSqlScript outerScript) =>
        callSite is { } span ? outerScript.SegmentMap.Map(span.Line, span.Column) : null;

    /// <summary>The worse (numerically higher) of two <see cref="FindingConfidence"/> values - a finding nested inside a script that itself rested on an assumption is never MORE trustworthy than that assumption.</summary>
    private static FindingConfidence Worse(FindingConfidence a, FindingConfidence b) => (FindingConfidence)Math.Max((int)a, (int)b);

    private static SargabilityFinding Remap(SargabilityFinding finding, DynamicSqlScript script, Func<int, int, SourceSpan> map)
    {
        var span = map(finding.Line, finding.Column);
        return finding with { SourcePath = span.SourcePath, Line = span.Line, Column = span.Column, DynamicSqlCallSite = script.CallSite, Confidence = script.Confidence };
    }

    private static TypedPredicateFinding Remap(TypedPredicateFinding finding, DynamicSqlScript script, Func<int, int, SourceSpan> map)
    {
        var span = map(finding.Line, finding.ColumnPosition);
        return finding with { SourcePath = span.SourcePath, Line = span.Line, ColumnPosition = span.Column, DynamicSqlCallSite = script.CallSite, Confidence = script.Confidence };
    }

    private static ExpressionDerivedFinding Remap(ExpressionDerivedFinding finding, DynamicSqlScript script, Func<int, int, SourceSpan> map)
    {
        var span = map(finding.Line, finding.ColumnPosition);
        return finding with { SourcePath = span.SourcePath, Line = span.Line, ColumnPosition = span.Column, DynamicSqlCallSite = script.CallSite, Confidence = script.Confidence };
    }

    private static CollationConflictFinding Remap(CollationConflictFinding finding, DynamicSqlScript script, Func<int, int, SourceSpan> map)
    {
        var span = map(finding.Line, finding.ColumnPosition);
        return finding with { SourcePath = span.SourcePath, Line = span.Line, ColumnPosition = span.Column, DynamicSqlCallSite = script.CallSite, Confidence = script.Confidence };
    }

    private static WriteLossFinding Remap(WriteLossFinding finding, DynamicSqlScript script, Func<int, int, SourceSpan> map)
    {
        var span = map(finding.Line, finding.ColumnPosition);
        return finding with { SourcePath = span.SourcePath, Line = span.Line, ColumnPosition = span.Column, DynamicSqlCallSite = script.CallSite, Confidence = script.Confidence };
    }

    private static SkippedConstruct Remap(SkippedConstruct entry, DynamicSqlScript script) =>
        Remap(entry, script.SegmentMap.Map);

    private static SkippedConstruct Remap(SkippedConstruct entry, Func<int, int, SourceSpan> map)
    {
        var span = map(entry.Line, entry.Column);
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
