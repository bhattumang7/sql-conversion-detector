using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

/// <summary>
/// The value flowing INTO a procedure parameter at a real <c>EXEC</c> call site has a caller-side
/// declared type that risks silent data loss against the parameter's own declared type
/// (docs/detection-checklist.md Tier 1 "call-boundary argument mismatch" - the genuinely new half
/// of the parameter type-mismatch item; the in-body half, comparing the parameter against a
/// column INSIDE the callee, was already shipped through the ordinary typed-predicate pipeline).
/// This is an assignment-shaped conversion at parameter marshalling, not a predicate - there is no
/// seek to lose - so it's classified with <see cref="Rules.WriteLossClassifier"/> (same family as
/// <see cref="WriteLossFinding"/>) rather than <see cref="Rules.VerdictClassifier"/>'s seek/scan
/// vocabulary, and carries no plan-XML oracle marker for the same reason
/// <see cref="WriteLossFinding"/> doesn't: a parameter binding conversion isn't a query predicate
/// SHOWPLAN_XML has any marker for. Also primes the exact mismatched value the already-shipped
/// in-body rule will then compare against a column inside the callee, though this pass does not
/// (yet) chain provenance to that specific downstream finding - see the checklist item for why.
/// </summary>
public sealed record ProcCallArgumentMismatchFinding(
    string? CallerScopeQualifiedName,
    string CalleeQualifiedName,
    string FormalParameterName,
    string CallerVariableName,
    string CallerTypeDisplay,
    string FormalParameterTypeDisplay,
    WriteLossKind Kind,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High)
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}

