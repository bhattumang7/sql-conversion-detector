namespace SilentScan.Core.Predicates;

/// <summary>
/// Roadmap Phase E1: the class of write-side (INSERT/UPDATE) implicit conversion an assignment
/// risks - unlike <see cref="Rules.Verdict"/> (a WHERE-clause seek/scan question), every member
/// here is about silent DATA LOSS: T-SQL rounds, truncates, or replaces the source value with no
/// error raised at all. Oracle-verified against a real Docker instance by actually inserting a
/// probe row into a throwaway table and reading it back (SET SHOWPLAN_XML is irrelevant here -
/// these are runtime DML behaviors, not query-plan ones). A too-long VARCHAR or an out-of-range
/// INT overflow instead raise a hard error and are deliberately NOT covered here - flagging
/// something T-SQL already stops for you would be a false "silent" claim CLAUDE.md's precision
/// discipline forbids.
/// </summary>
public enum WriteLossKind
{
    /// <summary>NVARCHAR/NCHAR source assigned to a VARCHAR/CHAR target: any character outside the target collation's codepage becomes '?', silently, with no error (oracle-verified: N'日本語' -&gt; VARCHAR(20) reads back '???').</summary>
    UnicodeToNonUnicodeReplacement,

    /// <summary>REAL/FLOAT source assigned to an exact integer target (TINYINT/SMALLINT/INT/BIGINT): the fractional part is silently dropped, not rounded or errored (oracle-verified: 7.9 -&gt; INT reads back 7).</summary>
    ApproximateToExactTruncation,

    /// <summary>DECIMAL/NUMERIC source assigned to a DECIMAL/NUMERIC target with a smaller scale: digits past the target's scale are silently rounded away (oracle-verified: 123.456 -&gt; DECIMAL(10,2) reads back 123.46).</summary>
    NumericScaleNarrowing,

    /// <summary>DATETIME/DATETIME2/SMALLDATETIME/DATETIMEOFFSET source assigned to a DATE target: the time-of-day component is silently dropped (oracle-verified: '2024-01-15 13:45:00' -&gt; DATE reads back 2024-01-15).</summary>
    TemporalPrecisionLoss,
}
