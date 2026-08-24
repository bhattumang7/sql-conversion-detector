using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Common;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class FormattingScanner
{
    public static IReadOnlyList<FormattingFinding> Scan(SqlParseResult parseResult)
    {
        var findings = new List<FormattingFinding>();

        ScanTabCharacters(parseResult, findings);
        ScanFileHeader(parseResult, findings);

        var visitor = new Visitor(parseResult.SourcePath);
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

    private sealed class Visitor : TSqlFragmentVisitor
    {
        private readonly string sourcePath;

        public Visitor(string sourcePath)
        {
            this.sourcePath = sourcePath;
            _currentModule = sourcePath;
        }

        public List<FormattingFinding> Findings { get; } = [];

        private string _currentModule;

        public override void ExplicitVisit(CreateProcedureStatement node)
        {
            _currentModule = SchemaObjectNameHelper.Qualify(node.ProcedureReference.Name);
            CheckStatements(node.StatementList?.Statements);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(AlterProcedureStatement node)
        {
            _currentModule = SchemaObjectNameHelper.Qualify(node.ProcedureReference.Name);
            CheckStatements(node.StatementList?.Statements);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CreateFunctionStatement node)
        {
            _currentModule = SchemaObjectNameHelper.Qualify(node.Name);
            CheckStatements(node.StatementList?.Statements);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(AlterFunctionStatement node)
        {
            _currentModule = SchemaObjectNameHelper.Qualify(node.Name);
            CheckStatements(node.StatementList?.Statements);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CreateTriggerStatement node)
        {
            _currentModule = SchemaObjectNameHelper.Qualify(node.Name);
            CheckStatements(node.StatementList?.Statements);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(AlterTriggerStatement node)
        {
            _currentModule = SchemaObjectNameHelper.Qualify(node.Name);
            CheckStatements(node.StatementList?.Statements);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(BeginEndBlockStatement node)
        {

            if (node.StatementList is not null)
            {
                CheckStatements(node.StatementList.Statements);
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(DeclareVariableStatement node)
        {
            CheckDeclarations(node.Declarations);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(IfStatement node)
        {
            CheckConditionalBody(node.StartLine, node.StartColumn, node.ThenStatement);

            if (node.ElseStatement is not null and not IfStatement)
            {
                var elseLine = FindPrecedingKeywordLine(node.ThenStatement, node.ElseStatement, "ELSE");
                CheckConditionalBody(elseLine, node.StartColumn, node.ElseStatement);
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(WhileStatement node)
        {
            CheckConditionalBody(node.StartLine, node.StartColumn, node.Statement);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(ParenthesisExpression node)
        {
            if (IsRedundantlyWrapped(node.Expression))
            {
                Findings.Add(new FormattingFinding(
                    FormattingFindingKind.RedundantParentheses, _currentModule, sourcePath, node.StartLine, node.StartColumn));
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(BooleanParenthesisExpression node)
        {
            if (node.Expression is BooleanParenthesisExpression)
            {

                Findings.Add(new FormattingFinding(
                    FormattingFindingKind.RedundantParentheses, _currentModule, sourcePath, node.StartLine, node.StartColumn));
            }

            base.ExplicitVisit(node);
        }

        private void CheckStatements(IList<TSqlStatement>? statements)
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
                        FormattingFindingKind.MultipleStatementsOnSameLine, _currentModule, sourcePath,
                        statements[i].StartLine, statements[i].StartColumn));
                }

                CheckDanglingStatement(statements, i);
                CheckIfFollowingPriorBlockEnd(statements, i);
            }
        }

        private void CheckDanglingStatement(IList<TSqlStatement> statements, int index)
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
                    FormattingFindingKind.DanglingStatementAfterUnbracedBody, _currentModule, sourcePath,
                    current.StartLine, current.StartColumn));
            }
        }

        private void CheckIfFollowingPriorBlockEnd(IList<TSqlStatement> statements, int index)
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
                    FormattingFindingKind.IfImmediatelyFollowingPriorBlockEnd, _currentModule, sourcePath,
                    current.StartLine, current.StartColumn));
            }
        }

        private void CheckConditionalBody(int keywordLine, int keywordColumn, TSqlStatement? body)
        {
            if (body is null or BeginEndBlockStatement or IfStatement)
            {

                return;
            }

            var kind = body.StartLine == keywordLine
                ? FormattingFindingKind.SingleLineConditionalBody
                : FormattingFindingKind.MissingBeginEndBlock;
            Findings.Add(new FormattingFinding(kind, _currentModule, sourcePath, keywordLine, keywordColumn));
        }

        private void CheckDeclarations(IList<DeclareVariableElement> declarations)
        {
            for (var i = 1; i < declarations.Count; i++)
            {
                if (declarations[i].StartLine == declarations[i - 1].StartLine)
                {
                    Findings.Add(new FormattingFinding(
                        FormattingFindingKind.MultipleDeclarationsOnSameLine, _currentModule, sourcePath,
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
