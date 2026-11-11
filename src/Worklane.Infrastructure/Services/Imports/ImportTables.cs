using Worklane.Application.Common.Exceptions;
using Worklane.Application.Imports;
using Worklane.Application.Imports.Dtos;

namespace Worklane.Infrastructure.Services.Imports;

internal readonly record struct ImportCell(string? Text, bool FormulaUncached);

internal sealed class ParsedRow
{
    public required int RowNumber { get; init; }
    public required IReadOnlyList<ImportCell> Cells { get; init; }
}

internal sealed class ParsedTable
{
    public required IReadOnlyList<string> Headers { get; init; }
    public required IReadOnlyList<ParsedRow> Rows { get; init; }
    public int SheetCount { get; init; } = 1;
}

internal sealed class ImportFormatException : Exception
{
    public ImportFormatException(string code, string detail, IReadOnlyDictionary<string, object?>? parameters = null)
        : base(detail)
    {
        Code = code;
        Parameters = parameters;
    }

    public string Code { get; }
    public IReadOnlyDictionary<string, object?>? Parameters { get; }

    public BadRequestException ToBadRequest() => new(Code, Message, Parameters);
}

internal static class ImportTables
{
    /// <summary>
    /// Preamble rows the Asana header scan may skip, plus the header, before data rows start.
    /// Readers refuse anything past this window so a sparse sheet cannot pad up to <see cref="int.MaxValue"/>.
    /// </summary>
    internal const int RowScanSlack = 11;

    internal static long MaxParsedRowNumber(int maxRows) => (long)maxRows + RowScanSlack;

    public static ParsedTable FromGrid(
        IReadOnlyList<IReadOnlyList<ImportCell>> grid,
        string preset,
        int maxRows,
        int maxColumns,
        int sheetCount)
    {
        if (grid.Count == 0)
            throw NoRows();

        var headerIndex = 0;
        if (string.Equals(preset, "asana", StringComparison.Ordinal))
        {
            var scan = Math.Min(10, grid.Count);
            for (var i = 0; i < scan; i++)
            {
                if (grid[i].Any(cell => string.Equals(cell.Text, "Task ID", StringComparison.OrdinalIgnoreCase)))
                {
                    headerIndex = i;
                    break;
                }
            }
        }

        var width = grid.Skip(headerIndex).Max(row => row.Count);
        if (width > maxColumns)
            throw ColumnLimit(maxColumns);
        if (width == 0)
            throw NoRows();

        var headers = new List<string>(width);
        var headerRow = grid[headerIndex];
        for (var i = 0; i < width; i++)
        {
            var text = i < headerRow.Count ? headerRow[i].Text : null;
            headers.Add(string.IsNullOrWhiteSpace(text) ? $"Column {i + 1}" : text.Trim());
        }

        RenameDuplicates(headers);

        var rows = new List<ParsedRow>();
        for (var i = headerIndex + 1; i < grid.Count; i++)
        {
            if (rows.Count == maxRows)
                throw RowLimit(maxRows);

            var source = grid[i];
            if (source.All(cell => cell.Text is null && !cell.FormulaUncached))
                continue;

            var cells = new ImportCell[width];
            for (var column = 0; column < width; column++)
                cells[column] = column < source.Count ? source[column] : default;
            rows.Add(new ParsedRow { RowNumber = rows.Count + 1, Cells = cells });
        }

        if (rows.Count == 0)
            throw NoRows();

        return new ParsedTable
        {
            Headers = headers,
            Rows = rows,
            SheetCount = sheetCount
        };
    }

    public static List<List<string?>> Sample(IReadOnlyList<ParsedRow> rows)
    {
        var sample = new List<List<string?>>();
        foreach (var row in rows.Take(5))
        {
            var cells = new List<string?>(row.Cells.Count);
            foreach (var cell in row.Cells)
                cells.Add(cell.Text is { Length: > 200 } text ? text[..200] : cell.Text);
            sample.Add(cells);
        }

        return sample;
    }

    private static void RenameDuplicates(List<string> headers)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < headers.Count; i++)
        {
            var name = headers[i];
            if (!seen.TryGetValue(name, out var count))
            {
                seen[name] = 1;
                continue;
            }

            count++;
            seen[name] = count;
            var renamed = $"{name} ({count})";
            while (seen.ContainsKey(renamed) || headers.Take(i).Contains(renamed, StringComparer.Ordinal))
            {
                count++;
                seen[name] = count;
                renamed = $"{name} ({count})";
            }

            headers[i] = renamed;
            seen[renamed] = 1;
        }
    }

    private static ImportFormatException NoRows() =>
        new(ImportErrors.NoRows, ImportErrors.NoRowsDetail);

    internal static ImportFormatException RowLimit(int maxRows) =>
        new(ImportErrors.RowLimit, ImportErrors.RowLimitDetail(maxRows), ErrorParams.Of(("maxRows", maxRows)));

    private static ImportFormatException ColumnLimit(int maxColumns) =>
        new(ImportErrors.ColumnLimit, ImportErrors.ColumnLimitDetail(maxColumns), ErrorParams.Of(("maxColumns", maxColumns)));
}
