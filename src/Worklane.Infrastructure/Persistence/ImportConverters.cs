using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Worklane.Domain.Enums;
using Worklane.Domain.Imports;

namespace Worklane.Infrastructure.Persistence;

/// <summary>Persists import enums as their wire names. There is no column default.</summary>
internal static class ImportConverters
{
    public static ValueConverter<ImportSource, string> Source { get; } = new(
        value => ImportWire.Source(value),
        value => ImportWire.ParseSource(value));

    public static ValueConverter<ImportSource?, string?> NullableSource { get; } = new(
        value => value.HasValue ? ImportWire.Source(value.Value) : null,
        value => value == null ? null : ImportWire.ParseSource(value));

    public static ValueConverter<ImportBatchStatus, string> BatchStatus { get; } = new(
        value => ImportWire.BatchStatus(value),
        value => ImportWire.ParseBatchStatus(value));

    public static ValueConverter<ImportRowStatus, string> RowStatus { get; } = new(
        value => ImportWire.RowStatus(value),
        value => ImportWire.ParseRowStatus(value));

    public static ValueConverter<ExportFormat, string> Format { get; } = new(
        value => ImportWire.Format(value),
        value => ImportWire.ParseFormat(value));
}
