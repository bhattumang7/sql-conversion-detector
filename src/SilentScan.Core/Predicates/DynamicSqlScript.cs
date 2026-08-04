using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;

namespace SilentScan.Core.Predicates;

/// <summary>A location in the original source. Distinct from a bare (path, line) pair because dynamic SQL remapping needs the column too.</summary>
public readonly record struct SourceSpan(string SourcePath, int Line, int Column);

/// <summary>
/// The enclosing proc/function/trigger a dynamic SQL call site was found inside, if any - a
/// reparsed EXEC/sp_executesql fragment has no CREATE PROCEDURE wrapper of its own, so without
/// this a #temp table or trigger inserted/deleted pseudo-table that resolves fine in the
/// surrounding STATIC SQL silently fails to resolve inside the dynamic text, even though it's
/// the exact same object. <paramref name="ProcScope"/> is the qualified proc/function/trigger
/// name (the same key <see cref="Catalog.CatalogBuilder"/> scopes a body-declared temp object
/// under); <paramref name="TriggerTarget"/> is the trigger's own target table/view (null unless
/// the call site is inside a trigger body with a real target - a DDL/LOGON trigger has none).
/// </summary>
public sealed record DynamicSqlScope(string? ProcScope, SchemaObjectName? TriggerTarget)
{
    public static readonly DynamicSqlScope None = new(null, null);
}

/// <summary>
/// A dynamic SQL call site whose argument was provably constant (Tier A of CLAUDE.md's dynamic
/// SQL policy) - reassembled into a single piece of T-SQL text ready to reparse, plus the map
/// needed to translate any finding inside it back to where that text actually came from in the
/// original file. <see cref="ParameterDeclarationText"/> is sp_executesql's own raw, provably-
/// constant @params argument text when present (Tier B) - null for a plain EXEC('...') call,
/// which has no parameter concept, or when @params itself couldn't be folded to a constant.
/// Kept as raw text rather than pre-parsed here: <see cref="DynamicSqlScanner"/> runs before
/// <see cref="Catalog.CatalogBuilder"/> in <see cref="Reporting.ScanReportBuilder"/>'s pipeline
/// (it needs no catalog for its own straight-line constant-folding job), so a user type alias
/// (<c>CREATE TYPE ... FROM</c>) declared on a parameter couldn't resolve if parsed here - the
/// text is parsed later, in <see cref="DynamicSqlPipeline"/>, where the real catalog already
/// exists (<see cref="DynamicSqlParameterDeclarations.TryParse"/>). <see cref="Scope"/> is the
/// enclosing proc/function/trigger the call site was found inside, if any.
/// <see cref="ArgumentBindings"/> is this call's own named execute-parameter bindings whose
/// value is a bare variable reference (e.g. <c>@P = @Code</c> maps <c>"@P"</c> to
/// <c>"@Code"</c>) - CLAUDE.md's nested-dynamic-SQL parameter propagation (<see
/// cref="DynamicSqlPipeline"/>) uses this, and only this - never a blanket name match across
/// scopes - to let an enclosing script's own declared parameter type stand in for this call's
/// declared parameter when its own declaration can't otherwise resolve it.
/// <see cref="Confidence"/> is how much this ONE assembly's own claim of being provably constant
/// can be trusted - <see cref="FindingConfidence.High"/> when every segment is a real literal
/// (every script today), lower once a segment can be an unknown-but-typed placeholder standing in
/// for a value this scanner could not prove constant. One EXEC/sp_executesql call site can emit
/// several <see cref="DynamicSqlScript"/>s (one per assembly) at DIFFERENT confidences - this
/// field is per-script, not per-call-site, exactly so that can be represented.
/// </summary>
public sealed record DynamicSqlScript(
    SourceSpan CallSite,
    string InnerText,
    DynamicSqlSegmentMap SegmentMap,
    string? ParameterDeclarationText,
    DynamicSqlScope Scope,
    IReadOnlyDictionary<string, string>? ArgumentBindings = null,
    FindingConfidence Confidence = FindingConfidence.High);
