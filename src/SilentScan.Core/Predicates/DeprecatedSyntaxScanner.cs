using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Common;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class DeprecatedSyntaxScanner
{

    private static readonly HashSet<string> LegacyCompatibilityViewNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "sysaltfiles", "syscacheobjects", "syscharsets", "syscolumns", "syscomments", "sysconfigures",
        "sysconstraints", "syscurconfigs", "sysdatabases", "sysdepends", "sysdevices", "sysfilegroups",
        "sysfiles", "sysforeignkeys", "sysfulltextcatalogs", "sysindexes", "sysindexkeys", "syslanguages",
        "syslockinfo", "syslocks", "syslogins", "sysmembers", "sysmessages", "sysobjects", "sysoledbusers",
        "sysopentapes", "sysperfinfo", "syspermissions", "sysprocesses", "sysprotects", "sysreferences",
        "sysremotelogins", "sysservers", "systypes", "sysusers",
    };

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

    public static IReadOnlyList<DeprecatedSyntaxFinding> Scan(SqlParseResult parseResult, DatabaseCatalog? catalog = null)
    {
        var findings = new List<DeprecatedSyntaxFinding>();

        ScanTaskComments(parseResult, findings);

        var moduleQualifiedName = TryGetModuleQualifiedName(parseResult.Fragment);
        var ansiNullsIsOff = catalog is not null
            && moduleQualifiedName is { } qualifiedName
            && catalog.TryGetModuleUsesAnsiNulls(qualifiedName, out var usesAnsiNulls)
            && !usesAnsiNulls;

        var isAdHocScript = moduleQualifiedName is null && HasNoModuleDefinition(parseResult.Fragment);

        var visitor = new Visitor(parseResult.SourcePath, ansiNullsIsOff, skipNullComparisonFindings: isAdHocScript);
        parseResult.Fragment.Accept(visitor);
        findings.AddRange(visitor.Findings);

        if (isAdHocScript && parseResult.Fragment is TSqlScript script)
        {
            findings.AddRange(ScanAdHocAnsiNullComparisons(script, parseResult.SourcePath));
        }

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

    private static bool HasNoModuleDefinition(TSqlFragment fragment)
    {
        if (fragment is not TSqlScript script)
        {
            return false;
        }

        var collector = new ModuleNameCollector();
        script.Accept(collector);
        return collector.Names.Count == 0;
    }

    private static List<DeprecatedSyntaxFinding> ScanAdHocAnsiNullComparisons(TSqlScript script, string sourcePath)
    {
        var findings = new List<DeprecatedSyntaxFinding>();
        var ansiNullsIsOff = false;

        foreach (var batch in script.Batches)
        {
            foreach (var statement in batch.Statements)
            {
                if (statement is PredicateSetStatement { Options: var options, IsOn: var isOn }
                    && (options & SetOptions.AnsiNulls) != 0)
                {
                    ansiNullsIsOff = !isOn;
                    continue;
                }

                var walker = new NullComparisonVisitor(sourcePath, ansiNullsIsOff);
                statement.Accept(walker);
                findings.AddRange(walker.Findings);
            }
        }

        return findings;
    }

    private sealed class Visitor : TSqlFragmentVisitor
    {
        private readonly string sourcePath;
        private readonly bool ansiNullsIsOff;
        private readonly bool skipNullComparisonFindings;

        public Visitor(string sourcePath, bool ansiNullsIsOff, bool skipNullComparisonFindings = false)
        {
            this.sourcePath = sourcePath;
            this.ansiNullsIsOff = ansiNullsIsOff;
            this.skipNullComparisonFindings = skipNullComparisonFindings;
        }

        public List<DeprecatedSyntaxFinding> Findings { get; } = [];

        public override void ExplicitVisit(BooleanComparisonExpression node)
        {
            var comparesToNull = IsNullLiteral(node.SecondExpression) || IsNullLiteral(node.FirstExpression);

            switch (node.ComparisonType)
            {
                case BooleanComparisonType.Equals when comparesToNull && !ansiNullsIsOff && !skipNullComparisonFindings:
                    Add(DeprecatedSyntaxFindingKind.EqualsNullComparison, node,
                        "\"= NULL\" never matches any row under the default ANSI_NULLS ON session setting, including a genuinely NULL value - use \"IS NULL\".");
                    break;

                case BooleanComparisonType.NotEqualToBrackets or BooleanComparisonType.NotEqualToExclamation
                    when comparesToNull && !ansiNullsIsOff && !skipNullComparisonFindings:
                    Add(DeprecatedSyntaxFindingKind.NotEqualsNullComparison, node,
                        "\"<> NULL\"/\"!= NULL\" never matches any row under the default ANSI_NULLS ON session setting - use \"IS NOT NULL\".");
                    break;

                case BooleanComparisonType.NotEqualToExclamation
                    or BooleanComparisonType.NotLessThan
                    or BooleanComparisonType.NotGreaterThan
                    when !(comparesToNull && skipNullComparisonFindings):
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
                    $"\"{SchemaObjectNameHelper.Qualify(node.ProcedureReference.Name)}\" is defined as a numbered-procedure-group member - a deprecated T-SQL feature.");
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
                        $"\"{SchemaObjectNameHelper.Qualify(procRef.Name)}\" invoked by its numbered-procedure-group number - a deprecated T-SQL feature.");
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

    private sealed class NullComparisonVisitor : TSqlFragmentVisitor
    {
        private readonly string sourcePath;
        private readonly bool ansiNullsIsOff;

        public NullComparisonVisitor(string sourcePath, bool ansiNullsIsOff)
        {
            this.sourcePath = sourcePath;
            this.ansiNullsIsOff = ansiNullsIsOff;
        }

        public List<DeprecatedSyntaxFinding> Findings { get; } = [];

        public override void ExplicitVisit(BooleanComparisonExpression node)
        {
            var comparesToNull = node.SecondExpression is NullLiteral || node.FirstExpression is NullLiteral;
            if (!comparesToNull)
            {
                base.ExplicitVisit(node);
                return;
            }

            switch (node.ComparisonType)
            {
                case BooleanComparisonType.Equals when !ansiNullsIsOff:
                    Add(DeprecatedSyntaxFindingKind.EqualsNullComparison, node,
                        "\"= NULL\" never matches any row under the default ANSI_NULLS ON session setting, including a genuinely NULL value - use \"IS NULL\".");
                    break;

                case BooleanComparisonType.NotEqualToBrackets or BooleanComparisonType.NotEqualToExclamation
                    when !ansiNullsIsOff:
                    Add(DeprecatedSyntaxFindingKind.NotEqualsNullComparison, node,
                        "\"<> NULL\"/\"!= NULL\" never matches any row under the default ANSI_NULLS ON session setting - use \"IS NOT NULL\".");
                    break;

                case BooleanComparisonType.NotEqualToExclamation:
                    Add(DeprecatedSyntaxFindingKind.NonAnsiComparisonOperator, node,
                        "Non-ANSI comparison operator \"!=\" used - write the ANSI-standard form instead.");
                    break;
            }

            base.ExplicitVisit(node);
        }

        private void Add(DeprecatedSyntaxFindingKind kind, TSqlFragment node, string detail) =>
            Findings.Add(new DeprecatedSyntaxFinding(
                kind, sourcePath, sourcePath, node.StartLine, node.StartColumn, detail,
                kind is DeprecatedSyntaxFindingKind.EqualsNullComparison
                    or DeprecatedSyntaxFindingKind.NotEqualsNullComparison
                    ? FindingConfidence.High
                    : FindingConfidence.Medium));
    }
}
