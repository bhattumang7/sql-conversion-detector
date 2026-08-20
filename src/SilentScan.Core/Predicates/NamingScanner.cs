using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Common;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 4 "Naming and identifiers" plus the standalone "redundant
/// database/schema qualifier" bullet. Fully syntax-only: no <see cref="DatabaseCatalog"/>, no oracle.
/// </summary>
public static class NamingScanner
{
    // The official T-SQL reserved keyword list (Microsoft Learn, "Reserved Keywords (Transact-SQL)")
    // - stable, publicly documented, unrelated to any third-party tool. An identifier spelled
    // identically to one of these needs bracket/quote delimiting on every future reference.
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

    public static IReadOnlyList<NamingFinding> Scan(SqlParseResult parseResult)
    {
        var visitor = new Visitor(parseResult.SourcePath);
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

    private sealed class Visitor : TSqlFragmentVisitor
    {
        private readonly string sourcePath;

        public Visitor(string sourcePath)
        {
            this.sourcePath = sourcePath;
            _currentModule = sourcePath;
        }

        public List<NamingFinding> Findings { get; } = [];

        private string _currentModule;

        public override void ExplicitVisit(CreateProcedureStatement node)
        {
            _currentModule = SchemaObjectNameHelper.Qualify(node.ProcedureReference.Name);
            CheckRoutineName(node.ProcedureReference.Name, "procedure", checkQualification: true);
            CheckParameters(node.Parameters);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(AlterProcedureStatement node)
        {
            _currentModule = SchemaObjectNameHelper.Qualify(node.ProcedureReference.Name);
            CheckRoutineName(node.ProcedureReference.Name, "procedure", checkQualification: true);
            CheckParameters(node.Parameters);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CreateFunctionStatement node)
        {
            _currentModule = SchemaObjectNameHelper.Qualify(node.Name);
            CheckRoutineName(node.Name, "function", checkQualification: true);
            CheckParameters(node.Parameters);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(AlterFunctionStatement node)
        {
            _currentModule = SchemaObjectNameHelper.Qualify(node.Name);
            CheckRoutineName(node.Name, "function", checkQualification: true);
            CheckParameters(node.Parameters);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CreateViewStatement node)
        {
            _currentModule = SchemaObjectNameHelper.Qualify(node.SchemaObjectName);
            CheckReservedName(node.SchemaObjectName.BaseIdentifier, "view");
            CheckQualification(node.SchemaObjectName, "view");
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(AlterViewStatement node)
        {
            _currentModule = SchemaObjectNameHelper.Qualify(node.SchemaObjectName);
            CheckReservedName(node.SchemaObjectName.BaseIdentifier, "view");
            CheckQualification(node.SchemaObjectName, "view");
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CreateTriggerStatement node)
        {
            _currentModule = SchemaObjectNameHelper.Qualify(node.Name);
            CheckReservedName(node.Name.BaseIdentifier, "trigger");
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(AlterTriggerStatement node)
        {
            _currentModule = SchemaObjectNameHelper.Qualify(node.Name);
            CheckReservedName(node.Name.BaseIdentifier, "trigger");
            base.ExplicitVisit(node);
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
                    NamingFindingKind.SpPrefixOnUserRoutine, _currentModule, sourcePath,
                    name.BaseIdentifier.StartLine, name.BaseIdentifier.StartColumn,
                    $"User-defined {kindLabel} \"{name.BaseIdentifier.Value}\" uses the \"sp_\" prefix reserved for system-shipped procedures."));
            }
        }

        private void CheckQualification(SchemaObjectName name, string kindLabel)
        {
            if (name.SchemaIdentifier is null)
            {
                Findings.Add(new NamingFinding(
                    NamingFindingKind.UnqualifiedCreate, _currentModule, sourcePath,
                    name.BaseIdentifier.StartLine, name.BaseIdentifier.StartColumn,
                    $"{char.ToUpperInvariant(kindLabel[0])}{kindLabel[1..]} \"{name.BaseIdentifier.Value}\" is created with no explicit schema qualifier - its real owning schema depends on the connecting principal's own default schema."));
            }
        }

        private void CheckReservedName(Identifier? identifier, string kindLabel)
        {
            // No QuoteType condition: T-SQL's own grammar already refuses to parse a true reserved
            // keyword as an UNQUOTED identifier at all (a script using one that way would already
            // have failed to parse, never reaching this scanner) - the only way a reserved keyword
            // ever becomes a real identifier is via bracket/quote delimiting, which is exactly the
            // case this rule exists to flag as a latent maintainability risk.
            if (identifier is not { } id || !ReservedKeywords.Contains(id.Value))
            {
                return;
            }

            Findings.Add(new NamingFinding(
                NamingFindingKind.ReservedKeywordAsIdentifier, _currentModule, sourcePath,
                id.StartLine, id.StartColumn,
                $"{char.ToUpperInvariant(kindLabel[0])}{kindLabel[1..]} name \"{id.Value}\" is a reserved T-SQL keyword."));
        }

        // Only an explicit "dbo." qualifier is flagged - the overwhelmingly common default schema
        // and the one this codebase's own SchemaObjectNameHelper.DefaultSchema already treats as
        // the baseline everywhere else. A qualifier naming any OTHER schema is left alone: whether
        // it is redundant depends on the connecting principal's own actual default schema, which
        // this static, no-catalog pass cannot know - flagging it would risk a real false positive
        // in a multi-schema database, so only the one case provably safe without that knowledge ships.
        private void CheckTypeQualifier(DataTypeReference? dataType)
        {
            if (dataType is not UserDataTypeReference { Name.SchemaIdentifier: { } schema } userType)
            {
                return;
            }

            if (!string.Equals(schema.Value, SchemaObjectNameHelper.DefaultSchema, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Findings.Add(new NamingFinding(
                NamingFindingKind.RedundantTypeQualifier, _currentModule, sourcePath,
                userType.StartLine, userType.StartColumn,
                $"Type reference \"{schema.Value}.{userType.Name.BaseIdentifier.Value}\" carries a redundant schema qualifier."));
        }
    }
}
