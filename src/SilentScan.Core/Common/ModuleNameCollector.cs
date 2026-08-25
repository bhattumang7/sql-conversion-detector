using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SilentScan.Core.Common;

public sealed class ModuleNameCollector : TSqlFragmentVisitor
{
    public List<string> Names { get; } = [];

    public override void Visit(CreateProcedureStatement node) => Names.Add(SchemaObjectNameHelper.Qualify(node.ProcedureReference.Name));

    public override void Visit(AlterProcedureStatement node) => Names.Add(SchemaObjectNameHelper.Qualify(node.ProcedureReference.Name));

    public override void Visit(CreateOrAlterProcedureStatement node) => Names.Add(SchemaObjectNameHelper.Qualify(node.ProcedureReference.Name));

    public override void Visit(CreateViewStatement node) => Names.Add(SchemaObjectNameHelper.Qualify(node.SchemaObjectName));

    public override void Visit(AlterViewStatement node) => Names.Add(SchemaObjectNameHelper.Qualify(node.SchemaObjectName));

    public override void Visit(CreateOrAlterViewStatement node) => Names.Add(SchemaObjectNameHelper.Qualify(node.SchemaObjectName));

    public override void Visit(CreateFunctionStatement node) => Names.Add(SchemaObjectNameHelper.Qualify(node.Name));

    public override void Visit(AlterFunctionStatement node) => Names.Add(SchemaObjectNameHelper.Qualify(node.Name));

    public override void Visit(CreateOrAlterFunctionStatement node) => Names.Add(SchemaObjectNameHelper.Qualify(node.Name));

    public override void Visit(CreateTriggerStatement node) => Names.Add(SchemaObjectNameHelper.Qualify(node.Name));

    public override void Visit(AlterTriggerStatement node) => Names.Add(SchemaObjectNameHelper.Qualify(node.Name));

    public override void Visit(CreateOrAlterTriggerStatement node) => Names.Add(SchemaObjectNameHelper.Qualify(node.Name));
}
