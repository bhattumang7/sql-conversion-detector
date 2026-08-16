namespace SilentScan.Core.Predicates;

/// <summary>
/// A predicate compares a column against a parameter/variable/expression declared with a
/// meaningfully SHORTER length than the column itself (docs/detection-checklist.md Tier 1
/// "Under-length and length-defaulted string declarations"), or with NO explicit length at all
/// (<see cref="IsImplicitDefault"/> - T-SQL defaults a length-less <c>DECLARE</c>/parameter
/// declaration to 1, a near-universal accident, not an intentional choice). The exact mirror of
/// <see cref="OversizedParameterFinding"/>, but strictly worse: an oversized parameter only risks
/// a memory-grant estimate; an under-length one TRUNCATES the assigned value before the predicate
/// ever runs, silently changing which rows match (or matching none).
///
/// Deliberately NOT verdict-bearing, same reasoning as <see cref="OversizedParameterFinding"/>:
/// this pass never traces the variable's actual assigned VALUE (CLAUDE.md "soundness first: no
/// heuristic string guessing" - the same discipline <c>WriteLossFinding</c> already follows for
/// assignment-site truncation), so it cannot claim truncation DID happen for a specific query,
/// only that the DECLARED length pairing risks it - a structural, catalog+AST report, same
/// severity tier <c>WriteLossFinding</c> already uses for this identical class of concern (an
/// INSERT/UPDATE assigning a wider value into a narrower target), observed here at a predicate
/// site instead of a write site.
///
/// <see cref="ChangesRangeOrPatternShape"/> is true when <see cref="Operator"/> is <c>LIKE</c> or
/// a range comparison (<c>&lt;</c>/<c>&lt;=</c>/<c>&gt;</c>/<c>&gt;=</c>) - truncating a LIKE
/// pattern or a range bound doesn't just risk excluding an exact match, it changes what the whole
/// comparison MEANS (a shorter LIKE pattern matches a broader set of rows; a truncated range
/// bound moves the boundary). Derived structurally from the operator actually used at this
/// predicate site, never guessed.
/// </summary>
public sealed record UnderLengthParameterFinding(
    string TableQualifiedName,
    string ColumnName,
    int ColumnLength,
    int? OtherOperandLength,
    bool IsImplicitDefault,
    string Operator,
    bool ChangesRangeOrPatternShape,
    string SourcePath,
    int Line,
    int Column,
    FindingConfidence Confidence = FindingConfidence.High);
