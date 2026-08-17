namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md "Second OSS/commercial sweep": <c>SET DATEFORMAT</c>/
/// <c>SET DATEFIRST</c> inside a module body. Distinct mechanism from the shipped SET-options
/// stream (<see cref="SetOptionFinding"/>) - those block a PLAN FEATURE (indexed view/filtered
/// index matching); these change how a string date LITERAL or a <c>DATEPART</c>-relative
/// expression is interpreted for the rest of that session, independent of the caller's own
/// session settings, so the identical literal silently means a different date/weekday depending
/// on which session compiled/executed the module. Oracle-confirmed directly (Docker instance):
/// the ambiguous literal <c>'03/04/2026'</c> resolves to 2026-03-04 under <c>SET DATEFORMAT
/// mdy</c> and to 2026-04-03 under <c>SET DATEFORMAT dmy</c> with no other change; <c>DATEPART(
/// weekday, ...)</c> for a fixed real date returns a different ordinal under <c>SET DATEFIRST 1</c>
/// vs. <c>SET DATEFIRST 7</c>. Fully syntax-only - a <c>SetCommandStatement</c> whose
/// <c>GeneralSetCommand.CommandType</c> is <c>DateFormat</c>/<c>DateFirst</c>, ScriptDom's own AST
/// shape for this SET form (NOT a <c>PredicateSetStatement</c>, which only carries the ON/OFF-style
/// boolean options - verified directly against a real parse before assuming). Purely informational
/// - this pass cannot see what value the CALLER's own session already had, so it cannot claim the
/// module's SET actually changes anything for a specific invocation, only that the module makes
/// its own date interpretation session-state-dependent - <see cref="FindingConfidence.Low"/>,
/// SARIF Note, the same no-magnitude-claim tier <see cref="LocalVariablePredicateFinding"/> uses.
/// </summary>
public enum SessionDateSettingKind
{
    DateFormat,
    DateFirst,
}

public sealed record SessionDateSettingFinding(
    SessionDateSettingKind Kind,
    string SourcePath,
    int Line,
    int Column,
    FindingConfidence Confidence = FindingConfidence.Low);
