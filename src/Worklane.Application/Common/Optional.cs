using System.Text.Json;
using System.Text.Json.Serialization;

namespace Worklane.Application.Common;

/// <summary>
/// Distinguishes "property omitted" from "property present, including JSON null"
/// on a PATCH body. Omitted leaves the stored value alone; an explicit null clears it.
/// </summary>
[JsonConverter(typeof(OptionalJsonConverterFactory))]
public readonly struct Optional<T>
{
    public bool IsSpecified { get; init; }
    public T? Value { get; init; }

    public static Optional<T> Specified(T? value) => new() { IsSpecified = true, Value = value };
}

public sealed class OptionalJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(Optional<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var valueType = typeToConvert.GetGenericArguments()[0];
        var converterType = typeof(OptionalJsonConverter<>).MakeGenericType(valueType);
        return (JsonConverter)Activator.CreateInstance(converterType)!;
    }
}

public sealed class OptionalJsonConverter<T> : JsonConverter<Optional<T>>
{
    public override Optional<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = JsonSerializer.Deserialize<T>(ref reader, options);
        return Optional<T>.Specified(value);
    }

    public override void Write(Utf8JsonWriter writer, Optional<T> value, JsonSerializerOptions options)
    {
        // An unspecified Optional serializes as JSON null. A C# client that re-sends
        // that null would clear the field. This converter is only used for incoming requests.
        if (!value.IsSpecified || value.Value is null)
        {
            writer.WriteNullValue();
            return;
        }

        JsonSerializer.Serialize(writer, value.Value, options);
    }
}
