using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Common;
using SilentScan.Core.Lineage;
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
        var rule = CreateRule(parseResult, catalog);
        var walker = new ModuleWalker(parseResult.SourcePath, catalog ?? new DatabaseCatalog(), EmptyResolvedViews, ledger: null, currentProcScope: null, callerScopeByCalleeScope: null, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return Harvest(parseResult, rule);
    }

    internal static Rule CreateRule(SqlParseResult parseResult, DatabaseCatalog? catalog = null)
    {
        var moduleQualifiedName = TryGetModuleQualifiedName(parseResult.Fragment);
        var ansiNullsIsOff = catalog is not null
            && moduleQualifiedName is { } qualifiedName
            && catalog.TryGetModuleUsesAnsiNulls(qualifiedName, out var usesAnsiNulls)
            && !usesAnsiNulls;

        var isAdHocScript = moduleQualifiedName is null && HasNoModuleDefinition(parseResult.Fragment);

        return new Rule(parseResult.SourcePath, ansiNullsIsOff, skipComparisonFindings: isAdHocScript);
    }

    internal static IReadOnlyList<DeprecatedSyntaxFinding> Harvest(SqlParseResult parseResult, Rule rule)
    {
        var findings = new List<DeprecatedSyntaxFinding>();

        ScanTaskComments(parseResult, findings);
        findings.AddRange(rule.Findings);

        var moduleQualifiedName = TryGetModuleQualifiedName(parseResult.Fragment);
        var isAdHocScript = moduleQualifiedName is null && HasNoModuleDefinition(parseResult.Fragment);
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
        var policy = new AnsiNullsFlowPolicy(sourcePath);
        var state = default(AnsiNullsFlowState);

        foreach (var batch in script.Batches)
        {
            state = ProcedureBodyFlowWalker.Walk(batch.Statements, state with { Depth = 0 }, policy);
        }

        return policy.Findings;
    }

    private readonly record struct AnsiNullsFlowState(bool IsOff, bool RestoreIsOff, int Depth);

    private sealed class AnsiNullsFlowPolicy(string sourcePath) : IStatementFlowPolicy<AnsiNullsFlowState>
    {
        public List<DeprecatedSyntaxFinding> Findings { get; } = [];

        public bool IsDeclined(AnsiNullsFlowState state) => false;

        public bool IsDone(AnsiNullsFlowState state) => false;

        public AnsiNullsFlowState PerStatement(TSqlStatement statement, AnsiNullsFlowState state)
        {
            if (statement is PredicateSetStatement { Options: var options, IsOn: var isOn } && (options & SetOptions.AnsiNulls) != 0)
            {
                return state with { IsOff = !isOn };
            }

            if (statement is BeginEndBlockStatement or IfStatement or WhileStatement or TryCatchStatement)
            {
                return state;
            }

            var walker = new ComparisonFindingVisitor(sourcePath, state.IsOff);
            statement.Accept(walker);
            Findings.AddRange(walker.Findings);
            return state;
        }

        public AnsiNullsFlowState OnReturn(AnsiNullsFlowState state, TSqlStatement statement) => state;

        public AnsiNullsFlowState OnThrow(AnsiNullsFlowState state) => state;

        public AnsiNullsFlowState OnGoTo(AnsiNullsFlowState state) => state;

        public AnsiNullsFlowState CloneForBranch(AnsiNullsFlowState state) =>
            state with { RestoreIsOff = state.IsOff, Depth = state.Depth + 1 };

        public AnsiNullsFlowState Merge(AnsiNullsFlowState a, AnsiNullsFlowState b)
        {
            var winner = a.Depth >= b.Depth ? a : b;
            return new AnsiNullsFlowState(winner.RestoreIsOff, winner.RestoreIsOff, winner.Depth - 1);
        }
    }

    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    private static bool IsNullLiteral(ScalarExpression expression) =>
        expression is NullLiteral;

    private static string OperatorText(BooleanComparisonType type) => type switch
    {
        BooleanComparisonType.NotLessThan => "!<",
        BooleanComparisonType.NotGreaterThan => "!>",
        BooleanComparisonType.NotEqualToExclamation => "!=",
        _ => type.ToString(),
    };

    private static (DeprecatedSyntaxFindingKind Kind, string Detail)? ClassifyComparison(
        BooleanComparisonType comparisonType, bool comparesToNull, bool ansiNullsIsOff)
    {
        switch (comparisonType)
        {
            case BooleanComparisonType.Equals when comparesToNull && !ansiNullsIsOff:
                return (DeprecatedSyntaxFindingKind.EqualsNullComparison,
                    "\"= NULL\" never matches any row under the default ANSI_NULLS ON session setting, including a genuinely NULL value - use \"IS NULL\".");

            case BooleanComparisonType.NotEqualToBrackets or BooleanComparisonType.NotEqualToExclamation
                when comparesToNull && !ansiNullsIsOff:
                return (DeprecatedSyntaxFindingKind.NotEqualsNullComparison,
                    "\"<> NULL\"/\"!= NULL\" never matches any row under the default ANSI_NULLS ON session setting - use \"IS NOT NULL\".");

            case BooleanComparisonType.NotEqualToExclamation or BooleanComparisonType.NotLessThan or BooleanComparisonType.NotGreaterThan:
                return (DeprecatedSyntaxFindingKind.NonAnsiComparisonOperator,
                    $"Non-ANSI comparison operator \"{OperatorText(comparisonType)}\" used - write the ANSI-standard form instead.");

            default:
                return null;
        }
    }

    private static DeprecatedSyntaxFinding BuildComparisonFinding(DeprecatedSyntaxFindingKind kind, TSqlFragment node, string detail, string sourcePath) =>
        new(kind, sourcePath, sourcePath, node.StartLine, node.StartColumn, detail,
            kind is DeprecatedSyntaxFindingKind.EqualsNullComparison or DeprecatedSyntaxFindingKind.NotEqualsNullComparison
                ? FindingConfidence.High
                : FindingConfidence.Medium);

    private sealed class ComparisonFindingVisitor(string sourcePath, bool ansiNullsIsOff) : TSqlFragmentVisitor
    {
        public List<DeprecatedSyntaxFinding> Findings { get; } = [];

        public override void ExplicitVisit(BooleanComparisonExpression node)
        {
            var comparesToNull = IsNullLiteral(node.SecondExpression) || IsNullLiteral(node.FirstExpression);
            if (ClassifyComparison(node.ComparisonType, comparesToNull, ansiNullsIsOff) is { } result)
            {
                Findings.Add(BuildComparisonFinding(result.Kind, node, result.Detail, sourcePath));
            }

            base.ExplicitVisit(node);
        }
    }

    internal sealed class Rule : IModuleRule
    {
        private readonly string sourcePath;
        private readonly bool ansiNullsIsOff;
        private readonly bool skipComparisonFindings;

        public Rule(string sourcePath, bool ansiNullsIsOff, bool skipComparisonFindings = false)
        {
            this.sourcePath = sourcePath;
            this.ansiNullsIsOff = ansiNullsIsOff;
            this.skipComparisonFindings = skipComparisonFindings;
        }

        public List<DeprecatedSyntaxFinding> Findings { get; } = [];

        public void OnBooleanComparisonExpression(BooleanComparisonExpression node, ModuleWalker walker)
        {
            var comparesToNull = IsNullLiteral(node.SecondExpression) || IsNullLiteral(node.FirstExpression);
            if (!skipComparisonFindings && ClassifyComparison(node.ComparisonType, comparesToNull, ansiNullsIsOff) is { } result)
            {
                Add(result.Kind, node, result.Detail);
            }
        }

        public void OnLikePredicate(LikePredicate node, ModuleWalker walker)
        {
            if (node.SecondExpression is StringLiteral { Value: { } pattern }
                && !pattern.Contains('%') && !pattern.Contains('_') && !pattern.Contains('[')
                && !pattern.EndsWith(' '))
            {
                Add(DeprecatedSyntaxFindingKind.LikeWithNoWildcard, node,
                    $"LIKE pattern \"{pattern}\" contains no wildcard - use \"=\" here instead, or add the intended wildcard.");
            }
        }

        public void OnEnterNamedTableReference(NamedTableReference node, ModuleWalker walker)
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
        }

        public void OnEnterCreateProcedureStatement(CreateProcedureStatement node, ModuleWalker walker)
        {
            if (node.ProcedureReference.Number is not null)
            {
                Add(DeprecatedSyntaxFindingKind.NumberedProcedureDefinition, node.ProcedureReference,
                    $"\"{SchemaObjectNameHelper.Qualify(node.ProcedureReference.Name)}\" is defined as a numbered-procedure-group member - a deprecated T-SQL feature.");
            }
        }

        public void OnEnterExecutableProcedureReference(ExecutableProcedureReference node, ModuleWalker walker)
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
        }

        public void OnEnterSelectScalarExpression(SelectScalarExpression node, ModuleWalker walker)
        {
            if (node.ColumnName?.ValueExpression is StringLiteral { Value: { } alias })
            {
                Add(DeprecatedSyntaxFindingKind.StringLiteralColumnAlias, node.ColumnName,
                    $"Column alias \"{alias}\" is written as a string literal - a deprecated aliasing form.");
            }
        }

        public void OnEnterSetRowCountStatement(SetRowCountStatement node, ModuleWalker walker)
        {
            Add(DeprecatedSyntaxFindingKind.DeprecatedSetRowcount, node,
                "SET ROWCOUNT is deprecated - use TOP (n) instead; Microsoft documents it as not honored by INSERT/UPDATE/DELETE in a future release.");
        }

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

        private void Add(DeprecatedSyntaxFindingKind kind, TSqlFragment node, string detail) =>
            Findings.Add(BuildComparisonFinding(kind, node, detail, sourcePath));
    }
}
