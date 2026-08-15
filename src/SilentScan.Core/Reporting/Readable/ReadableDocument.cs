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
/// How much detail a section about what the scan could NOT establish - a parse error, an
/// unresolvable dynamic SQL argument, a column whose type stayed ambiguous, a stale metadata
/// cache - renders at. Applies only to those coverage/caveat sections; a real finding (a forced
/// scan, a write-loss risk, a collation conflict, ...) is never gated by this, in either mode -
/// this tool exists to report those, not to make them optional. <see cref="Brief"/> is the
/// default: on a large real database these sections can run into the thousands of rows and bury
/// the findings that are actually the point of the report underneath them, so by default each
/// gated section states its count and nothing more granular. Nothing is ever silently dropped
/// either way (CLAUDE.md) - Brief still names every section and its exact count; it just leaves
/// the per-row detail to an explicit re-run with <see cref="Full"/> instead of printing it
/// unconditionally.
/// </summary>
public enum ReadableVerbosity
{
    /// <summary>State each coverage/caveat section's count only - no per-row detail.</summary>
    Brief,

    /// <summary>Every row, exactly as the JSON schema carries it.</summary>
    Full,
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
