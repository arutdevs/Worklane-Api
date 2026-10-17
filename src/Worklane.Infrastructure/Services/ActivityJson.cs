using System.Text.Json;

namespace Worklane.Infrastructure.Services;

internal static class ActivityJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Of(object value) => JsonSerializer.Serialize(value, Options);
}
