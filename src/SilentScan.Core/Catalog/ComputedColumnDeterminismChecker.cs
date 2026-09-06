using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Parsing;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Catalog;

internal static class ComputedColumnDeterminismChecker
{
    private static readonly HashSet<string> AlwaysNonDeterministicFunctionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "NEWID", "NEWSEQUENTIALID", "GETDATE", "GETUTCDATE",
        "SYSDATETIME", "SYSUTCDATETIME", "SYSDATETIMEOFFSET",
        "FORMAT", "PARSENAME",

        "OBJECT_ID", "OBJECT_NAME", "OBJECTPROPERTY", "OBJECTPROPERTYEX",
        "DB_ID", "DB_NAME", "DATABASEPROPERTY", "DATABASEPROPERTYEX",
        "SCHEMA_ID", "SCHEMA_NAME", "COL_NAME", "COL_LENGTH",
        "TYPE_ID", "TYPE_NAME", "TYPEPROPERTY",
        "COLUMNPROPERTY", "INDEXPROPERTY", "FILEPROPERTY",
        "ASSEMBLYPROPERTY", "COLLATIONPROPERTY", "CONNECTIONPROPERTY", "SESSIONPROPERTY",
        "SERVERPROPERTY",

        "USER_ID", "USER_NAME", "SUSER_ID", "SUSER_NAME", "SUSER_SID", "SUSER_SNAME",
        "IS_MEMBER", "IS_ROLEMEMBER", "IS_SRVROLEMEMBER",
        "HAS_PERMS_BY_NAME", "HAS_DBACCESS", "PERMISSIONS",

        "APP_NAME", "HOST_ID", "HOST_NAME", "PROGRAM_NAME", "ORIGINAL_LOGIN",
        "CONTEXT_INFO", "SESSION_CONTEXT",
        "CURRENT_TRANSACTION_ID", "XACT_STATE",
        "CURRENT_TIMEZONE", "CURRENT_TIMEZONE_ID",

        "IDENT_CURRENT", "IDENT_INCR", "IDENT_SEED", "SCOPE_IDENTITY", "ROWCOUNT_BIG", "GETANSINULL",
        "CURRENT_REQUEST_ID", "DATENAME", "FORMATMESSAGE",

        "VECTOR_DISTANCE", "VECTOR_NORM", "VECTOR_NORMALIZE", "VECTORPROPERTY",

        "INDEX_COL", "OBJECT_DEFINITION", "OBJECT_SCHEMA_NAME", "ORIGINAL_DB_NAME",
        "DATABASE_PRINCIPAL_ID", "DEFAULT_DOMAIN", "LOGINPROPERTY", "STATS_DATE",
        "APPLOCK_MODE", "APPLOCK_TEST",

        "COMPRESS", "DECOMPRESS", "PWDENCRYPT", "PWDCOMPARE",
        "KEY_GUID", "KEY_ID", "KEY_NAME", "CERTPROPERTY",
        "ASYMKEY_ID", "ASYMKEYPROPERTY", "SYMKEYPROPERTY", "SIGNBYASYMKEY",

        "CHANGE_TRACKING_CURRENT_VERSION", "CHANGE_TRACKING_MIN_VALID_VERSION",

        "ISDATE", "SQL_VARIANT_PROPERTY",
    };

    private static readonly HashSet<string> NonDeterministicDateParts = new(StringComparer.OrdinalIgnoreCase)
    {
        "weekday", "dw", "week", "wk", "ww",
    };

    private static readonly HashSet<int> NonDeterministicDateStyles =
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 100, 106, 107, 109, 113,
    ];

    public static bool IsNonDeterministic(ScalarExpression expression, Func<string, SqlType?>? resolveColumnType = null)
    {
        var visitor = new Visitor(resolveColumnType);
        expression.Accept(visitor);
        return visitor.Found;
    }

    private sealed class Visitor(Func<string, SqlType?>? resolveColumnType) : TSqlFragmentVisitor
    {
        public bool Found { get; private set; }

        public override void ExplicitVisit(FunctionCall node)
        {
            var name = node.FunctionName.Value;
            if (AlwaysNonDeterministicFunctionNames.Contains(name)
                || (node.Parameters.Count == 0 && string.Equals(name, "RAND", StringComparison.OrdinalIgnoreCase))
                || (string.Equals(name, "DATEPART", StringComparison.OrdinalIgnoreCase) && IsNonDeterministicDatePart(node.Parameters)))
            {
                Found = true;
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(GlobalVariableExpression node)
        {
            Found = true;
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(AtTimeZoneCall node)
        {
            Found = true;
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(ParameterlessCall node)
        {
            Found = true;
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(ParseCall node)
        {
            Found = true;
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(AIGenerateEmbeddingsFunctionCall node)
        {
            Found = true;
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(TryParseCall node)
        {
            Found = true;
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CastCall node)
        {
            if (IsCharToDateFamilyConversion(node.DataType, node.Parameter))
            {
                Found = true;
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(TryCastCall node)
        {
            if (IsCharToDateFamilyConversion(node.DataType, node.Parameter))
            {
                Found = true;
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(ConvertCall node)
        {
            if (IsCharToDateFamilyConversion(node.DataType, node.Parameter)
                && (TryGetStyle(node.Style) is not { } style || NonDeterministicDateStyles.Contains(style)))
            {
                Found = true;
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(TryConvertCall node)
        {
            if (IsCharToDateFamilyConversion(node.DataType, node.Parameter)
                && (TryGetStyle(node.Style) is not { } style || NonDeterministicDateStyles.Contains(style)))
            {
                Found = true;
            }

            base.ExplicitVisit(node);
        }

        private bool IsCharToDateFamilyConversion(DataTypeReference targetDataType, ScalarExpression parameter)
        {
            var targetType = SqlTypeReferenceResolver.Resolve(targetDataType, columnCollation: null);
            if (targetType is not { IsDateTimeFamily: true })
            {
                return false;
            }

            var sourceType = ResolveSourceType(parameter);
            return sourceType is { IsStringFamily: true };
        }

        private SqlType? ResolveSourceType(ScalarExpression expression) => expression switch
        {
            StringLiteral => new SqlType(SqlTypeCategory.VarChar),
            ColumnReferenceExpression { MultiPartIdentifier.Identifiers: [.., { } last] } =>
                resolveColumnType?.Invoke(last.Value),
            CastCall inner => SqlTypeReferenceResolver.Resolve(inner.DataType, columnCollation: null),
            ConvertCall inner => SqlTypeReferenceResolver.Resolve(inner.DataType, columnCollation: null),
            ParenthesisExpression inner => ResolveSourceType(inner.Expression),
            _ => null,
        };

        private static bool IsNonDeterministicDatePart(IList<ScalarExpression> parameters) =>
            parameters is [IdentifierLiteral datePart, ..]
            && NonDeterministicDateParts.Contains(datePart.Value);

        private static int? TryGetStyle(ScalarExpression? style) =>
            style is IntegerLiteral literal && int.TryParse(literal.Value, out var value) ? value : null;
    }
}
