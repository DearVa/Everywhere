using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;

namespace Everywhere.Patches.SemanticKernel.Tests;

/// <summary>
/// Runs against the woven <c>Microsoft.SemanticKernel.Abstractions</c> assembly and verifies that the
/// <c>AIFunctionKernelFunction</c> patch survives the real build-time MonoMod pipeline.
/// </summary>
/// <example>
/// A root <c>#/$defs/CountStatsInput</c> reference, including a recursive child reference, is expected to become
/// <c>#/properties/params/$defs/CountStatsInput</c> with the definition table embedded in the parameter schema.
/// </example>
public class AIFunctionKernelFunctionPatchTests
{
    [Test]
    public void AsKernelFunction_WithRootDefinitions_KeepsParameterReferencesSelfContained()
    {
        var function = new TestAIFunction(JsonSerializer.Deserialize<JsonElement>(
            """
            {
              "type": "object",
              "properties": {
                "params": {
                  "$ref": "#/$defs/CountStatsInput"
                }
              },
              "required": [ "params" ],
              "$defs": {
                "CountStatsInput": {
                  "type": "object",
                  "properties": {
                    "child": {
                      "$ref": "#/$defs/CountStatsInput"
                    }
                  }
                }
              }
            }
            """));

        var kernelFunction = function.AsKernelFunction();
        var parameterSchema = kernelFunction.JsonSchema.GetProperty("properties").GetProperty("params");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                parameterSchema.GetProperty("$ref").GetString(),
                Is.EqualTo("#/properties/params/$defs/CountStatsInput"));
            Assert.That(parameterSchema.TryGetProperty("$defs", out _), Is.True);
            Assert.That(
                parameterSchema
                    .GetProperty("$defs")
                    .GetProperty("CountStatsInput")
                    .GetProperty("properties")
                    .GetProperty("child")
                    .GetProperty("$ref")
                    .GetString(),
                Is.EqualTo("#/properties/params/$defs/CountStatsInput"));
            Assert.That(kernelFunction.Metadata.Parameters[0].IsRequired, Is.True);
        }
    }

    /// <summary>
    /// Minimal MCP-like function whose schema is passed through the real
    /// <see cref="AIFunctionExtensions.AsKernelFunction(AIFunction)"/> conversion path.
    /// </summary>
    /// <example>
    /// The integration test initializes this helper with a root <c>$defs</c> table and then inspects the
    /// <see cref="KernelFunction.JsonSchema"/> produced by the patched Semantic Kernel assembly.
    /// </example>
    private sealed class TestAIFunction(JsonElement jsonSchema) : AIFunction
    {
        public override string Name => "everything_count_stats";

        public override string Description => "Count file statistics";

        public override JsonElement JsonSchema => jsonSchema;

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<object?>(null);
    }
}
