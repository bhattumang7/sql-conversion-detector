using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Common;

namespace SilentScan.Core.Predicates;

public static class NamingScanner
{

    private static readonly HashSet<string> ReservedKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ADD", "ALL", "ALTER", "AND", "ANY", "AS", "ASC", "AUTHORIZATION", "BACKUP", "BEGIN",
        "BETWEEN", "BREAK", "BROWSE", "BULK", "BY", "CASCADE", "CASE", "CHECK", "CHECKPOINT",
        "CLOSE", "CLUSTERED", "COALESCE", "COLLATE", "COLUMN", "COMMIT", "COMPUTE", "CONSTRAINT",
        "CONTAINS", "CONTAINSTABLE", "CONTINUE", "CONVERT", "CREATE", "CROSS", "CURRENT",
        "CURRENT_DATE", "CURRENT_TIME", "CURRENT_TIMESTAMP", "CURRENT_USER", "CURSOR", "DATABASE",
        "DBCC", "DEALLOCATE", "DECLARE", "DEFAULT", "DELETE", "DENY", "DESC", "DISK", "DISTINCT",
        "DISTRIBUTED", "DOUBLE", "DROP", "DUMP", "ELSE", "END", "ERRLVL", "ESCAPE", "EXCEPT",
        "EXEC", "EXECUTE", "EXISTS", "EXIT", "EXTERNAL", "FETCH", "FILE", "FILLFACTOR", "FOR",
        "FOREIGN", "FREETEXT", "FREETEXTTABLE", "FROM", "FULL", "FUNCTION", "GOTO", "GRANT",
        "GROUP", "HAVING", "HOLDLOCK", "IDENTITY", "IDENTITY_INSERT", "IDENTITYCOL", "IF", "IN",
        "INDEX", "INNER", "INSERT", "INTERSECT", "INTO", "IS", "JOIN", "KEY", "KILL", "LEFT",
        "LIKE", "LINENO", "LOAD", "MERGE", "NATIONAL", "NOCHECK", "NONCLUSTERED", "NOT", "NULL",
        "NULLIF", "OF", "OFF", "OFFSETS", "ON", "OPEN", "OPENDATASOURCE", "OPENQUERY",
        "OPENROWSET", "OPENXML", "OPTION", "OR", "ORDER", "OUTER", "OVER", "PERCENT", "PIVOT",
        "PLAN", "PRECISION", "PRIMARY", "PRINT", "PROC", "PROCEDURE", "PUBLIC", "RAISERROR",
        "READ", "READTEXT", "RECONFIGURE", "REFERENCES", "REPLICATION", "RESTORE", "RESTRICT",
        "RETURN", "REVERT", "REVOKE", "RIGHT", "ROLLBACK", "ROWCOUNT", "ROWGUIDCOL", "RULE",
        "SAVE", "SCHEMA", "SECURITYAUDIT", "SELECT", "SEMANTICKEYPHRASETABLE",
        "SEMANTICSIMILARITYDETAILSTABLE", "SEMANTICSIMILARITYTABLE", "SESSION_USER", "SET",
        "SETUSER", "SHUTDOWN", "SOME", "STATISTICS", "SYSTEM_USER", "TABLE", "TABLESAMPLE",
        "TEXTSIZE", "THEN", "TO", "TOP", "TRAN", "TRANSACTION", "TRIGGER", "TRUNCATE", "TRY_CONVERT",
        "TSEQUAL", "UNION", "UNIQUE", "UNPIVOT", "UPDATE", "UPDATETEXT", "USE", "USER", "VALUES",
        "VARYING", "VIEW", "WAITFOR", "WHEN", "WHERE", "WHILE", "WITH", "WITHIN GROUP", "WRITETEXT",
    };

    public static IReadOnlyList<NamingFinding> Scan(SqlParseResult parseResult, DatabaseCatalog? catalog = null)
    {
        var visitor = new Visitor(parseResult.SourcePath, catalog?.IdentifierComparer ?? StringComparer.OrdinalIgnoreCase);
        parseResult.Fragment.Accept(visitor);

        return
        [
            .. visitor.Findings
                .OrderBy(f => f.Kind)
                .ThenBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];
    }

    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    private sealed class Visitor : ScopedRelationWalker
    {
        private readonly string sourcePath;

        private readonly StringComparer identifierComparer;

        private string? _currentViewModule;

        public Visitor(string sourcePath, StringComparer identifierComparer)
            : base(sourcePath, new DatabaseCatalog(), EmptyResolvedViews, ledger: null, currentProcScope: null, callerScopeByCalleeScope: null)
        {
            this.sourcePath = sourcePath;
            this.identifierComparer = identifierComparer;
        }

        public List<NamingFinding> Findings { get; } = [];

        private string CurrentModule => _currentViewModule ?? CurrentProcScope ?? sourcePath;

        protected override void OnEnterProcedureOrFunctionBody(ProcedureStatementBodyBase node)
        {
            var (name, kindLabel) = node switch
            {
                CreateProcedureStatement p => (p.ProcedureReference.Name, "procedure"),
                AlterProcedureStatement p => (p.ProcedureReference.Name, "procedure"),
                CreateOrAlterProcedureStatement p => (p.ProcedureReference.Name, "procedure"),
                CreateFunctionStatement f => (f.Name, "function"),
                AlterFunctionStatement f => (f.Name, "function"),
                CreateOrAlterFunctionStatement f => (f.Name, "function"),
                _ => (null, null),
            };

            if (name is null)
            {
                return;
            }

            CheckRoutineName(name, kindLabel!, checkQualification: true);
            CheckParameters(node.Parameters);
        }

        protected override void OnEnterTriggerBody(TriggerStatementBody node) =>
            CheckReservedName(node.Name.BaseIdentifier, "trigger");

        public override void ExplicitVisit(CreateViewStatement node)
        {
            var previous = _currentViewModule;
            _currentViewModule = SchemaObjectNameHelper.Qualify(node.SchemaObjectName);
            CheckReservedName(node.SchemaObjectName.BaseIdentifier, "view");
            CheckQualification(node.SchemaObjectName, "view");
            base.ExplicitVisit(node);
            _currentViewModule = previous;
        }

        public override void ExplicitVisit(AlterViewStatement node)
        {
            var previous = _currentViewModule;
            _currentViewModule = SchemaObjectNameHelper.Qualify(node.SchemaObjectName);
            CheckReservedName(node.SchemaObjectName.BaseIdentifier, "view");
            CheckQualification(node.SchemaObjectName, "view");
            base.ExplicitVisit(node);
            _currentViewModule = previous;
        }

        public override void ExplicitVisit(CreateTableStatement node)
        {
            if (node.Definition is not null)
            {
                CheckReservedName(node.SchemaObjectName.BaseIdentifier, "table");
                foreach (var column in node.Definition.ColumnDefinitions)
                {
                    if (column.ColumnIdentifier is { } columnName)
                    {
                        CheckReservedName(columnName, "column");
                    }

                    CheckTypeQualifier(column.DataType);
                }
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CreateIndexStatement node)
        {
            CheckReservedName(node.Name, "index");
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(DeclareVariableStatement node)
        {
            foreach (var element in node.Declarations)
            {
                CheckTypeQualifier(element.DataType);
            }

            base.ExplicitVisit(node);
        }

        private void CheckParameters(IList<ProcedureParameter> parameters)
        {
            foreach (var parameter in parameters)
            {
                CheckTypeQualifier(parameter.DataType);
            }
        }

        private void CheckRoutineName(SchemaObjectName name, string kindLabel, bool checkQualification)
        {
            CheckReservedName(name.BaseIdentifier, kindLabel);
            if (checkQualification)
            {
                CheckQualification(name, kindLabel);
            }

            if (name.BaseIdentifier.Value.StartsWith("sp_", StringComparison.OrdinalIgnoreCase))
            {
                Findings.Add(new NamingFinding(
                    NamingFindingKind.SpPrefixOnUserRoutine, CurrentModule, sourcePath,
                    name.BaseIdentifier.StartLine, name.BaseIdentifier.StartColumn,
                    $"User-defined {kindLabel} \"{name.BaseIdentifier.Value}\" uses the \"sp_\" prefix reserved for system-shipped procedures."));
            }
        }

        private void CheckQualification(SchemaObjectName name, string kindLabel)
        {
            if (name.SchemaIdentifier is null)
            {
                Findings.Add(new NamingFinding(
                    NamingFindingKind.UnqualifiedCreate, CurrentModule, sourcePath,
                    name.BaseIdentifier.StartLine, name.BaseIdentifier.StartColumn,
                    $"{char.ToUpperInvariant(kindLabel[0])}{kindLabel[1..]} \"{name.BaseIdentifier.Value}\" is created with no explicit schema qualifier - its real owning schema depends on the connecting principal's own default schema."));
            }
        }

        private void CheckReservedName(Identifier? identifier, string kindLabel)
        {

            if (identifier is not { } id || !ReservedKeywords.Contains(id.Value))
            {
                return;
            }

            Findings.Add(new NamingFinding(
                NamingFindingKind.ReservedKeywordAsIdentifier, CurrentModule, sourcePath,
                id.StartLine, id.StartColumn,
                $"{char.ToUpperInvariant(kindLabel[0])}{kindLabel[1..]} name \"{id.Value}\" is a reserved T-SQL keyword."));
        }

        private void CheckTypeQualifier(DataTypeReference? dataType)
        {
            if (dataType is not UserDataTypeReference { Name.SchemaIdentifier: { } schema } userType)
            {
                return;
            }

            if (!identifierComparer.Equals(schema.Value, SchemaObjectNameHelper.DefaultSchema))
            {
                return;
            }

            Findings.Add(new NamingFinding(
                NamingFindingKind.RedundantTypeQualifier, CurrentModule, sourcePath,
                userType.StartLine, userType.StartColumn,
                $"Type reference \"{schema.Value}.{userType.Name.BaseIdentifier.Value}\" carries a redundant schema qualifier."));
        }
    }
}
