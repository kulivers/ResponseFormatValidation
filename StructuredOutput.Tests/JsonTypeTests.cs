using System.Text.Json;
using Json.Schema;
using StructuredOutput.Validation;

namespace StructuredOutput.Tests;

public class JsonTypeTests
{
    public static IEnumerable<object[]> TypeMatrix()
    {
        string[] types = ["object", "array", "string", "number", "integer", "boolean", "null"];
        (string Json, string[] AcceptedTypes)[] values =
        [
            ("{}", ["object"]),
            ("[]", ["array"]),
            ("\"text\"", ["string"]),
            ("0", ["number", "integer"]),
            ("-12", ["number", "integer"]),
            ("1.0", ["number", "integer"]),
            ("1e2", ["number", "integer"]),
            ("1.5", ["number"]),
            ("1e-1", ["number"]),
            ("true", ["boolean"]),
            ("false", ["boolean"]),
            ("null", ["null"])
        ];

        foreach (string type in types)
        foreach (var (json, acceptedTypes) in values)
            yield return [type, json, acceptedTypes.Contains(type)];
    }

    [Theory]
    [MemberData(nameof(TypeMatrix))]
    public void SingleTypeMatchesReference(string type, string json, bool expected)
    {
        string schema = JsonSerializer.Serialize(new { type });
        AssertMatchesReference(schema, json, expected, "");
    }

    [Theory]
    [InlineData("{\"type\":[\"string\",\"null\"]}", "null", true)]
    [InlineData("{\"type\":[\"string\",\"null\"]}", "\"hello\"", true)]
    [InlineData("{\"type\":[\"string\",\"null\"]}", "false", false)]
    [InlineData("{\"type\":[\"integer\",\"boolean\"]}", "1.0", true)]
    [InlineData("{\"type\":[\"integer\",\"boolean\"]}", "true", true)]
    [InlineData("{\"type\":[\"integer\",\"boolean\"]}", "1.5", false)]
    public void UnionTypesMatchReference(string schema, string json, bool expected) =>
        AssertMatchesReference(schema, json, expected, "");

    [Theory]
    [InlineData("object", "[]")]
    [InlineData("array", "{}")]
    [InlineData("string", "false")]
    [InlineData("number", "\"1\"")]
    [InlineData("integer", "1.5")]
    [InlineData("boolean", "0")]
    [InlineData("null", "true")]
    public void NestedTypeErrorIdentifiesField(string type, string json)
    {
        string schema = JsonSerializer.Serialize(new { type = "object", properties = new { value = new { type } } });
        AssertMatchesReference(schema, "{\"value\":" + json + "}", false, "/value");
    }

    private static void AssertMatchesReference(string schema, string json, bool expected, string expectedPath)
    {
        using var schemaDocument = JsonDocument.Parse(schema);
        using var instanceDocument = JsonDocument.Parse(json);
        var reference = JsonSchema.Build(schemaDocument.RootElement).Evaluate(instanceDocument.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.Hierarchical });
        var actual = JsonSchemaValidator.Validate(json, schema);

        Assert.Equal(expected, reference.IsValid);
        Assert.Equal(expected, actual.IsValid);
        Assert.Equal(expected ? ValidationStatus.Valid : ValidationStatus.InvalidInstance, actual.Status);
        if (expected)
            Assert.Empty(actual.Errors);
        else
            Assert.Contains(actual.Errors, error => error.Keyword == "type" && error.InstancePath == expectedPath && !string.IsNullOrWhiteSpace(error.Message));
    }
}
