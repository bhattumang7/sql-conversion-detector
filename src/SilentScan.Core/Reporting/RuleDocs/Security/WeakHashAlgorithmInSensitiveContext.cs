using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Security;

internal static class WeakHashAlgorithmInSensitiveContext
{
    public static string RuleId => SarifRuleCatalog.SecurityRuleId(SecurityFindingKind.WeakHashAlgorithmInSensitiveContext);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            The same weak/deprecated `HASHBYTES` algorithm choice (MD2, MD4, MD5, SHA, SHA1) as the
            general-use sibling rule, but recognized in a context this pass can identify as
            security-sensitive: the value being hashed is itself a credential-suggestive-named
            variable or column, or the `HASHBYTES` call appears directly as an operand of a
            comparison (a WHERE/JOIN ON/HAVING predicate) - a shape strongly suggestive of an
            authentication check comparing a computed hash against a stored one.

            This is a sharper, more actionable claim than the general-use sibling, since a weak
            algorithm's broken collision resistance matters precisely in this kind of use: an
            attacker who can influence or observe the hashed value has real tools against MD5/SHA1
            that don't exist against a modern algorithm. Reported at Medium confidence rather than
            High - it names a real, actionable risk, but this pass never traces as far as an actual
            external-input boundary, so it can't prove the value is genuinely attacker-influenced,
            only that the shape of the code looks security-relevant.
            """,
        HowToFixIt: """
            Use HASHBYTES with SHA2_256 or SHA2_512 instead of a weak/deprecated algorithm,
            especially in a security-sensitive context like this one.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "HASHBYTES with SHA1 hashing a credential-named column",
                NoncompliantSql: """
                    SELECT HASHBYTES('SHA1', Password) AS Hashed
                    FROM dbo.Users;
                    """,
                NoncompliantExplanation: "Password's own name and the security-sensitive framing of hashing it together suggest this is an authentication-relevant computation - SHA1's broken collision resistance is a real risk here, not merely an algorithm-choice nicety.",
                CompliantSql: """
                    SELECT HASHBYTES('SHA2_256', Password) AS Hashed
                    FROM dbo.Users;
                    """,
                CompliantExplanation: "SHA2_256 removes the weak-algorithm risk from what looks like a security-sensitive hash computation. (In practice, application-layer password hashing should also use a slow, salted algorithm like bcrypt/PBKDF2 rather than a single fast HASHBYTES call - out of this rule's own scope, which is only the algorithm choice HASHBYTES itself was given.)"),
        ]);
}
