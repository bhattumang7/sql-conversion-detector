using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.TypeInference;
using SilentScan.Core.Common;

namespace SilentScan.Core.Predicates.DynamicSqlValue;

public sealed record TransferContext(
    Dictionary<string, SqlType> DeclaredTypes,
    string SourcePath,
    int Cap,
    DynamicSqlScope Scope,
    List<DynamicSqlFinding> Findings,
    List<DynamicSqlScript> Scripts,
    List<ProcedureOutputSummary> OutputSummaries,
    ProcCallGraph? CallGraph = null,
    IReadOnlyDictionary<(string ProcedureQualifiedName, string ParameterName), IReadOnlyList<string>>? OutputSummaryIndex = null,
    DatabaseCatalog? Catalog = null,
    ILiveRowValueFetcher? RowValueFetcher = null)
{
    public SourceSpan Span(TSqlFragment fragment) => new(SourcePath, fragment.StartLine, fragment.StartColumn);
}

public static class DynamicSqlTransfer
{
    public static Action<Dictionary<string, SqlTextValue>, bool> CompileLeaf(TSqlStatement statement, IReadOnlyList<string> activeGuards, TransferContext context) => statement switch
    {
        DeclareVariableStatement declare => (state, _) => CompileDeclare(declare, context, state),
        SetVariableStatement set => (state, _) => CompileAssignment(set.Variable.Name, set.AssignmentKind, set.Expression, set.FunctionCallExists, set, context, state),
        SelectStatement select => (state, _) => CompileSelectAssignment(select, context, state),
        ExecuteStatement exec => (state, emit) => CompileExecute(exec, activeGuards, context, state, emit),
        ProcedureStatementBodyBase { StatementList: not null } procOrFunc => (_, emit) => CompileScopedBody(procOrFunc, context, emit),
        ProcedureStatementBodyBase => static (_, _) => { },
        TriggerStatementBody { StatementList: not null } trigger => (_, emit) => CompileTriggerBody(trigger, context, emit),
        TriggerStatementBody => static (_, _) => { },
        _ => CompileHavocDefault(statement, context),
    };

    private static void CompileScopedBody(ProcedureStatementBodyBase procOrFunc, TransferContext context, bool emit)
    {
        if (!emit)
        {
            return;
        }

        var name = ProcedureOrFunctionName(procOrFunc);
        var qualifiedName = name is null ? null : SchemaObjectNameHelper.Qualify(name);
        var formalParameters = ProcedureOrFunctionParameters(procOrFunc);
        var nestedScope = qualifiedName is null ? context.Scope : new DynamicSqlScope(qualifiedName, context.Scope.TriggerTarget);
        var nestedContext = context with { Scope = nestedScope, DeclaredTypes = new Dictionary<string, SqlType>(StringComparer.OrdinalIgnoreCase) };

        var seed = qualifiedName is not null && formalParameters is { Count: > 0 }
            ? BuildParameterSeed(qualifiedName, formalParameters, nestedContext)
            : new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase);
        SeedBatchDeclaredVariables(procOrFunc.StatementList!.Statements, nestedContext, seed);

        var cfg = new DynamicSqlCfg(context.SourcePath, context.Cap, (s, activeGuards) => CompileLeaf(s, activeGuards, nestedContext));
        var folded = cfg.Solve(procOrFunc.StatementList!.Statements, seed);

        if (qualifiedName is not null && formalParameters is { Count: > 0 })
        {
            RecordOutputParameterSummaries(qualifiedName, formalParameters, folded, nestedContext);
        }
    }

    private static Dictionary<string, SqlTextValue> BuildParameterSeed(
        string qualifiedName, IList<ProcedureParameter> formalParameters, TransferContext context)
    {
        var seed = new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase);
        if (context.CallGraph is null)
        {
            return seed;
        }

        var edges = context.CallGraph.EdgesCalling(qualifiedName).ToList();
        if (edges.Count == 0)
        {
            foreach (var formal in formalParameters)
            {
                seed[formal.VariableName.Value] = SeedSymbolicOrTaint(formal, "procedure-parameter:no-known-call-site", context);
            }

            return seed;
        }

        if (edges.Count == 1)
        {
            SeedFromSingleEdge(edges[0], formalParameters, seed, context);
            return seed;
        }

        foreach (var formal in formalParameters)
        {
            seed[formal.VariableName.Value] = SeedFromMultipleEdges(edges, formal, context);
        }

        return seed;
    }

    private static SqlTextValue SeedSymbolicOrTaint(ProcedureParameter formal, string taintReasonIfUnresolvable, TransferContext context)
    {
        var location = context.Span(formal);
        var type = SqlTypeReferenceResolver.Resolve(formal.DataType, columnCollation: null);
        return type is null
            ? new SqlTextValue.Tainted(taintReasonIfUnresolvable, location)
            : new SqlTextValue.Template([new TemplatePiece.Hole(type, location, HoleKind.UntypedParameter)]) { DeclaredType = type };
    }

    private static void SeedFromSingleEdge(
        ProcCallEdge edge, IList<ProcedureParameter> formalParameters, Dictionary<string, SqlTextValue> seed, TransferContext context)
    {
        foreach (var formal in formalParameters)
        {
            var paramName = formal.VariableName.Value;
            var argument = edge.Arguments.FirstOrDefault(a => string.Equals(a.FormalParameterName, paramName, StringComparison.OrdinalIgnoreCase));
            if (argument is null)
            {

                var declaredType = SqlTypeReferenceResolver.Resolve(formal.DataType, columnCollation: null);
                seed[paramName] = formal.Value is { } defaultExpression
                    ? WidenForPossibleExternalCallers(
                        ExpressionEvaluator.Fold(defaultExpression, seed, context.SourcePath, context.Cap, context.Catalog) with { DeclaredType = declaredType },
                        formal,
                        context)
                    : SeedSymbolicOrTaint(formal, "parameter-not-seeded:default-value-applies", context);
                continue;
            }

            if (argument.FormalParameterIsOutput)
            {

                seed[paramName] = SeedSymbolicOrTaint(formal, "parameter-not-seeded:output-argument", context);
                continue;
            }

            if (argument.LiteralArgument is not { } literalArgument)
            {
                seed[paramName] = SeedSymbolicOrTaint(formal, "parameter-not-seeded:non-literal-caller", context);
                continue;
            }

            var literalValue = new SqlTextValue.Template([new TemplatePiece.Lit(
                literalArgument.Value, new SourceSpan(literalArgument.SourcePath, literalArgument.StartLine, literalArgument.StartColumn), literalArgument.PrefixLength)]);

            seed[paramName] = WidenForPossibleExternalCallers(literalValue, formal, context);
        }
    }

    private static SqlTextValue WidenForPossibleExternalCallers(SqlTextValue seeded, ProcedureParameter formal, TransferContext context) =>
        SqlTextValue.Join(seeded, SeedSymbolicOrTaint(formal, "parameter-not-seeded:external-caller-possible", context), guardText: string.Empty, context.Cap, context.Span(formal));

    private static SqlTextValue SeedFromMultipleEdges(IReadOnlyList<ProcCallEdge> edges, ProcedureParameter formal, TransferContext context)
    {
        var paramName = formal.VariableName.Value;
        var declaredType = SqlTypeReferenceResolver.Resolve(formal.DataType, columnCollation: null);
        var at = context.Span(formal);
        SqlTextValue combined = new SqlTextValue.Tainted("parameter-not-seeded:cardinality-cap", at) { DeclaredType = declaredType };
        var first = true;

        foreach (var edge in edges)
        {
            var argument = edge.Arguments.FirstOrDefault(a => string.Equals(a.FormalParameterName, paramName, StringComparison.OrdinalIgnoreCase));
            if (argument is null || argument.FormalParameterIsOutput || argument.LiteralArgument is not { } literalArgument)
            {
                return SeedSymbolicOrTaint(formal, "parameter-not-seeded:non-literal-caller", context);
            }

            var literalValue = new SqlTextValue.Template([new TemplatePiece.Lit(
                literalArgument.Value, new SourceSpan(literalArgument.SourcePath, literalArgument.StartLine, literalArgument.StartColumn), literalArgument.PrefixLength)])
            { DeclaredType = declaredType };
            combined = first ? literalValue : SqlTextValue.Join(combined, literalValue, guardText: string.Empty, context.Cap, at);
            first = false;
        }

        return WidenForPossibleExternalCallers(combined, formal, context);
    }

    private static void CompileTriggerBody(TriggerStatementBody trigger, TransferContext context, bool emit)
    {
        if (!emit)
        {
            return;
        }

        var nestedScope = new DynamicSqlScope(SchemaObjectNameHelper.Qualify(trigger.Name), trigger.TriggerObject.Name);
        var nestedContext = context with { Scope = nestedScope, DeclaredTypes = new Dictionary<string, SqlType>(StringComparer.OrdinalIgnoreCase) };
        var seed = new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase);
        SeedBatchDeclaredVariables(trigger.StatementList!.Statements, nestedContext, seed);
        var cfg = new DynamicSqlCfg(context.SourcePath, context.Cap, (s, activeGuards) => CompileLeaf(s, activeGuards, nestedContext));
        cfg.Solve(trigger.StatementList!.Statements, seed);
    }

    internal static void SeedBatchDeclaredVariables(IList<TSqlStatement> statements, TransferContext context, Dictionary<string, SqlTextValue> seed)
    {
        var collector = new BatchDeclaredVariableCollector();
        foreach (var statement in statements)
        {
            statement.Accept(collector);
        }

        foreach (var (name, element) in collector.Declarations)
        {
            var declaredType = SqlTypeReferenceResolver.Resolve(element.DataType, columnCollation: null);
            if (declaredType is null)
            {
                continue;
            }

            context.DeclaredTypes.TryAdd(name, declaredType);
            seed.TryAdd(name, new SqlTextValue.Template([new TemplatePiece.Hole(declaredType, context.Span(element), HoleKind.UninitializedDeclare)]) { DeclaredType = declaredType });
        }
    }

    private sealed class BatchDeclaredVariableCollector : TSqlFragmentVisitor
    {
        public List<(string Name, DeclareVariableElement Element)> Declarations { get; } = [];

        public override void ExplicitVisit(DeclareVariableStatement node)
        {
            foreach (var element in node.Declarations)
            {
                Declarations.Add((element.VariableName.Value, element));
            }
        }

        public override void ExplicitVisit(ProcedureStatementBodyBase node)
        {

        }

        public override void ExplicitVisit(TriggerStatementBody node)
        {

        }
    }

    private static SchemaObjectName? ProcedureOrFunctionName(ProcedureStatementBodyBase procOrFunc) => procOrFunc switch
    {
        ProcedureStatementBody proc => proc.ProcedureReference.Name,
        FunctionStatementBody func => func.Name,
        _ => null,
    };

    private static IList<ProcedureParameter>? ProcedureOrFunctionParameters(ProcedureStatementBodyBase procOrFunc) =>
        procOrFunc is ProcedureStatementBody proc ? proc.Parameters : null;

    private static void RecordOutputParameterSummaries(string qualifiedName, IList<ProcedureParameter> formalParameters, Dictionary<string, SqlTextValue> folded, TransferContext context)
    {
        foreach (var formal in formalParameters)
        {
            if (formal.Modifier != ParameterModifier.Output
                || !folded.TryGetValue(formal.VariableName.Value, out var value)
                || value is not SqlTextValue.Template template)
            {
                continue;
            }

            var widened = SqlTextValue.Widen(template, context.Cap, context.Span(formal));
            if (widened is not SqlTextValue.Template widenedTemplate)
            {
                continue;
            }

            var values = SqlTextValue.Expand(widenedTemplate, context.Cap)
                .Where(assembly => !SqlTextValue.ContainsHole(assembly))
                .Select(assembly => string.Concat(assembly.OfType<FlatPiece.Lit>().Select(l => l.Text)))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (values.Count > 0)
            {
                context.OutputSummaries.Add(new ProcedureOutputSummary(qualifiedName, formal.VariableName.Value, values));
            }
        }
    }

    private static void CompileDeclare(DeclareVariableStatement declare, TransferContext context, Dictionary<string, SqlTextValue> state)
    {
        foreach (var element in declare.Declarations)
        {
            var name = element.VariableName.Value;
            var declaredType = SqlTypeReferenceResolver.Resolve(element.DataType, columnCollation: null);
            if (declaredType is not null)
            {
                context.DeclaredTypes[name] = declaredType;
            }

            var site = context.Span(element);
            if (element.Value is null or NullLiteral)
            {

                state[name] = declaredType is { } type
                    ? new SqlTextValue.Template([new TemplatePiece.Hole(type, site, HoleKind.UninitializedDeclare)]) { DeclaredType = type }
                    : new SqlTextValue.Tainted("no-initializer", site);
                continue;
            }

            state[name] = FoldByDeclaredType(element.Value, declaredType, context, state, site);
        }
    }

    private static SqlTextValue FoldByDeclaredType(
        ScalarExpression expression, SqlType? declaredType, TransferContext context, Dictionary<string, SqlTextValue> state, SourceSpan site)
    {
        if (declaredType is { Category: SqlTypeCategory.TinyInt or SqlTypeCategory.SmallInt or SqlTypeCategory.Int or SqlTypeCategory.BigInt }
            && ExpressionEvaluator.FoldInteger(expression, state, context.SourcePath, context.Cap, out var value))
        {
            return new SqlTextValue.Template([new TemplatePiece.Lit(value.ToString(System.Globalization.CultureInfo.InvariantCulture), site, PrefixLength: 0)])
                with { DeclaredType = declaredType };
        }

        if (expression is ScalarSubquery { QueryExpression: QuerySpecification { FromClause: not null } } subquery
            && TryFoldScalarSubqueryFromSingleKnownTable(subquery, context, state) is { } fetched)
        {
            return fetched with { DeclaredType = declaredType };
        }

        var folded = ExpressionEvaluator.Fold(expression, state, context.SourcePath, context.Cap, context.Catalog);
        return folded with { DeclaredType = declaredType };
    }

    private static SqlTextValue? TryFoldScalarSubqueryFromSingleKnownTable(ScalarSubquery subquery, TransferContext context, Dictionary<string, SqlTextValue> state)
    {
        if (context.Catalog is not { } catalog
            || subquery.QueryExpression is not QuerySpecification { FromClause.TableReferences: [NamedTableReference namedTable] } spec
            || spec.SelectElements is not [SelectScalarExpression { Expression: { } expression }])
        {
            return null;
        }

        var qualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(namedTable.SchemaObject));
        if (catalog.Find(qualifiedName) is not { } table
            || TryFoldWithColumnSplice(expression, table, state, context) is not { } spliced)
        {
            return null;
        }

        return TryFetchLiveScalar(expression, table, spec.WhereClause, context) ?? spliced;
    }

    private static void CompileAssignment(
        string name, AssignmentKind kind, ScalarExpression? expression, bool functionCallExists, TSqlFragment site,
        TransferContext context, Dictionary<string, SqlTextValue> state)
    {
        var declaredType = context.DeclaredTypes.GetValueOrDefault(name);
        var span = context.Span(site);

        if (functionCallExists || kind is not (AssignmentKind.Equals or AssignmentKind.AddEquals))
        {
            state[name] = HavocOrTaint("unsupported-assignment", span, declaredType);
            return;
        }

        if (expression is null)
        {
            state[name] = HavocOrTaint("unsupported-assignment", span, declaredType);
            return;
        }

        if (kind == AssignmentKind.AddEquals)
        {
            var existing = state.TryGetValue(name, out var existingValue) ? existingValue : HavocOrTaint("variable-not-in-scope", span, declaredType);

            if (declaredType is { Category: SqlTypeCategory.TinyInt or SqlTypeCategory.SmallInt or SqlTypeCategory.Int or SqlTypeCategory.BigInt }
                && ExpressionEvaluator.TryLiteralAsInteger(existing, out var existingInt)
                && ExpressionEvaluator.FoldInteger(expression, state, context.SourcePath, context.Cap, out var addValue))
            {
                state[name] = new SqlTextValue.Template([new TemplatePiece.Lit((existingInt + addValue).ToString(System.Globalization.CultureInfo.InvariantCulture), span, PrefixLength: 0)])
                    with { DeclaredType = declaredType };
                return;
            }

            var rhs = ExpressionEvaluator.Fold(expression, state, context.SourcePath, context.Cap, context.Catalog);
            state[name] = SqlTextValue.Concat(existing, rhs) with { DeclaredType = declaredType };
            return;
        }

        state[name] = FoldByDeclaredType(expression, declaredType, context, state, span);
    }

    private static SqlTextValue HavocOrTaint(string reason, SourceSpan span, SqlType? declaredType) =>
        declaredType is { } type
            ? new SqlTextValue.Template([new TemplatePiece.Hole(type, span, HoleKind.HavocWrite)]) { DeclaredType = type }
            : new SqlTextValue.Tainted(reason, span);

    private static bool TryCompileSelfReferentialAppend(SelectStatement select, TransferContext context, Dictionary<string, SqlTextValue> state)
    {
        if (select.QueryExpression is not QuerySpecification { SelectElements: [SelectSetVariable { AssignmentKind: AssignmentKind.Equals } setVar] }
            || setVar.Expression is not BinaryExpression { BinaryExpressionType: BinaryExpressionType.Add } binary
            || !IsLeftmostSelfReference(binary, setVar.Variable.Name)
            || !state.TryGetValue(setVar.Variable.Name, out var existing))
        {
            return false;
        }

        var declaredType = context.DeclaredTypes.GetValueOrDefault(setVar.Variable.Name);
        var appended = HavocOrTaint("select-assignment-not-pure", context.Span(binary), declaredType);
        state[setVar.Variable.Name] = SqlTextValue.Concat(existing, appended) with { DeclaredType = declaredType };
        return true;
    }

    private static bool IsLeftmostSelfReference(ScalarExpression expression, string name) => expression switch
    {
        VariableReference variable => string.Equals(variable.Name, name, StringComparison.OrdinalIgnoreCase),
        BinaryExpression { BinaryExpressionType: BinaryExpressionType.Add, FirstExpression: { } left } => IsLeftmostSelfReference(left, name),
        _ => false,
    };

    private static void CompileSelectAssignment(SelectStatement select, TransferContext context, Dictionary<string, SqlTextValue> state)
    {
        var assignedNames = new SelectSetVariableCollector();
        select.Accept(assignedNames);
        if (assignedNames.Names.Count == 0)
        {
            return;
        }

        var span = context.Span(select);
        if (select.QueryExpression is not QuerySpecification { FromClause: null, WhereClause: null, HavingClause: null, TopRowFilter: null } spec
            || spec.SelectElements.Count == 0
            || !spec.SelectElements.All(e => e is SelectSetVariable))
        {
            if (!TryCompileSelectAssignmentFromSingleKnownTable(select, context, state)
                && !TryCompileSelfReferentialAppend(select, context, state))
            {
                foreach (var name in assignedNames.Names)
                {
                    state[name] = HavocOrTaint("select-assignment-not-pure", span, context.DeclaredTypes.GetValueOrDefault(name));
                }
            }

            return;
        }

        foreach (var element in spec.SelectElements.Cast<SelectSetVariable>())
        {
            CompileAssignment(element.Variable.Name, element.AssignmentKind, element.Expression, functionCallExists: false, select, context, state);
        }
    }

    private static bool TryCompileSelectAssignmentFromSingleKnownTable(SelectStatement select, TransferContext context, Dictionary<string, SqlTextValue> state)
    {
        if (context.Catalog is not { } catalog
            || select.QueryExpression is not QuerySpecification { FromClause.TableReferences: [NamedTableReference namedTable] } spec
            || spec.SelectElements.Count != 1
            || spec.SelectElements[0] is not SelectSetVariable { AssignmentKind: AssignmentKind.Equals, Expression: { } expression } setVar)
        {
            return false;
        }

        var qualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(namedTable.SchemaObject));
        if (catalog.Find(qualifiedName) is not { } table)
        {
            return false;
        }

        if (TryFoldWithColumnSplice(expression, table, state, context) is not { } spliced)
        {
            return false;
        }

        spliced = TryFetchLiveScalar(expression, table, spec.WhereClause, context) ?? spliced;

        var declaredType = context.DeclaredTypes.GetValueOrDefault(setVar.Variable.Name);
        state[setVar.Variable.Name] = spliced with { DeclaredType = declaredType };
        return true;
    }

    private static SqlTextValue.Template? TryFetchLiveScalar(ScalarExpression expression, CatalogTable table, WhereClause? whereClause, TransferContext context)
    {
        if (context.RowValueFetcher is not { } fetcher
            || expression is not ColumnReferenceExpression { MultiPartIdentifier.Identifiers: { Count: > 0 } selectedIdentifiers } selectedColumnRef
            || table.FindColumn(selectedIdentifiers[^1].Value) is not { } selectedColumn)
        {
            return null;
        }

        var equalityKeys = TryExtractLiteralEqualityKeys(whereClause, table);
        var fetchedValues = fetcher.TryFetchDistinctValues(table.QualifiedName, selectedColumn.Name, equalityKeys, context.Cap);
        if (fetchedValues is not { Count: > 0 })
        {
            return null;
        }

        var site = context.Span(selectedColumnRef);
        var guardText = $"live-fetch:{table.QualifiedName}.{selectedColumn.Name}";
        SqlTextValue combined = new SqlTextValue.Template([new TemplatePiece.Lit(fetchedValues[0], site, PrefixLength: 0)]);
        for (var i = 1; i < fetchedValues.Count; i++)
        {
            var next = new SqlTextValue.Template([new TemplatePiece.Lit(fetchedValues[i], site, PrefixLength: 0)]);
            combined = SqlTextValue.Join(combined, next, guardText, context.Cap, site);
        }

        return combined as SqlTextValue.Template;
    }

    private static List<(string Column, string LiteralValue)> TryExtractLiteralEqualityKeys(WhereClause? whereClause, CatalogTable table)
    {
        var keys = new List<(string, string)>();
        if (whereClause?.SearchCondition is { } condition)
        {
            CollectEqualityKeys(condition, table, keys);
        }

        return keys;
    }

    private static void CollectEqualityKeys(BooleanExpression expression, CatalogTable table, List<(string Column, string LiteralValue)> keys)
    {
        switch (expression)
        {
            case BooleanParenthesisExpression paren:
                CollectEqualityKeys(paren.Expression, table, keys);
                break;

            case BooleanBinaryExpression { BinaryExpressionType: BooleanBinaryExpressionType.And } and:
                CollectEqualityKeys(and.FirstExpression, table, keys);
                CollectEqualityKeys(and.SecondExpression, table, keys);
                break;

            case BooleanComparisonExpression { ComparisonType: BooleanComparisonType.Equals } cmp:
                if (!TryAddEqualityKey(cmp.FirstExpression, cmp.SecondExpression, table, keys))
                {
                    TryAddEqualityKey(cmp.SecondExpression, cmp.FirstExpression, table, keys);
                }

                break;

            default:

                break;
        }
    }

    private static bool TryAddEqualityKey(ScalarExpression columnSide, ScalarExpression literalSide, CatalogTable table, List<(string Column, string LiteralValue)> keys)
    {
        if (columnSide is not ColumnReferenceExpression { MultiPartIdentifier.Identifiers: { Count: > 0 } identifiers } colRef
            || table.FindColumn(identifiers[^1].Value) is null)
        {
            return false;
        }

        var literalText = literalSide switch
        {
            StringLiteral s => s.Value,
            IntegerLiteral i => i.Value,
            _ => null,
        };

        if (literalText is null)
        {
            return false;
        }

        keys.Add((colRef.MultiPartIdentifier.Identifiers[^1].Value, literalText));
        return true;
    }

    private static SqlTextValue? TryFoldWithColumnSplice(ScalarExpression expression, CatalogTable table, Dictionary<string, SqlTextValue> state, TransferContext context) => expression switch
    {
        StringLiteral literal => new SqlTextValue.Template([new TemplatePiece.Lit(literal.Value, context.Span(literal), literal.IsNational ? 2 : 1)]),
        ParenthesisExpression paren => TryFoldWithColumnSplice(paren.Expression, table, state, context),
        VariableReference variableRef => state.GetValueOrDefault(variableRef.Name),
        ColumnReferenceExpression { MultiPartIdentifier.Identifiers: { Count: > 0 } identifiers } colRef
            when table.FindColumn(identifiers[^1].Value) is { Type: { } columnType }
            => new SqlTextValue.Template([new TemplatePiece.Hole(columnType, context.Span(colRef), HoleKind.RowDependentColumn)]),
        BinaryExpression { BinaryExpressionType: BinaryExpressionType.Add } binary
            when TryFoldWithColumnSplice(binary.FirstExpression, table, state, context) is { } left
                && TryFoldWithColumnSplice(binary.SecondExpression, table, state, context) is { } right
            => SqlTextValue.Concat(left, right),
        _ => null,
    };

    private static void CompileExecute(ExecuteStatement node, IReadOnlyList<string> activeGuards, TransferContext context, Dictionary<string, SqlTextValue> state, bool emit)
    {
        switch (node.ExecuteSpecification.ExecutableEntity)
        {
            case ExecutableStringList stringList:
                if (emit)
                {

                    if (node.ExecuteSpecification.LinkedServer is { Value.Length: > 0 })
                    {
                        context.Findings.Add(Unanalyzable(node, context, "linked-server-execute-not-modeled"));
                    }
                    else
                    {
                        CompileStringList(stringList, node, activeGuards, context, state);
                    }
                }

                break;

            case ExecutableProcedureReference { ProcedureReference.ProcedureReference.Name.BaseIdentifier.Value: var name } procRef
                when string.Equals(name, "sp_executesql", StringComparison.OrdinalIgnoreCase):
                if (emit)
                {
                    CompileSpExecuteSql(procRef, node, activeGuards, context, state);
                }

                break;

            default:
                TaintReferencedVariables(node, context, state);
                break;
        }
    }

    private static void CompileStringList(ExecutableStringList stringList, ExecuteStatement node, IReadOnlyList<string> activeGuards, TransferContext context, Dictionary<string, SqlTextValue> state)
    {
        SqlTextValue combined = new SqlTextValue.Template([]);
        foreach (var element in stringList.Strings)
        {
            combined = SqlTextValue.Concat(combined, ExpressionEvaluator.Fold(element, state, context.SourcePath, context.Cap, context.Catalog));
            if (combined is SqlTextValue.Tainted)
            {
                break;
            }
        }

        EmitScriptsOrFinding(combined, node, activeGuards, context, parameterDeclarationText: null, argumentBindings: null, isExecString: true);
    }

    private static readonly HashSet<string> ReservedArgumentNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "@stmt", "@statement", "@params", "@parameters",
    };

    private static void CompileSpExecuteSql(ExecutableProcedureReference procRef, ExecuteStatement node, IReadOnlyList<string> activeGuards, TransferContext context, Dictionary<string, SqlTextValue> state)
    {
        if (procRef.Parameters.Count == 0)
        {
            context.Findings.Add(Unanalyzable(node, context, "non-literal-argument"));
            return;
        }

        var statementArg = ResolveNamedOrPositionalArgument(procRef.Parameters, index: 0, "@stmt", "@statement");
        if (statementArg is null)
        {
            context.Findings.Add(Unanalyzable(node, context, "non-literal-argument"));
            return;
        }

        var query = ExpressionEvaluator.Fold(statementArg, state, context.SourcePath, context.Cap, context.Catalog);
        var parameterDeclarationText = ResolveParameterDeclarationText(procRef, state, context);
        var argumentBindings = ResolveArgumentBindings(procRef);
        EmitScriptsOrFinding(query, node, activeGuards, context, parameterDeclarationText, argumentBindings, isExecString: false);

        TaintReferencedVariables(node, context, state);
    }

    private static Dictionary<string, string>? ResolveArgumentBindings(ExecutableProcedureReference procRef)
    {
        Dictionary<string, string>? bindings = null;
        foreach (var parameter in procRef.Parameters)
        {
            if (parameter.Variable is not { } formalName
                || ReservedArgumentNames.Contains(formalName.Name)
                || parameter.ParameterValue is not VariableReference valueVariable)
            {
                continue;
            }

            bindings ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bindings[formalName.Name] = valueVariable.Name;
        }

        return bindings;
    }

    private static string? ResolveParameterDeclarationText(ExecutableProcedureReference procRef, Dictionary<string, SqlTextValue> state, TransferContext context)
    {
        var paramsArg = ResolveNamedOrPositionalArgument(procRef.Parameters, index: 1, "@params", "@parameters");
        if (paramsArg is null)
        {
            return null;
        }

        var folded = ExpressionEvaluator.Fold(paramsArg, state, context.SourcePath, context.Cap, context.Catalog);
        if (folded is not SqlTextValue.Template template)
        {
            return null;
        }

        var widened = SqlTextValue.Widen(template, context.Cap, context.Span(paramsArg));
        if (widened is not SqlTextValue.Template widenedTemplate)
        {
            return null;
        }

        var assemblies = SqlTextValue.Expand(widenedTemplate, context.Cap);
        if (assemblies.Count != 1 || SqlTextValue.ContainsHole(assemblies[0]))
        {

            return null;
        }

        return string.Concat(assemblies[0].OfType<FlatPiece.Lit>().Select(l => l.Text));
    }

    private static ScalarExpression? ResolveNamedOrPositionalArgument(IList<ExecuteParameter> parameters, int index, params ReadOnlySpan<string> formalNames)
    {
        foreach (var parameter in parameters)
        {
            if (parameter.Variable is { } variable && formalNames.Contains(variable.Name, StringComparer.OrdinalIgnoreCase))
            {
                return parameter.ParameterValue;
            }
        }

        return index < parameters.Count ? parameters[index].ParameterValue : null;
    }

    private static void EmitScriptsOrFinding(
        SqlTextValue value, ExecuteStatement node, IReadOnlyList<string> activeGuards, TransferContext context, string? parameterDeclarationText, IReadOnlyDictionary<string, string>? argumentBindings, bool isExecString)
    {
        if (TryNarrowByActiveGuard(value, activeGuards) is { } narrowed)
        {
            if (TryEmitFromValue(narrowed, node, context, parameterDeclarationText, argumentBindings, isExecString) is { } narrowedFailureReason)
            {
                context.Findings.Add(Unanalyzable(node, context, narrowedFailureReason));
            }

            return;
        }

        if (value is SqlTextValue.Tainted tainted)
        {
            if (activeGuards.Count > 0)
            {
                context.Findings.Add(Unanalyzable(node, context, tainted.Reason));
                return;
            }

            var recovered = false;
            foreach (var alternative in tainted.GuardedAlternatives ?? [])
            {
                recovered |= TryEmitFromValue(alternative.Value, node, context, parameterDeclarationText, argumentBindings, isExecString) is null;
            }

            if (!recovered)
            {
                context.Findings.Add(Unanalyzable(node, context, tainted.Reason));
            }

            return;
        }

        if (SqlTextValue.Widen(value, context.Cap, context.Span(node)) is SqlTextValue.Template widenedForSizing
            && SqlTextValue.ExpandedPieceTotal(widenedForSizing) > SqlTextValue.MaxExpandedPieceTotal)
        {
            context.Findings.Add(Unanalyzable(node, context, SqlTextValue.ExpansionSizeCapReason));
            return;
        }

        if (TryEmitFromValue(value, node, context, parameterDeclarationText, argumentBindings, isExecString) is { } failureReason)
        {
            context.Findings.Add(Unanalyzable(node, context, failureReason));
        }
    }

    private static SqlTextValue.Template? TryNarrowByActiveGuard(SqlTextValue value, IReadOnlyList<string> activeGuards)
    {
        if (activeGuards.Count == 0 || value.GuardedAlternatives is not { Count: > 0 } alternatives)
        {
            return null;
        }

        return alternatives.Where(alternative => activeGuards.Contains(alternative.GuardText, StringComparer.Ordinal))
            .Select(alternative => alternative.Value)
            .FirstOrDefault();
    }

    private static string? TryEmitFromValue(
        SqlTextValue value, ExecuteStatement node, TransferContext context, string? parameterDeclarationText, IReadOnlyDictionary<string, string>? argumentBindings, bool isExecString)
    {
        var site = context.Span(node);
        var widened = SqlTextValue.Widen(value, context.Cap, site);
        if (widened is SqlTextValue.Tainted widenedTainted)
        {
            return widenedTainted.Reason;
        }

        var widenedTemplate = (SqlTextValue.Template)widened;
        if (SqlTextValue.ExpandedPieceTotal(widenedTemplate) > SqlTextValue.MaxExpandedPieceTotal)
        {

            return SqlTextValue.ExpansionSizeCapReason;
        }

        var assemblies = SqlTextValue.Expand(widenedTemplate, context.Cap);

        var seenText = new HashSet<string>(StringComparer.Ordinal);
        foreach (var assembly in assemblies)
        {
            var rendered = TemplateRenderer.Render(assembly);
            if (!seenText.Add(rendered.InnerText))
            {
                continue;
            }

            var confidence = SqlTextValue.ContainsHole(assembly) ? FindingConfidence.Medium : FindingConfidence.High;
            context.Scripts.Add(new DynamicSqlScript(
                CallSite(node, context), rendered.InnerText, rendered.SegmentMap, parameterDeclarationText,
                context.Scope, argumentBindings, confidence,
                rendered.Placeholders.Count > 0 ? rendered.Placeholders : null, isExecString));
        }

        return null;
    }

    private static void TaintReferencedVariables(ExecuteStatement node, TransferContext context, Dictionary<string, SqlTextValue> state)
    {
        var seeded = SeedKnownOutputArguments(node, context, state);
        var span = context.Span(node);
        foreach (var name in CollectWritableVariableNames(node).Where(n => state.ContainsKey(n) && !seeded.Contains(n)))
        {
            state[name] = HavocOrTaint("unsupported-execute-form", span, context.DeclaredTypes.GetValueOrDefault(name));
        }
    }

    private static IEnumerable<string> CollectWritableVariableNames(ExecuteStatement node)
    {
        if (node.ExecuteSpecification.Variable is { } returnStatusVariable)
        {
            yield return returnStatusVariable.Name;
        }

        if (node.ExecuteSpecification.ExecutableEntity is ExecutableProcedureReference { Parameters: { } parameters })
        {
            foreach (var parameter in parameters)
            {
                if (parameter is { IsOutput: true, ParameterValue: VariableReference outputVariable })
                {
                    yield return outputVariable.Name;
                }
            }

            yield break;
        }

        var collector = new ReferencedVariableCollector();
        node.Accept(collector);
        foreach (var name in collector.Names)
        {
            yield return name;
        }
    }

    private static HashSet<string> SeedKnownOutputArguments(ExecuteStatement node, TransferContext context, Dictionary<string, SqlTextValue> state)
    {
        var seeded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (context.CallGraph is null || context.OutputSummaryIndex is null)
        {
            return seeded;
        }

        var edge = context.CallGraph.EdgeAt(context.Span(node));
        if (edge is null)
        {
            return seeded;
        }

        var span = context.Span(node);
        foreach (var argument in edge.Arguments)
        {
            if (!argument.FormalParameterIsOutput
                || argument.CallerVariableName is not { } callerVariable
                || !context.OutputSummaryIndex.TryGetValue((edge.CalleeQualifiedName, argument.FormalParameterName), out var values))
            {
                continue;
            }

            SqlTextValue combined = new SqlTextValue.Template([new TemplatePiece.Lit(values[0], span, PrefixLength: 0)]);
            foreach (var value in values.Skip(1))
            {
                combined = SqlTextValue.Join(combined, new SqlTextValue.Template([new TemplatePiece.Lit(value, span, PrefixLength: 0)]), guardText: string.Empty, context.Cap, span);
            }

            state[callerVariable] = combined;
            seeded.Add(callerVariable);
        }

        return seeded;
    }

    private sealed class ReferencedVariableCollector : TSqlFragmentVisitor
    {
        public HashSet<string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);

        public override void Visit(VariableReference node) => Names.Add(node.Name);
    }

    private static DynamicSqlFinding Unanalyzable(ExecuteStatement node, TransferContext context, string reason)
    {
        var span = context.Span(node);
        return new DynamicSqlFinding(span.SourcePath, span.Line, span.Column, DynamicSqlOutcome.Unanalyzable, reason);
    }

    private static SourceSpan CallSite(ExecuteStatement node, TransferContext context) => context.Span(node);

    private sealed class SelectSetVariableCollector : TSqlFragmentVisitor
    {
        public List<string> Names { get; } = [];

        public override void Visit(SelectSetVariable node) => Names.Add(node.Variable.Name);
    }

    private static Action<Dictionary<string, SqlTextValue>, bool> CompileHavocDefault(TSqlStatement statement, TransferContext context)
    {
        var collector = new WrittenVariableCollector();
        statement.Accept(collector);
        if (collector.Names.Count == 0)
        {
            return static (_, _) => { };
        }

        var span = context.Span(statement);
        return (state, _) =>
        {
            foreach (var name in collector.Names)
            {
                state[name] = HavocOrTaint("unsupported-statement-in-scope", span, context.DeclaredTypes.GetValueOrDefault(name));
            }
        };
    }

    private sealed class WrittenVariableCollector : TSqlFragmentVisitor
    {
        public HashSet<string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);

        public override void Visit(AssignmentSetClause node)
        {
            if (node.Variable is not null)
            {
                Names.Add(node.Variable.Name);
            }
        }

        public override void Visit(FetchCursorStatement node)
        {
            if (node.IntoVariables is null)
            {
                return;
            }

            foreach (var variable in node.IntoVariables)
            {
                Names.Add(variable.Name);
            }
        }

        public override void Visit(SelectSetVariable node) => Names.Add(node.Variable.Name);
    }
}
