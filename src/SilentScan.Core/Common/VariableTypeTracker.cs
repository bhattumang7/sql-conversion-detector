using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Common;

internal sealed class VariableTypeTracker(DatabaseCatalog catalog)
{
    private readonly Dictionary<string, SqlType> _variableTypes = new(StringComparer.OrdinalIgnoreCase);

    public void Clear() => _variableTypes.Clear();

    public void TrackParameters(ProcedureStatementBodyBase node)
    {
        Clear();
        foreach (var parameter in node.Parameters)
        {
            Track(parameter.VariableName.Value, parameter.DataType);
        }
    }

    public void TrackDeclarations(DeclareVariableStatement node)
    {
        foreach (var declaration in node.Declarations)
        {
            Track(declaration.VariableName.Value, declaration.DataType);
        }
    }

    public bool TryGetValue(string variableName, out SqlType type) => _variableTypes.TryGetValue(variableName, out type!);

    private void Track(string variableName, DataTypeReference dataType)
    {
        var resolved = SqlTypeReferenceResolver.Resolve(dataType, columnCollation: null, catalog.TypeAliases);
        if (resolved is not null)
        {
            _variableTypes[variableName] = resolved;
        }
    }
}
