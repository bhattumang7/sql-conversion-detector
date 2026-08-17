namespace SilentScan.Core.Predicates;

public enum DeadCodeFindingKind
{
    /// <summary>A statement can never execute on any path - it structurally follows a statement
    /// that always ends the enclosing routine (RETURN/THROW, or an IF/TRY-CATCH whose every
    /// branch itself always ends it).</summary>
    UnreachableCode,

    /// <summary>A label target that no GOTO anywhere in the same routine ever jumps to.</summary>
    UnusedLabel,

    /// <summary>A DECLARE'd local variable that is never read anywhere after being declared -
    /// only ever assigned (SET/SELECT), or never referenced at all.</summary>
    UnusedLocalVariable,

    /// <summary>A non-OUTPUT formal parameter never referenced anywhere in the routine body.</summary>
    UnusedParameter,

    /// <summary>A GOTO whose target label is the very next statement in the same straight-line
    /// sequence - jumping to exactly where control flow would already go.</summary>
    RedundantJump,
}

/// <summary>
/// docs/detection-checklist.md Tier 4 "Dead and duplicated code" - the five members needing real
/// control-flow/dataflow analysis rather than pure AST pattern-matching (the pattern-matching half
/// of that same checklist bullet - duplicated literals, identical branch bodies, redundant
/// conditions, and so on - ships separately). Fully syntax-only: no <see
/// cref="Catalog.DatabaseCatalog"/>, no oracle (every member is a directly observable structural
/// fact about the parsed AST, never a plan-shape or runtime-behavior claim).
///
/// <b>Known v1 scope limit, stated honestly:</b> only <c>CREATE/ALTER PROCEDURE</c> and
/// <c>CREATE/ALTER TRIGGER</c> bodies are analyzed (matching <see cref="TransactionHygieneScanner"/>'s
/// own established scope for this class of reachability analysis in this codebase) - scalar and
/// multi-statement functions are declined, not silently swept in, since this pass's reachability
/// walk was built and validated against procedure/trigger control-flow shapes specifically.
///
/// <b>Precision guards (mandatory, this codebase's "never guess" discipline):</b>
/// <list type="bullet">
/// <item>A routine containing ANY <c>GOTO</c>/label anywhere declines <see
/// cref="DeadCodeFindingKind.UnreachableCode"/> analysis entirely for that routine - an arbitrary
/// jump target can make code that looks structurally unreachable actually reachable, the same
/// reasoning <see cref="TransactionHygieneScanner"/> already applies. <see
/// cref="DeadCodeFindingKind.UnusedLabel"/> and <see cref="DeadCodeFindingKind.RedundantJump"/>
/// are unaffected by this decline - they are specifically ABOUT label/GOTO topology, not
/// defeated by it.</item>
/// <item>A variable/parameter referenced only inside a string literal handed to dynamic SQL (not
/// a real AST <c>VariableReference</c> use this pass can see) is never counted as unused - if this
/// pass cannot prove NO real use exists, it declines rather than risks a false positive on a
/// variable genuinely consumed by dynamic SQL text it doesn't parse for this purpose.</item>
/// <item>Only the two most common unambiguous "pure write" shapes - a simple <c>SET @x = expr</c>
/// (not a compound <c>+=</c>/<c>-=</c> assignment, which reads the prior value too) and
/// <c>SELECT @x = expr</c> - are excluded from counting as a real "use". Every other reference
/// shape (a cursor <c>FETCH ... INTO @x</c>, a table variable used as a JOIN/INSERT target, an
/// <c>OUTPUT</c> argument, a cursor variable assignment) counts as a real use even though some of
/// those are themselves write-only in a strict sense - a deliberate under-report, never a
/// false-positive risk, matching this codebase's "prefer declining an ambiguous case" discipline.</item>
/// <item>A non-<c>OUTPUT</c> parameter is the only kind checked for "unused" - an unused
/// <c>OUTPUT</c> parameter is already a sharper, separately shipped claim (<see
/// cref="OutputParameterFinding"/>'s "never assigned on some path"), so it is intentionally
/// excluded here to avoid two findings restating the same underlying fact differently.</item>
/// </list>
///
/// <see cref="FindingConfidence.High"/> for the structurally-provable kinds (unreachable code,
/// unused label, redundant jump - hard facts once the CFG/label-topology is right, matching
/// <see cref="TransactionHygieneFinding"/>'s own tier for this class of reachability claim).
/// <see cref="FindingConfidence.Medium"/> for unused-variable/unused-parameter - a real, measured
/// AST fact, but the "pure write" exclusion list above is deliberately narrow, so a genuinely-used
/// variable referenced only through an unmodeled shape is a real, if rare, false-positive risk this
/// tier is honest about. SARIF Warning throughout (structural/maintainability risk, not itself a
/// proof of a wrong result - the same tier <see cref="ForcedSerialFinding"/>/<see
/// cref="FormattingFinding"/> use, not the Error correctness tier).
/// </summary>
public sealed record DeadCodeFinding(
    DeadCodeFindingKind Kind,
    string ModuleQualifiedName,
    string SourcePath,
    int Line,
    int Column,
    string? DetailText = null,
    FindingConfidence Confidence = FindingConfidence.Medium);
