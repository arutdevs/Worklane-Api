using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Worklane.Application.Imports;

namespace Worklane.Infrastructure.Services.Imports;

internal static class XlsxImportReader
{
    public const int MaxEntries = 64;

    public static (IReadOnlyList<IReadOnlyList<ImportCell>> Rows, int SheetCount) Read(
        byte[] bytes,
        long maxUncompressed,
        int maxColumns,
        int maxRows)
    {
        try
        {
            using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Count > MaxEntries)
                throw Unreadable();

            long declared = 0;
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.Contains("..", StringComparison.Ordinal))
                    throw Unreadable();
                if (entry.Length < 0 || entry.Length > maxUncompressed)
                    throw Unreadable();
                declared += entry.Length;
                if (declared > maxUncompressed)
                    throw Unreadable();
            }

            var read = 0L;
            var workbook = Load(archive, "xl/workbook.xml", ref read, maxUncompressed)
                ?? throw Unreadable();
            var rels = Load(archive, "xl/_rels/workbook.xml.rels", ref read, maxUncompressed)
                ?? throw Unreadable();

            var sheets = workbook.Descendants().Where(element => element.Name.LocalName == "sheet").ToList();
            if (sheets.Count == 0)
                throw Unreadable();

            var firstId = sheets[0].Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "id")?.Value
                ?? throw Unreadable();
            var target = rels.Descendants()
                .Where(element => element.Name.LocalName == "Relationship")
                .FirstOrDefault(element => element.Attribute("Id")?.Value == firstId)
                ?.Attribute("Target")?.Value ?? throw Unreadable();
            if (target.Contains("..", StringComparison.Ordinal))
                throw Unreadable();

            var sheetPath = target.Replace('\\', '/').TrimStart('/');
            if (!sheetPath.StartsWith("xl/", StringComparison.OrdinalIgnoreCase))
                sheetPath = "xl/" + sheetPath;

            var sheet = Load(archive, sheetPath, ref read, maxUncompressed) ?? throw Unreadable();
            var shared = LoadOptional(archive, "xl/sharedStrings.xml", ref read, maxUncompressed);
            var styles = LoadOptional(archive, "xl/styles.xml", ref read, maxUncompressed);
            var strings = shared is null ? [] : ReadSharedStrings(shared);
            var dates = styles is null ? [] : ReadDateStyles(styles);

            return (ReadSheet(sheet, strings, dates, maxColumns, maxRows), sheets.Count);
        }
        catch (ImportFormatException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or IOException)
        {
            throw Unreadable();
        }
    }

    private static List<IReadOnlyList<ImportCell>> ReadSheet(
        XDocument sheet,
        IReadOnlyList<string> shared,
        HashSet<int> dateStyles,
        int maxColumns,
        int maxRows)
    {
        var limit = ImportTables.MaxParsedRowNumber(maxRows);
        var byRow = new SortedDictionary<int, SortedDictionary<int, ImportCell>>();
        foreach (var row in sheet.Descendants().Where(element => element.Name.LocalName == "row"))
        {
            var rowNumber = ParseInt(row.Attribute("r")?.Value) ?? (byRow.Count == 0 ? 1 : byRow.Keys.Max() + 1);
            if (rowNumber < 1 || rowNumber > limit || (!byRow.ContainsKey(rowNumber) && byRow.Count >= limit))
                throw ImportTables.RowLimit(maxRows);
            if (!byRow.TryGetValue(rowNumber, out var cells))
            {
                cells = [];
                byRow[rowNumber] = cells;
            }

            foreach (var cell in row.Elements().Where(element => element.Name.LocalName == "c"))
            {
                var reference = cell.Attribute("r")?.Value ?? "";
                var column = ColumnIndex(reference);
                if (column < 0)
                    column = cells.Count == 0 ? 0 : cells.Keys.Max() + 1;
                if (column + 1 > maxColumns)
                {
                    throw new ImportFormatException(
                        ImportErrors.ColumnLimit,
                        ImportErrors.ColumnLimitDetail(maxColumns),
                        Application.Common.Exceptions.ErrorParams.Of(("maxColumns", maxColumns)));
                }

                cells[column] = ReadCell(cell, shared, dateStyles);
            }
        }

        var grid = new List<IReadOnlyList<ImportCell>>();
        if (byRow.Count == 0)
            return grid;

        var first = byRow.Keys.Min();
        var last = byRow.Keys.Max();
        if (first < 1 || last > limit)
            throw ImportTables.RowLimit(maxRows);
        var width = byRow.Values.SelectMany(cells => cells.Keys).DefaultIfEmpty(-1).Max() + 1;
        for (var rowNumber = first; rowNumber <= last; rowNumber++)
        {
            if (!byRow.TryGetValue(rowNumber, out var cells) || cells.Count == 0)
            {
                grid.Add([]);
                continue;
            }

            var line = new ImportCell[width];
            foreach (var (column, cell) in cells)
            {
                if (column >= 0 && column < width)
                    line[column] = cell;
            }

            grid.Add(line);
        }

        while (grid.Count > 0 && grid[^1].All(cell => cell.Text is null && !cell.FormulaUncached))
            grid.RemoveAt(grid.Count - 1);

        return grid;
    }

    private static ImportCell ReadCell(XElement cell, IReadOnlyList<string> shared, HashSet<int> dateStyles)
    {
        var formula = cell.Elements().FirstOrDefault(element => element.Name.LocalName == "f");
        var value = cell.Elements().FirstOrDefault(element => element.Name.LocalName == "v")?.Value;
        var type = cell.Attribute("t")?.Value;
        if (formula is not null && string.IsNullOrEmpty(value) && type != "inlineStr")
            return new ImportCell(null, true);

        string? text = type switch
        {
            "s" => Shared(value, shared),
            "inlineStr" => string.Concat(cell.Descendants().Where(element => element.Name.LocalName == "t").Select(element => element.Value)),
            "b" => value == "1" ? "true" : "false",
            "str" => value,
            _ => value
        };

        if ((type is null or "n") && text is not null && int.TryParse(cell.Attribute("s")?.Value, out var style) && dateStyles.Contains(style))
            text = FormatSerial(text);

        text = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        return new ImportCell(text, false);
    }

    private static string? Shared(string? index, IReadOnlyList<string> shared)
    {
        if (!int.TryParse(index, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return null;
        return parsed >= 0 && parsed < shared.Count ? shared[parsed] : null;
    }

    private static string FormatSerial(string text)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial))
            return text;
        // FromOADate throws ArgumentException below -657435 or at/above 2958466, which would leave the request as 500.
        if (!double.IsFinite(serial) || serial < -657435d || serial >= 2958466d)
            throw Unreadable();
        var date = DateTime.SpecifyKind(DateTime.FromOADate(serial), DateTimeKind.Utc);
        return date.TimeOfDay == TimeSpan.Zero
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : date.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }

    private static List<string> ReadSharedStrings(XDocument document)
    {
        var strings = new List<string>();
        foreach (var item in document.Descendants().Where(element => element.Name.LocalName == "si"))
        {
            var text = string.Concat(item.Descendants().Where(element => element.Name.LocalName == "t").Select(element => element.Value));
            strings.Add(text.Trim());
        }

        return strings;
    }

    private static HashSet<int> ReadDateStyles(XDocument document)
    {
        var custom = new Dictionary<int, string>();
        foreach (var format in document.Descendants().Where(element => element.Name.LocalName == "numFmt"))
        {
            if (int.TryParse(format.Attribute("numFmtId")?.Value, out var id))
                custom[id] = format.Attribute("formatCode")?.Value ?? "";
        }

        var dates = new HashSet<int>();
        var index = 0;
        var cellXfs = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "cellXfs");
        if (cellXfs is null)
            return dates;

        foreach (var xf in cellXfs.Elements().Where(element => element.Name.LocalName == "xf"))
        {
            if (int.TryParse(xf.Attribute("numFmtId")?.Value, out var formatId) && IsDate(formatId, custom))
                dates.Add(index);
            index++;
        }

        return dates;
    }

    private static bool IsDate(int formatId, Dictionary<int, string> custom)
    {
        if (formatId is >= 14 and <= 22 or >= 45 and <= 47)
            return true;
        if (!custom.TryGetValue(formatId, out var code))
            return false;

        var stripped = StripFormat(code);
        return stripped.Contains('y') || stripped.Contains('d') || stripped.Contains('h') || stripped.Contains('s') || stripped.Contains('m');
    }

    private static string StripFormat(string format)
    {
        var builder = new StringBuilder(format.Length);
        var quoted = false;
        for (var i = 0; i < format.Length; i++)
        {
            var ch = format[i];
            if (ch == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (quoted)
                continue;
            if (ch == '[')
            {
                var end = format.IndexOf(']', i);
                if (end < 0)
                    break;
                i = end;
                continue;
            }

            if (ch == '\\' && i + 1 < format.Length)
            {
                i++;
                continue;
            }

            builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString();
    }

    private static int ColumnIndex(string reference)
    {
        var index = 0;
        var seen = false;
        foreach (var ch in reference)
        {
            if (!char.IsLetter(ch))
                break;
            seen = true;
            index = index * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
        }

        return seen ? index - 1 : -1;
    }

    private static int? ParseInt(string? text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static XDocument? Load(ZipArchive archive, string name, ref long read, long max)
    {
        var entry = archive.GetEntry(name) ?? archive.Entries.FirstOrDefault(candidate =>
            string.Equals(candidate.FullName.Replace('\\', '/'), name, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            return null;
        return Load(entry, ref read, max);
    }

    private static XDocument? LoadOptional(ZipArchive archive, string name, ref long read, long max)
    {
        var entry = archive.GetEntry(name);
        return entry is null ? null : Load(entry, ref read, max);
    }

    private static XDocument Load(ZipArchiveEntry entry, ref long read, long max)
    {
        using var input = entry.Open();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int count;
        while ((count = input.Read(chunk, 0, chunk.Length)) > 0)
        {
            read += count;
            if (read > max)
                throw Unreadable();
            buffer.Write(chunk, 0, count);
        }

        buffer.Position = 0;
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 1024
        };
        using var reader = XmlReader.Create(buffer, settings);
        return XDocument.Load(reader);
    }

    private static ImportFormatException Unreadable() =>
        new(ImportErrors.Unreadable, ImportErrors.UnreadableDetail);
}
