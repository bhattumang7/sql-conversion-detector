using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Predicates;

internal static class AlwaysEncryptedAssignmentMismatch
{
    internal static class LiteralSource
    {
        public static string RuleId => SarifRuleCatalog.AlwaysEncryptedAssignmentMismatchRuleId(AlwaysEncryptedAssignmentMismatchKind.LiteralSource);

        public static RuleDocContent Content { get; } = new(
            WhyItMatters: """
                An Always Encrypted column's plaintext value never reaches the server - a
                column-encryption-aware client encrypts the value with the column's own key and
                algorithm before sending it as a parameter. Confirmed directly against a real SQL
                Server instance: assigning a bare literal into an encrypted column via `INSERT ...
                VALUES` or an `UPDATE`/`MERGE` `SET` clause fails to compile with Msg 206 ("Operand
                type clash"), because the server has no way to encrypt a plaintext literal itself.

                A `NULL` literal is exempt - it is untyped and assigns to an encrypted column
                without error. A parameter or variable source is never flagged - the client driver
                is expected to encrypt it appropriately before sending it.
                """,
            HowToFixIt: """
                Pass the value as a parameter from an Always Encrypted-enabled client connection
                instead of writing it as a literal, so the driver encrypts it to match the target
                column's key and algorithm before the statement reaches the server.
                """,
            Examples:
            [
                new RuleDocExample(
                    Title: "A literal assigned into an encrypted column never compiles",
                    NoncompliantSql: """
                        UPDATE dbo.Customer
                        SET Ssn = '123-45-6789'
                        WHERE CustomerId = 1;
                        """,
                    NoncompliantExplanation: "Ssn is an Always Encrypted column - this UPDATE fails to compile with Msg 206 every time it runs, since the server cannot encrypt the literal.",
                    CompliantSql: """
                        -- from an Always Encrypted-enabled client connection
                        UPDATE dbo.Customer
                        SET Ssn = @ssn
                        WHERE CustomerId = 1;
                        """,
                    CompliantExplanation: "The value arrives as a parameter, already encrypted by the client driver to match Ssn's own key and algorithm."),
            ]);
    }

    internal static class EncryptionStateMismatch
    {
        public static string RuleId => SarifRuleCatalog.AlwaysEncryptedAssignmentMismatchRuleId(AlwaysEncryptedAssignmentMismatchKind.EncryptionStateMismatch);

        public static RuleDocContent Content { get; } = new(
            WhyItMatters: """
                Two Always Encrypted columns are only implicitly compatible when their encryption
                state matches exactly. Confirmed directly against a real SQL Server instance:
                assigning one column into another via an `UPDATE`/`MERGE` `SET` clause fails to
                compile with Msg 206 ("Operand type clash ... is incompatible with ...") whenever
                their encryption state differs - encrypted vs. plaintext (in either direction), or
                a different encryption type (deterministic vs. randomized), even when both columns
                share the same column encryption key.

                Scoped to column-to-column assignments where both the target and the source resolve
                to a statically known base column (through the query's own scope, including joins
                and aliases) - an expression, function call, or parameter/variable source is never
                flagged, since the engine's own restriction (and the encrypted value's actual
                origin) is not staticaly decidable for those shapes.
                """,
            HowToFixIt: """
                Route the value through an Always Encrypted-enabled client - decrypt it and
                re-encrypt it to the target column's own key and algorithm - instead of assigning
                the encrypted value directly server-side.
                """,
            Examples:
            [
                new RuleDocExample(
                    Title: "Copying between differently-encrypted columns never compiles",
                    NoncompliantSql: """
                        UPDATE dbo.Customer
                        SET SsnRandomized = SsnDeterministic;
                        """,
                    NoncompliantExplanation: "SsnRandomized and SsnDeterministic use different encryption types - this UPDATE fails to compile with Msg 206 every time it runs, regardless of which column holds the source value.",
                    CompliantSql: """
                        -- from an Always Encrypted-enabled client connection:
                        -- read SsnDeterministic (client decrypts it), then write it back
                        -- as a parameter (client re-encrypts it for SsnRandomized)
                        UPDATE dbo.Customer
                        SET SsnRandomized = @decryptedSsn;
                        """,
                    CompliantExplanation: "The client decrypts the source value and re-encrypts it for the target column's own encryption type before the statement reaches the server."),
            ]);
    }
}
