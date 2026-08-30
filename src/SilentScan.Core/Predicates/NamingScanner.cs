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
        var rule = CreateRule(parseResult.SourcePath, catalog);
        var walker = new ModuleWalker(parseResult.SourcePath, new DatabaseCatalog(), EmptyResolvedViews, ledger: null, currentProcScope: null, callerScopeByCalleeScope: null, rules: [rule]);
        parseResult.Fragment.Accept(walker);

    return Harvest(rule);
    }
    internal static Rule CreateRule(string sourcePath, DatabaseCatalog? catalog = null) => new(sourcePath, catalog?.IdentifierComparer ?? StringComparer.OrdinalIgnoreCase);

    internal static IReadOnlyList<NamingFinding> Harvest(Rule rule) =>
            [
            .. rule.Findings
                .OrderBy(f => f.Kind)
                .ThenBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];


    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    internal sealed class Rule(string sourcePath, StringComparer identifierComparer) : IModuleRule
    {
        private string? _currentViewModule;

        public List<NamingFinding> Findings { get; } = [];

        private string CurrentModule(ModuleWalker walker) => _currentViewModule ?? walker.CurrentProcScope ?? sourcePath;

        public void OnEnterProcedureOrFunctionBody(ProcedureStatementBodyBase node, ModuleWalker walker)
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

            CheckRoutineName(name, kindLabel!, checkQualification: true, walker);
            CheckParameters(node.Parameters, walker);
        }

        public void OnEnterTriggerBody(TriggerStatementBody node, ModuleWalker walker) =>
            CheckReservedName(node.Name.BaseIdentifier, "trigger", walker);

        public void OnEnterCreateViewStatement(CreateViewStatement node, ModuleWalker walker)
        {
            _currentViewModule = SchemaObjectNameHelper.Qualify(node.SchemaObjectName);
            CheckReservedName(node.SchemaObjectName.BaseIdentifier, "view", walker);
            CheckQualification(node.SchemaObjectName, "view", walker);
        }

        public void OnLeaveCreateViewStatement(CreateViewStatement node, ModuleWalker walker) => _currentViewModule = null;

        public void OnEnterAlterViewStatement(AlterViewStatement node, ModuleWalker walker)
        {
            _currentViewModule = SchemaObjectNameHelper.Qualify(node.SchemaObjectName);
            CheckReservedName(node.SchemaObjectName.BaseIdentifier, "view", walker);
            CheckQualification(node.SchemaObjectName, "view", walker);
        }

        public void OnLeaveAlterViewStatement(AlterViewStatement node, ModuleWalker walker) => _currentViewModule = null;

        public void OnEnterCreateTableStatement(CreateTableStatement node, ModuleWalker walker)
        {
            if (node.Definition is not null)
            {
                CheckReservedName(node.SchemaObjectName.BaseIdentifier, "table", walker);
                foreach (var column in node.Definition.ColumnDefinitions)
                {
                    if (column.ColumnIdentifier is { } columnName)
                    {
                        CheckReservedName(columnName, "column", walker);
                    }

                    CheckTypeQualifier(column.DataType, walker);
                }
            }
        }

        public void OnEnterCreateIndexStatement(CreateIndexStatement node, ModuleWalker walker) =>
            CheckReservedName(node.Name, "index", walker);

        public void OnEnterDeclareVariableStatement(DeclareVariableStatement node, ModuleWalker walker)
        {
            foreach (var element in node.Declarations)
            {
                CheckTypeQualifier(element.DataType, walker);
            }
        }

        private void CheckParameters(IList<ProcedureParameter> parameters, ModuleWalker walker)
        {
            foreach (var parameter in parameters)
            {
                CheckTypeQualifier(parameter.DataType, walker);
            }
        }

        private void CheckRoutineName(SchemaObjectName name, string kindLabel, bool checkQualification, ModuleWalker walker)
        {
            CheckReservedName(name.BaseIdentifier, kindLabel, walker);
            if (checkQualification)
            {
                CheckQualification(name, kindLabel, walker);
            }

            if (name.BaseIdentifier.Value.StartsWith("sp_", StringComparison.OrdinalIgnoreCase))
            {
                Findings.Add(new NamingFinding(
                    NamingFindingKind.SpPrefixOnUserRoutine, CurrentModule(walker), sourcePath,
                    name.BaseIdentifier.StartLine, name.BaseIdentifier.StartColumn,
                    $"User-defined {kindLabel} \"{name.BaseIdentifier.Value}\" uses the \"sp_\" prefix reserved for system-shipped procedures."));
            }
        }

        private void CheckQualification(SchemaObjectName name, string kindLabel, ModuleWalker walker)
        {
            if (name.SchemaIdentifier is null)
            {
                Findings.Add(new NamingFinding(
                    NamingFindingKind.UnqualifiedCreate, CurrentModule(walker), sourcePath,
                    name.BaseIdentifier.StartLine, name.BaseIdentifier.StartColumn,
                    $"{char.ToUpperInvariant(kindLabel[0])}{kindLabel[1..]} \"{name.BaseIdentifier.Value}\" is created with no explicit schema qualifier - its real owning schema depends on the connecting principal's own default schema."));
            }
        }

        private void CheckReservedName(Identifier? identifier, string kindLabel, ModuleWalker walker)
        {

            if (identifier is not { } id || !ReservedKeywords.Contains(id.Value))
            {
                return;
            }

            Findings.Add(new NamingFinding(
                NamingFindingKind.ReservedKeywordAsIdentifier, CurrentModule(walker), sourcePath,
                id.StartLine, id.StartColumn,
                $"{char.ToUpperInvariant(kindLabel[0])}{kindLabel[1..]} name \"{id.Value}\" is a reserved T-SQL keyword."));
        }

        private void CheckTypeQualifier(DataTypeReference? dataType, ModuleWalker walker)
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
                NamingFindingKind.RedundantTypeQualifier, CurrentModule(walker), sourcePath,
                userType.StartLine, userType.StartColumn,
                $"Type reference \"{schema.Value}.{userType.Name.BaseIdentifier.Value}\" carries a redundant schema qualifier."));
        }
    }
}
