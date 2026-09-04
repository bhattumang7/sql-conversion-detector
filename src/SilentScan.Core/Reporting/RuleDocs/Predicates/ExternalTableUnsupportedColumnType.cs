using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Predicates;

internal static class ExternalTableUnsupportedColumnType
{
    public static string RuleId => SarifRuleCatalog.ExternalTableUnsupportedColumnTypeRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A `CREATE EXTERNAL TABLE` column list is checked against a fixed PolyBase type
            allow-list at DDL time, before the engine ever opens the referenced
            `DATA_SOURCE`/`LOCATION` - the check runs identically whether the file format is
            delimited text or Parquet, and whether the referenced location exists at all.
            Oracle-confirmed directly (real DDL execution, SQL Server 2022): `SQL_VARIANT`,
            `XML`, `HIERARCHYID`, `GEOMETRY`, `GEOGRAPHY`, `NTEXT`, `TEXT`, `IMAGE`, and
            `TIMESTAMP`/`ROWVERSION` are rejected outright with `Msg 46518: The type '...' is
            not supported with external tables.`, and so is a `VARCHAR(MAX)`/`NVARCHAR(MAX)`/
            `VARBINARY(MAX)` column - unlike an ordinary table, an external table has no
            fixed/MAX split by format; every MAX-length string or binary column is rejected
            regardless of format. The same fixed gate rejects a `CREATE EXTERNAL TABLE AS
            SELECT` column whose source expression resolves to one of these types (`Msg
            15877`), though that select-list-inferred form isn't covered by this rule - only
            an external table's own explicit column declarations are.

            This is exactly the class of bug static analysis exists to catch before it reaches
            a deployment pipeline: the DDL parses fine and reads fine - PolyBase's whole
            purpose is bridging to a schema the server doesn't control - so nothing about the
            statement looks wrong until someone actually runs it against a real data source,
            by which point it's a broken export/import pipeline rather than a two-second code
            review catch.
            """,
        HowToFixIt: """
            Retype the external table's own column declaration to a supported, non-MAX type.
            This is purely a declared-schema restriction on the external table object - the
            underlying source file's actual column can be whatever type it is; PolyBase's type
            conversion happens as it reads each row, so a fixed-length type wide enough for the
            real data (e.g. `NVARCHAR(4000)` in place of `NVARCHAR(MAX)`) is enough to satisfy
            the gate without losing any data the source file actually contains.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "An NVARCHAR(MAX) column on an external table declaration",
                NoncompliantSql: """
                    CREATE EXTERNAL TABLE dbo.SalesExport
                    (
                        SaleId  INT             NOT NULL,
                        Notes   NVARCHAR(MAX)   NULL
                    )
                    WITH
                    (
                        LOCATION    = '/sales/',
                        DATA_SOURCE = ExternalBlobSource,
                        FILE_FORMAT = ExternalParquetFormat
                    );
                    -- Fails to deploy: Msg 46518, "The type 'nvarchar(max)' is not supported
                    -- with external tables" - rejected before DATA_SOURCE is ever opened.
                    """,
                NoncompliantExplanation: "Notes is declared NVARCHAR(MAX) - PolyBase's external-table type gate rejects every MAX-length string/binary column outright, independent of FILE_FORMAT or whether /sales/ exists.",
                CompliantSql: """
                    CREATE EXTERNAL TABLE dbo.SalesExport
                    (
                        SaleId  INT             NOT NULL,
                        Notes   NVARCHAR(4000)  NULL
                    )
                    WITH
                    (
                        LOCATION    = '/sales/',
                        DATA_SOURCE = ExternalBlobSource,
                        FILE_FORMAT = ExternalParquetFormat
                    );
                    """,
                CompliantExplanation: "NVARCHAR(4000) is a fixed-length type wide enough for the real data - the DDL deploys, and PolyBase still converts each row's actual value as it reads it."),
        ]);
}
