using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 4 "Formatting and layout" - ten structural/textual checks over
/// the AST and raw token stream. Fully syntax-only: no <see cref="Catalog.DatabaseCatalog"/>, no
/// oracle (every member is a directly observable parse/token-stream fact, never a plan-shape or
/// runtime-behavior claim - the same reasoning <see cref="CodeMetricScanner"/> already established).
/// </summary>
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

    // ScriptDOM's ScriptTokenStream is a single shared list for the whole parsed input - every
    // fragment/sub-node returns the SAME list reference. Only FirstTokenIndex/LastTokenIndex mark
    // which slice belongs to THIS fragment (CodeMetricScanner's own doc comment records the real
    // bug this bounding avoids: an earlier version of that scanner walked the raw stream unbounded
    // and silently pulled in sibling modules' own text).
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

            // One finding per physical line containing a tab, not one per tab character or per
            // token (a single whitespace token can carry several tabs in a row).
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

    // A module's own definition text does not begin, before its first real statement, with a
    // comment (`--` or `/* ... */`). Reported at Low confidence purely as an informational fact -
    // whether a T-SQL module conventionally carries a file-style header at all is a much weaker,
    // less universal convention than it is for application source files, so this is stated
    // honestly as advisory rather than a real maintainability risk.
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
            _currentModule = QualifiedName(node.ProcedureReference.Name);
            CheckStatements(node.StatementList?.Statements);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(AlterProcedureStatement node)
        {
            _currentModule = QualifiedName(node.ProcedureReference.Name);
            CheckStatements(node.StatementList?.Statements);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CreateFunctionStatement node)
        {
            _currentModule = QualifiedName(node.Name);
            CheckStatements(node.StatementList?.Statements);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(AlterFunctionStatement node)
        {
            _currentModule = QualifiedName(node.Name);
            CheckStatements(node.StatementList?.Statements);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CreateTriggerStatement node)
        {
            _currentModule = QualifiedName(node.Name);
            CheckStatements(node.StatementList?.Statements);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(AlterTriggerStatement node)
        {
            _currentModule = QualifiedName(node.Name);
            CheckStatements(node.StatementList?.Statements);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(BeginEndBlockStatement node)
        {
            // An empty BEGIN...END is a hard parse error under this tool's own dialect (confirmed
            // directly: "BEGIN END" fails with "Incorrect syntax near 'END'." in every context
            // tried, and a bare ";" produces no statement node to attach a finding to at all) - so
            // StatementList is never actually empty here, matching the disposition already recorded
            // for COMPUTE/COMPUTE BY and *=/=* elsewhere in this codebase: not reachable, not built.
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
                // A double-wrapped boolean expression - the outer pair is always redundant
                // regardless of what the inner pair itself contains.
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

        // A statement immediately following an unbraced IF/WHILE's single-statement body, starting
        // on the very next line at the same or deeper indentation - visually still "inside" the
        // conditional/loop even though it structurally is not. Only checked against the immediately
        // preceding sibling, never across a nested scope.
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

            // A following IF/WHILE is unambiguously its own new conditional the moment its own
            // keyword is read - a real corpus run found this excludes the overwhelming majority
            // of raw matches, all a common, unambiguous "IF x insert; IF y insert; ..." chained-
            // conditionals idiom, never actually confusable with an unconditional statement. The
            // real risk this kind targets is narrower: a non-conditional statement that visually
            // reads as still belonging to the block above it.
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

        // An IF immediately following the closing END of a prior braced IF (with no ELSE), on the
        // exact same line as that END - easy to misread as an ELSE IF continuation.
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
                // A null else, an already-braced body, or an ELSE IF continuation never fire here.
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

        // Scans the shared token stream between two fragments for the last non-trivial token
        // (skipping whitespace/comment-only tokens by text shape) immediately before `after`
        // starts, and returns its line - used to locate an ELSE keyword, which ScriptDOM's
        // IfStatement does not expose as its own token/property.
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

        private static string QualifiedName(SchemaObjectName name) =>
            name.SchemaIdentifier is { } schema
                ? $"{schema.Value}.{name.BaseIdentifier.Value}"
                : name.BaseIdentifier.Value;
    }
}
