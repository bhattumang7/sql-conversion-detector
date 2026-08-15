namespace SilentScan.Core.Catalog;

/// <summary>
/// Which flavour of table-valued function a qualified name refers to. The call site
/// (<c>FROM dbo.fn(@x)</c>) is textually identical for all three, so this distinction exists
/// nowhere in the text a syntactic linter sees - only the catalog
/// (<c>sys.objects.type</c> = <c>'IF'</c>/<c>'TF'</c>/<c>'FT'</c>, or the parsed
/// <c>RETURNS</c> clause in file mode) can tell them apart. That is precisely what makes the
/// MSTVF-as-fence stream possible at all.
/// </summary>
public enum TableValuedFunctionKind
{
    /// <summary>
    /// <c>RETURNS TABLE AS RETURN (SELECT ...)</c> (<c>sys.objects.type = 'IF'</c>). Expanded
    /// into the calling query like a view - no fence, no fabricated estimate. Never a finding.
    /// </summary>
    Inline,

    /// <summary>
    /// <c>RETURNS @t TABLE(...) AS BEGIN ... END</c> (<c>sys.objects.type = 'TF'</c>). The
    /// optimizer cannot see into the body: the result is materialized into a statistics-less
    /// table variable and the reference carries a fixed cardinality guess (1 row under the
    /// legacy CE, 100 under the 2014+ CE), which then propagates into join order, join types
    /// and memory grants for the whole surrounding plan.
    /// </summary>
    MultiStatement,

    /// <summary>
    /// A SQLCLR table-valued function (<c>sys.objects.type = 'FT'</c>) - <c>RETURNS TABLE(...)</c>
    /// with an <c>EXTERNAL NAME</c> body and no <c>@variable</c> to materialize into. It is
    /// equally opaque to the optimizer and equally fixed in its estimate, but its streaming
    /// execution model differs enough from an MSTVF's worktable that this stream keeps the two
    /// apart rather than reporting one as the other.
    /// </summary>
    Clr,
}
