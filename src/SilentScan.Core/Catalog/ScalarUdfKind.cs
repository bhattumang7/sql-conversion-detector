namespace SilentScan.Core.Catalog;

/// <summary>
/// Which flavour of scalar UDF a qualified name refers to - a T-SQL body is subject to the
/// pre-2019 per-row/serial cost and the 2019+ inlining blocker scan; a CLR scalar
/// (<c>EXTERNAL NAME</c>, <c>sys.objects.type = 'FS'</c>) is never inlined and instead carries
/// its own data-access classification.
/// </summary>
public enum ScalarUdfKind
{
    TSql,
    Clr,
}
