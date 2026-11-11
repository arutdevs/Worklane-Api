using System.Text.Json;
using Worklane.Application.Imports.Dtos;

namespace Worklane.Infrastructure.Services.Imports;

internal static class ImportJsonStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Of<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Parse<T>(string json) where T : new() =>
        JsonSerializer.Deserialize<T>(json, Options) ?? new T();

    public static List<string> Headers(string? json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<string>>(json, Options) ?? [];

    public static List<List<string?>> Sample(string? json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<List<string?>>>(json, Options) ?? [];

    public static ColumnMapping? Mapping(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<ColumnMapping>(json, Options);

    public static List<BatchWarning> Warnings(string? json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<BatchWarning>>(json, Options) ?? [];
}
