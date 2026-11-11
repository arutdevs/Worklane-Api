using System.Text;
using Worklane.Application.Common.Exceptions;
using Worklane.Application.Imports;

namespace Worklane.Infrastructure.Services.Imports;

internal static class CsvImportReader
{
    public static IReadOnlyList<IReadOnlyList<ImportCell>> Read(string text, int maxColumns, int maxRows)
    {
        if (string.IsNullOrEmpty(text))
            throw new ImportFormatException(ImportErrors.NoRows, ImportErrors.NoRowsDetail);

        var firstLine = FirstLine(text);
        if (!firstLine.Contains(',') && firstLine.Contains(';'))
            throw new ImportFormatException(ImportErrors.DelimiterUnsupported, ImportErrors.DelimiterUnsupportedDetail);

        var rows = new List<IReadOnlyList<ImportCell>>();
        var field = new StringBuilder();
        var cells = new List<ImportCell>();
        var quoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(ch);
                }

                continue;
            }

            if (ch == '"')
            {
                quoted = true;
                continue;
            }

            if (ch == ',')
            {
                AddField(cells, field, maxColumns);
                continue;
            }

            if (ch is '\n' or '\r')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                AddField(cells, field, maxColumns);
                AddRow(rows, cells, maxRows);
                cells = [];
                continue;
            }

            field.Append(ch);
        }

        if (quoted)
            throw new ImportFormatException(ImportErrors.Unreadable, ImportErrors.UnreadableDetail);

        if (field.Length > 0 || cells.Count > 0)
        {
            AddField(cells, field, maxColumns);
            AddRow(rows, cells, maxRows);
        }

        if (rows.Count > 0 && rows[^1].All(cell => cell.Text is null))
            rows.RemoveAt(rows.Count - 1);

        return rows;
    }

    private static void AddRow(List<IReadOnlyList<ImportCell>> rows, List<ImportCell> cells, int maxRows)
    {
        rows.Add(cells);
        if (rows.Count > ImportTables.MaxParsedRowNumber(maxRows))
            throw ImportTables.RowLimit(maxRows);
    }

    private static void AddField(List<ImportCell> cells, StringBuilder field, int maxColumns)
    {
        cells.Add(Cell(field));
        if (cells.Count > maxColumns)
        {
            throw new ImportFormatException(
                ImportErrors.ColumnLimit,
                ImportErrors.ColumnLimitDetail(maxColumns),
                ErrorParams.Of(("maxColumns", maxColumns)));
        }
    }

    private static string FirstLine(string text)
    {
        var end = text.IndexOfAny(['\n', '\r']);
        return end < 0 ? text : text[..end];
    }

    private static ImportCell Cell(StringBuilder field)
    {
        var text = field.ToString().Trim();
        field.Clear();
        return new ImportCell(text.Length == 0 ? null : text, false);
    }
}
