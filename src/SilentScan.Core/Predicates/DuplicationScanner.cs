using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Parsing;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 4 "Dead and duplicated code" - see <see cref="DuplicationFinding"/>
/// for the full scope, precision-guard, and severity documentation.
/// </summary>
public static class DuplicationScanner
{
    /// <summary>A comment shorter than this (after stripping delimiters and surrounding
    /// whitespace) never qualifies as "commented-out code" regardless of parse success - a
    /// two-word comment that happens to parse as a degenerate single-identifier statement is
    /// noise, not a real code block.</summary>
    private const int MinCommentedCodeLength = 12;

    /// <summary>A string literal must recur at least this many times within one module to be
    /// reported - three is the smallest count that distinguishes "a real repeated magic value"
    /// from two independent, coincidental uses of a common short string.</summary>
    private const int DuplicatedLiteralThreshold = 3;

    /// <summary>A literal shorter than this (its raw quoted content) is excluded from the
    /// duplicated-literal check even at the threshold above - '', ' ', '%', 'Y'/'N' flags and
    /// similar single-character values are extremely common, low-value repeats, not the "magic
    /// string that should be a constant" case this rule targets.</summary>
    private const int MinDuplicatedLiteralLength = 3;

    public static IReadOnlyList<DuplicationFinding> Scan(SqlParseResult parseResult)
    {
        var findings = new List<DuplicationFinding>();

        ScanComments(parseResult, findings);

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

    private static string QualifiedName(SchemaObjectName name) =>
        name.SchemaIdentifier is { } schema
            ? $"{schema.Value}.{name.BaseIdentifier.Value}"
            : name.BaseIdentifier.Value;

    /// <summary>Walks the raw token stream (bounded to this fragment's own
    /// FirstTokenIndex/LastTokenIndex - see <see cref="CodeMetricScanner"/>'s own doc comment for
    /// the real bug that bounding avoids) for comment tokens whose stripped content reparses as a
    /// plausible T-SQL batch - the same "reparse through the real parser, trust nothing else"
    /// discipline <see cref="SchemaDependencyScanner"/> already established for a definition's own
    /// text, applied here to a comment's own text instead.</summary>
    private static void ScanComments(SqlParseResult parseResult, List<DuplicationFinding> findings)
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
                || token.Text is not { } raw)
            {
                continue;
            }

            var stripped = Strip(raw);
            if (stripped.Length < MinCommentedCodeLength || !LooksLikeCode(stripped))
            {
                continue;
            }

            findings.Add(new DuplicationFinding(
                DuplicationFindingKind.CommentedOutCode, parseResult.SourcePath, parseResult.SourcePath,
                token.Line, 1, Confidence: FindingConfidence.Low));
        }
    }

    private static string Strip(string commentToken)
    {
        var text = commentToken.TrimStart();
        if (text.StartsWith("--", StringComparison.Ordinal))
        {
            return text[2..].Trim();
        }

        if (text.StartsWith("/*", StringComparison.Ordinal))
        {
            text = text.Trim();
            if (text.EndsWith("*/", StringComparison.Ordinal))
            {
                text = text[..^2];
            }

            return text[2..].Trim();
        }

        return text;
    }

    /// <summary>Real T-SQL statement-starting keywords - a comment must begin with one of these
    /// (case-insensitive, as its own first word) before its reparse result is trusted at all. This
    /// guard is load-bearing, not a nicety: T-SQL's grammar accepts a bare "EXEC" keyword being
    /// OMITTED the moment a plain identifier appears where a statement is expected (`word1 word2`
    /// parses cleanly as an implicit `EXECUTE word1 word2`) - discovered directly against the
    /// local test database's own real comments, where an entirely ordinary two-word prose comment
    /// (`/* Distance Factor */`) reparsed with zero errors purely because of this shorthand, not
    /// because it was ever real code. Requiring an explicit leading keyword closes that hole
    /// without narrowing real coverage - genuine commented-out T-SQL essentially always starts
    /// with one of these.</summary>
    private static readonly HashSet<string> RealStatementKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "INSERT", "UPDATE", "DELETE", "MERGE", "TRUNCATE",
        "DECLARE", "SET", "IF", "WHILE", "BEGIN", "PRINT", "RETURN", "THROW", "RAISERROR",
        "EXEC", "EXECUTE", "CREATE", "ALTER", "DROP", "WITH", "GRANT", "REVOKE", "DENY", "USE",
    };

    /// <summary>Reparses the stripped comment text AS A BATCH, never wrapped in an artificial
    /// SELECT the way <see cref="SchemaDependencyScanner"/> wraps an expression - a bare
    /// SELECT-wrap would make almost any short phrase "parse" as a column-alias expression, which
    /// would defeat the whole point of this precision guard. A clean parse producing at least one
    /// real statement is necessary but NOT sufficient (see <see cref="RealStatementKeywords"/>'s
    /// own doc comment for why) - the comment's own first word must also be a real statement
    /// keyword.</summary>
    private static bool LooksLikeCode(string strippedText)
    {
        var firstWord = strippedText.TrimStart().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries) is [var word, ..]
            ? word
            : string.Empty;
        if (!RealStatementKeywords.Contains(firstWord))
        {
            return false;
        }

        var result = SqlScriptParser.ParseText("commented-out-code-probe.sql", strippedText);
        return !result.HasErrors
            && result.Fragment is TSqlScript script
            && script.Batches.Any(b => b.Statements.Count > 0);
    }

    private sealed class Visitor : TSqlFragmentVisitor
    {
        private readonly string sourcePath;

        public Visitor(string sourcePath)
        {
            this.sourcePath = sourcePath;
            _currentModule = sourcePath;
        }

        public List<DuplicationFinding> Findings { get; } = [];

        private string _currentModule;

        public override void ExplicitVisit(CreateProcedureStatement node)
        {
            _currentModule = QualifiedName(node.ProcedureReference.Name);
            CheckStringLiterals(node.StatementList);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(AlterProcedureStatement node)
        {
            _currentModule = QualifiedName(node.ProcedureReference.Name);
            CheckStringLiterals(node.StatementList);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CreateOrAlterProcedureStatement node)
        {
            _currentModule = QualifiedName(node.ProcedureReference.Name);
            CheckStringLiterals(node.StatementList);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CreateFunctionStatement node)
        {
            _currentModule = QualifiedName(node.Name);
            CheckStringLiterals(node.StatementList);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(AlterFunctionStatement node)
        {
            _currentModule = QualifiedName(node.Name);
            CheckStringLiterals(node.StatementList);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CreateOrAlterFunctionStatement node)
        {
            _currentModule = QualifiedName(node.Name);
            CheckStringLiterals(node.StatementList);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CreateTriggerStatement node)
        {
            _currentModule = QualifiedName(node.Name);
            CheckStringLiterals(node.StatementList);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(AlterTriggerStatement node)
        {
            _currentModule = QualifiedName(node.Name);
            CheckStringLiterals(node.StatementList);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CreateOrAlterTriggerStatement node)
        {
            _currentModule = QualifiedName(node.Name);
            CheckStringLiterals(node.StatementList);
            base.ExplicitVisit(node);
        }

        // --- Self-assignment ---

        public override void ExplicitVisit(SetVariableStatement node)
        {
            if (node.AssignmentKind == AssignmentKind.Equals
                && node.Expression is VariableReference sourceVariable
                && string.Equals(sourceVariable.Name, node.Variable.Name, StringComparison.OrdinalIgnoreCase))
            {
                Add(DuplicationFindingKind.SelfAssignment, node.Variable.Name, node, FindingConfidence.High);
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(SelectSetVariable node)
        {
            if (node.Expression is VariableReference sourceVariable
                && string.Equals(sourceVariable.Name, node.Variable.Name, StringComparison.OrdinalIgnoreCase))
            {
                Add(DuplicationFindingKind.SelfAssignment, node.Variable.Name, node, FindingConfidence.High);
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(AssignmentSetClause node)
        {
            // Compare the FULL rendered text of both sides (including any table alias) - a
            // multi-table `UPDATE t SET t.Col = s.Col FROM t JOIN s ON ...` renders "t.Col" and
            // "s.Col" as textually distinct, so it correctly never fires; only a genuine
            // same-alias-or-unqualified self-reference does.
            if (node.Column is { } column && node.NewValue is ColumnReferenceExpression)
            {
                var columnText = FragmentTextRenderer.Render(column);
                var valueText = FragmentTextRenderer.Render(node.NewValue);
                if (string.Equals(columnText, valueText, StringComparison.OrdinalIgnoreCase))
                {
                    Add(DuplicationFindingKind.SelfAssignment, columnText, node, FindingConfidence.High);
                }
            }

            base.ExplicitVisit(node);
        }

        // --- Single-iteration loop ---

        public override void ExplicitVisit(WhileStatement node)
        {
            var body = node.Statement is BeginEndBlockStatement block ? block.StatementList.Statements : [node.Statement];

            // An arbitrary GOTO/label target inside the loop body can make an apparently-
            // unconditional exit not actually reached on every path - decline entirely rather than
            // guess, the same discipline DeadCodeScanner applies to its own reachability walk.
            var gotoCollector = new GotoLabelCollector();
            foreach (var statement in body)
            {
                statement.Accept(gotoCollector);
            }

            if (gotoCollector.Count == 0 && AlwaysExitsLoop(body))
            {
                Add(DuplicationFindingKind.SingleIterationLoop, null, node, FindingConfidence.Medium);
            }

            base.ExplicitVisit(node);
        }

        /// <summary>True iff every path through this straight-line sequence unconditionally hits
        /// a BREAK/RETURN/THROW before falling off the end - the same terminality-walk shape
        /// <see cref="DeadCodeScanner"/>'s own ReachabilityWalker uses, with BREAK additionally
        /// counted as terminal (it exits the loop, which is exactly the fact this check needs) and
        /// a NESTED WHILE's own BREAK never counting toward the OUTER loop's terminality (it exits
        /// only the inner loop).</summary>
        private static bool AlwaysExitsLoop(IList<TSqlStatement> statements)
        {
            foreach (var statement in statements)
            {
                if (StatementAlwaysExits(statement))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool StatementAlwaysExits(TSqlStatement statement) => statement switch
        {
            BreakStatement or ReturnStatement or ThrowStatement => true,
            BeginEndBlockStatement block => AlwaysExitsLoop(block.StatementList.Statements),
            IfStatement ifStatement =>
                AlwaysExitsLoop(ToList(ifStatement.ThenStatement))
                && ifStatement.ElseStatement is not null
                && AlwaysExitsLoop(ToList(ifStatement.ElseStatement)),
            TryCatchStatement tryCatch =>
                AlwaysExitsLoop(tryCatch.TryStatements.Statements) && AlwaysExitsLoop(tryCatch.CatchStatements.Statements),

            // A nested WHILE/cursor loop's own BREAK exits only that inner loop, never the outer
            // one this check is analyzing - do not recurse into it for this purpose.
            _ => false,
        };

        private static IList<TSqlStatement> ToList(TSqlStatement statement) =>
            statement is BeginEndBlockStatement block ? block.StatementList.Statements : [statement];

        private sealed class GotoLabelCollector : TSqlFragmentVisitor
        {
            public int Count { get; private set; }

            public override void ExplicitVisit(GoToStatement node) => Count++;

            public override void ExplicitVisit(LabelStatement node) => Count++;

            // A nested WHILE's own body is a separate loop with its own reachability question -
            // its GOTOs/labels do not disqualify the OUTER loop's analysis. Do not recurse into it.
            public override void ExplicitVisit(WhileStatement node)
            {
            }
        }

        // --- Identical binary operands ---

        public override void ExplicitVisit(BooleanComparisonExpression node)
        {
            if (!BothLiterals(node.FirstExpression, node.SecondExpression) && SameText(node.FirstExpression, node.SecondExpression))
            {
                Add(DuplicationFindingKind.IdenticalBinaryOperands, node.ComparisonType.ToString(), node, FindingConfidence.High);
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(BooleanBinaryExpression node)
        {
            if (node.BinaryExpressionType is BooleanBinaryExpressionType.And or BooleanBinaryExpressionType.Or
                && SameText(node.FirstExpression, node.SecondExpression))
            {
                Add(DuplicationFindingKind.IdenticalBinaryOperands, node.BinaryExpressionType.ToString(), node, FindingConfidence.High);
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(BinaryExpression node)
        {
            // Subtract/Divide/Modulo are always a fixed degenerate result (0, 1-or-error, 0-or-
            // error) regardless of the shared operand's own value - Add/Multiply are deliberately
            // excluded, see DuplicationFinding's own doc comment (x+x doubling, x*x squaring are
            // both legitimate, commonly-intended patterns).
            if (node.BinaryExpressionType is BinaryExpressionType.Subtract or BinaryExpressionType.Divide or BinaryExpressionType.Modulo
                && !BothLiterals(node.FirstExpression, node.SecondExpression)
                && SameText(node.FirstExpression, node.SecondExpression))
            {
                Add(DuplicationFindingKind.IdenticalBinaryOperands, node.BinaryExpressionType.ToString(), node, FindingConfidence.Medium);
            }

            base.ExplicitVisit(node);
        }

        private static bool BothLiterals(ScalarExpression first, ScalarExpression second) =>
            first is Literal && second is Literal;

        private static bool SameText(TSqlFragment first, TSqlFragment second) =>
            string.Equals(FragmentTextRenderer.Render(first), FragmentTextRenderer.Render(second), StringComparison.OrdinalIgnoreCase);

        // --- Repeated unary operator ---

        public override void ExplicitVisit(UnaryExpression node)
        {
            if (node.Expression is UnaryExpression inner && inner.UnaryExpressionType == node.UnaryExpressionType)
            {
                Add(DuplicationFindingKind.RepeatedUnaryOperator, node.UnaryExpressionType.ToString(), node, FindingConfidence.High);
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(BooleanNotExpression node)
        {
            // `NOT (x)` parses the parenthesized operand as a BooleanParenthesisExpression
            // wrapping the real inner expression - unwrap it before pattern-matching, or every
            // parenthesized NOT (the overwhelmingly common way NOT is actually written, since a
            // bare `NOT x = y` is itself ambiguous/rare) would silently never match either check
            // below.
            var inner = Unwrap(node.Expression);
            if (inner is BooleanNotExpression)
            {
                Add(DuplicationFindingKind.RepeatedUnaryOperator, "NOT", node, FindingConfidence.High);
            }
            else
            {
                CheckNegatedComparison(node, inner);
            }

            base.ExplicitVisit(node);
        }

        private static BooleanExpression Unwrap(BooleanExpression expression) =>
            expression is BooleanParenthesisExpression parenthesis ? Unwrap(parenthesis.Expression) : expression;

        // --- Negated comparison written as the negation of its opposite ---

        /// <summary>
        /// NOT (x &gt; y) and x &lt;= y are provably equivalent under T-SQL's three-valued logic,
        /// not just when both operands are non-NULL: whenever either operand is NULL, "x &gt; y"
        /// evaluates to UNKNOWN, and NOT UNKNOWN is ALSO UNKNOWN - the same UNKNOWN that "x &lt;= y"
        /// itself produces for the identical NULL operand, since the "opposite" operator pairing
        /// used here (&gt;/&lt;=, &lt;/&gt;=, =/&lt;&gt;) is exactly the boolean-complement pairing at the
        /// two-valued level, and NOT's only effect on UNKNOWN is to leave it UNKNOWN. A WHERE
        /// clause (or any other boolean context) treats UNKNOWN identically to FALSE either way,
        /// so the two forms are indistinguishable in every real query result, with no nullability
        /// guard needed - unlike almost every other rewrite-suggestion elsewhere in this codebase,
        /// this one needs no catalog-provable-NOT-NULL guard because the equivalence never breaks.
        /// The identical reasoning covers NOT (x IS NULL) vs x IS NOT NULL, which ScriptDom itself
        /// already parses as two syntactically different shapes (a BooleanNotExpression wrapping a
        /// BooleanIsNullExpression, vs BooleanIsNullExpression.IsNot=true directly) rather than one
        /// normalized form.
        /// </summary>
        private void CheckNegatedComparison(BooleanNotExpression node, BooleanExpression inner)
        {
            switch (inner)
            {
                case BooleanComparisonExpression comparison when OppositeOperator(comparison.ComparisonType) is { } opposite:
                    Add(DuplicationFindingKind.NegatedComparisonAsOpposite, opposite, node, FindingConfidence.Medium);
                    break;

                case BooleanIsNullExpression { IsNot: false }:
                    Add(DuplicationFindingKind.NegatedComparisonAsOpposite, "IS NOT NULL", node, FindingConfidence.Medium);
                    break;
            }
        }

        private static string? OppositeOperator(BooleanComparisonType comparisonType) => comparisonType switch
        {
            BooleanComparisonType.Equals => "<>",
            BooleanComparisonType.GreaterThan => "<=",
            BooleanComparisonType.LessThan => ">=",
            BooleanComparisonType.GreaterThanOrEqualTo => "<",
            BooleanComparisonType.LessThanOrEqualTo => ">",
            BooleanComparisonType.NotEqualToBrackets or BooleanComparisonType.NotEqualToExclamation => "=",
            _ => null,
        };

        // --- Duplicated string literals ---

        private void CheckStringLiterals(StatementList? statementList)
        {
            if (statementList is null)
            {
                return;
            }

            var collector = new StringLiteralCollector();
            statementList.Accept(collector);

            var moduleName = _currentModule;
            foreach (var group in collector.Literals
                         .Where(l => l.Value.Length >= MinDuplicatedLiteralLength)
                         .GroupBy(l => (l.Value, l.IsNational), StringLiteralKeyComparer.Instance))
            {
                var occurrences = group.OrderBy(l => l.Line).ThenBy(l => l.Column).ToList();
                if (occurrences.Count < DuplicatedLiteralThreshold)
                {
                    continue;
                }

                var first = occurrences[0];
                Findings.Add(new DuplicationFinding(
                    DuplicationFindingKind.DuplicatedStringLiteral, moduleName, sourcePath,
                    first.Line, first.Column,
                    DetailText: $"{first.Value} ({occurrences.Count} occurrences)",
                    Confidence: FindingConfidence.Low));
            }
        }

        private sealed class StringLiteralKeyComparer : IEqualityComparer<(string Value, bool IsNational)>
        {
            public static readonly StringLiteralKeyComparer Instance = new();

            public bool Equals((string Value, bool IsNational) x, (string Value, bool IsNational) y) =>
                x.IsNational == y.IsNational && string.Equals(x.Value, y.Value, StringComparison.Ordinal);

            public int GetHashCode((string Value, bool IsNational) obj) =>
                HashCode.Combine(obj.IsNational, StringComparer.Ordinal.GetHashCode(obj.Value));
        }

        private sealed class StringLiteralCollector : TSqlFragmentVisitor
        {
            public List<(string Value, bool IsNational, int Line, int Column)> Literals { get; } = [];

            public override void ExplicitVisit(StringLiteral node) =>
                Literals.Add((node.Value, node.IsNational, node.StartLine, node.StartColumn));
        }

        private void Add(DuplicationFindingKind kind, string? detail, TSqlFragment fragment, FindingConfidence confidence) =>
            Findings.Add(new DuplicationFinding(
                kind, _currentModule, sourcePath, fragment.StartLine, fragment.StartColumn, detail, confidence));
    }
}
