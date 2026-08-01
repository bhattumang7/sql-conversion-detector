using SilentScan.Core.Catalog;

namespace SilentScan.Core.Predicates;

/// <summary>A location in the original source. Distinct from a bare (path, line) pair because dynamic SQL remapping needs the column too.</summary>
public readonly record struct SourceSpan(string SourcePath, int Line, int Column);

/// <summary>
/// A dynamic SQL call site whose argument was provably constant (Tier A of CLAUDE.md's dynamic
/// SQL policy) - reassembled into a single piece of T-SQL text ready to reparse, plus the map
/// needed to translate any finding inside it back to where that text actually came from in the
/// original file. <see cref="DeclaredParameters"/> carries sp_executesql's exact declared
/// parameter types when present (Tier B) - empty for a plain EXEC('...') call, which has no
/// parameter concept.
/// </summary>
public sealed record DynamicSqlScript(
    SourceSpan CallSite,
    string InnerText,
    DynamicSqlSegmentMap SegmentMap,
    IReadOnlyDictionary<string, SqlType?> DeclaredParameters);
