namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md "Catch-all / kitchen-sink predicates" sibling: "parameter
/// overwritten before use in a predicate". A formal <c>CREATE PROCEDURE</c>/function parameter is
/// the value the optimizer SNIFFS at compile time to build the cached plan - but if the
/// procedure's own body reassigns that parameter (<c>SET @p = ...</c>/<c>SELECT @p = ...</c>) on
/// every path reaching a later predicate use of the SAME name, the plan was compiled against the
/// ORIGINAL caller-supplied value while the predicate actually executes against the NEW one. This
/// is a genuinely different claim from <see cref="LocalVariablePredicateFinding"/> (a `DECLARE`d
/// local was NEVER a sniffable, caller-supplied value in the first place) - here, a value that
/// WAS sniffable had that sniffed value invalidated by the procedure's own code before the
/// predicate that would have benefited from it ever ran.
///
/// Deliberately carries no estimate magnitude and no verdict, matching <see
/// cref="LocalVariablePredicateFinding"/>'s own honesty: this pass never traces the reassigned
/// value itself, only that the sniffed value is provably stale by the time this predicate runs.
/// <see cref="FindingConfidence.Low"/> by default for the identical reason.
///
/// Fully suppressed (never even constructed), not merely downgraded, under an active
/// <c>OPTION (RECOMPILE)</c>/<c>WITH RECOMPILE</c> guard - a per-execution recompile sees the
/// parameter's REAL, post-reassignment value, so the staleness this finding exists to report no
/// longer holds. Same suppression discipline as the "Catch-all" and "Local-variable predicates"
/// siblings.
/// </summary>
public sealed record ParameterReassignmentPredicateFinding(
    string TableQualifiedName,
    string ColumnName,
    bool Indexed,
    string ParameterName,
    string Operator,
    int ReassignmentLine,
    int ReassignmentColumn,
    string SourcePath,
    int Line,
    int Column,
    FindingConfidence Confidence = FindingConfidence.Low);
