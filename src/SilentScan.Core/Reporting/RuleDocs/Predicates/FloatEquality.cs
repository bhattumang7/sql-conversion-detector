using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Predicates;

internal static class FloatEquality
{
    public static string RuleId => SarifRuleCatalog.FloatEqualityRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            `FLOAT` and `REAL` are SQL Server's IEEE-754 binary floating-point types - approximate by
            design, the same way they are in every other language that implements the standard. A
            binary floating-point value can only represent exactly those numbers expressible as a sum
            of powers of two within its available bits; most decimal fractions (0.1, 0.2, 10.5 divided
            by 3, and countless others) have no exact binary representation and are stored as the
            nearest representable value instead. The textbook illustration applies here exactly as it
            does anywhere else: `0.1 + 0.2` does not produce a value that compares equal to `0.3` in
            IEEE-754 arithmetic, because each of the three literals is already rounded to its nearest
            representable binary approximation before the addition even happens, and the rounding
            errors don't cancel out.

            A WHERE or ON predicate using `=` (or `<>`) against a FLOAT/REAL value is therefore
            comparing bit patterns, not comparing the numbers a person reading the query would say are
            "the same." Two computations that a person would call identical - the same formula applied
            to the same inputs in a different order, a value that was written to the column and then
            read back after passing through an intermediate calculation, a value computed on the
            application side in a different language's floating-point implementation and sent as a
              parameter - can produce bit patterns that differ in their last few bits and therefore
            compare unequal. This is a correctness defect, not a performance one: it's independent of
            indexing or plan shape entirely, and the predicate can silently exclude or include the
            wrong rows on data that any reasonable person would consider a match.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "Equality against a FLOAT column excludes a value that should match",
                NoncompliantSql: """
                    CREATE TABLE dbo.SensorReadings
                    (
                        ReadingId  INT   NOT NULL PRIMARY KEY,
                        Threshold  FLOAT NOT NULL
                    );
                    INSERT INTO dbo.SensorReadings (ReadingId, Threshold) VALUES (1, 0.1 + 0.2);

                    SELECT ReadingId
                    FROM dbo.SensorReadings
                    WHERE Threshold = 0.3;
                    """,
                NoncompliantExplanation: "0.1 + 0.2 does not produce the exact IEEE-754 bit pattern for 0.3 - the row inserted with a value any person would call 0.3 fails to match this predicate, silently, with no error anywhere.",
                CompliantSql: """
                    CREATE TABLE dbo.SensorReadings
                    (
                        ReadingId  INT             NOT NULL PRIMARY KEY,
                        Threshold  DECIMAL(10, 4)  NOT NULL
                    );
                    INSERT INTO dbo.SensorReadings (ReadingId, Threshold) VALUES (1, 0.1 + 0.2);

                    SELECT ReadingId
                    FROM dbo.SensorReadings
                    WHERE Threshold = 0.3;
                    """,
                CompliantExplanation: "DECIMAL is an exact base-10 type - 0.1 + 0.2 is stored as exactly 0.3000 with no representation error, so the equality predicate matches every value a person would consider equal. Where FLOAT is genuinely required (interop with a system that only speaks IEEE-754, say), compare with an explicit tolerance instead: ABS(Threshold - 0.3) < 0.0001."),
        ]);
}
