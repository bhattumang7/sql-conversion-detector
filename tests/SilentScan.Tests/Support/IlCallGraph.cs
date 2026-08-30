using System.Reflection;
using System.Reflection.Emit;

namespace SilentScan.Tests.Support;

internal static class IlCallGraph
{
    private static readonly Dictionary<short, OpCode> OpCodesByValue = BuildOpCodeTable();

    public static bool Calls(MethodInfo method, string calleeMethodName)
    {
        var body = method.GetMethodBody();
        if (body is null)
        {
            return false;
        }

        var il = body.GetILAsByteArray()!;
        var module = method.Module;
        var genericTypeArguments = method.DeclaringType is { IsGenericType: true } declaringType ? declaringType.GetGenericArguments() : null;
        var genericMethodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;

        var offset = 0;
        while (offset < il.Length)
        {
            var (opCode, operandOffset) = ReadOpCode(il, offset);
            var operandLength = OperandLength(opCode, il, operandOffset);

            if (opCode.Value is 0x28 or 0x6F
                && TryResolveMethodName(module, BitConverter.ToInt32(il, operandOffset), genericTypeArguments, genericMethodArguments) == calleeMethodName)
            {
                return true;
            }

            offset = operandOffset + operandLength;
        }

        return false;
    }

    private static (OpCode OpCode, int OperandOffset) ReadOpCode(byte[] il, int offset)
    {
        var first = il[offset];
        if (first == 0xFE)
        {
            return (OpCodesByValue[unchecked((short)(0xFE00 | il[offset + 1]))], offset + 2);
        }

        return (OpCodesByValue[first], offset + 1);
    }

    private static int OperandLength(OpCode opCode, byte[] il, int operandOffset) => opCode.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI or OperandType.InlineMethod
            or OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType
            or OperandType.ShortInlineR => 4,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, operandOffset)),
        _ => throw new NotSupportedException($"Unhandled IL operand type '{opCode.OperandType}' for opcode '{opCode.Name}'"),
    };

    private static string? TryResolveMethodName(Module module, int token, Type[]? genericTypeArguments, Type[]? genericMethodArguments)
    {
        try
        {
            var resolved = genericTypeArguments is null && genericMethodArguments is null
                ? module.ResolveMethod(token)
                : module.ResolveMethod(token, genericTypeArguments, genericMethodArguments);
            return resolved?.Name;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Dictionary<short, OpCode> BuildOpCodeTable()
    {
        var table = new Dictionary<short, OpCode>();
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is OpCode opCode)
            {
                table[opCode.Value] = opCode;
            }
        }

        return table;
    }
}
