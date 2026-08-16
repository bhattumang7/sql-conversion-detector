using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates.DynamicSqlValue;
using SilentScan.Core.Reporting;

namespace SilentScan.Core.Predicates;

/// <summary>
/// CLAUDE.md's dynamic SQL policy: reparses the provably-constant inner SQL of
/// EXEC('...')/sp_executesql N'...' call sites (see the dynamic SQL engine) through the
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

    private static readonly IReadOnlyDictionary<string, TvfFenceOrigin> NoTvfFenceMap = new Dictionary<string, TvfFenceOrigin>();

    private static readonly IReadOnlyDictionary<string, ScalarUdfOrigin> NoScalarUdfMap = new Dictionary<string, ScalarUdfOrigin>();

    /// <summary>Bundles everything a script needs from OUTSIDE its own recursion (the catalog/lineage/fence-maps every level shares, plus the proc-call-graph scoping) - kept as one record so splitting ProcessScript/AnalyzeNested into smaller pieces to reduce cognitive complexity doesn't just turn into another long parameter list (Sonar S107).</summary>
    private readonly record struct PipelineContext(
        DatabaseCatalog Catalog,
        LineageCatalog Lineage,
        IReadOnlyDictionary<string, TvfFenceOrigin> TvfFenceMap,
        IReadOnlyDictionary<string, ScalarUdfOrigin> ScalarUdfMap,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? CallerScopeByCalleeScope);

    public static DynamicSqlPipelineResult Analyze(
        IReadOnlyList<DynamicSqlScript> scripts, DatabaseCatalog catalog, LineageCatalog lineage, IReadOnlyDictionary<string, IReadOnlyList<string>>? callerScopeByCalleeScope = null) =>
        Analyze(scripts, new PipelineContext(catalog, lineage, NoTvfFenceMap, NoScalarUdfMap, callerScopeByCalleeScope), depth: 1, seeds: null);

    /// <summary>
    /// Same as the overload above, but also folds provably-constant dynamic SQL through the
    /// MSTVF-as-fence stream (docs/detection-checklist.md Tier 1 #2) - <paramref name="tvfFenceMap"/>
    /// is <see cref="Lineage.TvfFenceMap"/>'s corpus-wide output, built once by the caller (a
    /// dynamic SQL script's own reparsed text can reference a view/iTVF that inherits a fence
    /// exactly like static SQL can). Kept as a separate overload rather than a new required
    /// parameter on the one above so every existing caller (tests, anything that only cares about
    /// the sargability/typed streams) keeps compiling unchanged.
    /// </summary>
    public static DynamicSqlPipelineResult Analyze(
        IReadOnlyList<DynamicSqlScript> scripts, DatabaseCatalog catalog, LineageCatalog lineage, IReadOnlyDictionary<string, TvfFenceOrigin> tvfFenceMap, IReadOnlyDictionary<string, IReadOnlyList<string>>? callerScopeByCalleeScope = null) =>
        Analyze(scripts, catalog, lineage, tvfFenceMap, NoScalarUdfMap, callerScopeByCalleeScope);

    /// <summary>
    /// Same as the overload above, but also folds provably-constant dynamic SQL through the
    /// scalar-UDF stream (docs/detection-checklist.md Tier 1 #1) - <paramref name="scalarUdfMap"/>
    /// is <see cref="Lineage.ScalarUdfMap"/>'s corpus-wide output, built once by the caller.
    /// </summary>
    public static DynamicSqlPipelineResult Analyze(
        IReadOnlyList<DynamicSqlScript> scripts, DatabaseCatalog catalog, LineageCatalog lineage, IReadOnlyDictionary<string, TvfFenceOrigin> tvfFenceMap, IReadOnlyDictionary<string, ScalarUdfOrigin> scalarUdfMap, IReadOnlyDictionary<string, IReadOnlyList<string>>? callerScopeByCalleeScope = null) =>
        Analyze(scripts, new PipelineContext(catalog, lineage, tvfFenceMap, scalarUdfMap, callerScopeByCalleeScope), depth: 1, seeds: null);

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
        PipelineContext context,
        int depth,
        Dictionary<DynamicSqlScript, IReadOnlyDictionary<string, SqlType?>>? seeds)
    {
        var accumulator = new ResultAccumulator();

        // Branch-fold coverage (roadmap "trace dynamic SQL across IF/ELSE/TRY-CATCH branches")
        // can turn ONE call site into several DynamicSqlScripts, one per possible constant
        // assembly - grouping by CallSite (already identical across every assembly of the same
        // site, and scripts already arrive call-site-contiguous from the dynamic SQL engine's own
        // visitation order, so this never reorders anything observably) lets each call site's
        // own substantive findings dedupe against EACH OTHER before joining the overall result,
        // without ever merging two genuinely different call sites' findings together.
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

    private static List<UnparameterizedDynamicSqlFinding> DedupeUnparameterized(List<UnparameterizedDynamicSqlFinding> findings)
    {
        var seen = new HashSet<(string, int, int, UnparameterizedDynamicSqlFindingKind)>();
        return findings.Where(finding => seen.Add(UnparameterizedKey(finding))).ToList();
    }

    private static (string, int, int, UnparameterizedDynamicSqlFindingKind) UnparameterizedKey(UnparameterizedDynamicSqlFinding finding) =>
        (finding.SourcePath, finding.Line, finding.Column, finding.Kind);

    private static List<WriteLossFinding> DedupeWriteLoss(List<WriteLossFinding> findings)
    {
        var seen = new HashSet<(string, string, WriteLossKind, SqlType, SqlType)>();
        return findings.Where(finding => seen.Add(WriteLossKey(finding))).ToList();
    }

    private static (string, string, WriteLossKind, SqlType, SqlType) WriteLossKey(WriteLossFinding finding) =>
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

        public List<TvfFenceFinding> TvfFence { get; } = [];

        public List<ScalarUdfFinding> ScalarUdf { get; } = [];

        public List<UnparameterizedDynamicSqlFinding> Unparameterized { get; } = [];

        public List<SkippedConstruct> Skipped { get; } = [];

        public DynamicSqlPipelineResult ToResult() =>
            new(Findings, Tier1, Typed, ExpressionDerived, CollationConflicts, WriteLoss, TvfFence, ScalarUdf, Unparameterized, Skipped);
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
                // token, but a single space might - see TryReparseWithTargetedElision.
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

        // A script with no placeholder, or one whose placeholders parsed successfully wherever
        // they happen to sit, falls through to the same extraction path uniformly - no per-
        // position allow-list, no special-cased early return, and no "one statement only"
        // restriction either. There USED to be a position classifier here (string literal, or a
        // table reference's own identifier parts) that declined the WHOLE call site the moment a
        // placeholder sat anywhere else - deleted because it inverted the actual soundness
        // argument: a synthesized __silentscan_sym_...__ token can NEVER resolve against the real
        // catalog, in ANY grammar position, so it can only ever surface downstream as an
        // unresolvable column/table/object reference - which the ordinary
        // extractor/lineage/catalog-resolution machinery ALREADY handles by skipping it with its
        // own specific reason (SkippedConstructs), never by guessing. An allow-list of "safe"
        // positions can only ever enumerate a fraction of T-SQL's grammar (ORDER BY, EXECUTE's
        // own procedure-name reference, CREATE-family object names, ...) and every position not
        // yet added cost the ENTIRE statement its analysis, including any genuinely literal
        // predicate elsewhere in the same statement that has nothing to do with the placeholder
        // at all. Removing the gate can only ever LOSE a finding where the placeholder itself
        // would have contributed one (never possible, since it can't resolve) - it can never
        // FABRICATE one. Remap stamps script.Confidence (already Medium whenever a placeholder
        // exists at all) onto whatever it finds.
        innerParseResult = parseResult;
        return true;
    }

    /// <summary>
    /// A placeholder token's own rendered text - <see cref="TemplateRenderer"/>'s private
    /// <c>PlaceholderToken</c> reimplemented here since the two rarely need to agree on anything
    /// else, but MUST agree on this exact shape for the matching below to find anything.
    /// </summary>
    private static string PlaceholderToken(PlaceholderOccurrence occurrence) =>
        $"__silentscan_sym_L{occurrence.Origin.Line}C{occurrence.Origin.Column}__";

    /// <summary>Matches one placeholder's own token text - same shape as <see cref="TemplateRenderer"/>'s private PlaceholderToken, duplicated here (rather than shared) since a ScriptDOM error message is the only other place this exact string ever needs to be recognized rather than produced.</summary>
    [GeneratedRegex(@"__silentscan_sym_L\d+C\d+__")]
    private static partial Regex PlaceholderTokenRegex();

    /// <summary>
    /// The fallback for a symbolic value that broke the parse outright: it may not stand for a
    /// single scalar at all, but for a whole optional clause/fragment (an appended filter, a
    /// query hint), an entire missing search_condition, or a single missing value - no
    /// identifier-shaped token can ever sit legally in any of those positions, but the right
    /// grammar-neutral filler (see <see cref="ElisionFillerCandidates"/>) might. Blanking EVERY
    /// placeholder unconditionally (the old, cruder policy) is unsound whenever a script mixes
    /// one genuinely-optional placeholder with another that is a real, load-bearing identifier
    /// elsewhere in the SAME script (a real corpus shape: SQL-Server-First-Responder-Kit's
    /// sp_BlitzFirst.sql builds a temp table name AND a TOP-style clause as two separate
    /// placeholders in the same statement) - blanking the load-bearing one breaks a parse that
    /// blanking only the genuinely-optional one would have fixed. Instead, this targets ONLY the
    /// placeholder(s) ScriptDOM's own error message actually names ("Incorrect syntax near
    /// '__silentscan_sym_...__'"), elides just those, and reparses - repeating with any NEWLY
    /// blamed placeholder each round (a token-render depending on the one just elided can itself
    /// become the new complaint) until it succeeds, no round makes further progress, or every
    /// placeholder is already elided - then, if that whole loop still didn't converge, retrying
    /// the entire thing with the NEXT filler candidate. This can only ever CONVERGE to the old
    /// blank-everything behavior in the worst case, never do worse - every input the old policy
    /// recovered, this recovers too, in the same or fewer rounds. Every candidate filler is
    /// provably incapable of fabricating a typed-predicate verdict about a real column (see each
    /// candidate's own reasoning on <see cref="ElisionFillerCandidates"/>) - extraction against
    /// the result can only ever under-report relative to the true runtime query (the elided
    /// fragment's own content stays genuinely unknown), never fabricate a finding that depends on
    /// it.
    /// </summary>
    /// <summary>
    /// Fillers tried, in order, for each blamed placeholder round - a SINGLE filler is applied
    /// uniformly across every blamed placeholder within one attempt (never mixed per-placeholder;
    /// that would require knowing each one's own grammar position, which this scanner has no way
    /// to determine). Each candidate is provably incapable of fabricating a typed-predicate
    /// verdict about a real column, so widening past the original space-only policy costs nothing
    /// in soundness:
    /// - " " (space): the original policy - correct for a placeholder standing in for a whole
    ///   OPTIONAL clause/fragment that can vanish entirely (a TOP-style clause, an appended
    ///   filter). Tried first since it changes the fewest tokens.
    /// - "1=1": correct for a placeholder standing in for an entire missing search_condition
    ///   (a bare `WHERE __ph__`, or `WHERE __ph__ AND real.condition`) - integer-literal-vs-
    ///   integer-literal has no column operand at all, so TypedPredicateExtractor ledgers it as
    ///   the existing benign "no column operand" skip, never attributes a verdict to a real
    ///   column.
    /// - "NULL": correct for a placeholder standing in for a single missing SCALAR value (a
    ///   comparison's RHS, a SELECT-list item, a function argument) - LiteralTypeResolver
    ///   resolves NullLiteral to a null SqlType, so a predicate comparing a real column against
    ///   it collapses to the SAME "operand-type-unresolved" Unknown a column vs. an unseeded
    ///   parameter already gets, never a fabricated verdict.
    /// - "(SELECT 1)": correct for a placeholder standing in for an ENTIRE missing query (real
    ///   corpus shape: `DECLARE cur CURSOR FOR __ph__`, or a FROM-clause derived-table source) -
    ///   no real grammar position needing a full query accepts a bare scalar/boolean filler
    ///   instead, so this is tried only after all three above have already failed. `SELECT 1` has
    ///   no column operand and no FROM clause of its own, so it can never contribute a fabricated
    ///   predicate finding either.
    /// </summary>
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

                // Every position within a MULTI-character filler (e.g. "1=1") maps back to the
                // SAME original offset - there is no finer-grained original position to give it,
                // and Map() only ever needs to answer "does this location belong to elided
                // filler text or real source", not which filler character specifically.
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

        // A script whose OWN identity rests on a placeholder never recurses into further
        // nested dynamic SQL - a real runtime value could reshape the surrounding text in ways
        // this scanner never modeled, so treating a nested candidate's findings as independently
        // trustworthy would launder that same unproven assumption one level deeper. A partially-
        // analyzed script (elisionMap non-null) always has placeholders too, so it already routes
        // through the same refusal - never AnalyzeNested, which assumes innerParseResult's
        // coordinates are the script's own untranslated InnerText.
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

    /// <summary>
    /// docs/detection-checklist.md Tier 2 "Dynamic SQL quality" items 1+2 - see
    /// <see cref="UnparameterizedDynamicSqlFinding"/> for the full mechanism. Runs against THIS
    /// script's own reparse (never the nested-recursion machinery below) since the signal - where
    /// a real concatenation boundary from <see cref="DynamicSqlSegmentMap.ConcatenationBoundaryOffsets"/>
    /// lands grammatically - is entirely local to one assembly.
    /// </summary>
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

    /// <summary>Split out of <see cref="ProcessScript"/> purely to keep its own cognitive complexity under the Sonar threshold (Sonar S3776) - both scans share the same reparsed script/map/accumulator, so there is nothing else to parameterize.</summary>
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
        // Propagates the outer script's own scope into the nested scanner - the reparsed inner
        // text has no CREATE PROCEDURE wrapper for it to discover the scope from itself, so
        // without this, propagation would silently die at nesting depth 2. No callGraph/
        // outputSummaryIndex here, matching the old scanner's own behavior for a NESTED
        // reparse - only the top-level scan (ScanReportBuilder) threads those through.
        var nestedExtraction = DynamicSqlScannerV2.Scan(innerParseResult, script.Scope, catalog: context.Catalog);
        var findings = nestedExtraction.Findings.Select(f => RemapFinding(f, script)).ToList();

        if (nestedExtraction.AnalyzableScripts.Count == 0)
        {
            return new DynamicSqlPipelineResult(findings, [], [], [], [], [], [], [], [], []);
        }

        if (depth >= MaxNestingDepth)
        {
            // Never silently drop these (CLAUDE.md) - report exactly how far analysis got and
            // why it stopped, remapped to the real call site that would have been recursed into.
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
        var nestedExtraction = DynamicSqlScannerV2.Scan(innerParseResult, script.Scope);
        var findings = nestedExtraction.Findings.Select(f => RemapFinding(f, map)).ToList();
        findings.AddRange(nestedExtraction.AnalyzableScripts
            .Select(nestedScript => map(nestedScript.CallSite.Line, nestedScript.CallSite.Column))
            .Select(callSite => new DynamicSqlFinding(callSite.SourcePath, callSite.Line, callSite.Column, DynamicSqlOutcome.Unanalyzable, "nested-dynamic-sql-inside-symbolic-value")));

        return new DynamicSqlPipelineResult(findings, [], [], [], [], [], [], [], [], []);
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

    private static TvfFenceFinding Remap(TvfFenceFinding finding, DynamicSqlScript script, Func<int, int, SourceSpan> map)
    {
        var span = map(finding.Line, finding.Column);
        return finding with { SourcePath = span.SourcePath, Line = span.Line, Column = span.Column, DynamicSqlCallSite = script.CallSite, Confidence = script.Confidence };
    }

    private static ScalarUdfFinding Remap(ScalarUdfFinding finding, DynamicSqlScript script, Func<int, int, SourceSpan> map)
    {
        var span = map(finding.Line, finding.Column);
        return finding with { SourcePath = span.SourcePath, Line = span.Line, Column = span.Column, DynamicSqlCallSite = script.CallSite, Confidence = script.Confidence };
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
    /// text (that's what the nested dynamic SQL engine pass was actually parsing).
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

    private static TvfFenceFinding RemapNested(TvfFenceFinding finding, DynamicSqlScript outerScript)
    {
        var span = outerScript.SegmentMap.Map(finding.Line, finding.Column);
        return finding with { SourcePath = span.SourcePath, Line = span.Line, Column = span.Column, DynamicSqlCallSite = RemapCallSite(finding.DynamicSqlCallSite, outerScript), Confidence = Worse(finding.Confidence, outerScript.Confidence) };
    }

    private static ScalarUdfFinding RemapNested(ScalarUdfFinding finding, DynamicSqlScript outerScript)
    {
        var span = outerScript.SegmentMap.Map(finding.Line, finding.Column);
        return finding with { SourcePath = span.SourcePath, Line = span.Line, Column = span.Column, DynamicSqlCallSite = RemapCallSite(finding.DynamicSqlCallSite, outerScript), Confidence = Worse(finding.Confidence, outerScript.Confidence) };
    }

    /// <summary>
    /// Position-only remap, mirroring <see cref="RemapFinding(DynamicSqlFinding, DynamicSqlScript)"/> -
    /// <see cref="UnparameterizedDynamicSqlFinding"/> already points at the call site itself (see
    /// its own doc comment), so a nested finding's Line/Column are expressed in the OUTER script's
    /// reparsed-text coordinates and need exactly one hop through <paramref name="outerScript"/>'s
    /// segment map, same as any other call-site-anchored finding propagating up one nesting level.
    /// </summary>
    private static UnparameterizedDynamicSqlFinding RemapNested(UnparameterizedDynamicSqlFinding finding, DynamicSqlScript outerScript)
    {
        var span = outerScript.SegmentMap.Map(finding.Line, finding.Column);
        return finding with { SourcePath = span.SourcePath, Line = span.Line, Column = span.Column };
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
    IReadOnlyList<TvfFenceFinding> TvfFenceFindings,
    IReadOnlyList<ScalarUdfFinding> ScalarUdfFindings,
    IReadOnlyList<UnparameterizedDynamicSqlFinding> UnparameterizedFindings,
    IReadOnlyList<SkippedConstruct> SkippedConstructs);
