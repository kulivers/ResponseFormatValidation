using System.Text.Json;
using Json.Schema;
using Xunit.Abstractions;

namespace StructuredOutput.Tests;

public class ReferenceTests(ITestOutputHelper output)
{
    [Fact]
    public void ReportsErrorsForInvalidOrder()
    {
        const string schema = """
            {"type":"object","required":["customer","items"],"properties":{
              "customer":{"type":"string"},
              "items":{"type":"array","items":{"type":"object","required":["quantity"],
                "properties":{"quantity":{"type":"integer","minimum":1}}}}
            },"additionalProperties":false}
            """;
        const string json = """{"items":[{"quantity":0}],"extra":true}""";
        using var schemaDocument = JsonDocument.Parse(schema);
        using var instanceDocument = JsonDocument.Parse(json);
        var result = JsonSchema.Build(schemaDocument.RootElement).Evaluate(instanceDocument.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.Hierarchical });
        Assert.False(result.IsValid);
        Print(result);
    }

    private void Print(EvaluationResults result)
    {
        output.WriteLine($"Path: {result.InstanceLocation}, valid: {result.IsValid}");
        if (result.Errors is not null)
            foreach (var error in result.Errors)
                output.WriteLine($"  {error.Key}: {error.Value}");
        if (result.Details is not null)
            foreach (var child in result.Details)
                Print(child);
    }
}
