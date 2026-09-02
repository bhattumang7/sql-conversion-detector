using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.WriteLoss;

internal static class UnicodeReplacement
{
    public static string RuleId => SarifRuleCatalog.WriteLossUnicodeReplacementRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            Every non-Unicode column - VARCHAR, CHAR, TEXT - stores its characters using a single
            code page determined by the column's collation, one byte (or a handful of bytes for
            double-byte code pages) per character, and that code page can only represent a subset
            of Unicode. An NVARCHAR/NCHAR value, by contrast, is UCS-2/UTF-16 and can represent
            essentially any character in any language. The moment an INSERT or UPDATE assigns a
            Unicode source value into a non-Unicode target, SQL Server has to narrow every
            character down to whatever the target's code page can hold - and for any character
            that isn't in that code page, the engine does not raise an error, truncate the
            statement, or warn. It silently substitutes the code page's designated replacement
            character, '?' (0x3F), and continues.

            This makes the failure mode uniquely dangerous compared to ordinary length truncation:
            there is no SET ANSI_WARNINGS, no @@ROWCOUNT anomaly, no exception anywhere in the
            call stack. The row inserts cleanly, the transaction commits, and the application
            reports success. The only visible symptom is that some fraction of rows now contain
              literal question marks in place of names, addresses, or free-text fields that
            originally held an em-dash, a curly quote from a pasted Word document, a Cyrillic or
            CJK character, or an emoji - all common in real user input the moment a source column
            far upstream (or a parameter bound as NVARCHAR by default, which is exactly what
            ADO.NET and most ORMs do for a plain C# `string`) allowed Unicode through.

            Because this is a data-loss bug rather than a performance one, it compounds silently
            over time: every write against the mismatched column erodes data a little more, and by
            the time anyone notices the '?' characters in a report, the original values are
            already gone - there is no log of what was overwritten, since the statement itself
            reported no error.

            This rule excludes a target whose collation carries the _UTF8 flag: oracle-confirmed a
            UTF8-collation VARCHAR/CHAR target stores every character exactly as written, with no
            '?' substitution, since UTF-8 can encode any Unicode character. Such a target has a
            different risk instead - its declared length is a byte cap rather than a character cap,
            so a multi-byte character can still overflow it even though nothing gets replaced.
            """,
        HowToFixIt: """
            Change the target column's declared type from VARCHAR/CHAR (or TEXT) to NVARCHAR/NCHAR
            (or NTEXT), matching whatever range of characters the source data can actually contain.
            This is a schema change, not a query rewrite - the loss happens at the storage layer,
            so there is no WHERE-clause or parameter trick that fixes it once the column itself is
            non-Unicode. If the column is deliberately restricted to a known code page (for example
            a country/currency code guaranteed ASCII), the finding is a false positive for that
            specific column and the fix is to leave it as-is; the rule can't distinguish "genuinely
            ASCII-only by contract" from "happens to be ASCII-only so far" from static analysis
            alone.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "An NVARCHAR parameter written into a VARCHAR column",
                NoncompliantSql: """
                    CREATE TABLE dbo.Customers
                    (
                        CustomerId INT          NOT NULL PRIMARY KEY,
                        FullName   VARCHAR(100) NOT NULL
                    );

                    CREATE PROCEDURE dbo.AddCustomer (@fullName NVARCHAR(100))
                    AS
                    INSERT INTO dbo.Customers (CustomerId, FullName)
                    VALUES (1, @fullName);
                    """,
                NoncompliantExplanation: "If @fullName arrives as N'José Alanís' or contains any character outside the database's default code page, every unrepresentable character silently becomes '?' - the INSERT still succeeds with no error.",
                CompliantSql: """
                    CREATE TABLE dbo.Customers
                    (
                        CustomerId INT           NOT NULL PRIMARY KEY,
                        FullName   NVARCHAR(100) NOT NULL
                    );

                    CREATE PROCEDURE dbo.AddCustomer (@fullName NVARCHAR(100))
                    AS
                    INSERT INTO dbo.Customers (CustomerId, FullName)
                    VALUES (1, @fullName);
                    """,
                CompliantExplanation: "FullName is now NVARCHAR, so it can store any character the parameter carries - there is no narrowing conversion left to lose data."),
            new RuleDocExample(
                Title: "A Unicode literal assigned by UPDATE",
                NoncompliantSql: """
                    CREATE TABLE dbo.Products
                    (
                        ProductId   INT          NOT NULL PRIMARY KEY,
                        DisplayName VARCHAR(50)  NOT NULL
                    );

                    UPDATE dbo.Products
                    SET DisplayName = N'Café Noir — Deluxe'
                    WHERE ProductId = 42;
                    """,
                NoncompliantExplanation: "The N'...' literal is Unicode text containing an em dash and an accented character; writing it into the VARCHAR DisplayName column replaces whichever characters the default code page can't represent with '?'.",
                CompliantSql: """
                    CREATE TABLE dbo.Products
                    (
                        ProductId   INT           NOT NULL PRIMARY KEY,
                        DisplayName NVARCHAR(50)  NOT NULL
                    );

                    UPDATE dbo.Products
                    SET DisplayName = N'Café Noir — Deluxe'
                    WHERE ProductId = 42;
                    """,
                CompliantExplanation: "DisplayName is Unicode, so the literal's full character set - accents, em dashes, anything else - is stored exactly as written."),
        ]);
}
