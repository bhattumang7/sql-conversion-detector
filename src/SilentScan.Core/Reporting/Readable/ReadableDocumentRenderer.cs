using System.Globalization;
using System.Text;

namespace SilentScan.Core.Reporting.Readable;

/// <summary>
/// Draws a <see cref="ReadableDocument"/> as terminal text or as markdown. This is the only
/// place either syntax is written, so every report the tool emits (file scan, live scan,
/// corpus) is laid out the same way for free.
/// </summary>
public static class ReadableDocumentRenderer
{
    public static string Render(ReadableDocument document, ReadableStyle style)
    {
        ArgumentNullException.ThrowIfNull(document);

        var builder = new StringBuilder();
        var first = true;

        foreach (var block in document.Blocks)
        {
            if (!first)
            {
                builder.AppendLine();
            }

            first = false;

            switch (block)
            {
                case ReadableBlock.Heading heading:
                    AppendHeading(builder, heading, style);
                    break;
                case ReadableBlock.Paragraph paragraph:
                    builder.AppendLine(paragraph.Text);
                    break;
                case ReadableBlock.Table table:
                    AppendTable(builder, table, style);
                    break;
                case ReadableBlock.Bullets bullets:
                    AppendBullets(builder, bullets, style);
                    break;
            }
        }

        return builder.ToString();
    }

    private static void AppendHeading(StringBuilder builder, ReadableBlock.Heading heading, ReadableStyle style)
    {
        if (style == ReadableStyle.Markdown)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"{new string('#', Math.Clamp(heading.Level, 1, 6))} {heading.Text}");
            return;
        }

        builder.AppendLine(heading.Text);

        // Only the two outermost levels get a rule under them; deeper ones would turn a report
        // with a section per finding kind into more underline than content.
        var rule = heading.Level switch
        {
            1 => '=',
            2 => '-',
            _ => '\0',
        };

        if (rule != '\0')
        {
            builder.AppendLine(new string(rule, heading.Text.Length));
        }
    }

    private static void AppendTable(StringBuilder builder, ReadableBlock.Table table, ReadableStyle style)
    {
        if (style == ReadableStyle.Markdown)
        {
            AppendMarkdownTable(builder, table);
            return;
        }

        AppendTextTable(builder, table);
    }

    private static void AppendMarkdownTable(StringBuilder builder, ReadableBlock.Table table)
    {
        builder.AppendLine(CultureInfo.InvariantCulture, $"| {string.Join(" | ", table.Headers.Select(EscapeMarkdownCell))} |");
        builder.AppendLine(CultureInfo.InvariantCulture, $"| {string.Join(" | ", table.Headers.Select(_ => "---"))} |");

        foreach (var row in table.Rows)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"| {string.Join(" | ", row.Select(EscapeMarkdownCell))} |");
        }
    }

    private static void AppendTextTable(StringBuilder builder, ReadableBlock.Table table)
    {
        var widths = new int[table.Headers.Count];
        for (var i = 0; i < table.Headers.Count; i++)
        {
            widths[i] = table.Headers[i].Length;
        }

        foreach (var row in table.Rows)
        {
            for (var i = 0; i < row.Count && i < widths.Length; i++)
            {
                widths[i] = Math.Max(widths[i], row[i].Length);
            }
        }

        AppendTextRow(builder, table.Headers, widths);
        AppendTextRow(builder, [.. widths.Select(w => new string('-', w))], widths);

        foreach (var row in table.Rows)
        {
            AppendTextRow(builder, row, widths);
        }
    }

    private static void AppendTextRow(StringBuilder builder, IReadOnlyList<string> cells, int[] widths)
    {
        builder.Append("  ");

        for (var i = 0; i < cells.Count; i++)
        {
            // The last cell is never padded: trailing spaces are invisible in a terminal but
            // show up as diff noise the moment someone redirects the report to a file.
            builder.Append(i == cells.Count - 1 ? cells[i] : cells[i].PadRight(widths[i]));

            if (i != cells.Count - 1)
            {
                builder.Append("  ");
            }
        }

        builder.AppendLine();
    }

    private static void AppendBullets(StringBuilder builder, ReadableBlock.Bullets bullets, ReadableStyle style)
    {
        var prefix = style == ReadableStyle.Markdown ? "- " : "  - ";

        foreach (var item in bullets.Items)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"{prefix}{item}");
        }
    }

    /// <summary>
    /// Keeps a cell inside its markdown table row. A pipe would end the cell early and a newline
    /// would end the row - both silently shift every later cell into the wrong column, which is
    /// worse than an ugly cell because the table still renders and just says something false.
    /// </summary>
    private static string EscapeMarkdownCell(string cell) =>
        cell.Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal);
}
