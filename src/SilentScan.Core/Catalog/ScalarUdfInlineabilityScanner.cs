using System.Runtime.CompilerServices;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Common;

namespace SilentScan.Core.Catalog;

public static class ScalarUdfInlineabilityScanner
{
    private const int MaxNestingDepth = 32;

    private static readonly HashSet<string> TimeDependentIntrinsics = new(StringComparer.OrdinalIgnoreCase)
    {
        "GETDATE", "GETUTCDATE", "SYSDATETIME", "SYSUTCDATETIME", "SYSDATETIMEOFFSET", "CURRENT_TIMESTAMP",
    };

    private static readonly HashSet<string> XmlInstanceMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "value", "query", "exist",
    };

    private static readonly HashSet<string> NonInlineableGlobalVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        "@@DBTS", "@@ROWCOUNT", "@@ERROR", "@@NESTLEVEL", "@@PROCID",
    };

    private static readonly ConditionalWeakTable<DatabaseCatalog, List<(string Caller, string Callee)>> CallGraphsByCatalog = [];

    public static (string? Blocker, int TableReferenceCount) FindBlocker(
        StatementList? body, string ownQualifiedName, DatabaseCatalog catalog,
        IList<ProcedureParameter>? parameters = null, IList<FunctionOption>? options = null)
    {
        if (parameters is not null)
        {
            foreach (var parameter in parameters)
            {
                if (parameter.DataType is UserDataTypeReference userType
                    && catalog.Find(SchemaObjectNameHelper.Qualify(userType.Name)) is { Kind: CatalogTableKind.TableType })
                {
                    return ($"table-valued parameter {parameter.VariableName.Value}", 0);
                }
            }
        }

        if (options is not null && options.Any(option => option.OptionKind == FunctionOptionKind.ExecuteAs))
        {
            return ("EXECUTE AS clause", 0);
        }

        if (body is null)
        {
            return (null, 0);
        }

        var visitor = new Visitor(ownQualifiedName, catalog);
        body.Accept(visitor);
        return (visitor.Blocker, visitor.TableReferenceCount);
    }

    private sealed class Visitor(string ownQualifiedName, DatabaseCatalog catalog) : TSqlFragmentVisitor
    {
        private int _returnStatementCount;
        private int _nestingDepth;

        public string? Blocker { get; private set; }

        public int TableReferenceCount { get; private set; }

        public override void ExplicitVisit(WhileStatement node)
        {
            Report("WHILE loop");
            EnterNestedBlock();
            base.ExplicitVisit(node);
            ExitNestedBlock();
        }

        public override void ExplicitVisit(TryCatchStatement node)
        {
            Report("TRY/CATCH");
            EnterNestedBlock();
            base.ExplicitVisit(node);
            ExitNestedBlock();
        }

        public override void ExplicitVisit(BeginEndBlockStatement node)
        {
            EnterNestedBlock();
            base.ExplicitVisit(node);
            ExitNestedBlock();
        }

        public override void ExplicitVisit(IfStatement node)
        {
            EnterNestedBlock();
            base.ExplicitVisit(node);
            ExitNestedBlock();
        }

        public override void ExplicitVisit(DeclareTableVariableStatement node)
        {
            Report("table variable declaration");
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(ExecuteStatement node)
        {
            Report("EXECUTE statement");
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(DeclareCursorStatement node)
        {
            Report("cursor declaration");
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(ReturnStatement node)
        {
            _returnStatementCount++;
            if (_returnStatementCount > 1)
            {
                Report("multiple RETURN statements");
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(GoToStatement node)
        {
            Report("GOTO statement");
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(LabelStatement node)
        {
            Report("GOTO statement");
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(NamedTableReference node)
        {
            TableReferenceCount++;

            if (catalog.IdentifierComparer.Equals(node.SchemaObject.SchemaIdentifier?.Value, "sys"))
            {
                Report("system catalog access (sys." + node.SchemaObject.BaseIdentifier.Value + ")");
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(SelectStatement node)
        {
            if (node.WithCtesAndXmlNamespaces is not null)
            {
                Report("CTE (WITH clause)");
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(QuerySpecification node)
        {
            if (node.FromClause is not null)
            {
                foreach (var element in node.SelectElements)
                {
                    if (element is SelectSetVariable { Expression: { } expression } setVariable
                        && ReferencesVariable(expression, setVariable.Variable.Name))
                    {
                        Report("SELECT accumulator assignment reading its own variable");
                        break;
                    }
                }
            }

            if (node.OrderByClause is not null && node.TopRowFilter is null)
            {
                Report("ORDER BY without TOP");
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(FunctionCall node)
        {
            if (node.CallTarget is MultiPartIdentifierCallTarget)
            {
                var qualifiedName = SchemaObjectNameHelper.QualifyFunctionCall(node);
                if (catalog.IdentifierComparer.Equals(qualifiedName, ownQualifiedName))
                {
                    Report("recursive self-reference");
                }
                else if (catalog.TryGetScalarUdfInfo(qualifiedName, out var calleeInfo))
                {
                    if (calleeInfo is { InlineabilityBlocker: { Length: > 0 } } or { EngineIsInlineable: false })
                    {
                        Report($"references non-inlineable UDF {qualifiedName}");
                    }

                    RecordCallAndCheckForMutualRecursion(qualifiedName);
                }
            }
            else if (node.FunctionName is { Value: { } aggName } && string.Equals(aggName, "STRING_AGG", StringComparison.OrdinalIgnoreCase))
            {
                Report("STRING_AGG()");
            }
            else if (node.CallTarget is ExpressionCallTarget
                && node.FunctionName is { Value: { } xmlMethodName } && XmlInstanceMethods.Contains(xmlMethodName))
            {
                Report($"XML data-type method .{xmlMethodName}()");
            }
            else if (node.FunctionName is { Value: { } functionName } && TimeDependentIntrinsics.Contains(functionName))
            {
                Report($"time-dependent intrinsic {functionName.ToUpperInvariant()}()");
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(VariableMethodCallTableReference node)
        {
            Report("XML data-type method .nodes()");
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(SetVariableStatement node)
        {
            if (node.FunctionCallExists && string.Equals(node.Identifier?.Value, "modify", StringComparison.OrdinalIgnoreCase))
            {
                Report("XML data-type method .modify()");
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(GlobalVariableExpression node)
        {
            if (node.Name is { } name && NonInlineableGlobalVariables.Contains(name))
            {
                Report(name.ToUpperInvariant());
            }

            base.ExplicitVisit(node);
        }

        private void EnterNestedBlock()
        {
            _nestingDepth++;
            if (_nestingDepth >= MaxNestingDepth)
            {
                Report($"statement/block nesting depth reached {MaxNestingDepth}");
            }
        }

        private void ExitNestedBlock() => _nestingDepth--;

        private static bool ReferencesVariable(ScalarExpression expression, string variableName)
        {
            var finder = new VariableReferenceFinder(variableName);
            expression.Accept(finder);
            return finder.Found;
        }

        private sealed class VariableReferenceFinder(string variableName) : TSqlFragmentVisitor
        {
            public bool Found { get; private set; }

            public override void ExplicitVisit(VariableReference node)
            {
                if (string.Equals(node.Name, variableName, StringComparison.OrdinalIgnoreCase))
                {
                    Found = true;
                }

                base.ExplicitVisit(node);
            }
        }

        private void RecordCallAndCheckForMutualRecursion(string calleeQualifiedName)
        {
            var edges = CallGraphEdges(catalog);
            edges.Add((ownQualifiedName, calleeQualifiedName));

            if (CanReach(edges, calleeQualifiedName, ownQualifiedName, catalog.IdentifierComparer))
            {
                Report($"mutual recursion through {calleeQualifiedName}");
            }
        }

        private static List<(string Caller, string Callee)> CallGraphEdges(DatabaseCatalog catalog) =>
            CallGraphsByCatalog.GetValue(catalog, static _ => []);

        private static bool CanReach(List<(string Caller, string Callee)> edges, string from, string to, StringComparer comparer)
        {
            var visited = new HashSet<string>(comparer) { from };
            var stack = new Stack<string>();
            stack.Push(from);

            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (comparer.Equals(current, to))
                {
                    return true;
                }

                foreach (var edge in edges.Where(edge => comparer.Equals(edge.Caller, current) && visited.Add(edge.Callee)))
                {
                    stack.Push(edge.Callee);
                }
            }

            return false;
        }

        private void Report(string reason) => Blocker ??= reason;
    }
}
