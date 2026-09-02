using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Predicates;

internal static class ExecuteAtLargeObjectParameterCrashesSession
{
    public static string RuleId => SarifRuleCatalog.ExecuteAtLargeObjectParameterRuleId(SilentScan.Core.Predicates.ExecuteAtLargeObjectParameterFindingKind.CrashesSession);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            EXECUTE ( 'command_text', @param, ... ) AT linked_server (or AT DATA_SOURCE data_source_name,
            the elastic-query form) passes each additional comma-separated value as a remote-call
            parameter, independent of the command text itself. Confirmed directly against real SQL
            Server instances (2022 and 2025): passing a VARCHAR(MAX), NVARCHAR(MAX), or
            VARBINARY(MAX)-typed local variable or parameter at one of these positions does not
            produce a clean error - it crashes the connection outright with an internal engine
            assertion failure ("A system assertion check has failed", memilb.cpp, "pilb->m_cRef ==
            0") and kills the session. A same-length fixed-size parameter (e.g. NVARCHAR(100)) or an
            INT at the same position does not trigger this. The command text argument itself (the
            first string) is unaffected even when it is MAX-typed - only the additional parameter
            positions are.
            """,
        HowToFixIt: """
            Do not pass a MAX-typed variable or parameter as an EXECUTE (...) AT argument. Truncate it
            to a fixed-length type before the call, or restructure the remote call so the large value
            is embedded in the command text itself rather than passed as a separate parameter.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "Passing an NVARCHAR(MAX) parameter to EXECUTE AT crashes the connection",
                NoncompliantSql: """
                    DECLARE @payload NVARCHAR(MAX) = N'...';
                    EXEC ('SELECT 1', @payload) AT MyLinkedServer;
                    """,
                NoncompliantExplanation: "@payload is NVARCHAR(MAX) - this crashes the connection with an internal assertion failure instead of a clean error.",
                CompliantSql: """
                    DECLARE @payload NVARCHAR(4000) = N'...';
                    EXEC ('SELECT 1', @payload) AT MyLinkedServer;
                    """,
                CompliantExplanation: "@payload is a fixed-length NVARCHAR(4000) - the remote call parameter binds normally."),
        ]);
}

internal static class ExecuteAtXmlParameterRejected
{
    public static string RuleId => SarifRuleCatalog.ExecuteAtLargeObjectParameterRuleId(SilentScan.Core.Predicates.ExecuteAtLargeObjectParameterFindingKind.XmlRejected);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            EXECUTE ( 'command_text', @param, ... ) AT linked_server (or AT DATA_SOURCE
            data_source_name) rejects an xml-typed parameter at any of the additional
            comma-separated positions. Confirmed directly against real SQL Server instances (2022 and
            2025): the statement fails at execution with Msg 9512 ("Xml data type is not supported as
            a parameter to remote calls"), before any remote connection is attempted, regardless of
            the target or the document's actual content.
            """,
        HowToFixIt: """
            Convert the xml value to a string (for example via .value() or CAST to
            NVARCHAR(MAX)) before passing it as an EXECUTE (...) AT argument, and parse it back into
            xml on the remote side if it is genuinely needed there.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "Passing an xml parameter to EXECUTE AT always fails",
                NoncompliantSql: """
                    DECLARE @doc XML = '<a/>';
                    EXEC ('SELECT 1', @doc) AT MyLinkedServer;
                    """,
                NoncompliantExplanation: "@doc is xml - this statement fails every time it runs with Msg 9512.",
                CompliantSql: """
                    DECLARE @doc XML = '<a/>';
                    DECLARE @docText NVARCHAR(4000) = CAST(@doc AS NVARCHAR(4000));
                    EXEC ('SELECT 1', @docText) AT MyLinkedServer;
                    """,
                CompliantExplanation: "The xml value is cast to a plain NVARCHAR before being passed as the remote call parameter."),
        ]);
}
