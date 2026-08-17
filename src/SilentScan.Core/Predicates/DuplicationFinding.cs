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
/// </list>
///
/// <see cref="FindingConfidence.High"/> for the structurally-unambiguous kinds (self-assignment,
/// repeated unary operator, identical comparison/logical operands - hard facts once the AST shape
/// matches, matching <see cref="DeadCodeFinding"/>'s own tier for this class of claim).
/// <see cref="FindingConfidence.Medium"/> for the single-iteration-loop (a real reachability fact,
/// but a WHILE genuinely used as a structured one-shot construct with an early exit is a rare but
/// real legitimate pattern) and the negated-comparison readability suggestion (correctness-neutral
/// by construction, stated as a style suggestion, not a defect). <see cref="FindingConfidence.Low"/>
/// for the two fuzziest/most heuristic kinds (commented-out code, duplicated string literal) -
/// real, but with a materially higher false-positive/subjectivity ceiling than the rest of this
/// tier. SARIF Warning/Note throughout as appropriate to each tier - never the Error correctness
/// tier, since nothing in this stream is a proof of a wrong result.
/// </summary>
public sealed record DuplicationFinding(
    DuplicationFindingKind Kind,
    string ModuleQualifiedName,
    string SourcePath,
    int Line,
    int Column,
    string? DetailText = null,
    FindingConfidence Confidence = FindingConfidence.Medium);
