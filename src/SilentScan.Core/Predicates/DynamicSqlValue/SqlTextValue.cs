using SilentScan.Core.Catalog;
using SilentScan.Core.Predicates;

namespace SilentScan.Core.Predicates.DynamicSqlValue;

/// <summary>One branch's own known value, preserved through an overall taint - see <see cref="SqlTextValue.GuardedAlternatives"/>.</summary>
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
    /// concatenation instead of once, deliberately, in <see cref="Expand"/>). The result is a
    /// fresh value, not itself a declared variable, so <see cref="DeclaredType"/> is always null.
    /// </summary>
    public static SqlTextValue Concat(SqlTextValue a, SqlTextValue b)
    {
        if (a is Tainted taintedA)
        {
            if (taintedA.GuardedAlternatives is { Count: > 0 } alternatives && b is Template)
            {
                var extended = alternatives.Select(alt => alt with { Value = (Template)Concat(alt.Value, b) }).ToList();
                return taintedA with { GuardedAlternatives = extended };
            }

            return a;
        }

        if (b is Tainted) return b;

        var aTemplate = (Template)a;
        var bTemplate = (Template)b;
        return new Template([.. aTemplate.Pieces, .. bTemplate.Pieces]);
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
    /// at <see cref="MaxGuardedAlternatives"/>, oldest dropped first.
    /// </summary>
    public static SqlTextValue WithGuardedAlternative(SqlTextValue value, string guardText, Template branchValue)
    {
        var existing = value.GuardedAlternatives ?? [];
        var combined = existing.Where(a => !string.Equals(a.GuardText, guardText, StringComparison.Ordinal)).ToList();
        combined.Add(new GuardedAlternative(guardText, branchValue));
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
            (Tainted bothTaintedA, Tainted) => bothTaintedA with { DeclaredType = carriedType },
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
    /// <see cref="Catalog.SqlType"/>, enums) IS safe under the compiler-generated equality, so
    /// only the list-valued hops need this explicit recursion.
    /// </summary>
    public static bool StructurallyEqual(SqlTextValue a, SqlTextValue b) => (a, b) switch
    {
        (Tainted x, Tainted y) => x.Reason == y.Reason && x.Location.Equals(y.Location) && x.DeclaredType == y.DeclaredType,
        (Template x, Template y) => x.DeclaredType == y.DeclaredType && PiecesEqual(x.Pieces, y.Pieces),
        _ => false,
    };

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
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<FlatPiece>> Expand(Template template, int maxAssemblies)
    {
        IEnumerable<IReadOnlyList<FlatPiece>> assemblies = new[] { (IReadOnlyList<FlatPiece>)[] };

        foreach (var piece in template.Pieces)
        {
            assemblies = piece switch
            {
                TemplatePiece.Choice choice => assemblies.SelectMany(prefix =>
                    choice.Alternatives.SelectMany(alt => Expand(alt, maxAssemblies).Select(altAssembly => Append(prefix, altAssembly)))),
                _ => assemblies.Select(prefix => Append(prefix, FlatPiece.From(piece))),
            };
        }

        var result = assemblies.ToList();
        if (result.Count > maxAssemblies)
        {
            throw new InvalidOperationException(
                $"Expand produced {result.Count} assemblies, exceeding the {maxAssemblies} cap - Widen should have collapsed this Choice before Expand ever ran.");
        }

        return result;
    }

    private static IReadOnlyList<FlatPiece> Append(IReadOnlyList<FlatPiece> prefix, FlatPiece piece) => [.. prefix, piece];

    private static IReadOnlyList<FlatPiece> Append(IReadOnlyList<FlatPiece> prefix, IReadOnlyList<FlatPiece> suffix) => [.. prefix, .. suffix];

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
