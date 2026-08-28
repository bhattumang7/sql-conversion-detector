using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.CallGraph;

internal static class SpExecuteSqlParameterMismatch
{
    public static string RuleId => SarifRuleCatalog.SpExecuteSqlParameterMismatchRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            `sp_executesql` carries its own parameter list, declared inline as a literal string -
            `EXEC sp_executesql @sql, N'@Rate decimal(9,4)', @Rate = @callerVariable` - rather than
            through `sys.parameters` on a catalog-known procedure. This rule parses that literal
            parameter-definition string the same way SQL Server itself does, resolves each declared
            parameter's type, and compares it against the caller-side variable bound to it, exactly
            like the static EXEC call-graph argument-mismatch rule does for a real procedure's
            declared signature. When the caller's variable risks losing information on the way in -
            a DECIMAL variable with fewer fractional digits than the declared parameter expects, an
            INT passed where the declared parameter is a narrower type, a string variable shorter
            than the declared parameter's length - the value is silently narrowed during parameter
            marshalling, before the dynamic batch itself ever runs.

            This is the same assignment-shaped conversion as the static call-graph rule, only the
            source of the target type differs: here it comes from the literal string `sp_executesql`
            itself parses at the call site, not from a procedure's catalog-declared signature. The
            check only fires when that parameter-definition string is itself a literal - if it's
            built from a variable or concatenation, the declared types aren't statically decidable
            and nothing is reported.

            The same assignment happens in reverse for an OUTPUT parameter: at the end of the call,
            the dynamic batch's own final parameter value is implicitly converted to the caller-side
            variable's declared type and assigned back. A narrower caller-side variable loses
            information at that point exactly as it would on the way in.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A narrower declared parameter silently truncates a wider caller variable",
                NoncompliantSql: """
                    DECLARE @sql NVARCHAR(MAX) = N'UPDATE dbo.Products SET Sku = @SkuCode';
                    DECLARE @sku VARCHAR(20) = 'WIDGET-2024-CLEARANCE';
                    EXEC sp_executesql @sql, N'@SkuCode VARCHAR(10)', @SkuCode = @sku;
                    """,
                NoncompliantExplanation: "@sku is VARCHAR(20), but the parameter-definition string only declares @SkuCode as VARCHAR(10) - the value is silently truncated at parameter marshalling, before the dynamic batch's UPDATE ever runs, and the mismatch is invisible unless the caller's declaration and the parameter-definition string are compared side by side.",
                CompliantSql: """
                    DECLARE @sql NVARCHAR(MAX) = N'UPDATE dbo.Products SET Sku = @SkuCode';
                    DECLARE @sku VARCHAR(20) = 'WIDGET-2024-CLEARANCE';
                    EXEC sp_executesql @sql, N'@SkuCode VARCHAR(20)', @SkuCode = @sku;
                    """,
                CompliantExplanation: "The declared parameter's length now matches @sku exactly - the value crosses into the dynamic batch with no implicit truncation."),
            new RuleDocExample(
                Title: "An OUTPUT parameter's final value silently rounded on the way back",
                NoncompliantSql: """
                    DECLARE @sql NVARCHAR(MAX) = N'SET @Tax = 12.3456';
                    DECLARE @tax DECIMAL(4,1);
                    EXEC sp_executesql @sql, N'@Tax DECIMAL(10,4) OUTPUT', @Tax = @tax OUTPUT;
                    """,
                NoncompliantExplanation: "The dynamic batch computes a DECIMAL(10,4) result, but @tax can only hold one fractional digit - the value is silently rounded when SQL Server copies the OUTPUT parameter's final value back into @tax at the end of the call.",
                CompliantSql: """
                    DECLARE @sql NVARCHAR(MAX) = N'SET @Tax = 12.3456';
                    DECLARE @tax DECIMAL(10,4);
                    EXEC sp_executesql @sql, N'@Tax DECIMAL(10,4) OUTPUT', @Tax = @tax OUTPUT;
                    """,
                CompliantExplanation: "@tax now matches the declared OUTPUT parameter's type and scale exactly - the final value crosses back with no implicit narrowing conversion."),
        ]);
}
