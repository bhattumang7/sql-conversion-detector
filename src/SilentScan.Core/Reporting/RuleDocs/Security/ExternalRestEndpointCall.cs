using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Security;

internal static class ExternalRestEndpointCall
{
    public static string RuleId => SarifRuleCatalog.SecurityRuleId(SecurityFindingKind.ExternalRestEndpointCall);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            `sp_invoke_external_rest_endpoint` makes an outbound HTTPS call from inside the database
            engine itself, to whatever URL the call site supplies - a real outbound-network call
            surface distinct from the shipped hardcoded-IP-address finding. Where that finding only
            catches a literal address embedded directly in source text, this call's `@url` (and its
            optional `@payload`/`@headers`) can be entirely computed at runtime from table data,
            parameters, or string concatenation, with no literal address anywhere in the statement for
            a text-pattern check to see.

            This finding never claims the call is malicious or misconfigured - it only surfaces that
            this statement makes an outbound network call at all, which is exactly the population a
            manual security review of a module's egress surface needs to start from: is the target
            endpoint trusted, is the payload leaking anything sensitive, and is the target itself
            free of attacker influence.
            """,
        HowToFixIt: """
            Confirm the target endpoint is trusted, that no sensitive data is sent in the payload or
            headers, and that the URL itself cannot be influenced by untrusted input.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A module calling out to an external REST endpoint",
                NoncompliantSql: """
                    CREATE PROCEDURE dbo.usp_NotifyWebhook
                        @Payload NVARCHAR(MAX)
                    AS
                    BEGIN
                        EXEC sp_invoke_external_rest_endpoint
                            @url = 'https://example.com/webhook',
                            @method = 'POST',
                            @payload = @Payload;
                    END;
                    """,
                NoncompliantExplanation: "This statement makes a real outbound HTTPS call carrying caller-supplied data - worth a manual check that the target is trusted and the payload doesn't leak anything sensitive, the same review a hardcoded IP address would prompt."),
        ]);
}
