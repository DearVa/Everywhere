using System.Reflection;
using System.Text.Json;
using Microsoft.SemanticKernel.Connectors.Google;

namespace Everywhere.Core.Tests.AI;

/// <summary>
/// Verifies that the patched Gemini connector resolves local references and emits only Gemini-supported schema
/// keywords while retaining unsupported constraint information in descriptions.
/// </summary>
/// <example>
/// The fixture supplies both <c>#/$defs/Input</c> and
/// <c>#/properties/nestedParams/$defs/Input</c>; the transformed schema must inline both targets and remove
/// <c>$ref</c>, <c>$defs</c>, <c>propertyNames</c>, and <c>additionalProperties</c> from the Gemini request.
/// </example>
public class GeminiToolSchemaTransformerTests
{
    [Test]
    public void TransformToOpenApi3Schema_WithReferencesAndUnsupportedKeywords_ProducesSelfContainedWhitelist()
    {
        var schema = JsonSerializer.Deserialize<JsonElement>(
            """
            {
              "type": "object",
              "condition": "Duplicate function description",
              "properties": {
                "params": {
                  "$ref": "#/$defs/Input"
                },
                "nestedParams": {
                  "$ref": "#/properties/nestedParams/$defs/Input",
                  "$defs": {
                    "Input": {
                      "type": "object",
                      "properties": {
                        "path": { "type": "string" }
                      }
                    }
                  }
                },
                "data": {
                  "type": "object",
                  "propertyNames": { "type": "string" },
                  "additionalProperties": { "type": "string" }
                },
                "mode": {
                  "const": "fast"
                }
              },
              "$defs": {
                "Input": {
                  "type": "object",
                  "properties": {
                    "query": {
                      "type": "string",
                      "exclusiveMinimum": 0
                    }
                  },
                  "required": [ "query" ]
                }
              }
            }
            """);

        var transformed = TransformToOpenApi3Schema(schema);
        var properties = transformed.GetProperty("properties");
        var parameters = properties.GetProperty("params");
        var nestedParameters = properties.GetProperty("nestedParams");
        var data = properties.GetProperty("data");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(transformed.TryGetProperty("$defs", out _), Is.False);
            Assert.That(transformed.TryGetProperty("condition", out _), Is.False);
            Assert.That(parameters.TryGetProperty("$ref", out _), Is.False);
            Assert.That(parameters.GetProperty("properties").GetProperty("query").GetProperty("type").GetString(), Is.EqualTo("string"));
            Assert.That(nestedParameters.TryGetProperty("$ref", out _), Is.False);
            Assert.That(nestedParameters.TryGetProperty("$defs", out _), Is.False);
            Assert.That(
                nestedParameters.GetProperty("properties").GetProperty("path").GetProperty("type").GetString(),
                Is.EqualTo("string"));
            Assert.That(
                parameters.GetProperty("properties").GetProperty("query").GetProperty("description").GetString(),
                Does.Contain("exclusiveMinimum=0"));
            Assert.That(data.TryGetProperty("propertyNames", out _), Is.False);
            Assert.That(data.TryGetProperty("additionalProperties", out _), Is.False);
            Assert.That(data.GetProperty("description").GetString(), Does.Contain("additionalProperties"));
            Assert.That(properties.GetProperty("mode").GetProperty("enum")[0].GetString(), Is.EqualTo("fast"));
        }
    }

    private static JsonElement TransformToOpenApi3Schema(JsonElement schema)
    {
        var geminiRequestType = typeof(GeminiPromptExecutionSettings).Assembly.GetType(
            "Microsoft.SemanticKernel.Connectors.Google.Core.GeminiRequest",
            throwOnError: true)!;
        var method = geminiRequestType.GetMethod(
            "TransformToOpenApi3Schema",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (JsonElement)method.Invoke(null, [schema])!;
    }
}
