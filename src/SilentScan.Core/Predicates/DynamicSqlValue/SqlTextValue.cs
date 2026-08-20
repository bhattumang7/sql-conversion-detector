using SilentScan.Core.Catalog;
using SilentScan.Core.Predicates;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates.DynamicSqlValue;

/// <summary>One branch's own known value, preserved through an overall taint - see <see cref="SqlTextValue.GuardedAlternatives"/>. Invariant: <see cref="Value"/> never carries <see cref="SqlTextValue.GuardedAlternatives"/> of its own (enforced at every store - see <c>SqlTextValue.WithoutOwnAlternatives</c>).</summary>
public sealed record GuardedAlternative(string GuardText, SqlTextValue.Template Value);

/// <summary>
/// The dataflow lattice value for one dynamic-SQL variable: either a <see cref="Template"/> (a
/// literal/typed-hole/choice tree still capable of contributing real text) or
/// <see cref="Tainted"/> (bottom - analysis gave up, with a machine-readable reason). Replaces
/// the old scanner's <c>FoldState</c>/<c>LiteralSegment</c> string-splicing model; see
/// docs/dynamic-sql-rebuild-plan.md for the design rationale.
/// </summary>
public abstract record SqlTextValue
{
    /// <summary>
    /// The variable's OWN declared type, if statically known - independent of fold/taint status,
    /// and carried unchanged through <see cref="Concat"/>/taint/widen by every transfer function
    /// that assigns TO a declared variable (mirrors the old scanner's <c>FoldState.DeclaredType</c>).
    /// This is what lets a later <see cref="Join"/> recover a typed <see cref="TemplatePiece.Hole"/>
    /// instead of an untyped <see cref="Tainted"/> when two branches produce structurally
    /// incompatible values for the same variable. A value produced by <see cref="Concat"/> (a
    /// fresh piece of text, not itself a declared variable) has no declared type of its own.
    /// </summary>
    public SqlType? DeclaredType { get; init; }

    /// <summary>
    /// A side-channel that survives THROUGH a taint: when an IF's THEN branch resolves a
    /// variable to a real value but the ELSE branch does not (or vice versa - see
    /// <see cref="DynamicSqlCfg"/>'s own join-branch tracking), the branch that DID resolve is
    /// remembered here, tagged by the guard's own canonical predicate text, even though the
    /// variable's OVERALL value at this point is <see cref="Tainted"/> (we don't know which
    /// branch actually ran). A later consumer that can independently prove the SAME branch is
    /// the one in play - directly, an EXEC's own argument being exactly this tainted variable,
    /// with nothing else known - can recover the alternative's value rather than declining
    /// outright. Mirrors the old scanner's <c>FoldState.GuardedAlternatives</c>; capped at
    /// <see cref="MaxGuardedAlternatives"/> to bound growth across many joins.
    /// </summary>
    public IReadOnlyList<GuardedAlternative>? GuardedAlternatives { get; init; }

    public sealed record Template(IReadOnlyList<TemplatePiece> Pieces) : SqlTextValue;

    public sealed record Tainted(string Reason, SourceSpan Location) : SqlTextValue;

    /// <summary>Emitted by <see cref="Widen"/> when a <see cref="TemplatePiece.Choice"/>'s expansion exceeds the cap and no uniform type can be recovered - same string the old scanner used for its equivalent cap, so any consumer keying off it (tests, summary histograms) keeps working.</summary>
    public const string CardinalityCapReason = "diverges-across-if-branches:cardinality-cap";

    /// <summary>Emitted by <see cref="Join"/> when neither a structural match nor a uniform type lets two branches merge.</summary>
    public const string DivergesInControlFlowGraphReason = "diverges-in-control-flow-graph";

    /// <summary>
    /// String-concatenates two values left-to-right. <see cref="Tainted"/> absorbs (the FIRST
    /// tainted operand's own reason/location wins - concatenating a known-bad value with anything
    /// else is still bad, and reporting the earliest cause is more useful than the latest) - but
    /// if it carries <see cref="GuardedAlternatives"/>, <paramref name="b"/> is appended onto EACH
    /// alternative's own value too (the old scanner's <c>ConcatAlternativesWithAddend</c>): a
    /// later `SET @sql = @sql + '...'` after a branch that left @sql merely UNRESOLVED, not
    /// unknowable, must not silently drop the alternative the earlier join preserved. Two
    /// <see cref="Template"/>s simply append their piece lists - a <see cref="TemplatePiece.Choice"/>
    /// is NEVER distributed here (that would risk a cartesian explosion at every intermediate
    /// concatenation instead of once, deliberately, in <see cref="Expand"/>). EITHER side's own
    /// GuardedAlternatives are propagated the SAME way as the Tainted case above (the addend
    /// spliced onto each alternative's own value) - this is what lets a plain `EXEC(@sql)` (which
    /// internally Concats @sql onto an empty starting Template - see
    /// <see cref="DynamicSqlTransfer.CompileStringList"/>) still see @sql's own guard tags rather
    /// than losing them the moment they pass through a no-op concatenation. The result is a fresh
    /// value, not itself a declared variable, so <see cref="DeclaredType"/> is always null.
    ///
    /// When neither of those two more-precise recoveries applies but the Tainted operand DOES
    /// carry its own <see cref="DeclaredType"/> (the common shape: a declared
    /// <c>VARCHAR(MAX)</c>/<c>NVARCHAR(MAX)</c> accumulator that became unresolvable partway
    /// through its OWN construction, then gets concatenated onto a sibling accumulator that still
    /// has real literal content - the dominant real-corpus idiom this was traced against:
    /// <c>SET @where = @where + @dynamicFragment</c> chains building a WHERE clause from many
    /// `IF @col = 'x' SET @dynamicFragment = '...'` branches), the taint is demoted from "the
    /// whole concatenated value is unknowable" to "exactly ONE identifiable span of it is
    /// unknowable": a single <see cref="TemplatePiece.Hole"/> tagged <see cref="HoleKind.HavocWrite"/>
    /// (a typed value of unmodeled origin - deliberately NOT <see cref="HoleKind.OptionalFragment"/>,
    /// which <see cref="TemplateRenderer"/> renders straight to a bare space with no retry: correct
    /// only when blank-filling is already known to leave valid grammar behind, never provable here -
    /// an appended <c>AND fragment</c> left blank is itself invalid syntax. HavocWrite instead renders
    /// as an ordinary identifier-shaped placeholder token first and, only if THAT breaks the parse,
    /// falls through to <see cref="DynamicSqlPipeline"/>'s own existing grammar-neutral filler retry
    /// - the same recovery an unfoldable scalar already gets) takes the tainted operand's place in
    /// the piece sequence, and the OTHER operand's real literal/hole pieces survive around it
    /// instead of being discarded outright. This can only ever RECOVER analysis of the surviving
    /// literal text (the base SELECT/FROM/an unrelated real predicate) - it never fabricates the
    /// unresolvable fragment's own content, which stays exactly as unknown as it always was. No
    /// type at all on the Tainted side (a
    /// taint with nothing to attribute a Hole to) keeps today's behavior unchanged - the whole
    /// result stays Tainted, exactly as before this method could do anything more precise. Same
    /// for an EMPTY Template side (no <see cref="TemplatePiece"/> at all - the starting value a
    /// plain <c>EXEC(@sql)</c> Concats @sql onto, per <see cref="DynamicSqlTransfer.CompileStringList"/>'s
    /// own doc comment above): manufacturing a single whole-statement Hole there would recover
    /// NOTHING (there is no real literal content on the other side to preserve) while silently
    /// changing a bare "this call site's entire SQL text is unknowable" case from an immediate,
    /// specific Unanalyzable finding into extra pipeline indirection for no gain - so this demotion
    /// only fires when the surviving Template side already has at least one real piece.
    /// </summary>
    public static SqlTextValue Concat(SqlTextValue a, SqlTextValue b)
    {
        if (a is Tainted taintedA)
        {
            if (taintedA.GuardedAlternatives is { Count: > 0 } alternatives && b is Template)
            {
                // WithoutOwnAlternatives: this is the ONE store into an alternative slot that
                // does not go through WithGuardedAlternative's own flattening funnel - the
                // recursive Concat's result can carry b's alternatives, and storing them nested
                // here would break the depth invariant that helper documents.
                var extended = alternatives.Select(alt => alt with { Value = WithoutOwnAlternatives((Template)Concat(alt.Value, b)) }).ToList();
                return taintedA with { GuardedAlternatives = extended };
            }

            if (b is Template { Pieces.Count: > 0 } bTemplateForHole && taintedA.DeclaredType is { } typeA)
            {
                return new Template([new TemplatePiece.Hole(typeA, taintedA.Location, HoleKind.HavocWrite), .. bTemplateForHole.Pieces]);
            }

            return a;
        }

        if (b is Tainted taintedB)
        {
            if (taintedB.GuardedAlternatives is { Count: > 0 } alternativesB && a is Template)
            {
                // The mirror image of the a-is-Tainted case above: a's own literal/hole prefix
                // (e.g. the "SELECT " in `'SELECT ' + @select`) must PREPEND onto each of b's
                // alternatives, not just get silently dropped by returning b unchanged - a
                // GuardedAlternative's own Value is only ever meaningful as a substitute for the
                // WHOLE consumed expression (EmitScriptsOrFinding's unconditional recovery loop
                // has no other context to fall back on), so an alternative missing its own
                // surrounding literal context would misrepresent a bare fragment (e.g. a raw
                // column list with no SELECT/FROM around it) as if it were the complete dynamic
                // SQL text.
                var extended = alternativesB.Select(alt => alt with { Value = WithoutOwnAlternatives((Template)Concat(a, alt.Value)) }).ToList();
                return taintedB with { GuardedAlternatives = extended };
            }

            if (a is Template { Pieces.Count: > 0 } aTemplateForHole && taintedB.DeclaredType is { } typeB)
            {
                return new Template([.. aTemplateForHole.Pieces, new TemplatePiece.Hole(typeB, taintedB.Location, HoleKind.HavocWrite)]);
            }

            return b;
        }

        var aTemplate = (Template)a;
        var bTemplate = (Template)b;
        var result = new Template([.. aTemplate.Pieces, .. bTemplate.Pieces]);
        return PropagateGuardedAlternativesThroughConcat(result, aTemplate, bTemplate);
    }

    private static Template PropagateGuardedAlternativesThroughConcat(Template result, Template a, Template b)
    {
        var combined = (SqlTextValue)result;
        if (a.GuardedAlternatives is { Count: > 0 } aAlternatives)
        {
            foreach (var alt in aAlternatives)
            {
                combined = WithGuardedAlternative(combined, alt.GuardText, (Template)Concat(alt.Value, b));
            }
        }

        if (b.GuardedAlternatives is { Count: > 0 } bAlternatives)
        {
            foreach (var alt in bAlternatives)
            {
                combined = WithGuardedAlternative(combined, alt.GuardText, (Template)Concat(a, alt.Value));
            }
        }

        return (Template)combined;
    }

    /// <summary>
    /// Caps how many <see cref="GuardedAlternative"/>s a single value accumulates across
    /// repeated joins - same purpose as <see cref="Widen"/>'s own cap on Choice growth, applied
    /// to this side-channel instead.
    /// </summary>
    public const int MaxGuardedAlternatives = 8;

    /// <summary>
    /// Attaches one more <see cref="GuardedAlternative"/> to <paramref name="value"/> - used by
    /// <see cref="DynamicSqlCfg"/>'s own IF-join fixup (see that class for why branch identity
    /// must be tracked there, not inside <see cref="Join"/> itself, which is branch-agnostic).
    /// Deduplicates by <see cref="GuardedAlternative.GuardText"/> (a later attempt under the
    /// SAME guard text replaces the earlier one rather than accumulating a duplicate) and caps
    /// at <see cref="MaxGuardedAlternatives"/>, oldest dropped first. The stored value is
    /// FLATTENED via <see cref="WithoutOwnAlternatives"/> - see that helper for the depth
    /// invariant this maintains and the measured blowup it prevents.
    /// </summary>
    public static SqlTextValue WithGuardedAlternative(SqlTextValue value, string guardText, Template branchValue)
    {
        var existing = value.GuardedAlternatives ?? [];
        var combined = existing.Where(a => !string.Equals(a.GuardText, guardText, StringComparison.Ordinal)).ToList();
        combined.Add(new GuardedAlternative(guardText, WithoutOwnAlternatives(branchValue)));
        if (combined.Count > MaxGuardedAlternatives)
        {
            combined.RemoveAt(0);
        }

        return value switch
        {
            Template t => t with { GuardedAlternatives = combined },
            Tainted t => t with { GuardedAlternatives = combined },
            _ => value,
        };
    }

    /// <summary>
    /// Folds <paramref name="b"/>'s own <see cref="GuardedAlternatives"/> into <paramref name="a"/>'s
    /// rather than <see cref="Join"/>'s both-Tainted case silently keeping only <paramref name="a"/>'s
    /// - needed for a join that follows ANOTHER join under the same lineage of guards (e.g.
    /// `IF g1 SET @x = @x + lit ELSE SET @x = unfoldable()` immediately followed by
    /// `IF g2 SET @x = f(@x)`): if <c>f</c>'s own fold itself declines but still recovers a
    /// refined alternative for guard g1 (see <c>ExpressionEvaluator.TryTrimThroughAlternatives</c>),
    /// that refined value must not be thrown away just because the unconditional (g2-false) path
    /// is ALSO tainted. On a guard-text collision, <paramref name="a"/>'s own value wins, never
    /// <paramref name="b"/>'s - `a` is <see cref="DynamicSqlCfg"/>'s own THEN-branch predecessor
    /// (its own <c>MergeStateInto</c> always folds the then-side state in first, becoming this
    /// join's `a`), the branch that ran the ADDITIONAL transfer function (e.g. the trim above) on
    /// top of whatever produced the shared ancestor guard's alternative - so it is strictly more
    /// refined/up to date for that guard than `b`'s (the unconditional/ELSE-side) own stale copy,
    /// mirroring the "first operand's own fact wins" convention <see cref="Concat"/> already uses.
    /// </summary>
    private static Tainted MergeAlternatives(Tainted a, Tainted b)
    {
        var merged = (SqlTextValue)a;
        var ownGuards = new HashSet<string>((a.GuardedAlternatives ?? []).Select(alt => alt.GuardText), StringComparer.Ordinal);
        foreach (var alt in (b.GuardedAlternatives ?? []).Where(alt => ownGuards.Add(alt.GuardText)))
        {
            merged = WithGuardedAlternative(merged, alt.GuardText, alt.Value);
        }

        return (Tainted)merged;
    }

    /// <summary>
    /// The depth invariant that keeps the guarded-alternatives side channel bounded: a value
    /// stored AS an alternative never carries alternatives of its own. Without it,
    /// concatenating two alternative-bearing <see cref="Template"/>s nests each side's
    /// alternatives inside the other's stored values
    /// (<see cref="PropagateGuardedAlternativesThroughConcat"/>), so nesting depth grows by one
    /// per concatenation - and the recursive propagation then costs
    /// <see cref="MaxGuardedAlternatives"/>^depth. A real-world stored procedure doing
    /// <c>SET @sql = @sql + @piece</c> accumulation across variables built by IF/ELSE-IF chains
    /// hit exactly this: measured at 78M+ <see cref="Concat"/> calls in 60 seconds with no
    /// convergence (effectively a hang) and multi-GB heap growth, on a shape whose flattened
    /// cost is a few thousand calls. Provably free of any capability loss: every consumer of
    /// this side channel reads only the TOP level - <c>DynamicSqlTransfer.EmitScriptsOrFinding</c>
    /// and <c>TryNarrowByActiveGuard</c> enumerate <see cref="GuardedAlternatives"/> directly,
    /// and <see cref="DynamicSqlCfg"/>'s <c>PropagateNestedGuardedAlternatives</c> lifts a
    /// BRANCH OUT-STATE's own top-level tags (never a stored alternative's nested ones) - so a
    /// depth-2 tag was unreachable dead weight even before this invariant existed.
    /// </summary>
    private static Template WithoutOwnAlternatives(Template template) =>
        template.GuardedAlternatives is { Count: > 0 } ? template with { GuardedAlternatives = null } : template;

    /// <summary>
    /// The one merge operation every control-flow join point uses for the value ITSELF (replaces
    /// the old scanner's per-construct merge logic - <c>MergeUnioningDivergent</c>,
    /// <c>TryMergeFreshlyDeclaredInOneBranchOnly</c>). Branch-agnostic by design - it has no
    /// notion of "then" vs "else", so it never attaches a <see cref="GuardedAlternative"/> itself;
    /// <see cref="DynamicSqlCfg"/> does that separately, from the ONE place branch identity is
    /// still known (see <see cref="WithGuardedAlternative"/>). In order:
    /// (1) structurally identical values merge to themselves - most joins in straight-line-heavy
    /// code hit this; (2) two <see cref="Template"/>s become a single <see cref="TemplatePiece.Choice"/>
    /// (merging into an existing same-<paramref name="guardText"/> Choice on either side rather
    /// than nesting), then immediately <see cref="Widen"/>d against <paramref name="cap"/>; (3)
    /// otherwise, if both sides agree on one <see cref="SqlType"/> (their own
    /// <see cref="DeclaredType"/>, when set - never guessed from unrelated context), the merge
    /// becomes a typed <see cref="TemplatePiece.Hole"/> instead of giving up outright; (4)
    /// otherwise <see cref="Tainted"/>, preferring a genuinely explanatory reason over the generic
    /// <see cref="DivergesInControlFlowGraphReason"/> whenever one is available (see below), still
    /// carrying either side's <see cref="DeclaredType"/> forward for a LATER join to recover.
    /// </summary>
    public static SqlTextValue Join(SqlTextValue a, SqlTextValue b, string guardText, int cap, SourceSpan at)
    {
        if (StructurallyEqual(a, b))
        {
            return a;
        }

        if (a is Template aTemplate && b is Template bTemplate)
        {
            var declaredType = a.DeclaredType ?? b.DeclaredType;

            // Two holes of the SAME type but different origin (e.g. two separate FETCH sites
            // feeding the same loop-carried variable) carry no more information as a Choice than
            // as one hole - both mean "unknown value of this type" identically, so collapsing
            // immediately avoids manufacturing a Choice piece a caller expecting a bare Hole
            // (BuiltinArgument/ExpressionEvaluator's own pattern match) would fail to recognize,
            // silently declining a fold that should have transferred cleanly. This must run
            // BEFORE MergeAsChoice - a Choice-of-holes is never useful, only ever a liability.
            // Keeps `a`'s OWN hole (its own origin/kind) rather than manufacturing a fresh one at
            // the join site: for a loop back-edge, `a` is consistently the entry value (the
            // FIRST occurrence reaching this point), so this is deterministic and reproduces
            // real, traceable provenance instead of an arbitrary synthesized position.
            if (aTemplate.Pieces is [TemplatePiece.Hole { } aHole] && bTemplate.Pieces is [TemplatePiece.Hole { } bHole] && aHole.Type == bHole.Type)
            {
                return aTemplate with { DeclaredType = declaredType };
            }

            var merged = MergeAsChoice(aTemplate, bTemplate, guardText);
            return Widen(merged with { DeclaredType = declaredType }, cap, at);
        }

        var uniform = TryGetUniformType(a, b);
        var carriedType = a.DeclaredType ?? b.DeclaredType;
        if (uniform is { } type)
        {
            return new Template([new TemplatePiece.Hole(type, at, HoleKind.WidenedChoice)]) { DeclaredType = carriedType };
        }

        // At least one side is Tainted here (the all-Template case already returned above).
        // Exactly one side Tainted, the other a real Template that could not recover a
        // uniform-type Hole above: the taint here is NOT actually "control flow diverges" in any
        // useful sense - it is "one branch produced a genuinely unresolvable value, the other a
        // known one this class just happened not to reduce to a shared type". Reporting the
        // ALREADY-TAINTED side's own specific reason is strictly more informative than the generic
        // sentinel, and attaching the known side as a GuardedAlternative under guardText means a
        // later consumer that independently proves THAT branch is the one in play (an EXEC fed
        // this value directly, with nothing else known) can still recover real text - the same
        // recovery DynamicSqlCfg's own IF-only ApplyGuardedAlternativeFixup provides, generalized
        // here to every join site (loop back-edges, TRY/CATCH, GOTO convergence), not just IF/ELSE.
        // Both sides Tainted with different reasons (StructurallyEqual already caught the
        // same-reason case) keeps `a`'s reason - the same "first cause wins" convention as Concat.
        return (a, b) switch
        {
            (Tainted onlyA, Template bOnly) => WithGuardedAlternative(onlyA with { DeclaredType = carriedType }, guardText, bOnly),
            (Template aOnly, Tainted onlyB) => WithGuardedAlternative(onlyB with { DeclaredType = carriedType }, guardText, aOnly),
            (Tainted bothTaintedA, Tainted bothTaintedB) => MergeAlternatives(bothTaintedA with { DeclaredType = carriedType }, bothTaintedB),
            _ => new Tainted(DivergesInControlFlowGraphReason, at) { DeclaredType = carriedType }, // defensive: unreachable given today's two-subtype lattice
        };
    }

    /// <summary>
    /// Merges two Templates for the same join point into one Choice - if EITHER side is already
    /// a single-piece Choice under the SAME <paramref name="guardText"/>, its alternatives extend
    /// (deduped structurally) rather than nesting a redundant Choice-of-Choice. This is what keeps
    /// a loop's fixpoint iteration finite: repeatedly joining "the same variable, widening by one
    /// more alternative each round" converges once no NEW alternative appears, rather than growing
    /// a new nesting level every round.
    /// </summary>
    private static Template MergeAsChoice(Template a, Template b, string guardText)
    {
        var aAlternatives = AsChoiceAlternatives(a, guardText);
        var bAlternatives = AsChoiceAlternatives(b, guardText);

        if (aAlternatives is null && bAlternatives is null)
        {
            return new Template([new TemplatePiece.Choice(guardText, [a, b])]);
        }

        var combined = new List<Template>(aAlternatives ?? [a]);
        combined.AddRange((bAlternatives ?? [b]).Where(alt => !combined.Any(existing => StructurallyEqual(existing, alt))));

        return new Template([new TemplatePiece.Choice(guardText, combined)]);
    }

    private static IReadOnlyList<Template>? AsChoiceAlternatives(Template value, string guardText) =>
        value.Pieces is [TemplatePiece.Choice { GuardText: var g } choice] && string.Equals(g, guardText, StringComparison.Ordinal)
            ? choice.Alternatives
            : null;

    /// <summary>
    /// Deep value equality for <see cref="SqlTextValue"/>/<see cref="TemplatePiece"/> - NOT the
    /// same as the compiler-generated record <c>Equals</c>/<c>==</c>, which for a record holding
    /// an <see cref="IReadOnlyList{T}"/> field (<see cref="Template.Pieces"/>,
    /// <see cref="TemplatePiece.Choice.Alternatives"/>) compares that field by REFERENCE (the
    /// default <see cref="List{T}"/>/array backing store implements no structural equality of its
    /// own), so two independently-built values with identical content would otherwise compare
    /// unequal. Every other field on every record here (strings, <see cref="SourceSpan"/>,
    /// <see cref="TypeInference.SqlType"/>, enums) IS safe under the compiler-generated equality, so
    /// only the list-valued hops need this explicit recursion.
    /// </summary>
    public static bool StructurallyEqual(SqlTextValue a, SqlTextValue b) => (a, b) switch
    {
        (Tainted x, Tainted y) => x.Reason == y.Reason && x.Location.Equals(y.Location) && x.DeclaredType == y.DeclaredType && GuardedAlternativesEqual(x.GuardedAlternatives, y.GuardedAlternatives),
        (Template x, Template y) => x.DeclaredType == y.DeclaredType && PiecesEqual(x.Pieces, y.Pieces) && GuardedAlternativesEqual(x.GuardedAlternatives, y.GuardedAlternatives),
        _ => false,
    };

    /// <summary>
    /// Order-independent equality over the <see cref="GuardedAlternatives"/> side channel, keyed
    /// by <see cref="GuardedAlternative.GuardText"/> (the same key <see cref="WithGuardedAlternative"/>
    /// dedupes by, so two lists containing the same (guardText, value) pairs in different orders -
    /// an artifact of which side of a join happened to process a guard first - are still equal).
    /// Missing entirely on both sides is the common case and short-circuits without allocating.
    /// This closes a real gap in <see cref="StructurallyEqual"/>'s own OLD behavior (comparing
    /// only Reason/Location/DeclaredType for Tainted, ignoring this field outright): two Tainted
    /// values that share the same cause but were refined to carry DIFFERENT known alternatives
    /// (e.g. one further narrowed by a later transfer function - see
    /// <c>ExpressionEvaluator.TryTrimThroughAlternatives</c>) were treated as identical, silently
    /// discarding whichever side's alternatives <see cref="Join"/>'s own equal-shortcut, or
    /// <see cref="DynamicSqlCfg"/>'s <c>ApplyGuardedAlternativeFixup</c> early-continue, happened
    /// not to keep.
    /// </summary>
    private static bool GuardedAlternativesEqual(IReadOnlyList<GuardedAlternative>? a, IReadOnlyList<GuardedAlternative>? b)
    {
        if (a is null or { Count: 0 } && b is null or { Count: 0 })
        {
            return true;
        }

        if (a is null || b is null || a.Count != b.Count)
        {
            return false;
        }

        var byGuardTextA = a.ToDictionary(alt => alt.GuardText, alt => alt.Value, StringComparer.Ordinal);
        return b.All(altB => byGuardTextA.TryGetValue(altB.GuardText, out var valueA) && StructurallyEqual(valueA, altB.Value));
    }

    private static bool PiecesEqual(IReadOnlyList<TemplatePiece> a, IReadOnlyList<TemplatePiece> b) =>
        a.Count == b.Count && a.Zip(b, PieceEqual).All(equal => equal);

    private static bool PieceEqual(TemplatePiece a, TemplatePiece b) => (a, b) switch
    {
        (TemplatePiece.Lit x, TemplatePiece.Lit y) => x == y,
        (TemplatePiece.Hole x, TemplatePiece.Hole y) => x == y,
        (TemplatePiece.Choice x, TemplatePiece.Choice y) => x.GuardText == y.GuardText && AlternativesEqual(x.Alternatives, y.Alternatives),
        _ => false,
    };

    private static bool AlternativesEqual(IReadOnlyList<Template> a, IReadOnlyList<Template> b) =>
        a.Count == b.Count && a.Zip(b, StructurallyEqual).All(equal => equal);

    /// <summary>
    /// Collapses any <see cref="TemplatePiece.Choice"/> whose expansion would exceed
    /// <paramref name="cap"/> assemblies to a typed <see cref="TemplatePiece.Hole"/> (when a
    /// uniform type can be recovered) or the whole value to <see cref="Tainted"/> (when it can't) -
    /// the SAME cap and the SAME <see cref="CardinalityCapReason"/> string the old scanner used
    /// for <c>MaxAssembliesPerVariable</c>, just enforced as a value operation instead of scattered
    /// across the union/cross-concat/argument-combination call sites that used to each need it.
    /// Idempotent: widening an already-widened value returns it unchanged (no Choice survives
    /// widening, so a second pass finds nothing left to collapse) - this is also why a loop's
    /// fixpoint, which widens every round, is guaranteed to terminate rather than growing forever.
    /// If ANY inner alternative itself widens to <see cref="Tainted"/> (a nested divergence that
    /// couldn't resolve), the whole value taints - a conservative default, matching the
    /// unmodeled-is-conservative philosophy for the dataflow engine as a whole.
    /// </summary>
    public static SqlTextValue Widen(SqlTextValue value, int cap, SourceSpan at)
    {
        if (value is Tainted)
        {
            return value;
        }

        var template = (Template)value;
        var newPieces = new List<TemplatePiece>(template.Pieces.Count);
        foreach (var piece in template.Pieces)
        {
            if (piece is not TemplatePiece.Choice choice)
            {
                newPieces.Add(piece);
                continue;
            }

            var widenedAlternatives = new List<Template>(choice.Alternatives.Count);
            foreach (var alternative in choice.Alternatives)
            {
                var widenedAlternative = Widen(alternative, cap, at);
                if (widenedAlternative is Tainted)
                {
                    return new Tainted(CardinalityCapReason, at) { DeclaredType = value.DeclaredType };
                }

                widenedAlternatives.Add((Template)widenedAlternative);
            }

            newPieces.Add(choice with { Alternatives = widenedAlternatives });
        }

        var widened = new Template(newPieces) { DeclaredType = value.DeclaredType };
        if (ExpansionCount(widened) <= cap)
        {
            return widened;
        }

        var uniform = value.DeclaredType ?? TryGetUniformType(newPieces.OfType<TemplatePiece.Choice>().SelectMany(c => c.Alternatives));
        return uniform is { } type
            ? new Template([new TemplatePiece.Hole(type, at, HoleKind.WidenedChoice)]) { DeclaredType = value.DeclaredType }
            : new Tainted(CardinalityCapReason, at) { DeclaredType = value.DeclaredType };
    }

    /// <summary>
    /// Emitted when a value's total expanded size (see <see cref="ExpandedPieceTotal"/>) exceeds
    /// <see cref="MaxExpandedPieceTotal"/> - the assembly-LENGTH counterpart to
    /// <see cref="CardinalityCapReason"/>'s assembly-COUNT cap. Same reason-string style so
    /// summary consumers group it alongside the other capacity declines.
    /// </summary>
    public const string ExpansionSizeCapReason = "expanded-assembly-size-cap";

    /// <summary>
    /// The most total <see cref="FlatPiece"/>s across all of a value's expanded assemblies that
    /// <see cref="Expand"/> is allowed to materialize. <see cref="Widen"/> caps how many
    /// assemblies exist (32), but nothing upstream caps how LONG one is - and a real database
    /// produced a value whose expansion, though within the assembly-count cap, totalled tens of
    /// millions of pieces (~9.5GB materialized, an OOM) from a 280KB procedure body. A million
    /// pieces is far beyond any dynamic SQL text worth reparsing as a probe script (the largest
    /// real module body observed - 3.5MB of T-SQL - folds to well under 100k pieces), so
    /// exceeding this is a capacity decline (<see cref="ExpansionSizeCapReason"/>, an honest
    /// Unanalyzable finding), never a silent drop.
    /// </summary>
    public const long MaxExpandedPieceTotal = 1L << 20;

    /// <summary>
    /// The exact total number of <see cref="FlatPiece"/>s <see cref="Expand"/> would emit,
    /// summed across every assembly - computed WITHOUT materializing anything, so
    /// <c>DynamicSqlTransfer.TryEmitFromValue</c> can decline an absurdly large expansion
    /// (<see cref="MaxExpandedPieceTotal"/>) before it allocates. The recurrence per piece:
    /// a plain piece adds one to each of the current assemblies (total += count); a Choice
    /// multiplies the assemblies (count *= sum of alternatives' counts) after replicating every
    /// existing prefix (total *= that sum) and appending each alternative expansion onto each
    /// prefix (total += count * sum of alternatives' own totals).
    /// </summary>
    public static long ExpandedPieceTotal(Template template)
    {
        long count = 1;
        long total = 0;
        foreach (var piece in template.Pieces)
        {
            if (piece is TemplatePiece.Choice choice)
            {
                long alternativeCountSum = 0;
                long alternativeTotalSum = 0;
                foreach (var alternative in choice.Alternatives)
                {
                    alternativeCountSum += ExpansionCount(alternative);
                    alternativeTotalSum += ExpandedPieceTotal(alternative);
                }

                total = total * alternativeCountSum + count * alternativeTotalSum;
                count *= alternativeCountSum;
            }
            else
            {
                total += count;
            }
        }

        return total;
    }

    /// <summary>The number of concrete assemblies <see cref="Expand"/> would produce - the product, across every <see cref="TemplatePiece.Choice"/> piece, of the sum of each alternative's own count (1 for a Choice-free Template).</summary>
    public static long ExpansionCount(Template template)
    {
        long count = 1;
        foreach (var piece in template.Pieces)
        {
            if (piece is TemplatePiece.Choice choice)
            {
                count *= choice.Alternatives.Sum(ExpansionCount);
            }
        }

        return count;
    }

    /// <summary>
    /// Resolves every <see cref="TemplatePiece.Choice"/> to concrete assemblies via a cartesian
    /// expansion, called EXACTLY ONCE per script (in <c>BuildScript</c>) - a Choice stays lazy
    /// everywhere else. <paramref name="maxAssemblies"/> is an assertion, not a decline path:
    /// <see cref="Widen"/> must already have collapsed anything that would exceed it, so hitting
    /// the cap here means Widen has a bug, not that this call site needs its own cap-handling.
    /// Imperative and linear in the OUTPUT size, deliberately: <see cref="Widen"/> caps how many
    /// assemblies exist but nothing caps how many PIECES one carries, and a real multi-megabyte
    /// stored procedure body builds dynamic SQL through tens of thousands of concatenation
    /// pieces - an earlier implementation that layered one lazy Select/SelectMany per piece and
    /// re-copied the whole prefix list per appended piece was O(pieces^2) with a pieces-deep
    /// iterator chain, and OOM'd at ~9.5GB expanding exactly such a proc. Non-Choice pieces
    /// append IN PLACE to every assembly under construction; only a Choice forks (copies), and
    /// fork counts are already bounded by <see cref="Widen"/>'s own cap. The emitted assembly
    /// ORDER is identical to the lazy version's (prefix-major, then alternative, then the
    /// alternative's own expansion) - deterministic output ordering is a CLAUDE.md requirement,
    /// so this is a load-bearing property, not a stylistic one.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<FlatPiece>> Expand(Template template, int maxAssemblies)
    {
        var assemblies = new List<List<FlatPiece>> { new() };

        foreach (var piece in template.Pieces)
        {
            if (piece is TemplatePiece.Choice choice)
            {
                assemblies = ForkAssemblies(assemblies, choice, maxAssemblies);
            }
            else
            {
                var flat = FlatPiece.From(piece);
                foreach (var assembly in assemblies)
                {
                    assembly.Add(flat);
                }
            }
        }

        if (assemblies.Count > maxAssemblies)
        {
            throw new InvalidOperationException(
                $"Expand produced {assemblies.Count} assemblies, exceeding the {maxAssemblies} cap - Widen should have collapsed this Choice before Expand ever ran.");
        }

        return assemblies;
    }

    /// <summary>Extracted from <see cref="Expand"/> solely to keep that method's Cognitive Complexity (Sonar S3776) under the triple-nested fork loop it would otherwise carry. Replicates each existing assembly once per (alternative, alternative-assembly) pair, preserving <see cref="Expand"/>'s documented prefix-major output order.</summary>
    private static List<List<FlatPiece>> ForkAssemblies(List<List<FlatPiece>> assemblies, TemplatePiece.Choice choice, int maxAssemblies)
    {
        var forked = new List<List<FlatPiece>>();
        foreach (var prefix in assemblies)
        {
            foreach (var alternative in choice.Alternatives)
            {
                foreach (var alternativeAssembly in Expand(alternative, maxAssemblies))
                {
                    var combined = new List<FlatPiece>(prefix.Count + alternativeAssembly.Count);
                    combined.AddRange(prefix);
                    combined.AddRange(alternativeAssembly);
                    forked.Add(combined);
                }
            }
        }

        return forked;
    }

    public static bool ContainsHole(IReadOnlyList<FlatPiece> assembly) => assembly.Any(p => p is FlatPiece.Hole);

    /// <summary>
    /// The single SqlType two (or more, via the <see cref="IEnumerable{T}"/> overload) values
    /// agree on, or null if they disagree or either has none - the ONLY source of a type here is
    /// each value's own <see cref="DeclaredType"/> (never inferred from unrelated context, per
    /// CLAUDE.md's "never guess" policy: a variable's declared type is a real fact from its own
    /// DECLARE/parameter list, not a guess).
    /// </summary>
    public static SqlType? TryGetUniformType(SqlTextValue a, SqlTextValue b) => TryGetUniformType([a, b]);

    public static SqlType? TryGetUniformType(IEnumerable<SqlTextValue> values)
    {
        SqlType? uniform = null;
        var any = false;
        foreach (var value in values)
        {
            any = true;
            if (value.DeclaredType is not { } type)
            {
                return null;
            }

            if (uniform is null)
            {
                uniform = type;
            }
            else if (uniform != type)
            {
                return null;
            }
        }

        return any ? uniform : null;
    }
}
