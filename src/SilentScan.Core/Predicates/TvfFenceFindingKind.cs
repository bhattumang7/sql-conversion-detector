namespace SilentScan.Core.Predicates;

/// <summary>
/// How a multi-statement (or CLR) table-valued function's optimization fence is being reached.
/// Ordered most- to least-damaging, which is also the order
/// <see cref="TvfFenceFinding"/>s are ranked in for reporting.
/// </summary>
public enum TvfFenceFindingKind
{
    /// <summary>
    /// <c>CROSS/OUTER APPLY dbo.fn(t.col)</c> where an argument references a column from an
    /// outer relation: the entire function body re-executes once per outer row. SQL Server
    /// 2017's interleaved execution explicitly does NOT rescue this - it applies to
    /// uncorrelated references only - so this stays broken on every engine version and ranks
    /// first.
    /// </summary>
    CorrelatedApply,

    /// <summary>
    /// The fence is not at this call site at all: the referenced view or inline TVF is itself
    /// (transitively) built over a multi-statement TVF, so every consumer inherits the fence
    /// invisibly. Depth and origin say which layer introduced it. This is the case no
    /// text-matching tool can see, since the call site names something that looks harmless.
    /// </summary>
    NestedUnderViewOrTvf,

    /// <summary>
    /// A direct <c>FROM</c>/<c>JOIN</c> reference, uncorrelated: the body stays opaque and the
    /// reference carries a fixed cardinality estimate (1 row under the legacy CE, 100 under
    /// 2014+), which propagates into the surrounding plan's join order, join types and memory
    /// grant. On 2017+ an uncorrelated reference is an interleaved-execution candidate, which
    /// fixes the estimate but not the fence.
    /// </summary>
    FromOrJoin,

    /// <summary>
    /// <c>INSERT ... EXEC</c>: the same family of forced full materialization to a worktable,
    /// with the added constraint that it cannot nest. Reached from a procedure call rather than
    /// a function reference, so it carries no function kind of its own.
    /// </summary>
    InsertExec,

    /// <summary>
    /// A standalone <c>SELECT ... FROM dbo.fn(@x)</c> with nothing joined to it. The fence and
    /// the fabricated estimate are both genuinely present; what is absent is a surrounding plan
    /// for the bad estimate to poison.
    /// </summary>
    Standalone,
}
