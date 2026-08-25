using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.WriteLoss;

internal static class LengthTruncation
{
    public static string RuleId => SarifRuleCatalog.WriteLossLengthTruncationRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            VARCHAR/NVARCHAR/CHAR/NCHAR and BINARY/VARBINARY are all bounded by a declared length.
            When a value from a longer variable or OUTPUT parameter is assigned into a shorter one -
            for example a stored procedure writes a 10-character result into a VARCHAR(10) OUTPUT
            parameter, and the caller copies it back into a local VARCHAR(3) variable - SQL Server
            does not raise an error. It silently keeps only the leading characters/bytes that fit
            and discards the rest.

            This is scoped to variable and parameter targets specifically, because the same
            narrowing behaves completely differently against a table column: inserting or updating
            a table column with a value longer than its declared length raises a hard error
            ("String or binary data would be truncated"), not a silent loss. So this rule only fires
            where the loss is actually silent - local variables, OUTPUT parameters, and proc-call
            arguments - never for INSERT/UPDATE into a real column, where the engine already stops
            you.

            The OUTPUT-parameter case is the sharpest form of this: the procedure's own contract
            (its declared parameter length) can be wider than what any individual caller happens to
            declare its receiving variable as, and nothing at the call site signals that a mismatch
            exists. The caller sees a shorter value than the procedure actually produced, with no
            error and no indication anything was cut off.
            """,
        HowToFixIt: """
            Declare the receiving variable/parameter with a length at least as long as the source
            it's assigned from, so nothing is silently cut off. If a shorter value is genuinely
            intended, make that explicit with LEFT() or SUBSTRING() at the assignment site, so a
            reader sees the truncation as a deliberate decision rather than an invisible consequence
            of the variable's declared length.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "An OUTPUT parameter copied back into a narrower caller variable",
                NoncompliantSql: """
                    CREATE PROCEDURE dbo.BuildReference @Reference VARCHAR(10) OUTPUT AS
                    BEGIN
                        SET @Reference = 'REF-000123';
                    END;

                    DECLARE @CallerReference VARCHAR(3);
                    EXEC dbo.BuildReference @Reference = @CallerReference OUTPUT;
                    """,
                NoncompliantExplanation: "@Reference is declared VARCHAR(10) and the procedure writes a 10-character value into it; copying that back into @CallerReference VARCHAR(3) silently keeps only 'REF' and drops the rest, with no error.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.BuildReference @Reference VARCHAR(10) OUTPUT AS
                    BEGIN
                        SET @Reference = 'REF-000123';
                    END;

                    DECLARE @CallerReference VARCHAR(10);
                    EXEC dbo.BuildReference @Reference = @CallerReference OUTPUT;
                    """,
                CompliantExplanation: "@CallerReference is now declared with the same length as the OUTPUT parameter it receives, so the full value survives the call."),
        ]);
}
