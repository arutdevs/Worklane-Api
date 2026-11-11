using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Worklane.IntegrationTests.Infrastructure;

namespace Worklane.IntegrationTests.Imports;

internal static class ImportHttp
{
    public static string SimpleCsv(string title, string externalId, string? email = null) =>
        "Title,External Id,Description,Column,Assignee Email,Priority,Due Date,Estimate Days,Parent External Id\n"
        + title + "," + externalId + ",,To Do," + email + ",,,,\n";

    public static async Task<HttpResponseMessage> UploadAsync(
        HttpClient client,
        Guid projectId,
        string fileName,
        string csv,
        string preset = "generic",
        string? contentType = null) =>
        await UploadBytesAsync(client, projectId, fileName, Encoding.UTF8.GetBytes(csv), preset, contentType);

    public static async Task<HttpResponseMessage> UploadBytesAsync(
        HttpClient client,
        Guid projectId,
        string fileName,
        byte[] bytes,
        string preset,
        string? contentType = null)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType ?? "application/octet-stream");
        form.Add(file, "file", fileName);
        form.Add(new StringContent(preset), "preset");
        return await client.PostAsync($"/api/projects/{projectId}/imports", form);
    }

    public static async Task<Guid> PrepareAsync(BoardContext board, string csv, string preset)
    {
        var uploaded = await UploadAsync(board.Client, board.Project.Id, "import.csv", csv, preset);
        var body = await uploaded.Content.ReadAsStringAsync();
        if (!uploaded.IsSuccessStatusCode)
            throw new InvalidOperationException(body);
        var batch = JsonDocument.Parse(body).RootElement;
        var batchId = batch.GetProperty("id").GetGuid();
        var assignments = JsonSerializer.Deserialize<JsonElement>(batch.GetProperty("suggestedMapping").GetProperty("assignments").GetRawText());
        var mapped = await board.Client.PutAsJsonAsync(
            $"/api/projects/{board.Project.Id}/imports/{batchId}/mapping",
            new { fallbackColumnId = board.Todo.Id, assignments });
        var mappedBody = await mapped.Content.ReadAsStringAsync();
        if (!mapped.IsSuccessStatusCode)
            throw new InvalidOperationException(mappedBody);
        return batchId;
    }

    public static async Task<JsonElement> CommitAsync(BoardContext board, string csv, string preset)
    {
        var batchId = await PrepareAsync(board, csv, preset);
        var response = await board.Client.PostAsJsonAsync(
            $"/api/projects/{board.Project.Id}/imports/{batchId}/commit",
            new { skipInvalidRows = false });
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    public static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    public static byte[] ZipWithControlByte()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("a.txt");
            using var writer = new BinaryWriter(entry.Open());
            writer.Write(new byte[] { 0x03, 0x00, 0x41 });
        }

        return buffer.ToArray();
    }

    public static byte[] SimpleXlsx(string[] headers, string[][] rows)
    {
        var sheet = new XElement(XName.Get("worksheet", Ns),
            new XElement(XName.Get("sheetData", Ns),
                Row(1, headers),
                rows.Select((cells, index) => Row(index + 2, cells))));
        return Pack(sheet, styles: null);
    }

    public static byte[] XlsxWithDataRow(int dataRowNumber)
    {
        var sheet = new XElement(XName.Get("worksheet", Ns),
            new XElement(XName.Get("sheetData", Ns),
                Row(1, new[] { "Title", "External Id" }),
                Row(dataRowNumber, new[] { "Card", "X-1" })));
        return Pack(sheet, styles: null);
    }

    public static byte[] XlsxWithDateSerial(string serial)
    {
        var header = Row(1, new[] { "Title", "External Id" });
        var data = new XElement(XName.Get("row", Ns), new XAttribute("r", 2),
            InlineCell("A", 2, "Card"),
            new XElement(XName.Get("c", Ns),
                new XAttribute("r", "B2"),
                new XAttribute("s", "0"),
                new XElement(XName.Get("v", Ns), serial)));
        var sheet = new XElement(XName.Get("worksheet", Ns),
            new XElement(XName.Get("sheetData", Ns), header, data));
        var styles = new XElement(XName.Get("styleSheet", Ns),
            new XElement(XName.Get("cellXfs", Ns),
                new XElement(XName.Get("xf", Ns), new XAttribute("numFmtId", "14"))));
        return Pack(sheet, styles);
    }

    private static byte[] Pack(XElement sheet, XElement? styles)
    {
        var workbook = new XElement(XName.Get("workbook", Ns),
            new XElement(XName.Get("sheets", Ns),
                new XElement(XName.Get("sheet", Ns),
                    new XAttribute("name", "Sheet1"),
                    new XAttribute("sheetId", "1"),
                    new XAttribute(XName.Get("id", Rel), "rId1"))));
        var rels = new XElement(XName.Get("Relationships", Pkg),
            new XElement(XName.Get("Relationship", Pkg),
                new XAttribute("Id", "rId1"),
                new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"),
                new XAttribute("Target", "worksheets/sheet1.xml")));
        var contentTypes = new XElement(XName.Get("Types", "http://schemas.openxmlformats.org/package/2006/content-types"),
            new XElement(XName.Get("Default", "http://schemas.openxmlformats.org/package/2006/content-types"),
                new XAttribute("Extension", "xml"),
                new XAttribute("ContentType", "application/xml")));

        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(archive, "[Content_Types].xml", new XDocument(contentTypes));
            Write(archive, "xl/workbook.xml", new XDocument(workbook));
            Write(archive, "xl/_rels/workbook.xml.rels", new XDocument(rels));
            Write(archive, "xl/worksheets/sheet1.xml", new XDocument(sheet));
            if (styles is not null)
                Write(archive, "xl/styles.xml", new XDocument(styles));
        }

        return buffer.ToArray();
    }

    private static XElement Row(int number, IReadOnlyList<string> cells)
    {
        var row = new XElement(XName.Get("row", Ns), new XAttribute("r", number));
        for (var i = 0; i < cells.Count; i++)
            row.Add(InlineCell(((char)('A' + i)).ToString(), number, cells[i]));

        return row;
    }

    private static XElement InlineCell(string column, int number, string text) =>
        new(XName.Get("c", Ns),
            new XAttribute("r", column + number),
            new XAttribute("t", "inlineStr"),
            new XElement(XName.Get("is", Ns), new XElement(XName.Get("t", Ns), text)));

    private static void Write(ZipArchive archive, string name, XDocument document)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        document.Save(stream);
    }

    private const string Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string Pkg = "http://schemas.openxmlformats.org/package/2006/relationships";
}
