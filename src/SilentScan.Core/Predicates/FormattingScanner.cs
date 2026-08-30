using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Common;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class FormattingScanner
{
    public static IReadOnlyList<FormattingFinding> Scan(SqlParseResult parseResult)
    {
        var findings = new List<FormattingFinding>();

        ScanTabCharacters(parseResult, findings);
        ScanFileHeader(parseResult, findings);

        var rule = new Rule(parseResult.SourcePath);
        var walker = new ModuleWalker(parseResult.SourcePath, new DatabaseCatalog(), EmptyResolvedViews, ledger: null, currentProcScope: null, callerScopeByCalleeScope: null, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        findings.AddRange(rule.Findings);

        return
        [
            .. findings
                .OrderBy(f => f.Kind)
                .ThenBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];
    }

    private static void ScanTabCharacters(SqlParseResult parseResult, List<FormattingFinding> findings)
    {
        var fragment = parseResult.Fragment;
        if (fragment.ScriptTokenStream is null || fragment.LastTokenIndex < fragment.FirstTokenIndex)
        {
            return;
        }

        var tokens = fragment.ScriptTokenStream;
        var seenLines = new HashSet<int>();
        for (var i = fragment.FirstTokenIndex; i <= fragment.LastTokenIndex; i++)
        {
            var token = tokens[i];
            if (token.Text is not { } text || !text.Contains('\t'))
            {
                continue;
            }

            var line = token.Line;
            var offset = 0;
            var index = text.IndexOf('\t');
            while (index >= 0)
            {
                var tabLine = line + text[..index].Count(c => c == '\n');
                if (seenLines.Add(tabLine))
                {
                    findings.Add(new FormattingFinding(
                        FormattingFindingKind.TabCharacterUsed, parseResult.SourcePath, parseResult.SourcePath, tabLine, 1));
                }

                offset = index + 1;
                index = offset < text.Length ? text.IndexOf('\t', offset) : -1;
            }
        }
    }

    private static void ScanFileHeader(SqlParseResult parseResult, List<FormattingFinding> findings)
    {
        var fragment = parseResult.Fragment;
        if (fragment.ScriptTokenStream is null || fragment.LastTokenIndex < fragment.FirstTokenIndex)
        {
            return;
        }

        var tokens = fragment.ScriptTokenStream;
        var firstToken = tokens[fragment.FirstTokenIndex];
        var text = firstToken.Text ?? string.Empty;
        var isComment = text.TrimStart().StartsWith("--", StringComparison.Ordinal)
            || text.TrimStart().StartsWith("/*", StringComparison.Ordinal);
        if (!isComment)
        {
            findings.Add(new FormattingFinding(
                FormattingFindingKind.MissingFileHeaderComment, parseResult.SourcePath, parseResult.SourcePath,
                firstToken.Line, 1));
        }
    }

    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    private sealed class Rule(string sourcePath) : IModuleRule
    {
        public List<FormattingFinding> Findings { get; } = [];

        private string CurrentModule(ModuleWalker walker) => walker.CurrentProcScope ?? sourcePath;

        public void OnEnterProcedureOrFunctionBody(ProcedureStatementBodyBase node, ModuleWalker walker) =>
            CheckStatements(node.StatementList?.Statements, walker);

        public void OnEnterTriggerBody(TriggerStatementBody node, ModuleWalker walker) =>
            CheckStatements(node.StatementList?.Statements, walker);

        public void OnEnterBeginEndBlockStatement(BeginEndBlockStatement node, ModuleWalker walker)
        {

            if (node.StatementList is not null)
            {
                CheckStatements(node.StatementList.Statements, walker);
            }
        }

        public void OnEnterDeclareVariableStatement(DeclareVariableStatement node, ModuleWalker walker) =>
            CheckDeclarations(node.Declarations, walker);

        public void OnEnterIfStatement(IfStatement node, ModuleWalker walker)
        {
            CheckConditionalBody(node.StartLine, node.StartColumn, node.ThenStatement, walker);

            if (node.ElseStatement is not null and not IfStatement)
            {
                var elseLine = FindPrecedingKeywordLine(node.ThenStatement, node.ElseStatement, "ELSE");
                CheckConditionalBody(elseLine, node.StartColumn, node.ElseStatement, walker);
            }
        }

        public void OnEnterWhileStatement(WhileStatement node, ModuleWalker walker) =>
            CheckConditionalBody(node.StartLine, node.StartColumn, node.Statement, walker);

        public void OnEnterParenthesisExpression(ParenthesisExpression node, ModuleWalker walker)
        {
            if (IsRedundantlyWrapped(node.Expression))
            {
                Findings.Add(new FormattingFinding(
                    FormattingFindingKind.RedundantParentheses, CurrentModule(walker), sourcePath, node.StartLine, node.StartColumn));
            }
        }

        public void OnEnterBooleanParenthesisExpression(BooleanParenthesisExpression node, ModuleWalker walker)
        {
            if (node.Expression is BooleanParenthesisExpression)
            {

                Findings.Add(new FormattingFinding(
                    FormattingFindingKind.RedundantParentheses, CurrentModule(walker), sourcePath, node.StartLine, node.StartColumn));
            }
        }

        private void CheckStatements(IList<TSqlStatement>? statements, ModuleWalker walker)
        {
            if (statements is null)
            {
                return;
            }

            for (var i = 0; i < statements.Count; i++)
            {
                if (i > 0 && statements[i].StartLine == statements[i - 1].StartLine)
                {
                    Findings.Add(new FormattingFinding(
                        FormattingFindingKind.MultipleStatementsOnSameLine, CurrentModule(walker), sourcePath,
                        statements[i].StartLine, statements[i].StartColumn));
                }

                CheckDanglingStatement(statements, i, walker);
                CheckIfFollowingPriorBlockEnd(statements, i, walker);
            }
        }

        private void CheckDanglingStatement(IList<TSqlStatement> statements, int index, ModuleWalker walker)
        {
            if (index == 0)
            {
                return;
            }

            var previous = statements[index - 1];
            var bodyStatement = previous switch
            {
                IfStatement { ElseStatement: null } ifNode => ifNode.ThenStatement,
                WhileStatement whileNode => whileNode.Statement,
                _ => null,
            };

            if (bodyStatement is null || bodyStatement is BeginEndBlockStatement)
            {
                return;
            }

            var bodyLastLine = LastLine(bodyStatement);
            var current = statements[index];

            if (current is IfStatement or WhileStatement)
            {
                return;
            }

            if (current.StartLine == bodyLastLine + 1 && current.StartColumn >= bodyStatement.StartColumn)
            {
                Findings.Add(new FormattingFinding(
                    FormattingFindingKind.DanglingStatementAfterUnbracedBody, CurrentModule(walker), sourcePath,
                    current.StartLine, current.StartColumn));
            }
        }

        private void CheckIfFollowingPriorBlockEnd(IList<TSqlStatement> statements, int index, ModuleWalker walker)
        {
            if (index == 0 || statements[index] is not IfStatement current)
            {
                return;
            }

            if (statements[index - 1] is not IfStatement { ElseStatement: null, ThenStatement: BeginEndBlockStatement block })
            {
                return;
            }

            var endLine = LastLine(block);
            if (current.StartLine == endLine)
            {
                Findings.Add(new FormattingFinding(
                    FormattingFindingKind.IfImmediatelyFollowingPriorBlockEnd, CurrentModule(walker), sourcePath,
                    current.StartLine, current.StartColumn));
            }
        }

        private void CheckConditionalBody(int keywordLine, int keywordColumn, TSqlStatement? body, ModuleWalker walker)
        {
            if (body is null or BeginEndBlockStatement or IfStatement)
            {

                return;
            }

            var kind = body.StartLine == keywordLine
                ? FormattingFindingKind.SingleLineConditionalBody
                : FormattingFindingKind.MissingBeginEndBlock;
            Findings.Add(new FormattingFinding(kind, CurrentModule(walker), sourcePath, keywordLine, keywordColumn));
        }

        private void CheckDeclarations(IList<DeclareVariableElement> declarations, ModuleWalker walker)
        {
            for (var i = 1; i < declarations.Count; i++)
            {
                if (declarations[i].StartLine == declarations[i - 1].StartLine)
                {
                    Findings.Add(new FormattingFinding(
                        FormattingFindingKind.MultipleDeclarationsOnSameLine, CurrentModule(walker), sourcePath,
                        declarations[i].StartLine, declarations[i].StartColumn,
                        DetailText: declarations[i].VariableName?.Value));
                }
            }
        }

        private static bool IsRedundantlyWrapped(ScalarExpression expression) => expression switch
        {
            ColumnReferenceExpression => true,
            VariableReference => true,
            Literal => true,
            ParenthesisExpression => true,
            _ => false,
        };

        private static int LastLine(TSqlFragment fragment)
        {
            if (fragment.ScriptTokenStream is null || fragment.LastTokenIndex < 0)
            {
                return fragment.StartLine;
            }

            var lastToken = fragment.ScriptTokenStream[fragment.LastTokenIndex];
            return lastToken.Line + (lastToken.Text?.Count(c => c == '\n') ?? 0);
        }

        private static int FindPrecedingKeywordLine(TSqlFragment before, TSqlFragment after, string expectedKeyword)
        {
            if (after.ScriptTokenStream is not { } tokens)
            {
                return after.StartLine;
            }

            for (var i = after.FirstTokenIndex - 1; i > before.LastTokenIndex; i--)
            {
                var text = tokens[i].Text;
                if (string.IsNullOrWhiteSpace(text) || text.StartsWith("--", StringComparison.Ordinal) || text.StartsWith("/*", StringComparison.Ordinal))
                {
                    continue;
                }

                return string.Equals(text, expectedKeyword, StringComparison.OrdinalIgnoreCase) ? tokens[i].Line : after.StartLine;
            }

            return after.StartLine;
        }

    }
}
