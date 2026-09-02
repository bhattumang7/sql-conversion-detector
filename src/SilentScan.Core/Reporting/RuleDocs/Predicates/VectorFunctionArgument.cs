using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Predicates;

internal static class VectorFunctionNonVectorOperand
{
    public static string RuleId => SarifRuleCatalog.VectorFunctionArgumentRuleId(SilentScan.Core.Predicates.VectorFunctionArgumentFindingKind.NonVectorOperand);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            VECTOR_DISTANCE, VECTOR_NORM, and VECTORPROPERTY - SQL Server 2025's native vector
            functions - require an actual VECTOR(n)-typed value at every vector-position argument.
            Confirmed directly against a real SQL Server 2025 instance: passing anything else there -
            a VARCHAR/NVARCHAR column or variable holding the vector's string representation, a
            VARCHAR(MAX)/NVARCHAR(MAX) value, a SQL_VARIANT, an XML value, or a bare string literal -
            fails to compile with Msg 8116 ("Argument data type ... is invalid for argument ... of
            ... function"). This is not limited to large-object types: even a short, ordinary
            VARCHAR(10) column fails identically. There is no implicit conversion from a
            vector-literal string into VECTOR(n) at these call sites; the value must already carry
            the VECTOR(n) type, typically via an explicit CAST(... AS VECTOR(n)).

            A NULL literal or an expression whose type this pass cannot statically resolve is never
            flagged - only an argument whose declared type is provably not VECTOR is reported.
            """,
        HowToFixIt: """
            Pass an actual VECTOR(n)-typed value at this argument position - a column or variable
            declared VECTOR(n), or an explicit CAST(... AS VECTOR(n)) expression - instead of a
            string/other-typed value the engine would otherwise have to implicitly convert.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "Passing a VARCHAR column to VECTOR_DISTANCE never compiles",
                NoncompliantSql: """
                    CREATE TABLE dbo.Embedding
                    (
                        EmbeddingId INT NOT NULL PRIMARY KEY,
                        RawVector   VARCHAR(4000) NOT NULL,
                        Query       VECTOR(3) NOT NULL
                    );

                    SELECT EmbeddingId
                    FROM dbo.Embedding
                    WHERE VECTOR_DISTANCE('cosine', RawVector, Query) < 0.2;
                    """,
                NoncompliantExplanation: "RawVector is VARCHAR - this statement fails to compile with Msg 8116 every time it runs.",
                CompliantSql: """
                    SELECT EmbeddingId
                    FROM dbo.Embedding
                    WHERE VECTOR_DISTANCE('cosine', CAST(RawVector AS VECTOR(3)), Query) < 0.2;
                    """,
                CompliantExplanation: "RawVector is explicitly cast to VECTOR(3) before being passed - a genuinely VECTOR-typed argument."),
        ]);
}

internal static class VectorFunctionDimensionMismatch
{
    public static string RuleId => SarifRuleCatalog.VectorFunctionArgumentRuleId(SilentScan.Core.Predicates.VectorFunctionArgumentFindingKind.DimensionMismatch);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            VECTOR_DISTANCE requires its two vector arguments to share the same dimension.
            Confirmed directly against a real SQL Server 2025 instance: calling VECTOR_DISTANCE with
            a VECTOR(3) value and a VECTOR(4) value fails at execution with Msg 42204 ("The vector
            dimensions ... and ... do not match"), for every row, regardless of the actual vector
            contents - the dimension counts are a static property of each argument's declared type,
            not something the row's data could ever satisfy.
            """,
        HowToFixIt: """
            Declare both vector arguments with the same dimension, or cast one to match the other's
            dimension, before calling VECTOR_DISTANCE.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "Comparing a VECTOR(3) against a VECTOR(4) always fails",
                NoncompliantSql: """
                    DECLARE @a VECTOR(3) = CAST('[1,2,3]' AS VECTOR(3));
                    DECLARE @b VECTOR(4) = CAST('[1,2,3,4]' AS VECTOR(4));

                    SELECT VECTOR_DISTANCE('cosine', @a, @b);
                    """,
                NoncompliantExplanation: "@a and @b declare different dimensions (3 vs 4) - this call fails at execution with Msg 42204 every time it runs.",
                CompliantSql: """
                    DECLARE @a VECTOR(3) = CAST('[1,2,3]' AS VECTOR(3));
                    DECLARE @b VECTOR(3) = CAST('[1,2,3]' AS VECTOR(3));

                    SELECT VECTOR_DISTANCE('cosine', @a, @b);
                    """,
                CompliantExplanation: "@a and @b both declare VECTOR(3) - the dimensions match."),
        ]);
}
