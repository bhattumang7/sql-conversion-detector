using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

/// <summary>
/// A <c>LIKE</c> predicate compares a non-ANSI-padded <c>varchar</c>/<c>varbinary</c> column
/// (<c>sys.columns.is_ansi_padded = 0</c>) against a literal pattern whose own trailing
/// whitespace is significant (docs/detection-checklist.md Tier 1 "SET options that silently
/// disable plan features": "ANSI_PADDING OFF as a second, independent finding - a comparison
/// seed, not just a plan-feature blocker"). With ANSI_PADDING off, trailing blanks are stripped
/// from the STORED value at INSERT time - the column can never hold a value ending in whitespace
/// at all - so a pattern like <c>'abc '</c> (or <c>'abc %'</c>) can never match anything the
/// column could ever contain, while the identical predicate against a padded column, or against
/// a fixed-length/<c>nvarchar</c> column (neither of which this stream fires on - unaffected by
/// ANSI_PADDING), behaves as written.
///
/// Oracle-confirmed directly (Docker SQL Server, real seeded rows): a plain equality comparison
/// (<c>=</c>) is NOT affected regardless of padding or trailing whitespace - T-SQL's own
/// comparison semantics trim trailing spaces for equality on both sides either way, confirmed by
/// probing a non-padded column against a padded one AND against a trailing-whitespace literal,
/// both matching identically. Only <c>LIKE</c> (where a pattern's own trailing whitespace is
/// semantically significant, not trimmed) shows a real difference - <c>LIKE 'abc %'</c> matched a
/// padded column storing <c>'abc   '</c> but not a non-padded column storing the identical value
/// as stripped <c>'abc'</c>. This is why the finding is scoped to <c>LIKE</c>-against-a-literal
/// only, narrower than the checklist's original "column vs column, or column vs literal" framing -
/// the column-vs-column and equality shapes were investigated and found NOT to reproduce, so they
/// are not claimed here (CLAUDE.md "precision beats recall everywhere").
///
/// Data-semantics finding, not a plan-shape one - changes which rows match, not how they're
/// found. No verdict, no oracle-confirmable plan claim; the oracle work above confirms the
/// general MECHANISM once, not a per-finding proof.
///
/// <b>Known, deliberate scope limit:</b> only the literal's own FINAL character is checked (e.g.
/// <c>'abc '</c>) - a pattern with significant whitespace immediately before a trailing wildcard
/// (<c>'abc %'</c>, which a non-padded column also can never match) is not caught, since
/// detecting that would need wildcard-aware pattern parsing (distinguishing literal characters
/// from <c>%</c>/<c>_</c> wildcards inside the pattern text) this stream doesn't attempt. A real
/// gap, left honestly uncaught rather than guessed at with a heuristic that could misfire on a
/// pattern where a mid-string <c>%</c> or <c>_</c> is itself escaped text, not a wildcard.
/// </summary>
public sealed record AnsiPaddingMismatchFinding(
    string TableQualifiedName,
    string ColumnName,
    string PatternLiteralText,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High)
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}

