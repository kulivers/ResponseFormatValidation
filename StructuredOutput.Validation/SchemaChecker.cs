using System.Text.Json;
using System.Text.RegularExpressions;

namespace StructuredOutput.Validation;

internal sealed class SchemaChecker(JsonElement root)
{
    private static readonly HashSet<string> Allowed = ["$schema", "$defs", "$ref", "type", "properties", "required", "additionalProperties", "items", "minItems", "maxItems", "enum", "const", "minLength", "maxLength", "pattern", "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "anyOf", "title", "description", "default", "examples", "format"];
    private static readonly HashSet<string> Types = ["object", "array", "string", "number", "integer", "boolean", "null"];
    private readonly HashSet<JsonElement> checkedSchemas = [];
    private readonly HashSet<JsonElement> checkingSchemas = [];

    public ValidationError? Check() => CheckSchema(root, "");

    private ValidationError? CheckSchema(JsonElement schema, string path)
    {
        ValidationError Error(string keyword, string message) => new(path, keyword, message);
        if (schema.ValueKind is JsonValueKind.True or JsonValueKind.False) return null;
        if (schema.ValueKind != JsonValueKind.Object) return Error("schema", "A schema must be an object or boolean.");
        if (checkingSchemas.Contains(schema) || checkedSchemas.Contains(schema)) return null;
        checkingSchemas.Add(schema);
        foreach (var property in schema.EnumerateObject())
        {
            var v = property.Value;
            var keyword = property.Name;
            if (!Allowed.Contains(keyword)) return Error(keyword, $"Unsupported schema keyword '{keyword}'.");
            if (keyword == "$schema" && (v.ValueKind != JsonValueKind.String || v.GetString() != "https://json-schema.org/draft/2020-12/schema")) return Error(keyword, "Only JSON Schema draft 2020-12 is supported.");
            if (keyword == "$ref")
            {
                if (v.ValueKind != JsonValueKind.String) return Error(keyword, "Reference must be a string.");
                try
                {
                    var target = JsonSchemaValidator.Resolve(root, v.GetString()!);
                    var failure = CheckSchema(target, v.GetString()!);
                    if (failure is not null) return failure;
                }
                catch (Exception ex) when (ex is FormatException or UriFormatException) { return Error(keyword, ex.Message); }
            }
            if (keyword == "type")
            {
                if (v.ValueKind == JsonValueKind.String) { if (!Types.Contains(v.GetString()!)) return Error(keyword, "Unknown type."); }
                else if (v.ValueKind == JsonValueKind.Array)
                {
                    var names = new HashSet<string>();
                    foreach (var item in v.EnumerateArray())
                        if (item.ValueKind != JsonValueKind.String || !Types.Contains(item.GetString()!) || !names.Add(item.GetString()!)) return Error(keyword, "Types must be unique recognized strings.");
                    if (names.Count == 0) return Error(keyword, "Type array must not be empty.");
                }
                else return Error(keyword, "Type must be a string or an array of strings.");
            }
            if (keyword is "properties" or "$defs")
            {
                if (v.ValueKind != JsonValueKind.Object) return Error(keyword, "Expected an object of schemas.");
                foreach (var child in v.EnumerateObject())
                { var failure = CheckSchema(child.Value, JsonSchemaValidator.Path(JsonSchemaValidator.Path(path, keyword), child.Name)); if (failure is not null) return failure; }
            }
            if (keyword is "items" or "additionalProperties")
            { var failure = CheckSchema(v, JsonSchemaValidator.Path(path, keyword)); if (failure is not null) return failure; }
            if (keyword == "anyOf")
            {
                if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() == 0) return Error(keyword, "Expected a nonempty array of schemas.");
                int i = 0;
                foreach (var child in v.EnumerateArray())
                { var failure = CheckSchema(child, JsonSchemaValidator.Path(JsonSchemaValidator.Path(path, keyword), (i++).ToString())); if (failure is not null) return failure; }
            }
            if (keyword == "required")
            {
                if (v.ValueKind != JsonValueKind.Array) return Error(keyword, "Expected an array of unique strings.");
                var names = new HashSet<string>();
                foreach (var item in v.EnumerateArray()) if (item.ValueKind != JsonValueKind.String || !names.Add(item.GetString()!)) return Error(keyword, "Expected an array of unique strings.");
            }
            if (keyword == "enum" && (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() == 0)) return Error(keyword, "Expected a nonempty array.");
            if (keyword is "minimum" or "maximum" or "exclusiveMinimum" or "exclusiveMaximum")
                if (v.ValueKind != JsonValueKind.Number) return Error(keyword, "Expected a number.");
            if (keyword is "minLength" or "maxLength" or "minItems" or "maxItems")
                if (v.ValueKind != JsonValueKind.Number || !IsNonNegativeInteger(v)) return Error(keyword, "Expected a non-negative integer.");
            if (keyword == "pattern")
            {
                if (v.ValueKind != JsonValueKind.String) return Error(keyword, "Expected a string.");
                try { _ = new Regex(v.GetString()!, RegexOptions.ECMAScript, TimeSpan.FromSeconds(1)); }
                catch (ArgumentException ex) { return Error(keyword, ex.Message); }
            }
            if (keyword is "title" or "description" or "format") if (v.ValueKind != JsonValueKind.String) return Error(keyword, "Expected a string.");
            if (keyword == "examples" && v.ValueKind != JsonValueKind.Array) return Error(keyword, "Expected an array.");
        }
        checkingSchemas.Remove(schema);
        checkedSchemas.Add(schema);
        return null;
    }

    private static bool IsNonNegativeInteger(JsonElement value)
    {
        var text = value.GetRawText();
        var parts = text.Split('e', 'E');
        var exponent = parts.Length == 2 ? System.Numerics.BigInteger.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 0;
        var mantissa = parts[0].TrimStart('-');
        var dot = mantissa.IndexOf('.');
        var digits = mantissa.Replace(".", "").TrimEnd('0');
        if (text.StartsWith('-') && digits.Trim('0').Length != 0) return false;
        return digits.Trim('0').Length == 0 || exponent - (dot < 0 ? 0 : mantissa.Length - dot - 1) + (mantissa.Replace(".", "").Length - digits.Length) >= 0;
    }
}
