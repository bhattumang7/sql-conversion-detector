using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public enum FormattingFindingKind
{
    /// <summary>A literal tab character appears in the source text.</summary>
    TabCharacterUsed,

    /// <summary>Two or more statements in the same block start on the same physical source line.</summary>
    MultipleStatementsOnSameLine,

    /// <summary>Two or more variables in the same DECLARE are declared on the same physical source line.</summary>
    MultipleDeclarationsOnSameLine,

    /// <summary>An IF/WHILE/ELSE body is a single statement with no BEGIN...END, on a different line
    /// than its own keyword - the general "always brace your conditionals" risk.</summary>
    MissingBeginEndBlock,

    /// <summary>An IF/WHILE/ELSE body is a single statement with no BEGIN...END, sharing the exact
    /// same line as its own keyword - visually easy to misread as no-op or as part of a larger block.</summary>
    SingleLineConditionalBody,

    /// <summary>A statement immediately follows an unbraced IF/WHILE's single-statement body and starts
    /// on the very next line, visually appearing to still be inside the conditional/loop when it is not.</summary>
    DanglingStatementAfterUnbracedBody,

    /// <summary>An IF statement immediately follows the closing END of a prior braced IF, on the very
    /// same line as that END - easy to misread as an ELSE IF continuation when it is really a
    /// separate, unconditional statement.</summary>
    IfImmediatelyFollowingPriorBlockEnd,

    /// <summary>A parenthesized expression whose parentheses do not change grouping/precedence at all.</summary>
    RedundantParentheses,

    /// <summary>A module's own definition does not begin with a comment before its first real statement.</summary>
    MissingFileHeaderComment,
}

/// <summary>
/// docs/detection-checklist.md Tier 4 "Formatting and layout" - ten configurable-threshold-free
/// structural/textual checks over the AST and raw token stream, no catalog needed. Purely a
/// readability/maintainability signal for every member except <see
/// cref="FormattingFindingKind.DanglingStatementAfterUnbracedBody"/> and <see
/// cref="FormattingFindingKind.IfImmediatelyFollowingPriorBlockEnd"/>, which flag a genuine
/// visual-ambiguity risk (a statement that LOOKS like it belongs to a conditional/loop but
/// structurally does not) - still <see cref="FindingConfidence.Low"/> like every other member,
/// since the statement's OWN behavior is unaffected; only a future edit relying on the misleading
/// visual shape is at risk. No oracle applies to any of them - every one is a directly observable
/// parse/token-stream fact, never a plan-shape or runtime-behavior claim (the same reasoning <see
/// cref="CodeMetricFinding"/> already established for this class of Tier 4 finding).
/// </summary>
public sealed record FormattingFinding(
    FormattingFindingKind Kind,
    string ModuleQualifiedName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    string? DetailText = null,
    FindingConfidence Confidence = FindingConfidence.Low)
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}

