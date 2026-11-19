# Проверка структурированного ответа

`StructuredOutput.Validation` проверяет JSON по ограниченному профилю JSON Schema Draft 2020-12 с помощью стандартного `System.Text.Json` на .NET 10. Проект не содержит сторонних пакетов. `StructuredOutput.Tests` использует JsonSchema.Net 9.7.0 для сравнения. Клон исходного репозитория расположен в `upstream`.

```csharp
using StructuredOutput.Validation;

ValidationResult result = JsonSchemaValidator.Validate(json, schemaJson);
if (result.Status == ValidationStatus.InvalidInstance)
{
    IReadOnlyList<string> errors = StructuredOutputValidationAdapter.FormatErrors(result);
    // Передать errors модели для исправления ответа.
}
// MalformedJson означает ошибку синтаксиса ответа, InvalidSchema — ошибку схемы.
```

Для интеграции с показанным `StructuredOutputJsonHandler.Validate` можно использовать `result.IsValid ? SchemaValidationResult.Valid() : SchemaValidationResult.Invalid(StructuredOutputValidationAdapter.FormatErrors(result))`. Если важно различать ошибку схемы и неверный ответ, проверяйте `Status` до преобразования. `InstancePath` — JSON Pointer, пустая строка обозначает корень. Ошибка синтаксиса JSON использует путь, предоставленный `JsonException`.

Поддерживаются булевы схемы, `type`, `properties`, `required`, `additionalProperties`, `items`, `minItems`, `maxItems`, `enum`, `const`, `minLength`, `maxLength`, `pattern`, `minimum`, `maximum`, `exclusiveMinimum`, `exclusiveMaximum`, `anyOf`, `$defs` и локальные `$ref` по JSON Pointer. Аннотации `title`, `description`, `default`, `examples` и `format` принимаются, `format` не проверяет значение. Другие ключевые слова и диалекты отклоняются с `InvalidSchema`. Регулярные выражения используют .NET ECMAScript с ограничением времени, поэтому отдельные конструкции других реализаций ECMAScript могут отличаться. Ссылки на внешние ресурсы не поддерживаются. Ошибки веток `anyOf` возвращаются, когда все ветки не прошли.

Запуск сравнения и всех тестов

```powershell
dotnet test --logger 'console;verbosity=detailed'
```

Вывод теста заказа с отсутствующим `customer`, неверным `quantity` и лишним `extra`

```text
reference  [required] Required properties ["customer"] are not present
reference  [properties] Some properties did not match the required schema: ["items"]
reference  [additionalProperties] Some additional properties did not match the required schema: ["extra"]
reference /items [items] Some items do not match the required schema; failing indexes: [0]
reference /items/0 [properties] Some properties did not match the required schema: ["quantity"]
reference /items/0/quantity [minimum] 0 should be at least 1
reference /extra [] All values fail against the false schema
custom /customer [required] Required property 'customer' is missing.
custom /items/0/quantity [minimum] Value 0 violates minimum 1.
custom /extra [additionalProperties] Property 'extra' is not allowed.
```
