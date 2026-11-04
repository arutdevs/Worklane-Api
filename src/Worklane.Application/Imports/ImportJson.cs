using System.Text.Json;
using System.Text.Json.Serialization;
using Worklane.Domain.Enums;
using Worklane.Domain.Imports;

namespace Worklane.Application.Imports;

/// <summary>
/// Writes import enums as the contract spellings (<c>skipped_existing</c>), which are not
/// the camelCase of the CLR names. Register after <see cref="JsonStringEnumConverter"/> so
/// this converter wins for these types only.
/// </summary>
public sealed class ImportEnumConverter : JsonConverterFactory
{
    public static readonly JsonSerializerOptions Storage = new(JsonSerializerDefaults.Web);

    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert == typeof(ImportSource)
        || typeToConvert == typeof(ImportBatchStatus)
        || typeToConvert == typeof(ImportRowStatus)
        || typeToConvert == typeof(ExportFormat)
        || typeToConvert == typeof(ImportUndoReason);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        if (typeToConvert == typeof(ImportSource)) return new WireConverter<ImportSource>(ImportWire.Source, ImportWire.ParseSource);
        if (typeToConvert == typeof(ImportBatchStatus)) return new WireConverter<ImportBatchStatus>(ImportWire.BatchStatus, ImportWire.ParseBatchStatus);
        if (typeToConvert == typeof(ImportRowStatus)) return new WireConverter<ImportRowStatus>(ImportWire.RowStatus, ImportWire.ParseRowStatus);
        if (typeToConvert == typeof(ExportFormat)) return new WireConverter<ExportFormat>(ImportWire.Format, ImportWire.ParseFormat);
        if (typeToConvert == typeof(ImportUndoReason)) return new WireConverter<ImportUndoReason>(ImportWire.UndoReason, ParseUndo);
        throw new ArgumentOutOfRangeException(nameof(typeToConvert), typeToConvert, null);
    }

    private static ImportUndoReason ParseUndo(string text) => text switch
    {
        "edited_after_import" => ImportUndoReason.EditedAfterImport,
        "has_active_children" => ImportUndoReason.HasActiveChildren,
        "already_archived" => ImportUndoReason.AlreadyArchived,
        _ => throw new JsonException("Unknown undo reason.")
    };

    private sealed class WireConverter<T> : JsonConverter<T> where T : struct, Enum
    {
        private readonly Func<T, string> _write;
        private readonly Func<string, T> _read;

        public WireConverter(Func<T, string> write, Func<string, T> read)
        {
            _write = write;
            _read = read;
        }

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
                throw new JsonException("Expected a string.");
            var text = reader.GetString();
            if (text is null)
                throw new JsonException("Expected a string.");
            try
            {
                return _read(text);
            }
            catch (ArgumentOutOfRangeException)
            {
                throw new JsonException("Unknown value.");
            }
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            writer.WriteStringValue(_write(value));
    }
}
