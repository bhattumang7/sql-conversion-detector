using System.Text.Json.Serialization;
using SilentScan.Core.Common;

namespace SilentScan.Core.Predicates;

public enum DuplicationFindingKind
{
    /// <summary>A comment whose stripped content itself reparses cleanly as a plausible T-SQL
    /// statement/batch - not prose that merely mentions a keyword.</summary>
    CommentedOutCode,

    /// <summary>The same non-trivial string literal appears 3 or more times within one module -
    /// a magic value that should be a variable/constant instead.</summary>
    DuplicatedStringLiteral,

    /// <summary>A WHILE loop whose own body unconditionally reaches a BREAK/RETURN/THROW on
    /// every path through the first iteration - it structurally can never loop a second time.</summary>
    SingleIterationLoop,

    /// <summary>A pure no-op assignment: SET/SELECT @x = @x, or an UPDATE's own SET clause
    /// assigning a column to itself.</summary>
    SelfAssignment,

    /// <summary>The identical expression appears on both sides of a comparison, AND/OR, or a
    /// self-referential arithmetic operator (-, /, %) - always the same value, a tautology, or a
    /// fixed degenerate result regardless of what the expression evaluates to.</summary>
    IdenticalBinaryOperands,

    /// <summary>The same unary operator applied twice in a row (NOT NOT x, - - x, ~ ~ x) -
    /// always simplifiable to a single application (or none, for NOT/unary-minus).</summary>
    RepeatedUnaryOperator,

    /// <summary>NOT (x > y) written instead of the simpler, provably equivalent x &lt;= y (and
    /// the analogous rewrites for the other four comparison operators, plus NOT (x IS NULL)
    /// instead of x IS NOT NULL) - a readability suggestion, not a correctness claim.</summary>
    NegatedComparisonAsOpposite,

    /// <summary>A later sibling in an IF/ELSE IF chain, or a later WHEN clause in a CASE
    /// expression, repeats an earlier sibling's own condition verbatim - the later branch can
    /// never be reached, since the earlier identical condition already claims every row/execution
    /// that would have matched it.</summary>
    DuplicateSiblingCondition,

    /// <summary>Two (but not all) branches of an IF/ELSE IF/ELSE chain or a CASE expression have
    /// an identical body/result - either the conditional is partly pointless, or one branch's
    /// body should have differed and a copy-paste mistake left it matching another.</summary>
    IdenticalBranchBodies,

    /// <summary>EVERY branch of an IF/ELSE IF/ELSE chain or CASE expression (including IIF) has
    /// an identical body/result - the entire conditional structure produces the same outcome no
    /// matter which branch is taken, a stronger and more confident claim than
    /// <see cref="IdenticalBranchBodies"/>'s partial-match case.</summary>
    AllBranchesIdentical,

    /// <summary>Two conjuncts of one AND-combined boolean condition compare the SAME operand
    /// against two different numeric literal bounds where one bound's range is already a subset
    /// of (or equal to) the other's - the looser bound adds nothing once combined with AND.
    /// </summary>
    RedundantAndCondition,

    /// <summary>Two conjuncts of one AND-combined boolean condition compare the SAME operand
    /// against two numeric literal bounds whose ranges cannot both hold at once - the whole
    /// condition can never be true, regardless of what the operand's real value is.</summary>
    MutuallyExclusiveAndCondition,

    /// <summary>An IF with no ELSE whose entire THEN body is a single nested IF, also with no
    /// ELSE - semantically identical to one IF combining both conditions with AND, so the nesting
    /// adds a level of indentation without changing behavior.</summary>
    CollapsibleNestedIf,

    /// <summary>An IIF call nested inside another IIF call's own THEN or ELSE branch - T-SQL's
    /// equivalent of a nested ternary expression, correctness-neutral but a real readability/
    /// maintainability risk once nesting goes more than one level deep.</summary>
    NestedConditionalExpression,

    /// <summary>A comparison between two literal values (never involving a column, variable, or
    /// any other non-literal operand) whose truth value is provable at parse time regardless of
    /// any row's real data - the predicate is dead weight (always true) or the query can never
    /// match through this path (always false).</summary>
    AlwaysTrueOrFalseLiteralComparison,
}

/// <summary>
/// docs/detection-checklist.md Tier 4 "Dead and duplicated code" - the pattern-matching half of
/// that checklist bullet (the control-flow/dataflow half - unreachable code, unused labels/
/// variables/parameters, redundant jumps - ships separately as <see cref="DeadCodeFinding"/>).
/// Fully syntax-only: no <see cref="Catalog.DatabaseCatalog"/> needed for any member. No plan-XML
/// oracle either - every member is a directly observable structural/textual AST fact, except
/// <see cref="DuplicationFindingKind.NegatedComparisonAsOpposite"/>'s own equivalence claim, which
/// is a pure three-valued-logic proof (see that finding kind's own scanner-side reasoning) rather
/// than a plan-shape claim needing a live-engine probe.
///
/// <b>Precision guards (mandatory, this codebase's "never guess" discipline):</b>
/// <list type="bullet">
/// <item><see cref="DuplicationFindingKind.CommentedOutCode"/> only fires when the comment's own
/// stripped text reparses CLEANLY as a real T-SQL batch containing at least one non-trivial
/// statement - never a bare keyword match or a prose comment that happens to mention SQL syntax
/// words. A short comment (fewer than a real threshold's worth of non-whitespace characters) is
/// excluded regardless of parse success, since a two-word comment parsing as a degenerate
/// single-identifier "statement" is noise, not a real code block.</item>
/// <item><see cref="DuplicationFindingKind.IdenticalBinaryOperands"/> never fires when BOTH
/// operands are literals (any kind) - a literal-vs-literal comparison like the extremely common
/// <c>WHERE 1 = 1</c>/<c>0 = 0</c> dynamic-SQL-base-predicate idiom is a deliberate placeholder,
/// not a copy-paste bug, and flagging it would be a real, well-known false-positive risk. Only
/// comparison operators (=, &lt;&gt;, &lt;, &gt;, &lt;=, &gt;=), the logical AND/OR operators, and
/// the self-referentially-degenerate arithmetic operators (Subtract, Divide, Modulo) are checked -
/// Add and Multiply are deliberately excluded (<c>x + x</c> doubling and <c>x * x</c> squaring are
/// both legitimate, commonly-intended patterns, not authoring mistakes), matching the same
/// reasoning that excludes them from every general-purpose "identical operands" rule of this
/// shape. <b>Known v1 scope limit:</b> only a DIRECT sibling pair either side of one operator is
/// checked - a duplicate reachable only by walking a longer AND/OR chain (<c>A AND B AND A</c>)
/// is left unanalyzed rather than guessed at, a deliberate narrowing for precision.</item>
/// <item><see cref="DuplicationFindingKind.SelfAssignment"/> on an UPDATE's own SET clause
/// compares the FULL rendered text of both sides (including any table alias) - <c>t.Col = t.Col</c>
/// fires, but <c>t.Col = s.Col</c> (a multi-table UPDATE...FROM reading a different aliased
/// source) never does, even when both columns happen to share the same bare name.</item>
/// <item><see cref="DuplicationFindingKind.SingleIterationLoop"/> and
/// <see cref="Predicates.DeadCodeFinding"/>'s own reachability walk share the same "prefer
/// declining an ambiguous case" discipline: a WHILE body containing any GOTO/label is skipped
/// entirely (an arbitrary jump target can make an apparently-unconditional BREAK not actually
/// reached on every path), matching <see cref="DeadCodeScanner"/>'s own established GOTO-declines-
/// analysis precedent.</item>
/// <item><see cref="DuplicationFindingKind.DuplicateSiblingCondition"/>,
/// <see cref="DuplicationFindingKind.IdenticalBranchBodies"/>, and
/// <see cref="DuplicationFindingKind.AllBranchesIdentical"/> all compare branches by FULL rendered
/// text (the same <c>FragmentTextRenderer</c>-based structural-equality approach every other kind
/// in this file already uses) - never a partial/heuristic match. An IF/ELSE IF chain needs at
/// least two branches with a real condition to compare (a bare IF with only an ELSE has nothing to
/// duplicate against); <see cref="DuplicationFindingKind.AllBranchesIdentical"/> additionally
/// requires an explicit ELSE/default to exist - an IF/ELSE-IF chain with no ELSE, or a CASE with no
/// ELSE, has an implicit "do nothing"/NULL branch that is never compared against the written
/// branches, since claiming that implicit branch is "identical" to real code would be a guess.
/// </item>
/// <item><see cref="DuplicationFindingKind.RedundantAndCondition"/> and
/// <see cref="DuplicationFindingKind.MutuallyExclusiveAndCondition"/> are deliberately narrow:
/// only two conjuncts of the SAME top-level AND-chain, each comparing the identical operand
/// (by rendered text) against a numeric literal via &gt;/&gt;=/&lt;/&lt;=/=, are considered - each
/// bound is modeled as a real numeric interval and the pair is classified by interval subset/
/// intersection, never a guess about semantically related but not literally comparable
/// expressions. OR-combinations, non-numeric literals, and &lt;&gt; bounds are all declined rather
/// than approximated.</item>
/// <item><see cref="DuplicationFindingKind.NestedConditionalExpression"/> is scoped to IIF
/// specifically (ScriptDom's own dedicated <c>IIfCall</c> node) - a CASE expression nested inside
/// another CASE's own WHEN/THEN/ELSE is a far more common and often perfectly legitimate T-SQL
/// idiom (unlike a true ternary, CASE already reads as a real, explicit control structure) and is
/// deliberately NOT flagged here, matching the real rule's own narrower IIF-only scope.</item>
/// <item><see cref="DuplicationFindingKind.AlwaysTrueOrFalseLiteralComparison"/> only asserts a
/// truth value where collation cannot change the answer: two numeric literals are compared
/// arithmetically (collation-independent); two string literals are only compared for EXACT,
/// case-sensitive textual equality/inequality via =/&lt;&gt; (byte-identical text is always
/// =-true and &lt;&gt;-false regardless of which collation eventually resolves the comparison; two
/// textually DIFFERENT string literals are declined entirely for =/&lt;&gt;, since a
/// case-insensitive collation could still make them compare equal - a real "never guess" guard,
/// not an oversight). Never fires when both operands render as the exact same text - that
/// degenerate, more common case is already <see cref="DuplicationFindingKind.IdenticalBinaryOperands"/>'s
/// own literal-vs-literal exclusion, so the two kinds partition disjoint territory instead of
/// double-reporting the same predicate.</item>
/// </list>
///
/// <see cref="FindingConfidence.High"/> for the structurally-unambiguous kinds (self-assignment,
/// repeated unary operator, identical comparison/logical operands, all-branches-identical,
/// mutually-exclusive-AND-condition, always-true/false literal comparison - hard facts once the
/// AST shape matches, matching <see cref="DeadCodeFinding"/>'s own tier for this class of claim).
/// <see cref="FindingConfidence.Medium"/> for the single-iteration-loop, the negated-comparison
/// readability suggestion, duplicate-sibling-condition, identical-branch-bodies (a PARTIAL match
/// is a real but softer "maybe copy-paste" smell, not the harder all-branches fact),
/// redundant-AND-condition, collapsible-nested-IF, and nested-conditional-expression - all real,
/// but each is either a readability/maintainability call rather than a hard defect, or (for
/// duplicate-sibling-condition) carries the same "maybe intentional defensive redundancy" caveat
/// this codebase applies elsewhere. <see cref="FindingConfidence.Low"/> for the two fuzziest/most
/// heuristic kinds (commented-out code, duplicated string literal) - real, but with a materially
/// higher false-positive/subjectivity ceiling than the rest of this tier. SARIF Warning/Note
/// throughout as appropriate to each tier - never the Error correctness tier, since nothing in
/// this stream is a proof of a wrong result.
/// </summary>
public sealed record DuplicationFinding(
    DuplicationFindingKind Kind,
    string ModuleQualifiedName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    string? DetailText = null,
    FindingConfidence Confidence = FindingConfidence.Medium)
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}

