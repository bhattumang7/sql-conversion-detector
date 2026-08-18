namespace SilentScan.Core.Predicates;

public enum DeprecatedSyntaxFindingKind
{
    /// <summary>A `--`/`/* */` comment containing an untracked "to-do" marker.</summary>
    TaskCommentTodo,

    /// <summary>A `--`/`/* */` comment containing an untracked "fix-me" marker.</summary>
    TaskCommentFixme,

    /// <summary>A non-ANSI comparison operator (`!=`, `!&lt;`, `!&gt;`) written instead of the
    /// ANSI-standard `&lt;&gt;`/`&gt;=`/`&lt;=` form.</summary>
    NonAnsiComparisonOperator,

    /// <summary>`x = NULL` written instead of `x IS NULL` - under the default `ANSI_NULLS ON`
    /// session setting this silently never matches any row, including a genuinely NULL one -
    /// oracle-confirmed directly (a real seeded NULL row: `= NULL` and `&lt;&gt; NULL` both match
    /// zero rows where `IS NULL` correctly matches one).</summary>
    EqualsNullComparison,

    /// <summary>`x &lt;&gt; NULL` / `x != NULL` written instead of `x IS NOT NULL` - the same silent
    /// always-false trap as <see cref="EqualsNullComparison"/>, the other direction.</summary>
    NotEqualsNullComparison,

    /// <summary>A `LIKE` pattern containing no wildcard character (`%`, `_`, `[`) and no trailing
    /// space - behaviorally equivalent to a plain `=` comparison, so `LIKE` adds only the
    /// suggestion of a pattern match that was never actually written.</summary>
    LikeWithNoWildcard,

    /// <summary>A reference to one of the pre-SQL-Server-2005 system compatibility views
    /// (`sysobjects`, `syscolumns`, ...) - retained only for backward compatibility, unmaintained,
    /// and missing columns/rows the real `sys.*` catalog views expose.</summary>
    LegacySystemCompatibilityView,

    /// <summary>A table hint written without the `WITH` keyword (`FROM T (NOLOCK)` instead of
    /// `FROM T WITH (NOLOCK)`) - a deprecated syntax form still accepted by the parser and engine
    /// alike (oracle-confirmed: still parses and executes on the current engine).</summary>
    TableHintWithoutWith,

    /// <summary>`CREATE PROCEDURE Name;N` - a numbered-procedure-group definition, a deprecated
    /// T-SQL feature (grouping unrelated procedure bodies under one droppable name) still accepted
    /// by the parser and engine (oracle-confirmed: still compiles and executes on the current
    /// engine).</summary>
    NumberedProcedureDefinition,

    /// <summary>`EXEC Name;N` - invoking one member of a numbered-procedure-group by its number
    /// suffix.</summary>
    NumberedProcedureExecution,

    /// <summary>A string literal used as a column alias (`SELECT Col 'My Alias'`) instead of a
    /// real identifier or a bracket/quote-delimited one - a deprecated aliasing form still
    /// accepted by the parser and engine (oracle-confirmed: still parses and executes).</summary>
    StringLiteralColumnAlias,

    /// <summary>An `EXEC` of one of the pre-2005 legacy security-administration system stored
    /// procedures (`sp_addlogin`, `sp_password`, ...) - superseded by
    /// `CREATE LOGIN`/`CREATE USER`/`ALTER ROLE`/`ALTER SERVER ROLE` and the modern principal
    /// model; several of the named procedures have already been fully removed from current SQL
    /// Server versions and will hard-fail at compile time, others remain present but deprecated.</summary>
    RemovedSecurityStoredProcedure,

    /// <summary>`SET ROWCOUNT n` - superseded by `TOP (n)`, and explicitly documented by
    /// Microsoft as not honored by `INSERT`/`UPDATE`/`DELETE` in a future release; a
    /// forward-compatibility risk distinct from any of the already-shipped `SET`-option plan-feature
    /// findings.</summary>
    DeprecatedSetRowcount,
}

/// <summary>
/// docs/detection-checklist.md Tier 4 "Task-comment tracking" (to-do/fix-me) and "Non-ANSI and
/// deprecated spellings". Fully syntax-only: no <see cref="Catalog.DatabaseCatalog"/> needed for any
/// member. No oracle needed except where noted per-kind above (the `= NULL`/`&lt;&gt; NULL` silent
/// always-false trap, and the still-parses-on-the-current-engine claims for the deprecated-but-not-
/// removed syntax forms) - those claims were verified directly against the Docker oracle before this
/// stream shipped, never assumed from documentation or the third-party rule set this Tier 4 entry is
/// derived from (CLAUDE.md: never mention that third party's own code or numbers anywhere in this
/// codebase - every name list and threshold here is independently sourced from Microsoft's own public
/// documentation or measured directly against this codebase's own corpus).
///
/// Two related concepts from the same real underlying third-party rule were oracle-confirmed to be
/// hard PARSE errors under this tool's own `TSql160Parser` dialect and are therefore closed, not
/// built, the same documented disposition `COMPUTE`/`COMPUTE BY` and the old `*=`/`=*` outer-join
/// operators already received elsewhere in this file: old-style unparenthesized `RAISERROR 50001
/// 'message'` (modern parenthesized `RAISERROR('message', severity, state)` is the only form that
/// still parses), and an `INDEX` table hint naming an index with an explicit schema-qualified
/// two-part name (`WITH (INDEX(dbo.IX_Foo))` - only a bare, unqualified index name parses in an
/// `INDEX` hint).
/// </summary>
public sealed record DeprecatedSyntaxFinding(
    DeprecatedSyntaxFindingKind Kind,
    string ModuleQualifiedName,
    string SourcePath,
    int Line,
    int Column,
    string DetailText,
    FindingConfidence Confidence = FindingConfidence.Medium);
