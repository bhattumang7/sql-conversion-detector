using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public sealed record CodeMetricThresholds(
    int MaxLineLength = 200,
    int MaxModuleLines = 1000,
    int MaxRoutineLines = 400,
    int MaxParameters = 15,
    int MaxNestingDepth = 10,
    int MaxConditionalOperators = 4,
    int MaxCaseBranches = 5,
    int MaxCaseBranchLines = 5)
{
    public static readonly CodeMetricThresholds Default = new();
}

public static class CodeMetricScanner
{
    public static IReadOnlyList<CodeMetricFinding> Scan(SqlParseResult parseResult, CodeMetricThresholds? thresholds = null)
    {
        var rule = CreateRule(parseResult.SourcePath, thresholds ?? CodeMetricThresholds.Default);
        var walker = new ModuleWalker(parseResult.SourcePath, new DatabaseCatalog(), EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return Harvest(parseResult, thresholds ?? CodeMetricThresholds.Default, rule);
    }

    internal static Rule CreateRule(string sourcePath, CodeMetricThresholds thresholds) => new(sourcePath, thresholds);

    internal static IReadOnlyList<CodeMetricFinding> Harvest(SqlParseResult parseResult, CodeMetricThresholds thresholds, Rule rule)
    {
        var findings = new List<CodeMetricFinding>();

        ScanLineLength(parseResult, thresholds, findings);
        ScanModuleLength(parseResult, thresholds, findings);
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

    private static void ScanLineLength(SqlParseResult parseResult, CodeMetricThresholds thresholds, List<CodeMetricFinding> findings)
    {
        var lines = ReconstructedLines(parseResult.Fragment);
        var firstLine = FirstLine(parseResult.Fragment);
        for (var i = 0; i < lines.Length; i++)
        {
            var length = lines[i].Length;
            if (length > thresholds.MaxLineLength)
            {
                findings.Add(new CodeMetricFinding(
                    CodeMetricFindingKind.LineTooLong, parseResult.SourcePath, parseResult.SourcePath,
                    firstLine + i, 1, length, thresholds.MaxLineLength));
            }
        }
    }

    private static int FirstLine(TSqlFragment fragment) =>
        fragment.ScriptTokenStream is { } tokens && fragment.FirstTokenIndex >= 0 && fragment.FirstTokenIndex < tokens.Count
            ? tokens[fragment.FirstTokenIndex].Line
            : fragment.StartLine;

    private static void ScanModuleLength(SqlParseResult parseResult, CodeMetricThresholds thresholds, List<CodeMetricFinding> findings)
    {
        var lines = ReconstructedLines(parseResult.Fragment);
        var lineCount = lines.Length;
        if (lineCount > thresholds.MaxModuleLines)
        {
            findings.Add(new CodeMetricFinding(
                CodeMetricFindingKind.ModuleTooLong, parseResult.SourcePath, parseResult.SourcePath,
                1, 1, lineCount, thresholds.MaxModuleLines));
        }
    }

    private static string[] ReconstructedLines(TSqlFragment fragment)
    {

        if (fragment.ScriptTokenStream is null || fragment.LastTokenIndex < fragment.FirstTokenIndex)
        {
            return [];
        }

        var tokens = fragment.ScriptTokenStream;
        var text = string.Concat(
            Enumerable.Range(fragment.FirstTokenIndex, fragment.LastTokenIndex - fragment.FirstTokenIndex + 1)
                .Select(i => tokens[i].Text ?? string.Empty));
        return text.Split('\n');
    }

    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    internal sealed class Rule(string sourcePath, CodeMetricThresholds thresholds) : IModuleRule
    {
        public List<CodeMetricFinding> Findings { get; } = [];

        private int _nestingDepth;

        public void OnEnterProcedureOrFunctionBody(ProcedureStatementBodyBase node, ModuleWalker walker)
        {
            var (kindLabel, name) = node switch
            {
                CreateProcedureStatement p => ("procedure", p.ProcedureReference.Name),
                AlterProcedureStatement p => ("procedure", p.ProcedureReference.Name),
                CreateOrAlterProcedureStatement p => ("procedure", p.ProcedureReference.Name),
                CreateFunctionStatement f => ("function", f.Name),
                AlterFunctionStatement f => ("function", f.Name),
                CreateOrAlterFunctionStatement f => ("function", f.Name),
                _ => (null, null),
            };

            if (name is null)
            {
                return;
            }

            AnalyzeRoutine(kindLabel!, name, node.Parameters, node.StatementList, walker);
        }

        public void OnEnterTriggerBody(TriggerStatementBody node, ModuleWalker walker) =>
            AnalyzeRoutine("trigger", node.Name, [], node.StatementList, walker);

        public void OnEnterIfStatement(IfStatement node, ModuleWalker walker)
        {
            CheckConditionalOperatorCount(node.Predicate, node.StartLine, node.StartColumn);
            EnterNested(node);
        }

        public void OnLeaveIfStatement(IfStatement node, ModuleWalker walker) => LeaveNested();

        public void OnEnterWhileStatement(WhileStatement node, ModuleWalker walker)
        {
            CheckConditionalOperatorCount(node.Predicate, node.StartLine, node.StartColumn);
            EnterNested(node);
        }

        public void OnLeaveWhileStatement(WhileStatement node, ModuleWalker walker) => LeaveNested();

        public void OnEnterTryCatchStatement(TryCatchStatement node, ModuleWalker walker) => EnterNested(node);

        public void OnLeaveTryCatchStatement(TryCatchStatement node, ModuleWalker walker) => LeaveNested();

        public void OnEnterOperandPosition(TSqlFragment node, ModuleWalker walker)
        {
            IList<SimpleWhenClause>? simpleWhens = null;
            IList<SearchedWhenClause>? searchedWhens = null;

            switch (node)
            {
                case SearchedCaseExpression searched:
                    searchedWhens = searched.WhenClauses;
                    break;

                case SimpleCaseExpression simple:
                    simpleWhens = simple.WhenClauses;
                    break;

                default:
                    return;
            }

            if (searchedWhens is not null)
            {
                CheckCaseBranches(searchedWhens.Count, node.StartLine, node.StartColumn);
                foreach (var when in searchedWhens)
                {
                    CheckCaseBranchLength(when.ThenExpression);
                }
            }
            else if (simpleWhens is not null)
            {
                CheckCaseBranches(simpleWhens.Count, node.StartLine, node.StartColumn);
                foreach (var when in simpleWhens)
                {
                    CheckCaseBranchLength(when.ThenExpression);
                }
            }
        }

        private void EnterNested(TSqlStatement node)
        {
            _nestingDepth++;
            if (_nestingDepth == thresholds.MaxNestingDepth + 1)
            {

                Findings.Add(new CodeMetricFinding(
                    CodeMetricFindingKind.NestingTooDeep, sourcePath, sourcePath,
                    node.StartLine, node.StartColumn, _nestingDepth, thresholds.MaxNestingDepth));
            }
        }

        private void LeaveNested() => _nestingDepth--;

        private void AnalyzeRoutine(
            string kindLabel, SchemaObjectName nameNode, IList<ProcedureParameter> parameters, StatementList? statementList, ModuleWalker walker)
        {
            _nestingDepth = 0;
            var qualifiedName = walker.CurrentProcScope!;

            if (parameters.Count > thresholds.MaxParameters)
            {
                Findings.Add(new CodeMetricFinding(
                    CodeMetricFindingKind.TooManyParameters, qualifiedName, sourcePath,
                    nameNode.BaseIdentifier.StartLine, nameNode.BaseIdentifier.StartColumn,
                    parameters.Count, thresholds.MaxParameters, DetailText: kindLabel));
            }

            if (statementList is null)
            {

                return;
            }

            var lineCount = RoutineLineCount(statementList);
            if (lineCount > thresholds.MaxRoutineLines)
            {
                Findings.Add(new CodeMetricFinding(
                    CodeMetricFindingKind.RoutineTooLong, qualifiedName, sourcePath,
                    nameNode.BaseIdentifier.StartLine, nameNode.BaseIdentifier.StartColumn,
                    lineCount, thresholds.MaxRoutineLines, DetailText: kindLabel));
            }
        }

        private static int RoutineLineCount(TSqlFragment fragment)
        {
            if (fragment.ScriptTokenStream is null || fragment.LastTokenIndex < fragment.FirstTokenIndex)
            {
                return 0;
            }

            var firstLine = fragment.ScriptTokenStream[fragment.FirstTokenIndex].Line;
            var lastToken = fragment.ScriptTokenStream[fragment.LastTokenIndex];
            var lastLine = lastToken.Line + (lastToken.Text?.Count(c => c == '\n') ?? 0);
            return lastLine - firstLine + 1;
        }

        private void CheckConditionalOperatorCount(BooleanExpression? predicate, int line, int column)
        {
            if (predicate is null)
            {
                return;
            }

            var count = CountConditionalOperators(predicate);
            if (count > thresholds.MaxConditionalOperators)
            {
                Findings.Add(new CodeMetricFinding(
                    CodeMetricFindingKind.TooManyConditionalOperators, sourcePath, sourcePath,
                    line, column, count, thresholds.MaxConditionalOperators));
            }
        }

        private static int CountConditionalOperators(BooleanExpression expression) => expression switch
        {
            BooleanBinaryExpression binary =>
                (binary.BinaryExpressionType is BooleanBinaryExpressionType.And or BooleanBinaryExpressionType.Or ? 1 : 0)
                + CountConditionalOperators(binary.FirstExpression) + CountConditionalOperators(binary.SecondExpression),
            BooleanParenthesisExpression paren => CountConditionalOperators(paren.Expression),
            BooleanNotExpression not => CountConditionalOperators(not.Expression),
            _ => 0,
        };

        private void CheckCaseBranches(int whenCount, int line, int column)
        {
            if (whenCount > thresholds.MaxCaseBranches)
            {
                Findings.Add(new CodeMetricFinding(
                    CodeMetricFindingKind.TooManyCaseBranches, sourcePath, sourcePath,
                    line, column, whenCount, thresholds.MaxCaseBranches));
            }
        }

        private void CheckCaseBranchLength(ScalarExpression thenExpression)
        {
            if (thenExpression.ScriptTokenStream is null || thenExpression.LastTokenIndex < thenExpression.FirstTokenIndex)
            {
                return;
            }

            var firstLine = thenExpression.ScriptTokenStream[thenExpression.FirstTokenIndex].Line;
            var lastToken = thenExpression.ScriptTokenStream[thenExpression.LastTokenIndex];
            var lastLine = lastToken.Line + (lastToken.Text?.Count(c => c == '\n') ?? 0);
            var lineCount = lastLine - firstLine + 1;

            if (lineCount > thresholds.MaxCaseBranchLines)
            {
                Findings.Add(new CodeMetricFinding(
                    CodeMetricFindingKind.CaseBranchTooLong, sourcePath, sourcePath,
                    thenExpression.StartLine, thenExpression.StartColumn, lineCount, thresholds.MaxCaseBranchLines));
            }
        }
    }
}
