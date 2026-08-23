namespace SilentScan.Core.Reporting.Readable;

public enum ReadableStyle
{
Text,

Markdown,
}

public enum ReadableVerbosity
{
Brief,

Full,
}

public sealed record ReadableDocument(IReadOnlyList<ReadableBlock> Blocks);

public abstract record ReadableBlock
{
    private ReadableBlock()
    {
    }

public sealed record Heading(int Level, string Text) : ReadableBlock;

public sealed record Paragraph(string Text) : ReadableBlock;

public sealed record Table(IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows) : ReadableBlock;

public sealed record Bullets(IReadOnlyList<string> Items) : ReadableBlock;
}
