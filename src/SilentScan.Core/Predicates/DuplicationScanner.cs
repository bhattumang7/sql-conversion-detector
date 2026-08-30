using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Common;

namespace SilentScan.Core.Predicates;

public static class DuplicationScanner
{
    private const int MinCommentedCodeLength = 12;

    private const int DuplicatedLiteralThreshold = 3;

    private const int MinDuplicatedLiteralLength = 3;

    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    public static IReadOnlyList<DuplicationFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
    {
        var rule = CreateRule(parseResult.SourcePath, catalog);
        var walker = new ModuleWalker(parseResult.SourcePath, catalog, EmptyResolvedViews, ledger: null, currentProcScope: null, callerScopeByCalleeScope: null, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return Harvest(parseResult, catalog, rule);
    }

    internal static Rule CreateRule(string sourcePath, DatabaseCatalog catalog) => new(sourcePath, catalog);

    internal static IReadOnlyList<DuplicationFinding> Harvest(SqlParseResult parseResult, DatabaseCatalog catalog, Rule rule)
    {
        var findings = new List<DuplicationFinding>();

        ScanComments(parseResult, catalog.CompatibilityLevel, findings);
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

    private static void ScanComments(SqlParseResult parseResult, int? compatibilityLevel, List<DuplicationFinding> findings)
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
            if (stripped.Length < MinCommentedCodeLength || !LooksLikeCode(stripped, compatibilityLevel))
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

    private static readonly HashSet<string> RealStatementKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "INSERT", "UPDATE", "DELETE", "MERGE", "TRUNCATE",
        "DECLARE", "SET", "IF", "WHILE", "BEGIN", "PRINT", "RETURN", "THROW", "RAISERROR",
        "EXEC", "EXECUTE", "CREATE", "ALTER", "DROP", "WITH", "GRANT", "REVOKE", "DENY", "USE",
    };

    private static bool LooksLikeCode(string strippedText, int? compatibilityLevel)
    {
        var firstWord = strippedText.TrimStart().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries) is [var word, ..]
            ? word
            : string.Empty;
        if (!RealStatementKeywords.Contains(firstWord))
        {
            return false;
        }

        var result = SqlScriptParser.ParseText("commented-out-code-probe.sql", strippedText, initialQuotedIdentifiers: true, compatibilityLevel);
        return !result.HasErrors
            && result.Fragment is TSqlScript script
            && script.Batches.Any(b => b.Statements.Count > 0);
    }

    internal sealed class Rule(string sourcePath, DatabaseCatalog catalog) : IModuleRule
    {
        public List<DuplicationFinding> Findings { get; } = [];

        private string? _currentProcScope;

        private string CurrentModule => _currentProcScope ?? sourcePath;

        private readonly HashSet<IfStatement> _ifChainContinuations = new(ReferenceEqualityComparer.Instance);

        public void OnEnterProcedureOrFunctionBody(ProcedureStatementBodyBase node, ModuleWalker walker)
        {
            _currentProcScope = walker.CurrentProcScope;
            CheckStringLiterals(node.StatementList);
        }

        public void OnEnterTriggerBody(TriggerStatementBody node, ModuleWalker walker)
        {
            _currentProcScope = walker.CurrentProcScope;
            CheckStringLiterals(node.StatementList);
        }

        public void OnEnterSetVariableStatement(SetVariableStatement node, ModuleWalker walker)
        {
            if (node.AssignmentKind == AssignmentKind.Equals
                && node.Expression is VariableReference sourceVariable
                && string.Equals(sourceVariable.Name, node.Variable.Name, StringComparison.OrdinalIgnoreCase))
            {
                Add(DuplicationFindingKind.SelfAssignment, node.Variable.Name, node, FindingConfidence.High);
            }
        }

        public void OnEnterSelectSetVariable(SelectSetVariable node, ModuleWalker walker)
        {
            if (node.Expression is VariableReference sourceVariable
                && string.Equals(sourceVariable.Name, node.Variable.Name, StringComparison.OrdinalIgnoreCase))
            {
                Add(DuplicationFindingKind.SelfAssignment, node.Variable.Name, node, FindingConfidence.High);
            }
        }

        public void OnEnterAssignmentSetClause(AssignmentSetClause node, ModuleWalker walker)
        {

            if (node.Column is { } column && node.NewValue is ColumnReferenceExpression)
            {
                var columnText = FragmentTextRenderer.Render(column);
                var valueText = FragmentTextRenderer.Render(node.NewValue);
                if (catalog.IdentifierComparer.Equals(columnText, valueText))
                {
                    Add(DuplicationFindingKind.SelfAssignment, columnText, node, FindingConfidence.High);
                }
            }
        }

        public void OnEnterWhileStatement(WhileStatement node, ModuleWalker walker)
        {
            var body = node.Statement is BeginEndBlockStatement block ? block.StatementList.Statements : [node.Statement];

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
        }

        public void OnBooleanComparisonExpression(BooleanComparisonExpression node, ModuleWalker walker)
        {
            if (BothLiterals(node.FirstExpression, node.SecondExpression))
            {
                CheckAlwaysTrueOrFalseLiteralComparison(node);
            }
            else if (SameText(node.FirstExpression, node.SecondExpression) && CanClaimTautologyOrContradiction(node.FirstExpression, walker))
            {
                Add(DuplicationFindingKind.IdenticalBinaryOperands, node.ComparisonType.ToString(), node, FindingConfidence.High);
            }
        }

        public void OnEnterBooleanBinaryExpression(BooleanBinaryExpression node, ModuleWalker walker)
        {
            if (node.BinaryExpressionType is BooleanBinaryExpressionType.And or BooleanBinaryExpressionType.Or
                && SameText(node.FirstExpression, node.SecondExpression))
            {
                Add(DuplicationFindingKind.IdenticalBinaryOperands, node.BinaryExpressionType.ToString(), node, FindingConfidence.High);
            }
        }

        public void OnEnterBinaryExpression(BinaryExpression node, ModuleWalker walker)
        {

            if (node.BinaryExpressionType is BinaryExpressionType.Subtract or BinaryExpressionType.Divide or BinaryExpressionType.Modulo
                && !BothLiterals(node.FirstExpression, node.SecondExpression)
                && SameText(node.FirstExpression, node.SecondExpression))
            {
                Add(DuplicationFindingKind.IdenticalBinaryOperands, node.BinaryExpressionType.ToString(), node, FindingConfidence.Medium);
            }
        }

        public void OnEnterUnaryExpression(UnaryExpression node, ModuleWalker walker)
        {
            if (node.Expression is UnaryExpression inner && inner.UnaryExpressionType == node.UnaryExpressionType)
            {
                Add(DuplicationFindingKind.RepeatedUnaryOperator, node.UnaryExpressionType.ToString(), node, FindingConfidence.High);
            }
        }

        public void OnEnterBooleanNotExpression(BooleanNotExpression node, ModuleWalker walker)
        {

            var inner = Unwrap(node.Expression);
            if (inner is BooleanNotExpression)
            {
                Add(DuplicationFindingKind.RepeatedUnaryOperator, "NOT", node, FindingConfidence.High);
            }
            else
            {
                CheckNegatedComparison(node, inner);
            }
        }

        public void OnEnterIfStatement(IfStatement node, ModuleWalker walker)
        {
            if (!_ifChainContinuations.Contains(node))
            {
                AnalyzeIfChain(node);
            }

            CheckCollapsibleNestedIf(node);
        }

        public void OnEnterOperandPosition(TSqlFragment node, ModuleWalker walker)
        {
            switch (node)
            {
                case SearchedCaseExpression searchedCase:
                    CheckCaseDuplicateWhenConditions(searchedCase.WhenClauses);
                    CheckCaseBranchResultIdentity(searchedCase, searchedCase.WhenClauses);
                    break;

                case IIfCall iif:
                    if (iif.ThenExpression is IIfCall)
                    {
                        Add(DuplicationFindingKind.NestedConditionalExpression, "THEN", iif, FindingConfidence.Medium);
                    }

                    if (iif.ElseExpression is IIfCall)
                    {
                        Add(DuplicationFindingKind.NestedConditionalExpression, "ELSE", iif, FindingConfidence.Medium);
                    }

                    break;
            }
        }

        private bool CanClaimTautologyOrContradiction(ScalarExpression expression, ModuleWalker walker)
        {
            if (expression is not ColumnReferenceExpression columnRef)
            {
                return true;
            }

            return ResolveColumn(columnRef, walker) is { IsNullable: false };
        }

        private CatalogColumn? ResolveColumn(ColumnReferenceExpression columnRef, ModuleWalker walker)
        {
            var scopeChain = walker.CurrentScopeChain();
            if (columnRef.MultiPartIdentifier is not { Identifiers: { Count: > 0 } identifiers } || scopeChain.Count == 0)
            {
                return null;
            }

            var scope = scopeChain[0].ByAlias;
            var columnName = identifiers[^1].Value;

            if (identifiers.Count >= 2 && scope.TryGetValue(identifiers[^2].Value, out var qualifiedEntry))
            {
                return CatalogColumnOf(qualifiedEntry, columnName);
            }

            if (identifiers.Count == 1 && scope.Count == 1)
            {
                return CatalogColumnOf(scope.Values.Single(), columnName);
            }

            return null;
        }

        private CatalogColumn? CatalogColumnOf(ScopeEntry entry, string columnName) =>
            !entry.IsViewLayer && entry.Relation.QualifiedName is { } qualifiedName
                ? catalog.Find(qualifiedName)?.FindColumn(columnName, catalog.IdentifierComparer)
                : null;

        private static bool AlwaysExitsLoop(IList<TSqlStatement> statements) =>
            statements.Any(StatementAlwaysExits);

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

            _ => false,
        };

        private static IList<TSqlStatement> ToList(TSqlStatement statement) =>
            statement is BeginEndBlockStatement block ? block.StatementList.Statements : [statement];

        private sealed class GotoLabelCollector : TSqlFragmentVisitor
        {
            public int Count { get; private set; }

            public override void ExplicitVisit(GoToStatement node) => Count++;

            public override void ExplicitVisit(LabelStatement node) => Count++;

            public override void ExplicitVisit(WhileStatement node)
            {
                _ = node;
            }
        }

        private void CheckAlwaysTrueOrFalseLiteralComparison(BooleanComparisonExpression node)
        {
            if (LiteralComparisonFolder.TryFoldComparison(node.FirstExpression, node.SecondExpression, node.ComparisonType) is { } value)
            {
                Add(DuplicationFindingKind.AlwaysTrueOrFalseLiteralComparison, value ? "always true" : "always false", node, FindingConfidence.High);
            }
        }

        private static bool BothLiterals(ScalarExpression first, ScalarExpression second) =>
            first is Literal && second is Literal;

        private static bool SameText(TSqlFragment first, TSqlFragment second) =>
            string.Equals(FragmentTextRenderer.Render(first), FragmentTextRenderer.Render(second), StringComparison.OrdinalIgnoreCase);

        private static BooleanExpression Unwrap(BooleanExpression expression) =>
            expression is BooleanParenthesisExpression parenthesis ? Unwrap(parenthesis.Expression) : expression;

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

        private void AnalyzeIfChain(IfStatement root)
        {
            var (branches, finalElseBody) = CollectIfChainBranches(root);

            CheckDuplicateSiblingConditions(branches);
            CheckIfChainBranchBodyIdentity(root, branches, finalElseBody);

            foreach (var (condition, _) in branches)
            {
                CheckAndChainBounds(condition);
            }
        }

        private (List<(BooleanExpression Condition, TSqlStatement Body)> Branches, TSqlStatement? FinalElseBody) CollectIfChainBranches(IfStatement root)
        {
            var branches = new List<(BooleanExpression Condition, TSqlStatement Body)>();
            var current = root;
            while (true)
            {
                branches.Add((current.Predicate, current.ThenStatement));
                if (current.ElseStatement is IfStatement nextIf)
                {
                    _ifChainContinuations.Add(nextIf);
                    current = nextIf;
                    continue;
                }

                return (branches, current.ElseStatement);
            }
        }

        private void CheckDuplicateSiblingConditions(List<(BooleanExpression Condition, TSqlStatement Body)> branches)
        {
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
        }

        private void CheckIfChainBranchBodyIdentity(IfStatement root, List<(BooleanExpression Condition, TSqlStatement Body)> branches, TSqlStatement? finalElseBody)
        {
            var allBodies = branches.Select(b => b.Body).ToList();
            if (finalElseBody is not null)
            {
                allBodies.Add(finalElseBody);
            }

            if (allBodies.Count < 2)
            {
                return;
            }

            var bodyTexts = allBodies.Select(FragmentTextRenderer.Render).ToList();
            var allIdentical = finalElseBody is not null
                && bodyTexts.All(t => string.Equals(t, bodyTexts[0], StringComparison.OrdinalIgnoreCase));

            if (allIdentical)
            {
                Add(DuplicationFindingKind.AllBranchesIdentical, null, root, FindingConfidence.High);
                return;
            }

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

        private void CheckCaseDuplicateWhenConditions(IList<SearchedWhenClause> whens)
        {
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
        }

        private void CheckCaseBranchResultIdentity(SearchedCaseExpression node, IList<SearchedWhenClause> whens)
        {
            if (whens.Count < 2)
            {
                return;
            }

            var resultTexts = whens.Select(w => FragmentTextRenderer.Render(w.ThenExpression)).ToList();
            var allSameAsFirst = resultTexts.All(t => string.Equals(t, resultTexts[0], StringComparison.OrdinalIgnoreCase));
            var allIdentical = allSameAsFirst && node.ElseExpression is not null
                && string.Equals(FragmentTextRenderer.Render(node.ElseExpression), resultTexts[0], StringComparison.OrdinalIgnoreCase);

            if (allIdentical)
            {
                Add(DuplicationFindingKind.AllBranchesIdentical, null, node, FindingConfidence.High);
                return;
            }

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
                    CompareAndChainBoundPair(bounds[i], bounds[j]);
                }
            }
        }

        private void CompareAndChainBoundPair(
            (BooleanExpression Fragment, string Operand, NumericBound Interval) later,
            (BooleanExpression Fragment, string Operand, NumericBound Interval) earlier)
        {
            if (!string.Equals(later.Operand, earlier.Operand, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (later.Interval.IsEmptyIntersectionWith(earlier.Interval))
            {
                Add(DuplicationFindingKind.MutuallyExclusiveAndCondition, later.Operand, later.Fragment, FindingConfidence.High);
            }
            else if (later.Interval.IsSubsetOf(earlier.Interval))
            {

                Add(DuplicationFindingKind.RedundantAndCondition, earlier.Operand, earlier.Fragment, FindingConfidence.Medium);
            }
            else if (earlier.Interval.IsSubsetOf(later.Interval))
            {
                Add(DuplicationFindingKind.RedundantAndCondition, later.Operand, later.Fragment, FindingConfidence.Medium);
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
                && TryGetDirectNumericLiteralValue(comparison.SecondExpression) is { } rightValue)
            {
                return (FragmentTextRenderer.Render(comparison.FirstExpression), comparison.ComparisonType, rightValue);
            }

            if (comparison.SecondExpression is not Literal
                && TryGetDirectNumericLiteralValue(comparison.FirstExpression) is { } leftValue)
            {
                return (FragmentTextRenderer.Render(comparison.SecondExpression), MirrorOperator(comparison.ComparisonType), leftValue);
            }

            return null;
        }

        private static decimal? TryGetDirectNumericLiteralValue(ScalarExpression expression) => expression switch
        {
            IntegerLiteral integer when decimal.TryParse(integer.Value, out var value) => value,
            NumericLiteral numeric when decimal.TryParse(numeric.Value, out var value) => value,
            _ => null,
        };

        private static BooleanComparisonType MirrorOperator(BooleanComparisonType op) => op switch
        {
            BooleanComparisonType.GreaterThan => BooleanComparisonType.LessThan,
            BooleanComparisonType.GreaterThanOrEqualTo => BooleanComparisonType.LessThanOrEqualTo,
            BooleanComparisonType.LessThan => BooleanComparisonType.GreaterThan,
            BooleanComparisonType.LessThanOrEqualTo => BooleanComparisonType.GreaterThanOrEqualTo,
            var other => other,
        };

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

        private void CheckStringLiterals(StatementList? statementList)
        {
            if (statementList is null)
            {
                return;
            }

            var collector = new StringLiteralCollector();
            statementList.Accept(collector);

            var moduleName = CurrentModule;
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
                kind, CurrentModule, sourcePath, fragment.StartLine, fragment.StartColumn, detail, confidence));
    }
}
