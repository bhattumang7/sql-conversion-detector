namespace SilentScan.Verify.Catalog;

/// <summary>One row of <c>sys.sql_modules</c> joined back to its owning <c>sys.objects</c> entry - a view, procedure, scalar/inline/multi-statement function, or trigger with a readable T-SQL body.</summary>
public sealed record LiveModule(string SchemaName, string ObjectName, string ObjectTypeCode, string Definition, bool UsesQuotedIdentifier)
{
    /// <summary>The provenance identifier findings from this module are reported under - <c>[schema].[object]</c>, read verbatim as the source "file" name so a finding's origin reads as <c>dbo.usp_GetOrders:37</c>.</summary>
    public string QualifiedName => $"{SchemaName}.{ObjectName}";
}
