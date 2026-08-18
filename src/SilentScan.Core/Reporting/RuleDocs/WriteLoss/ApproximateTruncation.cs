using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.WriteLoss;

internal static class ApproximateTruncation
{
    public static string RuleId => SarifRuleCatalog.WriteLossApproximateTruncationRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            REAL and FLOAT store values in IEEE-754 binary floating point - an approximate
            representation with no fixed number of digits after the point, chosen for range rather
            than exactness. INT, BIGINT, SMALLINT, and TINYINT store exact whole numbers with no
            fractional component at all. When an INSERT or UPDATE assigns a REAL/FLOAT value into
            one of these exact integer targets, SQL Server has to reconcile the two representations,
            and it does so by implicit conversion rules that truncate toward zero - not round - and
            it does this without raising an error or a truncation warning under default session
            settings. 19.99 assigned into an INT column silently becomes 19, not 20; -4.7 becomes
            -4, not -5.

            The truncation-not-rounding behavior is the part that catches people off guard even
            when they know narrowing is happening: many developers reasonably assume SQL Server
            "rounds" numeric conversions the way CAST/ROUND-aware code paths do elsewhere, but the
            plain implicit conversion from an approximate to an exact type simply discards
            everything past the decimal point. Because this happens silently and the row commits
            successfully, the loss is invisible until someone compares a computed total against
            what the integer column actually holds and the numbers don't reconcile - and by then,
            the discarded fractional data was never persisted anywhere to recover.

            This is especially easy to hit anywhere a calculation - an average, a percentage, a
            unit conversion - flows directly from a REAL/FLOAT expression into a column that was
            modeled as an integer count or quantity, since nothing in the query's syntax marks the
            conversion as happening; it is purely a consequence of the two columns'/expressions'
            declared types disagreeing.
            """,
        HowToFixIt: """
            If the fractional part is genuinely meaningless for this column (e.g. it's a count that
            should never have had a fraction in the first place), round the value explicitly with
            ROUND() before assigning it, so the conversion happens on a value the author already
            decided how to handle, rather than being silently truncated toward zero by the engine.
            If the fractional part is meaningful and should be preserved, widen the target column
            to DECIMAL/NUMERIC with an appropriate scale (or REAL/FLOAT itself) instead of an exact
            integer type.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A computed average written into an INT column",
                NoncompliantSql: """
                    CREATE TABLE dbo.OrderStats
                    (
                        OrderId      INT NOT NULL PRIMARY KEY,
                        AvgUnitPrice INT NOT NULL
                    );

                    UPDATE dbo.OrderStats
                    SET AvgUnitPrice = 19.99
                    WHERE OrderId = 1;
                    """,
                NoncompliantExplanation: "19.99 is an approximate/decimal value being narrowed into an exact INT column - the engine truncates it to 19 with no error, silently losing .99.",
                CompliantSql: """
                    CREATE TABLE dbo.OrderStats
                    (
                        OrderId      INT NOT NULL PRIMARY KEY,
                        AvgUnitPrice INT NOT NULL
                    );

                    UPDATE dbo.OrderStats
                    SET AvgUnitPrice = ROUND(19.99, 0)
                    WHERE OrderId = 1;
                    """,
                CompliantExplanation: "ROUND(19.99, 0) evaluates to 20 before the assignment happens, so the value written matches what a reader would expect from '19.99 rounded' rather than a silent truncation toward zero."),
        ]);
}
