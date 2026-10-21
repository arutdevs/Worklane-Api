using System.Text.Json;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;
using Worklane.Application.Common;

namespace Worklane.WebApi.Swagger;

/// <summary>
/// Swagger reads <c>Optional&lt;T&gt;</c> as a CLR object with <c>isSpecified</c> and <c>value</c>.
/// Callers send the inner value, or JSON null, so the document should show T as nullable.
/// </summary>
public sealed class OptionalSchemaFilter : ISchemaFilter
{
    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        if (schema is not OpenApiSchema concrete)
            return;

        if (TryGetValueType(context.Type, out var valueType))
        {
            CopyOnto(concrete, NullableSchema(valueType, context));
            return;
        }

        if (concrete.Properties is null || concrete.Properties.Count == 0)
            return;

        var optionalSchemaIds = new List<string>();
        foreach (var property in context.Type.GetProperties())
        {
            if (!TryGetValueType(property.PropertyType, out var inner))
                continue;
            if (!TryFindPropertyName(concrete, property.Name, out var schemaName))
                continue;

            if (concrete.Properties[schemaName] is OpenApiSchemaReference { Reference.Id: { } schemaId })
                optionalSchemaIds.Add(schemaId);

            concrete.Properties[schemaName] = NullableSchema(inner, context);
        }

        foreach (var schemaId in optionalSchemaIds.Distinct())
            context.SchemaRepository.Schemas.Remove(schemaId);
    }

    private static OpenApiSchema NullableSchema(Type valueType, SchemaFilterContext context)
    {
        var generated = context.SchemaGenerator.GenerateSchema(valueType, context.SchemaRepository);
        if (generated is OpenApiSchema inline)
        {
            return new OpenApiSchema
            {
                Type = (inline.Type ?? JsonSchemaType.Null) | JsonSchemaType.Null,
                Format = inline.Format,
                Items = inline.Items,
                Enum = inline.Enum,
                AllOf = inline.AllOf,
                OneOf = inline.OneOf,
                AnyOf = inline.AnyOf,
                Description = inline.Description,
                MinLength = inline.MinLength,
                MaxLength = inline.MaxLength,
                Minimum = inline.Minimum,
                Maximum = inline.Maximum,
                Pattern = inline.Pattern
            };
        }

        return new OpenApiSchema
        {
            Type = JsonSchemaType.Null,
            AllOf = [generated]
        };
    }

    private static void CopyOnto(OpenApiSchema target, OpenApiSchema source)
    {
        target.Properties?.Clear();
        target.Required?.Clear();
        target.AdditionalProperties = null;
        target.AdditionalPropertiesAllowed = true;
        target.Type = source.Type;
        target.Format = source.Format;
        target.Items = source.Items;
        target.Enum = source.Enum;
        target.AllOf = source.AllOf;
        target.OneOf = source.OneOf;
        target.AnyOf = source.AnyOf;
        target.Description = source.Description;
        target.MinLength = source.MinLength;
        target.MaxLength = source.MaxLength;
        target.Minimum = source.Minimum;
        target.Maximum = source.Maximum;
        target.Pattern = source.Pattern;
    }

    private static bool TryGetValueType(Type type, out Type valueType)
    {
        var effective = Nullable.GetUnderlyingType(type) ?? type;
        if (effective.IsGenericType && effective.GetGenericTypeDefinition() == typeof(Optional<>))
        {
            valueType = effective.GetGenericArguments()[0];
            return true;
        }

        valueType = null!;
        return false;
    }

    private static bool TryFindPropertyName(OpenApiSchema schema, string clrName, out string schemaName)
    {
        if (schema.Properties!.ContainsKey(clrName))
        {
            schemaName = clrName;
            return true;
        }

        var camel = JsonNamingPolicy.CamelCase.ConvertName(clrName);
        if (schema.Properties.ContainsKey(camel))
        {
            schemaName = camel;
            return true;
        }

        schemaName = "";
        return false;
    }
}
