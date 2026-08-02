namespace SilentScan.Core.Reporting.Readable;

/// <summary>
/// The two human-facing renderings of a scan. Both are produced from the same
/// <see cref="ReadableDocument"/>, so a section that exists in one exists in the other - the
/// style only decides how a heading, a table or a bullet is drawn, never what is said. Keeping
/// that split is what stops the terminal view and the shareable markdown view from drifting
/// into two different accounts of the same run.
/// </summary>
public enum ReadableStyle
{
    /// <summary>Plain aligned text for a terminal or a pipe.</summary>
    Text,

    /// <summary>CommonMark, for a report file handed to someone else.</summary>
    Markdown,
}

/// <summary>
/// A rendering-independent report body: an ordered list of blocks that
/// <see cref="ReadableDocumentRenderer"/> draws in either <see cref="ReadableStyle"/>.
/// </summary>
public sealed record ReadableDocument(IReadOnlyList<ReadableBlock> Blocks);

/// <summary>One piece of a <see cref="ReadableDocument"/>.</summary>
public abstract record ReadableBlock
{
    private ReadableBlock()
    {
    }

    /// <summary>A section title. <paramref name="Level"/> is 1-based, as in markdown.</summary>
    public sealed record Heading(int Level, string Text) : ReadableBlock;

    /// <summary>A line or short paragraph of prose - typically why a section's findings matter.</summary>
    public sealed record Paragraph(string Text) : ReadableBlock;

    /// <summary>
    /// A table. Every row must have exactly as many cells as <paramref name="Headers"/>;
    /// <see cref="ReadableDocumentRenderer"/> depends on that to align text columns and to emit
    /// a well-formed markdown table.
    /// </summary>
    public sealed record Table(IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows) : ReadableBlock;

    /// <summary>An unordered list.</summary>
    public sealed record Bullets(IReadOnlyList<string> Items) : ReadableBlock;
}
