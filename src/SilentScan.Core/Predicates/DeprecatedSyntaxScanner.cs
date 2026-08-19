using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 4 "Task-comment tracking" and "Non-ANSI and deprecated
/// spellings" - see <see cref="DeprecatedSyntaxFinding"/> for the full scope, oracle-verification,
/// and closed-item documentation.
/// </summary>
public static class DeprecatedSyntaxScanner
{
    // Microsoft's own documented pre-SQL-Server-2005 system compatibility views ("Mapping System
    // Tables to System Views", Microsoft Learn) - retained for backward compatibility only, missing
    // columns/rows the real sys.* catalog views expose. Independently sourced from Microsoft's own
    // public documentation, not the third-party rule set this Tier 4 entry is derived from.
    private static readonly HashSet<string> LegacyCompatibilityViewNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "sysaltfiles", "syscacheobjects", "syscharsets", "syscolumns", "syscomments", "sysconfigures",
        "sysconstraints", "syscurconfigs", "sysdatabases", "sysdepends", "sysdevices", "sysfilegroups",
        "sysfiles", "sysforeignkeys", "sysfulltextcatalogs", "sysindexes", "sysindexkeys", "syslanguages",
        "syslockinfo", "syslocks", "syslogins", "sysmembers", "sysmessages", "sysobjects", "sysoledbusers",
        "sysopentapes", "sysperfinfo", "syspermissions", "sysprocesses", "sysprotects", "sysreferences",
        "sysremotelogins", "sysservers", "systypes", "sysusers",
    };

    // Microsoft's own documented pre-2005 legacy security-administration system stored procedures
    // ("Deprecated Database Engine Features in SQL Server", Microsoft Learn) - superseded by
    // CREATE LOGIN/CREATE USER/ALTER ROLE/ALTER SERVER ROLE. Independently sourced from Microsoft's
    // own public documentation.
    private static readonly HashSet<string> RemovedSecurityStoredProcedureNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "sp_addapprole", "sp_addlogin", "sp_addremotelogin", "sp_addrole", "sp_addrolemember",
        "sp_addserver", "sp_addsrvrolemember", "sp_adduser", "sp_approlepassword", "sp_changeobjectowner",
        "sp_dbfixedrolepermission", "sp_defaultdb", "sp_defaultlanguage", "sp_denylogin", "sp_dropalias",
        "sp_dropapprole", "sp_droplogin", "sp_dropremotelogin", "sp_droprole", "sp_droprolemember",
        "sp_dropsrvrolemember", "sp_dropuser", "sp_grantdbaccess", "sp_grantlogin", "sp_helpremotelogin",
        "sp_helprotect", "sp_helpuser", "sp_password", "sp_remoteoption", "sp_revokedbaccess",
        "sp_revokelogin", "sp_srvrolepermission",
    };

    /// <param name="parseResult">The already-parsed module/file to scan.</param>
    /// <param name="catalog">
    /// When supplied (live/corpus mode only - file-mode scanning never populates this), used to
    /// suppress the "= NULL"/"&lt;&gt; NULL" findings for a module whose own live-read
    /// <c>sys.sql_modules.uses_ansi_nulls</c> is false: under ANSI_NULLS OFF (baked in at
    /// CREATE/ALTER time), <c>col = NULL</c> behaves as <c>col IS NULL</c> and genuinely matches
    /// NULL rows, so the finding's core claim - a silent always-false trap - would be actively
    /// wrong for that module. A module this pass can't resolve the flag for (file-mode, or a
    /// live catalog lookup miss) keeps the finding - CLAUDE.md precision discipline treats a
    /// false positive as worse than a missed one, but an unresolved flag is the documented
    /// majority-case default (ANSI_NULLS ON), not a license to suppress speculatively.
    /// </param>
    public static IReadOnlyList<DeprecatedSyntaxFinding> Scan(SqlParseResult parseResult, DatabaseCatalog? catalog = null)
    {
        var findings = new List<DeprecatedSyntaxFinding>();

        ScanTaskComments(parseResult, findings);

        var ansiNullsIsOff = catalog is not null
            && TryGetModuleQualifiedName(parseResult.Fragment) is { } qualifiedName
            && catalog.TryGetModuleUsesAnsiNulls(qualifiedName, out var usesAnsiNulls)
            && !usesAnsiNulls;

        var visitor = new Visitor(parseResult.SourcePath, ansiNullsIsOff);
        parseResult.Fragment.Accept(visitor);
        findings.AddRange(visitor.Findings);

        return
        [
            .. findings
                .OrderBy(f => f.Kind)
                .ThenBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];
    }

    private static void ScanTaskComments(SqlParseResult parseResult, List<DeprecatedSyntaxFinding> findings)
    {
        var fragment = parseResult.Fragment;
        if (fragment.ScriptTokenStream is null || fragment.LastTokenIndex < fragment.FirstTokenIndex)
        {
            return;
        }

        var tokens = fragment.ScriptTokenStream;
        for (var i = fragment.FirstTokenIndex; i <= fragment.LastTokenIndex; i++)
        {
            var token = tokens[i];
            if (token.TokenType is not (TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment)
                || token.Text is not { } text)
            {
                continue;
            }

            if (ContainsWord(text, "TODO"))
            {
                findings.Add(new DeprecatedSyntaxFinding(
                    DeprecatedSyntaxFindingKind.TaskCommentTodo, parseResult.SourcePath, parseResult.SourcePath,
                    token.Line, 1, "Comment contains an untracked \"TODO\" marker.", FindingConfidence.Low));
            }

            if (ContainsWord(text, "FIXME"))
            {
                findings.Add(new DeprecatedSyntaxFinding(
                    DeprecatedSyntaxFindingKind.TaskCommentFixme, parseResult.SourcePath, parseResult.SourcePath,
                    token.Line, 1, "Comment contains an untracked \"FIXME\" marker.", FindingConfidence.Low));
            }
        }
    }

    // Word-boundary match, case-insensitive - the target word fires as a whole word only; a longer
    // word merely containing it as a substring (e.g. a to-do-tracking word embedded in "TODOLIST"
    // or "AUTODOC") does not.
    private static bool ContainsWord(string text, string word)
    {
        var index = 0;
        while ((index = text.IndexOf(word, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var before = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
            var afterIndex = index + word.Length;
            var after = afterIndex >= text.Length || !char.IsLetterOrDigit(text[afterIndex]);
            if (before && after)
            {
                return true;
            }

            index += word.Length;
        }

        return false;
    }

    /// <summary>The qualified name of the single procedure/function/view/trigger a parse result's own top-level CREATE/ALTER statement declares, if any - null for a parse result with no such statement (an ad-hoc script, a batch of plain DML) or with more than one candidate (a multi-object file-mode script, where no single module's own ANSI_NULLS flag would apply to the whole fragment anyway).</summary>
    private static string? TryGetModuleQualifiedName(TSqlFragment fragment)
    {
        if (fragment is not TSqlScript script)
        {
            return null;
        }

        var collector = new ModuleNameCollector();
        script.Accept(collector);
        return collector.Names is [{ } only] ? only : null;
    }

    private static string QualifiedName(SchemaObjectName name) =>
        name.SchemaIdentifier is { } schema
            ? $"{schema.Value}.{name.BaseIdentifier.Value}"
            : name.BaseIdentifier.Value;

    private sealed class ModuleNameCollector : TSqlFragmentVisitor
    {
        public List<string> Names { get; } = [];

        public override void Visit(CreateProcedureStatement node) => Names.Add(QualifiedName(node.ProcedureReference.Name));

        public override void Visit(AlterProcedureStatement node) => Names.Add(QualifiedName(node.ProcedureReference.Name));

        public override void Visit(CreateOrAlterProcedureStatement node) => Names.Add(QualifiedName(node.ProcedureReference.Name));

        public override void Visit(CreateViewStatement node) => Names.Add(QualifiedName(node.SchemaObjectName));

        public override void Visit(AlterViewStatement node) => Names.Add(QualifiedName(node.SchemaObjectName));

        public override void Visit(CreateOrAlterViewStatement node) => Names.Add(QualifiedName(node.SchemaObjectName));

        public override void Visit(CreateFunctionStatement node) => Names.Add(QualifiedName(node.Name));

        public override void Visit(AlterFunctionStatement node) => Names.Add(QualifiedName(node.Name));

        public override void Visit(CreateOrAlterFunctionStatement node) => Names.Add(QualifiedName(node.Name));

        public override void Visit(CreateTriggerStatement node) => Names.Add(QualifiedName(node.Name));

        public override void Visit(AlterTriggerStatement node) => Names.Add(QualifiedName(node.Name));

        public override void Visit(CreateOrAlterTriggerStatement node) => Names.Add(QualifiedName(node.Name));
    }

    private sealed class Visitor : TSqlFragmentVisitor
    {
        private readonly string sourcePath;
        private readonly bool ansiNullsIsOff;

        public Visitor(string sourcePath, bool ansiNullsIsOff)
        {
            this.sourcePath = sourcePath;
            this.ansiNullsIsOff = ansiNullsIsOff;
        }

        public List<DeprecatedSyntaxFinding> Findings { get; } = [];

        public override void ExplicitVisit(BooleanComparisonExpression node)
        {
            var comparesToNull = IsNullLiteral(node.SecondExpression) || IsNullLiteral(node.FirstExpression);

            switch (node.ComparisonType)
            {
                case BooleanComparisonType.Equals when comparesToNull && !ansiNullsIsOff:
                    Add(DeprecatedSyntaxFindingKind.EqualsNullComparison, node,
                        "\"= NULL\" never matches any row under the default ANSI_NULLS ON session setting, including a genuinely NULL value - use \"IS NULL\".");
                    break;

                case BooleanComparisonType.NotEqualToBrackets or BooleanComparisonType.NotEqualToExclamation
                    when comparesToNull && !ansiNullsIsOff:
                    Add(DeprecatedSyntaxFindingKind.NotEqualsNullComparison, node,
                        "\"<> NULL\"/\"!= NULL\" never matches any row under the default ANSI_NULLS ON session setting - use \"IS NOT NULL\".");
                    break;

                // Not NULL-compared: the plain non-ANSI-spelling complaint applies instead. "!="
                // is functionally identical to the ANSI "<>" (both NotEqualToBrackets/Exclamation
                // already produce the same BooleanComparisonType family), but ANSI SQL only ever
                // defines "<>" - "!=" is a T-SQL-specific spelling of it, same as "!<"/"!>" are
                // T-SQL-specific spellings with no ANSI equivalent at all ("<>" has no direction,
                // so only the exclamation form itself, never NotEqualToBrackets, is non-ANSI here).
                case BooleanComparisonType.NotEqualToExclamation
                    or BooleanComparisonType.NotLessThan
                    or BooleanComparisonType.NotGreaterThan:
                    Add(DeprecatedSyntaxFindingKind.NonAnsiComparisonOperator, node,
                        $"Non-ANSI comparison operator \"{OperatorText(node.ComparisonType)}\" used - write the ANSI-standard form instead.");
                    break;
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(LikePredicate node)
        {
            if (node.SecondExpression is StringLiteral { Value: { } pattern }
                && !pattern.Contains('%') && !pattern.Contains('_') && !pattern.Contains('[')
                && !pattern.EndsWith(' '))
            {
                Add(DeprecatedSyntaxFindingKind.LikeWithNoWildcard, node,
                    $"LIKE pattern \"{pattern}\" contains no wildcard - use \"=\" here instead, or add the intended wildcard.");
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(NamedTableReference node)
        {
            if (node.SchemaObject.SchemaIdentifier is null or { Value: "sys" or "dbo" }
                && LegacyCompatibilityViewNames.Contains(node.SchemaObject.BaseIdentifier.Value))
            {
                Add(DeprecatedSyntaxFindingKind.LegacySystemCompatibilityView, node.SchemaObject,
                    $"\"{node.SchemaObject.BaseIdentifier.Value}\" is a pre-SQL-Server-2005 system compatibility view - use the real sys.* catalog view instead.");
            }

            if (node.TableHints is { Count: > 0 } hints
                && node.ScriptTokenStream is { } tokens
                && !HasPrecedingWithKeyword(tokens, hints[0].FirstTokenIndex))
            {
                Add(DeprecatedSyntaxFindingKind.TableHintWithoutWith, node,
                    "Table hint written without the \"WITH\" keyword - a deprecated syntax form.");
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CreateProcedureStatement node)
        {
            if (node.ProcedureReference.Number is not null)
            {
                Add(DeprecatedSyntaxFindingKind.NumberedProcedureDefinition, node.ProcedureReference,
                    $"\"{QualifiedName(node.ProcedureReference.Name)}\" is defined as a numbered-procedure-group member - a deprecated T-SQL feature.");
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(ExecutableProcedureReference node)
        {
            if (node.ProcedureReference?.ProcedureReference is { } procRef)
            {
                if (procRef.Number is not null)
                {
                    Add(DeprecatedSyntaxFindingKind.NumberedProcedureExecution, node,
                        $"\"{QualifiedName(procRef.Name)}\" invoked by its numbered-procedure-group number - a deprecated T-SQL feature.");
                }

                var routineName = procRef.Name.BaseIdentifier.Value;
                if (RemovedSecurityStoredProcedureNames.Contains(routineName))
                {
                    Add(DeprecatedSyntaxFindingKind.RemovedSecurityStoredProcedure, node,
                        $"\"{routineName}\" is a legacy security-administration procedure superseded by CREATE LOGIN/CREATE USER/ALTER ROLE - some names in this family are already fully removed from current SQL Server versions.");
                }
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(SelectScalarExpression node)
        {
            if (node.ColumnName?.ValueExpression is StringLiteral { Value: { } alias })
            {
                Add(DeprecatedSyntaxFindingKind.StringLiteralColumnAlias, node.ColumnName,
                    $"Column alias \"{alias}\" is written as a string literal - a deprecated aliasing form.");
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(SetRowCountStatement node)
        {
            Add(DeprecatedSyntaxFindingKind.DeprecatedSetRowcount, node,
                "SET ROWCOUNT is deprecated - use TOP (n) instead; Microsoft documents it as not honored by INSERT/UPDATE/DELETE in a future release.");
            base.ExplicitVisit(node);
        }

        private static bool IsNullLiteral(ScalarExpression expression) =>
            expression is NullLiteral;

        // The first table hint's own FirstTokenIndex points at the hint keyword itself (e.g.
        // "NOLOCK"), not at the enclosing "(" - ScriptDom's raw token stream also includes
        // WhiteSpace as its own token type (confirmed directly: dumping the stream for
        // "WITH (NOLOCK)" shows With, WhiteSpace, LeftParenthesis, Identifier as four distinct
        // tokens), so walk back past whitespace, then exactly one LeftParenthesis, then past any
        // further whitespace, to reach the token that would be "WITH" if the modern form was used.
        private static bool HasPrecedingWithKeyword(IList<TSqlParserToken> tokens, int hintFirstTokenIndex)
        {
            var index = SkipBackWhitespace(tokens, hintFirstTokenIndex - 1);
            if (index < 0 || tokens[index].TokenType != TSqlTokenType.LeftParenthesis)
            {
                return false;
            }

            index = SkipBackWhitespace(tokens, index - 1);
            return index >= 0 && tokens[index].TokenType == TSqlTokenType.With;
        }

        private static int SkipBackWhitespace(IList<TSqlParserToken> tokens, int index)
        {
            while (index >= 0 && tokens[index].TokenType == TSqlTokenType.WhiteSpace)
            {
                index--;
            }

            return index;
        }

        private static string OperatorText(BooleanComparisonType type) => type switch
        {
            BooleanComparisonType.NotLessThan => "!<",
            BooleanComparisonType.NotGreaterThan => "!>",
            BooleanComparisonType.NotEqualToExclamation => "!=",
            _ => type.ToString(),
        };


        private void Add(DeprecatedSyntaxFindingKind kind, TSqlFragment node, string detail) =>
            Findings.Add(new DeprecatedSyntaxFinding(
                kind, sourcePath, sourcePath, node.StartLine, node.StartColumn, detail,
                kind is DeprecatedSyntaxFindingKind.EqualsNullComparison
                    or DeprecatedSyntaxFindingKind.NotEqualsNullComparison
                    ? FindingConfidence.High
                    : FindingConfidence.Medium));
    }
}
