using System.Diagnostics;
using System.Net;
using System.Text;
using FluentAssertions;
using Worklane.Application.Imports;
using Worklane.Infrastructure.Services.Imports;
using Worklane.IntegrationTests.Infrastructure;

namespace Worklane.IntegrationTests.Imports;

public class ImportReaderLimitTests : IClassFixture<WorklaneWebApplicationFactory>
{
    private readonly WorklaneWebApplicationFactory _factory;

    public ImportReaderLimitTests(WorklaneWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData(1_048_576)]
    [InlineData(int.MaxValue)]
    public async Task SparseSheetRow_IsRowLimitBeforeTheSheetIsPadded(int rowNumber)
    {
        var board = await BoardTestSetup.CreateAsync(_factory, $"import-sparse-{rowNumber}-{Guid.NewGuid():N}@example.com");
        var bytes = ImportHttp.XlsxWithDataRow(rowNumber);
        bytes.Length.Should().BeLessThan(2_000);

        var clock = Stopwatch.StartNew();
        var response = await ImportHttp.UploadBytesAsync(board.Client, board.Project.Id, "sparse.xlsx", bytes, "generic");
        clock.Stop();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
        var problem = await ImportHttp.ReadAsync(response);
        problem.GetProperty("code").GetString().Should().Be("import_row_limit");
        problem.GetProperty("maxRows").GetInt32().Should().Be(2000);
        problem.GetProperty("detail").GetString().Should().Be("The file has more than 2000 rows.");
    }

    [Fact]
    public void CsvPastTheScanWindow_StopsBeforeALaterSyntaxError()
    {
        var csv = new StringBuilder("Title,External Id\n");
        for (var i = 0; i < 2_011; i++)
            csv.Append("Row,ID\n");
        csv.Append("\"unterminated");

        var clock = Stopwatch.StartNew();
        var error = Assert.Throws<ImportFormatException>(() => CsvImportReader.Read(csv.ToString(), 64, 2000));
        clock.Stop();

        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
        error.Code.Should().Be(ImportErrors.RowLimit);
        error.Message.Should().Be(ImportErrors.RowLimitDetail(2000));
    }

    [Fact]
    public async Task CsvPastTheScanWindow_IsRowLimit()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, $"import-csv-cap-{Guid.NewGuid():N}@example.com");
        var csv = new StringBuilder("Title,External Id\n");
        for (var i = 0; i < 2_011; i++)
            csv.Append("Row,ID\n");
        csv.Append("\"unterminated");

        var response = await ImportHttp.UploadAsync(board.Client, board.Project.Id, "wide.csv", csv.ToString());
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await ImportHttp.ReadAsync(response);
        problem.GetProperty("code").GetString().Should().Be("import_row_limit");
        problem.GetProperty("maxRows").GetInt32().Should().Be(2000);
    }

    [Theory]
    [InlineData("2958466")]
    [InlineData("-657436")]
    public async Task OutOfRangeOADate_IsUnreadable(string serial)
    {
        var board = await BoardTestSetup.CreateAsync(_factory, $"import-oadate-{Guid.NewGuid():N}@example.com");
        var response = await ImportHttp.UploadBytesAsync(
            board.Client,
            board.Project.Id,
            "dates.xlsx",
            ImportHttp.XlsxWithDateSerial(serial),
            "generic");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await ImportHttp.ReadAsync(response);
        problem.GetProperty("code").GetString().Should().Be("import_unreadable");
        problem.GetProperty("detail").GetString().Should().Be("The file could not be read.");
    }

    [Fact]
    public async Task InRangeOADate_Uploads()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, $"import-oadate-ok-{Guid.NewGuid():N}@example.com");
        var response = await ImportHttp.UploadBytesAsync(
            board.Client,
            board.Project.Id,
            "dates.xlsx",
            ImportHttp.XlsxWithDateSerial("44927"),
            "generic");

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }
}
