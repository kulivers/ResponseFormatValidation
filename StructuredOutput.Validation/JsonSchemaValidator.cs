using System.Text.Json;
using System.Text.RegularExpressions;
using System.Numerics;
using System.Text;

namespace StructuredOutput.Validation;

public enum ValidationStatus
{
    Valid,
    InvalidInstance,
    MalformedJson,
    InvalidSchema
}

public sealed record ValidationError(string InstancePath, string Keyword, string Message);

public sealed record ValidationResult(ValidationStatus Status, IReadOnlyList<ValidationError> Errors)
{
    public bool IsValid => Status == ValidationStatus.Valid;
}

public static class JsonSchemaValidator
{
    public static ValidationResult Validate(string json, string schemaJson)
    {
        JsonDocument schema;
        try
        {
            schema = JsonDocument.Parse(schemaJson);
        }
        catch (JsonException ex)
        {
            return Failure(ValidationStatus.InvalidSchema, "", "schema", ex.Message);
        }

        using (schema)
        {
            var checker = new SchemaChecker(schema.RootElement);
            var schemaError = checker.Check();
            if (schemaError is not null) return new(ValidationStatus.InvalidSchema, [schemaError]);
            JsonDocument instance;
            try
            {
                instance = JsonDocument.Parse(json);
            }
            catch (JsonException ex)
            {
                return Failure(ValidationStatus.MalformedJson, ex.Path ?? "", "json", ex.Message);
            }

            using (instance)
            {
                var errors = new List<ValidationError>();
                try
                {
                    Evaluate(schema.RootElement, instance.RootElement, "", schema.RootElement, errors, new());
                }
                catch (EvaluationException ex)
                {
                    return Failure(ValidationStatus.InvalidSchema, ex.Path, ex.Keyword, ex.Message);
                }

                return new(errors.Count == 0 ? ValidationStatus.Valid : ValidationStatus.InvalidInstance, errors);
            }
        }
    }

    private static ValidationResult Failure(ValidationStatus status, string path, string keyword, string message) =>
        new(status, [new(path, keyword, message)]);

    internal static string Path(string parent, string token) => parent + "/" + token.Replace("~", "~0").Replace("/", "~1");

    internal static JsonElement Resolve(JsonElement root, string reference)
    {
        if (reference == "#") return root;
        if (!reference.StartsWith('#')) throw new FormatException("Only local JSON Pointer references are supported.");
        var pointer = Uri.UnescapeDataString(reference[1..]);
        if (!pointer.StartsWith('/')) throw new FormatException("Only local JSON Pointer references are supported.");
        var current = root;
        foreach (var raw in pointer[1..].Split('/'))
        {
            var token = raw;
            if (Regex.IsMatch(token, "~(?![01])")) throw new FormatException("Invalid JSON Pointer escape.");
            token = token.Replace("~1", "/").Replace("~0", "~");
            if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(token, out var property)) current = property;
            else if (current.ValueKind == JsonValueKind.Array && int.TryParse(token, out var index) && index >= 0 && index < current.GetArrayLength() && token == index.ToString())
                current = current[index];
            else throw new FormatException($"Reference '{reference}' cannot be resolved.");
        }

        return current;
    }

    private static void Evaluate(JsonElement schema, JsonElement value, string path, JsonElement root, List<ValidationError> errors, HashSet<(JsonElement, string)> active)
    {
        if (schema.ValueKind == JsonValueKind.True) return;
        if (schema.ValueKind == JsonValueKind.False)
        {
            errors.Add(new(path, "false", "This value is not allowed."));
            return;
        }

        if (!active.Add((schema, path))) throw new EvaluationException(path, "$ref", "A schema reference cycle does not advance through the instance.");
        try
        {
            void Error(string keyword, string message, string? errorPath = null) => errors.Add(new(errorPath ?? path, keyword, message));
            if (schema.TryGetProperty("$ref", out var reference)) Evaluate(Resolve(root, reference.GetString()!), value, path, root, errors, active);
            if (schema.TryGetProperty("type", out var type))
            {
                var types = type.ValueKind == JsonValueKind.String ? new[] { type.GetString()! } : type.EnumerateArray().Select(x => x.GetString()!).ToArray();
                if (!types.Any(t => MatchesType(value, t))) Error("type", $"Expected {string.Join(" or ", types)}, received {value.ValueKind}.");
            }

            if (schema.TryGetProperty("enum", out var choices) && !choices.EnumerateArray().Any(x => JsonElement.DeepEquals(x, value))) Error("enum", $"Expected one of {choices.GetRawText()}.");
            if (schema.TryGetProperty("const", out var constant) && !JsonElement.DeepEquals(constant, value)) Error("const", $"Expected {constant.GetRawText()}.");
            if (schema.TryGetProperty("anyOf", out var alternatives))
            {
                var failures = new List<List<ValidationError>>();
                foreach (var alternative in alternatives.EnumerateArray())
                {
                    var branch = new List<ValidationError>();
                    Evaluate(alternative, value, path, root, branch, active);
                    if (branch.Count == 0)
                    {
                        failures.Clear();
                        break;
                    }

                    failures.Add(branch);
                }

                if (failures.Count > 0)
                {
                    Error("anyOf", "Value does not match any alternative.");
                    foreach (var branch in failures) errors.AddRange(branch);
                }
            }

            if (value.ValueKind == JsonValueKind.Object)
            {
                if (schema.TryGetProperty("required", out var required))
                    foreach (var name in required.EnumerateArray())
                        if (!value.TryGetProperty(name.GetString()!, out _))
                            Error("required", $"Required property '{name.GetString()}' is missing.", Path(path, name.GetString()!));
                bool hasProperties = schema.TryGetProperty("properties", out var properties);
                foreach (var property in value.EnumerateObject())
                {
                    if (hasProperties && properties.TryGetProperty(property.Name, out var propertySchema)) Evaluate(propertySchema, property.Value, Path(path, property.Name), root, errors, active);
                    else if (schema.TryGetProperty("additionalProperties", out var additional))
                    {
                        if (additional.ValueKind == JsonValueKind.False) Error("additionalProperties", $"Property '{property.Name}' is not allowed.", Path(path, property.Name));
                        else Evaluate(additional, property.Value, Path(path, property.Name), root, errors, active);
                    }
                }
            }

            if (value.ValueKind == JsonValueKind.Array)
            {
                var length = value.GetArrayLength();
                CheckLength(schema, "minItems", "maxItems", length, Error);
                if (schema.TryGetProperty("items", out var items))
                {
                    var index = 0;
                    foreach (var item in value.EnumerateArray()) Evaluate(items, item, Path(path, (index++).ToString()), root, errors, active);
                }
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString()!;
                CheckLength(schema, "minLength", "maxLength", text.EnumerateRunes().Count(), Error);
                if (schema.TryGetProperty("pattern", out var pattern))
                    try
                    {
                        if (!Regex.IsMatch(text, pattern.GetString()!, RegexOptions.ECMAScript, TimeSpan.FromSeconds(1))) Error("pattern", $"Value does not match pattern {pattern.GetRawText()}.");
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        throw new EvaluationException(path, "pattern", "Pattern matching timed out.");
                    }
            }

            if (value.ValueKind == JsonValueKind.Number)
                foreach (var keyword in new[] { "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum" })
                    if (schema.TryGetProperty(keyword, out var limit))
                    {
                        int comparison = CompareNumbers(value.GetRawText(), limit.GetRawText());
                        bool valid = keyword switch { "minimum" => comparison >= 0, "maximum" => comparison <= 0, "exclusiveMinimum" => comparison > 0, _ => comparison < 0 };
                        if (!valid) Error(keyword, $"Value {value.GetRawText()} violates {keyword} {limit.GetRawText()}.");
                    }
        }
        finally
        {
            active.Remove((schema, path));
        }
    }

    private static void CheckLength(JsonElement schema, string min, string max, int length, Action<string, string, string?> error)
    {
        if (schema.TryGetProperty(min, out var lower) && CompareNumbers(length.ToString(), lower.GetRawText()) < 0) error(min, $"Length {length} is below {lower.GetRawText()}.", null);
        if (schema.TryGetProperty(max, out var upper) && CompareNumbers(length.ToString(), upper.GetRawText()) > 0) error(max, $"Length {length} exceeds {upper.GetRawText()}.", null);
    }

    private static bool MatchesType(JsonElement value, string type) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && IsInteger(value.GetRawText()),
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => false
    };

    private static (int Sign, string Digits, BigInteger Scale) ParseNumber(string text)
    {
        int sign = text.StartsWith('-') ? -1 : 1;
        if (sign < 0) text = text[1..];
        var parts = text.Split('e', 'E');
        BigInteger exponent = parts.Length == 2 ? BigInteger.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : BigInteger.Zero;
        int dot = parts[0].IndexOf('.');
        int fractional = dot < 0 ? 0 : parts[0].Length - dot - 1;
        string digits = parts[0].Replace(".", "").TrimStart('0').TrimEnd('0');
        if (digits.Length == 0) return (0, "0", 0);
        string untrimmed = parts[0].Replace(".", "").TrimStart('0');
        return (sign, digits, exponent - fractional + untrimmed.Length - digits.Length);
    }

    private static bool IsInteger(string text)
    {
        var n = ParseNumber(text);
        return n.Sign == 0 || n.Scale >= 0;
    }

    private static int CompareNumbers(string left, string right)
    {
        var a = ParseNumber(left);
        var b = ParseNumber(right);
        if (a.Sign != b.Sign) return a.Sign.CompareTo(b.Sign);
        if (a.Sign == 0) return 0;
        int magnitude = (a.Scale + a.Digits.Length).CompareTo(b.Scale + b.Digits.Length);
        if (magnitude == 0)
        {
            int length = Math.Max(a.Digits.Length, b.Digits.Length);
            magnitude = string.CompareOrdinal(a.Digits.PadRight(length, '0'), b.Digits.PadRight(length, '0'));
        }

        return a.Sign * Math.Sign(magnitude);
    }

    private sealed class EvaluationException(string path, string keyword, string message) : Exception(message)
    {
        public string Path { get; } = path;
        public string Keyword { get; } = keyword;
    }
}