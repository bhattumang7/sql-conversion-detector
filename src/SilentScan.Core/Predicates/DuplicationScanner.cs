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

            CheckAndChainBounds(node.Predicate);

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
            if (BothLiterals(node.FirstExpression, node.SecondExpression))
            {
                CheckAlwaysTrueOrFalseLiteralComparison(node);
            }
            else if (SameText(node.FirstExpression, node.SecondExpression))
            {
                Add(DuplicationFindingKind.IdenticalBinaryOperands, node.ComparisonType.ToString(), node, FindingConfidence.High);
            }

            base.ExplicitVisit(node);
        }

        // --- Always-true/always-false literal-vs-literal comparison ---

        /// <summary>Only asserts a truth value where collation cannot change the answer - see
        /// <see cref="DuplicationFindingKind.AlwaysTrueOrFalseLiteralComparison"/>'s own doc
        /// comment for the full collation-safety reasoning.</summary>
        private void CheckAlwaysTrueOrFalseLiteralComparison(BooleanComparisonExpression node)
        {
            bool? truth = (node.FirstExpression, node.SecondExpression) switch
            {
                (IntegerLiteral or NumericLiteral, IntegerLiteral or NumericLiteral)
                    when TryGetNumericLiteralValue(node.FirstExpression) is { } a && TryGetNumericLiteralValue(node.SecondExpression) is { } b
                    => EvaluateNumericComparison(node.ComparisonType, a, b),

                (StringLiteral s1, StringLiteral s2)
                    when node.ComparisonType is BooleanComparisonType.Equals
                        or BooleanComparisonType.NotEqualToBrackets
                        or BooleanComparisonType.NotEqualToExclamation
                    => EvaluateExactStringMatch(node.ComparisonType, s1, s2),

                _ => null,
            };

            if (truth is { } value)
            {
                Add(DuplicationFindingKind.AlwaysTrueOrFalseLiteralComparison, value ? "always true" : "always false", node, FindingConfidence.High);
            }
        }

        private static bool? EvaluateNumericComparison(BooleanComparisonType op, decimal a, decimal b) => op switch
        {
            BooleanComparisonType.Equals => a == b,
            BooleanComparisonType.NotEqualToBrackets or BooleanComparisonType.NotEqualToExclamation => a != b,
            BooleanComparisonType.GreaterThan => a > b,
            BooleanComparisonType.GreaterThanOrEqualTo => a >= b,
            BooleanComparisonType.LessThan => a < b,
            BooleanComparisonType.LessThanOrEqualTo => a <= b,
            _ => null,
        };

        /// <summary>Only a byte-identical (case-sensitive, ordinal) textual match/mismatch is
        /// collation-proof - two textually DIFFERENT string literals are declined entirely for
        /// both '=' and '&lt;&gt;', since a case-insensitive collation could still make them
        /// compare equal at runtime. Never guess.</summary>
        private static bool? EvaluateExactStringMatch(BooleanComparisonType op, StringLiteral first, StringLiteral second)
        {
            if (!string.Equals(first.Value, second.Value, StringComparison.Ordinal))
            {
                return null;
            }

            return op == BooleanComparisonType.Equals;
        }

        private static decimal? TryGetNumericLiteralValue(ScalarExpression expression) => expression switch
        {
            IntegerLiteral integer when decimal.TryParse(integer.Value, out var value) => value,
            NumericLiteral numeric when decimal.TryParse(numeric.Value, out var value) => value,
            _ => null,
        };

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

        // --- IF/ELSE IF chain analysis: duplicate conditions, branch-body identity, collapsible nesting ---

        /// <summary>Tracks every IfStatement reached as a PRIOR IfStatement's own ElseStatement -
        /// the default top-down traversal will visit these nodes again on its own once we recurse
        /// into them, and they must not be re-analyzed as a fresh chain start (that would re-walk
        /// and re-report a strict suffix of the same chain this method already covered in full).
        /// Populated before <c>base.ExplicitVisit</c> descends into the chain, so the check at the
        /// top of the next visit always sees an up-to-date set.</summary>
        private readonly HashSet<IfStatement> _ifChainContinuations = new(ReferenceEqualityComparer.Instance);

        public override void ExplicitVisit(IfStatement node)
        {
            if (!_ifChainContinuations.Contains(node))
            {
                AnalyzeIfChain(node);
            }

            CheckCollapsibleNestedIf(node);

            base.ExplicitVisit(node);
        }

        private void AnalyzeIfChain(IfStatement root)
        {
            var branches = new List<(BooleanExpression Condition, TSqlStatement Body)>();
            var current = root;
            TSqlStatement? finalElseBody;
            while (true)
            {
                branches.Add((current.Predicate, current.ThenStatement));
                if (current.ElseStatement is IfStatement nextIf)
                {
                    _ifChainContinuations.Add(nextIf);
                    current = nextIf;
                    continue;
                }

                finalElseBody = current.ElseStatement;
                break;
            }

            // Duplicate sibling conditions: every later branch's condition compared against every
            // earlier one in the same chain (full rendered-text structural equality).
            for (var i = 1; i < branches.Count; i++)
            {
                var laterText = FragmentTextRenderer.Render(branches[i].Condition);
                for (var j = 0; j < i; j++)
                {
                    if (string.Equals(laterText, FragmentTextRenderer.Render(branches[j].Condition), StringComparison.OrdinalIgnoreCase))
                    {
                        Add(DuplicationFindingKind.DuplicateSiblingCondition, laterText, branches[i].Condition, FindingConfidence.Medium);
                        break;
                    }
                }
            }

            // Branch-body identity compares every real body, INCLUDING the final ELSE (when
            // present) as just another branch with no condition of its own - needs at least two
            // bodies total to compare against each other.
            var allBodies = branches.Select(b => b.Body).ToList();
            if (finalElseBody is not null)
            {
                allBodies.Add(finalElseBody);
            }

            if (allBodies.Count >= 2)
            {
                var bodyTexts = allBodies.Select(FragmentTextRenderer.Render).ToList();
                var allIdentical = finalElseBody is not null
                    && bodyTexts.All(t => string.Equals(t, bodyTexts[0], StringComparison.OrdinalIgnoreCase));

                if (allIdentical)
                {
                    Add(DuplicationFindingKind.AllBranchesIdentical, null, root, FindingConfidence.High);
                }
                else
                {
                    var reported = new HashSet<int>();
                    for (var i = 1; i < bodyTexts.Count; i++)
                    {
                        for (var j = 0; j < i; j++)
                        {
                            if (string.Equals(bodyTexts[i], bodyTexts[j], StringComparison.OrdinalIgnoreCase) && reported.Add(i))
                            {
                                Add(DuplicationFindingKind.IdenticalBranchBodies, null, allBodies[i], FindingConfidence.Medium);
                                break;
                            }
                        }
                    }
                }
            }

            foreach (var (condition, _) in branches)
            {
                CheckAndChainBounds(condition);
            }
        }

        /// <summary>An IF with no ELSE whose entire THEN body is a single nested IF, also with no
        /// ELSE - semantically identical to one IF combining both conditions with AND.</summary>
        private void CheckCollapsibleNestedIf(IfStatement node)
        {
            if (node.ElseStatement is not null)
            {
                return;
            }

            var nested = node.ThenStatement switch
            {
                IfStatement inner => inner,
                BeginEndBlockStatement { StatementList.Statements: [IfStatement inner] } => inner,
                _ => null,
            };

            if (nested is { ElseStatement: null })
            {
                Add(DuplicationFindingKind.CollapsibleNestedIf, null, node, FindingConfidence.Medium);
            }
        }

        // --- CASE expression analysis: duplicate WHEN conditions, branch-result identity ---

        public override void ExplicitVisit(SearchedCaseExpression node)
        {
            var whens = node.WhenClauses;

            for (var i = 1; i < whens.Count; i++)
            {
                var laterText = FragmentTextRenderer.Render(whens[i].WhenExpression);
                for (var j = 0; j < i; j++)
                {
                    if (string.Equals(laterText, FragmentTextRenderer.Render(whens[j].WhenExpression), StringComparison.OrdinalIgnoreCase))
                    {
                        Add(DuplicationFindingKind.DuplicateSiblingCondition, laterText, whens[i].WhenExpression, FindingConfidence.Medium);
                        break;
                    }
                }
            }

            if (whens.Count >= 2)
            {
                var resultTexts = whens.Select(w => FragmentTextRenderer.Render(w.ThenExpression)).ToList();
                var allSameAsFirst = resultTexts.All(t => string.Equals(t, resultTexts[0], StringComparison.OrdinalIgnoreCase));
                var allIdentical = allSameAsFirst && node.ElseExpression is not null
                    && string.Equals(FragmentTextRenderer.Render(node.ElseExpression), resultTexts[0], StringComparison.OrdinalIgnoreCase);

                if (allIdentical)
                {
                    Add(DuplicationFindingKind.AllBranchesIdentical, null, node, FindingConfidence.High);
                }
                else
                {
                    var reported = new HashSet<int>();
                    for (var i = 1; i < resultTexts.Count; i++)
                    {
                        for (var j = 0; j < i; j++)
                        {
                            if (string.Equals(resultTexts[i], resultTexts[j], StringComparison.OrdinalIgnoreCase) && reported.Add(i))
                            {
                                Add(DuplicationFindingKind.IdenticalBranchBodies, null, whens[i].ThenExpression, FindingConfidence.Medium);
                                break;
                            }
                        }
                    }
                }
            }

            base.ExplicitVisit(node);
        }

        // --- Nested IIF (T-SQL's ternary equivalent) ---

        /// <summary>Deliberately scoped to IIF specifically, and only a DIRECT nesting in the
        /// immediate Then/Else branch - see <see cref="DuplicationFindingKind.NestedConditionalExpression"/>'s
        /// own doc comment for why CASE-inside-CASE is excluded.</summary>
        public override void ExplicitVisit(IIfCall node)
        {
            if (node.ThenExpression is IIfCall)
            {
                Add(DuplicationFindingKind.NestedConditionalExpression, "THEN", node, FindingConfidence.Medium);
            }

            if (node.ElseExpression is IIfCall)
            {
                Add(DuplicationFindingKind.NestedConditionalExpression, "ELSE", node, FindingConfidence.Medium);
            }

            base.ExplicitVisit(node);
        }

        // --- Redundant / mutually-exclusive AND-combined numeric bounds ---

        /// <summary>Scoped to IF/WHILE conditions only (not WHERE clauses) - this codebase's
        /// existing predicate/sargability streams already deeply cover WHERE-clause AND-chains for
        /// a different purpose (seek/scan verdicts, catch-all predicates); this control-flow-
        /// adjacent Tier 4 stream targets a distinct, narrower "this specific comparison adds or
        /// removes nothing once ANDed with a sibling" structural claim instead.</summary>
        private void CheckAndChainBounds(BooleanExpression condition)
        {
            var conjuncts = FlattenAnd(condition);
            if (conjuncts.Count < 2)
            {
                return;
            }

            var bounds = new List<(BooleanExpression Fragment, string Operand, NumericBound Interval)>();
            foreach (var conjunct in conjuncts)
            {
                if (TryExtractNumericBound(conjunct) is not { } extracted)
                {
                    continue;
                }

                if (NumericBound.FromComparison(extracted.Op, extracted.Literal) is not { } interval)
                {
                    continue;
                }

                bounds.Add((conjunct, extracted.OperandText, interval));
            }

            for (var i = 1; i < bounds.Count; i++)
            {
                for (var j = 0; j < i; j++)
                {
                    if (!string.Equals(bounds[i].Operand, bounds[j].Operand, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (bounds[i].Interval.IsEmptyIntersectionWith(bounds[j].Interval))
                    {
                        Add(DuplicationFindingKind.MutuallyExclusiveAndCondition, bounds[i].Operand, bounds[i].Fragment, FindingConfidence.High);
                    }
                    else if (bounds[i].Interval.IsSubsetOf(bounds[j].Interval))
                    {
                        // bounds[j] is the looser bound - it adds nothing once ANDed with the
                        // stricter bounds[i], so the LOOSER condition is the redundant one.
                        Add(DuplicationFindingKind.RedundantAndCondition, bounds[j].Operand, bounds[j].Fragment, FindingConfidence.Medium);
                    }
                    else if (bounds[j].Interval.IsSubsetOf(bounds[i].Interval))
                    {
                        Add(DuplicationFindingKind.RedundantAndCondition, bounds[i].Operand, bounds[i].Fragment, FindingConfidence.Medium);
                    }
                }
            }
        }

        private static List<BooleanExpression> FlattenAnd(BooleanExpression expression)
        {
            var result = new List<BooleanExpression>();
            void Recurse(BooleanExpression expr)
            {
                var unwrapped = Unwrap(expr);
                if (unwrapped is BooleanBinaryExpression { BinaryExpressionType: BooleanBinaryExpressionType.And } and)
                {
                    Recurse(and.FirstExpression);
                    Recurse(and.SecondExpression);
                }
                else
                {
                    result.Add(unwrapped);
                }
            }

            Recurse(expression);
            return result;
        }

        /// <summary>Extracts (operand text, comparison operator, literal value) from a single
        /// comparison where exactly one side is a numeric literal and the other is anything else -
        /// a literal-on-the-left form (<c>5 &gt; x</c>) is mirrored to the equivalent operand-
        /// relative operator (<c>x &lt; 5</c>) so both spellings compare uniformly.</summary>
        private static (string OperandText, BooleanComparisonType Op, decimal Literal)? TryExtractNumericBound(BooleanExpression expression)
        {
            if (expression is not BooleanComparisonExpression comparison)
            {
                return null;
            }

            if (comparison.ComparisonType is not (BooleanComparisonType.GreaterThan or BooleanComparisonType.GreaterThanOrEqualTo
                or BooleanComparisonType.LessThan or BooleanComparisonType.LessThanOrEqualTo or BooleanComparisonType.Equals))
            {
                return null;
            }

            if (comparison.FirstExpression is not Literal
                && TryGetNumericLiteralValue(comparison.SecondExpression) is { } rightValue)
            {
                return (FragmentTextRenderer.Render(comparison.FirstExpression), comparison.ComparisonType, rightValue);
            }

            if (comparison.SecondExpression is not Literal
                && TryGetNumericLiteralValue(comparison.FirstExpression) is { } leftValue)
            {
                return (FragmentTextRenderer.Render(comparison.SecondExpression), MirrorOperator(comparison.ComparisonType), leftValue);
            }

            return null;
        }

        private static BooleanComparisonType MirrorOperator(BooleanComparisonType op) => op switch
        {
            BooleanComparisonType.GreaterThan => BooleanComparisonType.LessThan,
            BooleanComparisonType.GreaterThanOrEqualTo => BooleanComparisonType.LessThanOrEqualTo,
            BooleanComparisonType.LessThan => BooleanComparisonType.GreaterThan,
            BooleanComparisonType.LessThanOrEqualTo => BooleanComparisonType.GreaterThanOrEqualTo,
            var other => other,
        };

        /// <summary>A half-open/closed numeric interval representing one comparison bound
        /// (<c>x &gt; 5</c> ⇒ (5, exclusive, +∞)), used to classify a pair of AND-combined bounds
        /// on the same operand as redundant (one subsumes the other) or mutually exclusive (their
        /// ranges cannot both hold).</summary>
        private readonly record struct NumericBound(decimal? Lower, bool LowerInclusive, decimal? Upper, bool UpperInclusive)
        {
            public static NumericBound? FromComparison(BooleanComparisonType comparisonType, decimal literal) => comparisonType switch
            {
                BooleanComparisonType.GreaterThan => new NumericBound(literal, false, null, true),
                BooleanComparisonType.GreaterThanOrEqualTo => new NumericBound(literal, true, null, true),
                BooleanComparisonType.LessThan => new NumericBound(null, true, literal, false),
                BooleanComparisonType.LessThanOrEqualTo => new NumericBound(null, true, literal, true),
                BooleanComparisonType.Equals => new NumericBound(literal, true, literal, true),
                _ => null,
            };

            public bool IsEmptyIntersectionWith(NumericBound other)
            {
                var (lowerValue, lowerInclusive) = CombineLower(Lower, LowerInclusive, other.Lower, other.LowerInclusive);
                var (upperValue, upperInclusive) = CombineUpper(Upper, UpperInclusive, other.Upper, other.UpperInclusive);
                if (lowerValue is null || upperValue is null)
                {
                    return false;
                }

                if (lowerValue.Value > upperValue.Value)
                {
                    return true;
                }

                return lowerValue.Value == upperValue.Value && !(lowerInclusive && upperInclusive);
            }

            public bool IsSubsetOf(NumericBound other) =>
                LowerAtLeastAsRestrictive(Lower, LowerInclusive, other.Lower, other.LowerInclusive)
                && UpperAtLeastAsRestrictive(Upper, UpperInclusive, other.Upper, other.UpperInclusive);

            private static bool LowerAtLeastAsRestrictive(decimal? thisLower, bool thisInclusive, decimal? otherLower, bool otherInclusive)
            {
                if (otherLower is null)
                {
                    return true;
                }

                if (thisLower is null)
                {
                    return false;
                }

                if (thisLower.Value != otherLower.Value)
                {
                    return thisLower.Value > otherLower.Value;
                }

                return !thisInclusive || otherInclusive;
            }

            private static bool UpperAtLeastAsRestrictive(decimal? thisUpper, bool thisInclusive, decimal? otherUpper, bool otherInclusive)
            {
                if (otherUpper is null)
                {
                    return true;
                }

                if (thisUpper is null)
                {
                    return false;
                }

                if (thisUpper.Value != otherUpper.Value)
                {
                    return thisUpper.Value < otherUpper.Value;
                }

                return !thisInclusive || otherInclusive;
            }

            private static (decimal? Value, bool Inclusive) CombineLower(decimal? a, bool aInc, decimal? b, bool bInc)
            {
                if (a is null)
                {
                    return (b, bInc);
                }

                if (b is null)
                {
                    return (a, aInc);
                }

                if (a.Value != b.Value)
                {
                    return a.Value > b.Value ? (a, aInc) : (b, bInc);
                }

                return (a, aInc && bInc);
            }

            private static (decimal? Value, bool Inclusive) CombineUpper(decimal? a, bool aInc, decimal? b, bool bInc)
            {
                if (a is null)
                {
                    return (b, bInc);
                }

                if (b is null)
                {
                    return (a, aInc);
                }

                if (a.Value != b.Value)
                {
                    return a.Value < b.Value ? (a, aInc) : (b, bInc);
                }

                return (a, aInc && bInc);
            }
        }

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
