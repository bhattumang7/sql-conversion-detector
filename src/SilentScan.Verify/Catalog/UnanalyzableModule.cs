namespace SilentScan.Verify.Catalog;

/// <summary>Why a module found in the catalog could not be analyzed - never a silent omission (CLAUDE.md dynamic-SQL policy's same honesty rule, applied to modules with no T-SQL body to analyze at all).</summary>
public enum UnanalyzableModuleReason
{
    /// <summary>An assembly-backed CLR procedure/scalar function/table function (<c>sys.assembly_modules</c>) - there is no T-SQL body to parse. A T-SQL predicate that calls one is still caught: it is an ordinary function-wrapped-column shape, which Tier-1's syntactic scan already flags regardless of what kind of function is on the other end.</summary>
    ClrAssemblyModule,

    /// <summary>A module created <c>WITH ENCRYPTION</c> - <c>sys.sql_modules.definition</c> is NULL and the real T-SQL body is unrecoverable from metadata.</summary>
    Encrypted,

    /// <summary>A <c>sys.objects</c> row with a readable <c>sys.sql_modules</c> body whose own type code (e.g. replication-filter procedure <c>RF</c>, rule <c>R</c>, default <c>D</c>) is outside the standard view/procedure/function/trigger set this reader parses - a real body exists but this pass has never modeled the construct.</summary>
    NonStandardModuleType,

    /// <summary>A numbered stored procedure's body beyond #1 (<c>sys.numbered_procedures</c>, <c>procedure_number &gt; 1</c>) - <c>sys.sql_modules</c> only ever holds body #1's text, so bodies 2..n are never read at all despite <see cref="SilentScan.Core.Predicates.DeprecatedSyntaxScanner"/> already flagging numbered-procedure syntax found in scanned text.</summary>
    NumberedProcedureBody,
}

/// <summary>One database object this pass saw in the catalog but could not read a T-SQL body for.</summary>
public sealed record UnanalyzableModule(string SchemaName, string ObjectName, string ObjectTypeCode, UnanalyzableModuleReason Reason)
{
    public string QualifiedName => $"{SchemaName}.{ObjectName}";
}
