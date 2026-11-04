using Worklane.Application.Common.Options;
using Worklane.Application.Imports;

namespace Worklane.Infrastructure.Services.Imports;

internal static class ImportFileReader
{
    public static ParsedTable Read(string extension, ReadOnlySpan<byte> bytes, string preset, ImportSettings settings)
    {
        try
        {
            if (extension == ".csv")
            {
                if (!FileSignature.TryDecodeText(bytes, out var text))
                    throw new ImportFormatException(ImportErrors.Unreadable, ImportErrors.UnreadableDetail);
                var grid = CsvImportReader.Read(text, settings.MaxColumns);
                return ImportTables.FromGrid(grid, preset, settings.MaxRows, settings.MaxColumns, 1);
            }

            var (rows, sheetCount) = XlsxImportReader.Read(bytes.ToArray(), settings.MaxUncompressedBytes, settings.MaxColumns);
            return ImportTables.FromGrid(rows, preset, settings.MaxRows, settings.MaxColumns, sheetCount);
        }
        catch (ImportFormatException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or System.Xml.XmlException)
        {
            throw new ImportFormatException(ImportErrors.Unreadable, ImportErrors.UnreadableDetail);
        }
    }
}
