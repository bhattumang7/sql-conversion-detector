using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;

namespace SilentScan.Core.Predicates;

/// <summary>
/// A scalar UDF's cost reached from a query (docs/detection-checklist.md Tier 1 #1) - per-row
/// execution always, non-sargability when the call sits in a predicate, and a forced-serial plan
/// pre-2019 (or on any engine when the UDF proves non-inlineable). This exists only because the
/// catalog knows what the text cannot say: a bare function call reads identically whether it
/// resolves to a registered scalar UDF or an unrelated name this scan never saw DDL for, so an
/// unresolved call never produces a finding here rather than being guessed at.
/// </summary>
/// <param name="Kind">How the cost is being reached - also the report rank.</param>
/// <param name="FunctionQualifiedName">The scalar UDF actually being called.</param>
/// <param name="ReferencedObjectQualifiedName">
/// For <see cref="ScalarUdfFindingKind.NestedUnderViewOrTvf"/>, the innocent-looking view/iTVF the
/// call site actually names (equal to <paramref name="FunctionQualifiedName"/> for every other
/// kind). For <see cref="ScalarUdfFindingKind.SchemaDependency"/>, the table carrying the
/// computed column/constraint.
/// </param>
/// <param name="UdfKind">T-SQL vs CLR.</param>
/// <param name="Inlineability">This finding's own read of SQL 2019+ inlineability - see <see cref="ScalarUdfInlineability"/>.</param>
/// <param name="InlineabilityBlocker">A human-readable explanation when <paramref name="Inlineability"/> is <see cref="ScalarUdfInlineability.NotInlineable"/>; null otherwise.</param>
/// <param name="IsSchemaBound"><c>WITH SCHEMABINDING</c> presence - null when this scan never determined it.</param>
/// <param name="ConstantArgumentsNotFolded">True only when every argument at this call site is a literal AND the catalog proves the function non-schemabound - the engine can't constant-fold even a literal-only call it can't prove deterministic.</param>
/// <param name="ClrDataAccess">True only when the catalog proves a CLR scalar UDF touches data; null when unproven - still reported for per-row cost, just without the forces-serial claim.</param>
/// <param name="Context">The exact clause this call was found in.</param>
/// <param name="SchemaDependencyKind">Which schema construct, for <see cref="ScalarUdfFindingKind.SchemaDependency"/>; null otherwise.</param>
/// <param name="SourcePath">Where this finding's own reference lives.</param>
/// <param name="Line">1-based line of the reference.</param>
/// <param name="Column">1-based column of the reference.</param>
/// <param name="Depth">View/iTVF layers between the call site and the UDF call - 0 for a direct reference.</param>
/// <param name="OriginSourcePath">Where the layer that introduced the call is defined - null for a direct reference.</param>
/// <param name="OriginLine">1-based line within <paramref name="OriginSourcePath"/>; 0 when that is null.</param>
/// <param name="ReferenceFragmentText">The call's own source text, re-rendered to valid T-SQL, so the oracle can build a probe from the finding rather than trusting the classifier that produced it.</param>
/// <param name="DynamicSqlCallSite">Set when found inside reparsed dynamic SQL - the EXEC/sp_executesql call site, distinct from <paramref name="SourcePath"/>/<paramref name="Line"/>.</param>
/// <param name="Confidence">How much this finding's own claim can be trusted.</param>
public sealed record ScalarUdfFinding(
    ScalarUdfFindingKind Kind,
    string FunctionQualifiedName,
    string ReferencedObjectQualifiedName,
    ScalarUdfKind UdfKind,
    ScalarUdfInlineability Inlineability,
    string? InlineabilityBlocker,
    bool? IsSchemaBound,
    bool ConstantArgumentsNotFolded,
    bool? ClrDataAccess,
    ScalarUdfContext Context,
    SchemaDependencyKind? SchemaDependencyKind,
    string SourcePath,
    int Line,
    int Column,
    int Depth = 0,
    string? OriginSourcePath = null,
    int OriginLine = 0,
    string? ReferenceFragmentText = null,
    SourceSpan? DynamicSqlCallSite = null,
    FindingConfidence Confidence = FindingConfidence.High) : IRelocatableFinding<ScalarUdfFinding>
{
    int IRelocatableFinding<ScalarUdfFinding>.PositionColumn => Column;

    ScalarUdfFinding IRelocatableFinding<ScalarUdfFinding>.Relocated(SourceSpan span, SourceSpan? callSite, FindingConfidence confidence) =>
        this with { SourcePath = span.SourcePath, Line = span.Line, Column = span.Column, DynamicSqlCallSite = callSite, Confidence = confidence };
}
