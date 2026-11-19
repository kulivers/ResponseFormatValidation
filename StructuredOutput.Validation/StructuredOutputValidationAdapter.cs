namespace StructuredOutput.Validation;

// Example adapter for the string-based result used by StructuredOutputJsonHandler.
public static class StructuredOutputValidationAdapter
{
    public static IReadOnlyList<string> FormatErrors(ValidationResult result) =>
        result.Errors.Select(error => $"{(error.InstancePath.Length == 0 ? "/" : error.InstancePath)} [{error.Keyword}] {error.Message}").ToArray();
}
