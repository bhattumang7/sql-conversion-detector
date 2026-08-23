using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Common;

namespace SilentScan.Core.Catalog;

public static class ScalarUdfInlineabilityScanner
{
    private static readonly HashSet<string> TimeDependentIntrinsics = new(StringComparer.OrdinalIgnoreCase)
    {
        "GETDATE", "GETUTCDATE", "SYSDATETIME", "SYSUTCDATETIME", "SYSDATETIMEOFFSET", "CURRENT_TIMESTAMP",
    };

    private static readonly HashSet<string> XmlInstanceMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "value", "query", "exist",
    };

    public static (string? Blocker, int TableReferenceCount) FindBlocker(
        StatementList? body, string ownQualifiedName, DatabaseCatalog catalog, IList<ProcedureParameter>? parameters = null)
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

        public string? Blocker { get; private set; }

        public int TableReferenceCount { get; private set; }

        public override void ExplicitVisit(WhileStatement node)
        {
            Report("WHILE loop");
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(TryCatchStatement node)
        {
            Report("TRY/CATCH");
            base.ExplicitVisit(node);
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

            if (string.Equals(node.SchemaObject.SchemaIdentifier?.Value, "sys", StringComparison.OrdinalIgnoreCase))
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
                if (string.Equals(qualifiedName, ownQualifiedName, StringComparison.OrdinalIgnoreCase))
                {
                    Report("recursive self-reference");
                }
                else if (catalog.TryGetScalarUdfInfo(qualifiedName, out var calleeInfo)
                    && calleeInfo is { InlineabilityBlocker: { Length: > 0 } } or { EngineIsInlineable: false })
                {
                    Report($"references non-inlineable UDF {qualifiedName}");
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
            if (string.Equals(node.Name, "@@DBTS", StringComparison.OrdinalIgnoreCase))
            {
                Report("@@DBTS");
            }

            base.ExplicitVisit(node);
        }

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

        private void Report(string reason) => Blocker ??= reason;
    }
}
