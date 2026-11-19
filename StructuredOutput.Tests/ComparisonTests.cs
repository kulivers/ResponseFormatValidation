using System.Text.Json;
using Json.Schema;
using StructuredOutput.Validation;
using Xunit.Abstractions;

namespace StructuredOutput.Tests;

public class ComparisonTests(ITestOutputHelper output)
{
    public static TheoryData<string, string, bool> Cases => new()
    {
        { "true", "null", true }, { "false", "null", false },
        { """{"type":["string","null"]}""", "null", true },
        { """{"type":"integer"}""", "1.0", true },
        { """{"type":"integer"}""", "1.01", false },
        { """{"type":"object","properties":{"a":{"type":"string"}},"required":["a"],"additionalProperties":false}""", """{"a":"x"}""", true },
        { """{"type":"object","properties":{"a":{"type":"string"}},"required":["a"],"additionalProperties":false}""", """{"b":2}""", false },
        { """{"items":{"type":"integer"},"minItems":2,"maxItems":3}""", "[1,2]", true },
        { """{"items":{"type":"integer"},"minItems":2,"maxItems":3}""", "[1.5]", false },
        { """{"minLength":1,"maxLength":1,"pattern":"^.$"}""", "\"a\"", true },
        { """{"minLength":2,"maxLength":4}""", "\"a\"", false },
        { """{"enum":[1,"x"],"const":1}""", "1.0", true },
        { """{"enum":[1,"x"],"const":1}""", "\"x\"", false },
        { """{"minimum":1.1,"maximum":3.2,"exclusiveMinimum":1,"exclusiveMaximum":4}""", "1.1", true },
        { """{"minimum":1.1,"maximum":3.2,"exclusiveMinimum":1,"exclusiveMaximum":4}""", "4", false },
        { """{"anyOf":[{"type":"string"},{"type":"integer"}]}""", "5", true },
        { """{"anyOf":[{"type":"string"},{"type":"integer"}]}""", "true", false },
        { """{"$defs":{"x":{"type":"integer"}},"$ref":"#/$defs/x"}""", "5", true },
        { """{"$defs":{"x":{"type":"integer"}},"$ref":"#/$defs/x"}""", "\"5\"", false }
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void ValidityMatchesReference(string schema, string json, bool valid)
    {
        using var schemaDoc = JsonDocument.Parse(schema);
        using var instanceDoc = JsonDocument.Parse(json);
        var reference = JsonSchema.Build(schemaDoc.RootElement).Evaluate(instanceDoc.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.Hierarchical });
        var actual = JsonSchemaValidator.Validate(json, schema);
        Assert.Equal(valid, reference.IsValid);
        Assert.Equal(reference.IsValid, actual.IsValid);
        if (!valid) Assert.NotEmpty(actual.Errors);
    }

    [Fact]
    public void ReportsActionablePathsAndShowsBothOutputs()
    {
        const string schema = """{"type":"object","required":["customer","items"],"properties":{"customer":{"type":"string"},"items":{"type":"array","items":{"$ref":"#/$defs/item"}}},"additionalProperties":false,"$defs":{"item":{"type":"object","required":["quantity"],"properties":{"quantity":{"type":"integer","minimum":1}}}}}""";
        const string json = """{"items":[{"quantity":0}],"extra":true}""";
        using var s = JsonDocument.Parse(schema); using var i = JsonDocument.Parse(json);
        var reference = JsonSchema.Build(s.RootElement).Evaluate(i.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.Hierarchical });
        void Print(EvaluationResults node)
        {
            if (node.Errors != null) foreach (var e in node.Errors) output.WriteLine($"reference {node.InstanceLocation} [{e.Key}] {e.Value}");
            if (node.Details != null) foreach (var child in node.Details) Print(child);
        }
        Print(reference);
        var result = JsonSchemaValidator.Validate(json, schema);
        foreach (var line in StructuredOutputValidationAdapter.FormatErrors(result)) output.WriteLine($"custom {line}");
        Assert.False(reference.IsValid); Assert.Equal(ValidationStatus.InvalidInstance, result.Status);
        Assert.Contains(result.Errors, e => e.InstancePath == "/customer" && e.Keyword == "required");
        Assert.Contains(result.Errors, e => e.InstancePath == "/extra" && e.Keyword == "additionalProperties");
        Assert.Contains(result.Errors, e => e.InstancePath == "/items/0/quantity" && e.Keyword == "minimum");
    }

    [Theory]
    [InlineData("{", "{}", ValidationStatus.MalformedJson)]
    [InlineData("{}", "{", ValidationStatus.InvalidSchema)]
    [InlineData("{}", "{\"unknown\":true}", ValidationStatus.InvalidSchema)]
    [InlineData("{}", "{\"$ref\":\"#/missing\"}", ValidationStatus.InvalidSchema)]
    [InlineData("{}", "{\"$ref\":\"#\"}", ValidationStatus.InvalidSchema)]
    [InlineData("{}", "{\"required\":[1]}", ValidationStatus.InvalidSchema)]
    public void DistinguishesFailures(string json, string schema, ValidationStatus status) => Assert.Equal(status, JsonSchemaValidator.Validate(json, schema).Status);

    [Fact]
    public void EscapesPathsAndCountsUnicodeScalars()
    {
        var result = JsonSchemaValidator.Validate("""{"a/b~c":"😀"}""", """{"properties":{"a/b~c":{"minLength":2}}}""");
        Assert.Contains(result.Errors, e => e.InstancePath == "/a~1b~0c" && e.Keyword == "minLength");
    }

    [Fact]
    public void ComparesNumbersWithoutRounding()
    {
        Assert.False(JsonSchemaValidator.Validate("1.00000000000000000000000000000000001", """{"maximum":1}""").IsValid);
        Assert.True(JsonSchemaValidator.Validate("1e100000", """{"minimum":1e99999}""").IsValid);
        Assert.True(JsonSchemaValidator.Validate("1.00e2", """{"type":"integer"}""").IsValid);
    }

    [Fact]
    public void SupportsRecursiveSchemasThatConsumeInstanceNodes()
    {
        const string schema = """{"type":"object","properties":{"child":{"$ref":"#"}}}""";
        Assert.True(JsonSchemaValidator.Validate("""{"child":{"child":{}}}""", schema).IsValid);
    }

    [Fact]
    public void AnyOfKeepsBranchErrorsOnlyWhenAllBranchesFail()
    {
        const string schema = """{"anyOf":[{"type":"string"},{"type":"number","minimum":3}]}""";
        Assert.Empty(JsonSchemaValidator.Validate("\"ok\"", schema).Errors);
        var result = JsonSchemaValidator.Validate("2", schema);
        Assert.Contains(result.Errors, e => e.Keyword == "anyOf");
        Assert.Contains(result.Errors, e => e.Keyword == "type");
        Assert.Contains(result.Errors, e => e.Keyword == "minimum");
    }

    [Theory]
    [InlineData("{\"minItems\":-0.1}")]
    [InlineData("{\"maxLength\":1.2}")]
    [InlineData("{\"type\":[\"string\",\"string\"]}")]
    [InlineData("{\"pattern\":\"[\"}")]
    [InlineData("{\"anyOf\":[]}")]
    [InlineData("{\"$schema\":\"https://json-schema.org/draft-07/schema\"}")]
    public void RejectsInvalidSchemas(string schema) => Assert.Equal(ValidationStatus.InvalidSchema, JsonSchemaValidator.Validate("null", schema).Status);

    [Fact]
    public void AcceptsZeroLengthExpressedWithExponent()
    {
        Assert.True(JsonSchemaValidator.Validate("[]", """{"minItems":-0.0e-3}""").IsValid);
    }

    [Theory]
    [InlineData("{\"minItems\":2}", "[1]", "minItems")]
    [InlineData("{\"maxItems\":1}", "[1,2]", "maxItems")]
    [InlineData("{\"items\":false}", "[1]", "false")]
    [InlineData("{\"minLength\":2}", "\"a\"", "minLength")]
    [InlineData("{\"maxLength\":1}", "\"ab\"", "maxLength")]
    [InlineData("{\"pattern\":\"^a\"}", "\"b\"", "pattern")]
    [InlineData("{\"minimum\":2}", "1", "minimum")]
    [InlineData("{\"maximum\":2}", "3", "maximum")]
    [InlineData("{\"exclusiveMinimum\":2}", "2", "exclusiveMinimum")]
    [InlineData("{\"exclusiveMaximum\":2}", "2", "exclusiveMaximum")]
    [InlineData("{\"enum\":[1,2]}", "3", "enum")]
    [InlineData("{\"const\":1}", "2", "const")]
    [InlineData("{\"type\":\"string\"}", "2", "type")]
    public void ReportsSpecificKeyword(string schema, string json, string keyword)
    {
        var result = JsonSchemaValidator.Validate(json, schema);
        Assert.Contains(result.Errors, error => error.Keyword == keyword);
        using var s = JsonDocument.Parse(schema);
        using var i = JsonDocument.Parse(json);
        Assert.False(JsonSchema.Build(s.RootElement).Evaluate(i.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.Hierarchical }).IsValid);
    }

    [Fact]
    public void ResolvesEscapedFragmentPointers()
    {
        const string schema = """{"$defs":{"a/b":{"type":"integer"}},"$ref":"#%2F$defs%2Fa~1b"}""";
        Assert.True(JsonSchemaValidator.Validate("1", schema).IsValid);
        Assert.False(JsonSchemaValidator.Validate("\"x\"", schema).IsValid);
    }
}
