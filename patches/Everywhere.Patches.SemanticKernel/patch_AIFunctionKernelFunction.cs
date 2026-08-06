// ReSharper disable UnusedType.Global
// ReSharper disable UnusedMember.Local

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using MonoMod;

namespace Everywhere.Patches.SemanticKernel;

/// <summary>
/// MonoMod donor that preserves JSON Schema definitions when Semantic Kernel converts an
/// <see cref="AIFunction"/> into its internal <c>AIFunctionKernelFunction</c> representation.
/// </summary>
/// <remarks>
/// Semantic Kernel builds <see cref="KernelParameterMetadata"/> from the root schema's
/// <c>properties</c> object and then rebuilds the function schema from those individual parameter schemas.
/// Root-level <c>$defs</c> and <c>definitions</c> are otherwise discarded while their <c>$ref</c> values remain,
/// producing invalid, dangling references. This patch moves the referenced definition table into the parameter
/// schema and rewrites each reference to its new document-relative location. Definitions are kept as references
/// instead of being expanded so recursive schemas remain finite and valid.
/// </remarks>
/// <example>
/// A parameter that originally references a root definition:
/// <code>
/// {
///   "properties": { "params": { "$ref": "#/$defs/Input" } },
///   "$defs": { "Input": { "type": "object" } }
/// }
/// </code>
/// is represented after Semantic Kernel rebuilds the schema as:
/// <code>
/// {
///   "properties": {
///     "params": {
///       "$ref": "#/properties/params/$defs/Input",
///       "$defs": { "Input": { "type": "object" } }
///     }
///   }
/// }
/// </code>
/// </example>
[MonoModPatch("Microsoft.SemanticKernel.ChatCompletion.AIFunctionKernelFunction")]
internal sealed class patch_AIFunctionKernelFunction
{
    [MonoModReplace]
    private static IReadOnlyList<KernelParameterMetadata> MapParameterMetadata(AIFunction aiFunction)
    {
        if (aiFunction is KernelFunction kernelFunction)
        {
            return kernelFunction.Metadata.Parameters;
        }

        if (!aiFunction.JsonSchema.TryGetProperty("properties", out var properties))
        {
            return [];
        }

        var requiredParameters = GetRequiredParameterNames(aiFunction.JsonSchema);
        var kernelParams = new List<KernelParameterMetadata>();
        var parameterInfos = aiFunction.UnderlyingMethod?.GetParameters().ToDictionary(p => p.Name!, StringComparer.Ordinal);
        foreach (var param in properties.EnumerateObject())
        {
            ParameterInfo? paramInfo = null;
            parameterInfos?.TryGetValue(param.Name, out paramInfo);
            var schema = param.Value.TryGetProperty("schema", out var nestedSchema) ? nestedSchema : param.Value;
            kernelParams.Add(
                new KernelParameterMetadata(param.Name, aiFunction.JsonSerializerOptions)
                {
                    Description = param.Value.TryGetProperty("description", out var description) ? description.GetString() : null,
                    DefaultValue = param.Value.TryGetProperty("default", out var defaultValue) ? defaultValue : null,
                    IsRequired = requiredParameters?.Contains(param.Name) ?? false,
                    ParameterType = paramInfo?.ParameterType,
                    Schema = new KernelJsonSchema(MakeSelfContained(schema, aiFunction.JsonSchema, param.Name)),
                });
        }

        return kernelParams;
    }

    private static JsonElement MakeSelfContained(JsonElement parameterSchema, JsonElement functionSchema, string parameterName)
    {
        var node = JsonNode.Parse(parameterSchema.GetRawText());
        if (node is not JsonObject schema)
        {
            return parameterSchema;
        }

        MakeDefinitionsSelfContained(schema, functionSchema, parameterName, "$defs");
        MakeDefinitionsSelfContained(schema, functionSchema, parameterName, "definitions");
        return JsonSerializer.SerializeToElement(node);
    }

    private static void MakeDefinitionsSelfContained(
        JsonObject parameterSchema,
        JsonElement functionSchema,
        string parameterName,
        string definitionsPropertyName)
    {
        var referencePrefix = $"#/{definitionsPropertyName}/";
        if (!ContainsReference(parameterSchema, referencePrefix))
        {
            return;
        }

        var definitions = new JsonObject();
        if (functionSchema.TryGetProperty(definitionsPropertyName, out var rootDefinitions) &&
            JsonNode.Parse(rootDefinitions.GetRawText()) is JsonObject rootDefinitionsObject)
        {
            foreach (var definition in rootDefinitionsObject)
            {
                definitions[definition.Key] = definition.Value?.DeepClone();
            }
        }

        if (parameterSchema.TryGetPropertyValue(definitionsPropertyName, out var localDefinitionsNode) &&
            localDefinitionsNode is JsonObject localDefinitions)
        {
            foreach (var definition in localDefinitions)
            {
                definitions[definition.Key] = definition.Value?.DeepClone();
            }
        }

        if (definitions.Count == 0)
        {
            return;
        }

        parameterSchema[definitionsPropertyName] = definitions;
        var escapedParameterName = parameterName.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
        RewriteReferences(
            parameterSchema,
            referencePrefix,
            $"#/properties/{escapedParameterName}/{definitionsPropertyName}/");
    }

    private static bool ContainsReference(JsonNode? node, string referencePrefix)
    {
        if (node is JsonObject obj)
        {
            if (obj.TryGetPropertyValue("$ref", out var referenceNode) &&
                referenceNode is JsonValue referenceValue &&
                referenceValue.TryGetValue<string>(out var reference) &&
                reference.StartsWith(referencePrefix, StringComparison.Ordinal))
            {
                return true;
            }

            return obj.Any(property => ContainsReference(property.Value, referencePrefix));
        }

        return node is JsonArray array && array.Any(item => ContainsReference(item, referencePrefix));
    }

    private static void RewriteReferences(JsonNode? node, string oldPrefix, string newPrefix)
    {
        if (node is JsonObject obj)
        {
            if (obj.TryGetPropertyValue("$ref", out var referenceNode) &&
                referenceNode is JsonValue referenceValue &&
                referenceValue.TryGetValue<string>(out var reference) &&
                reference.StartsWith(oldPrefix, StringComparison.Ordinal))
            {
                obj["$ref"] = string.Concat(newPrefix, reference.AsSpan(oldPrefix.Length));
            }

            foreach (var property in obj)
            {
                RewriteReferences(property.Value, oldPrefix, newPrefix);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                RewriteReferences(item, oldPrefix, newPrefix);
            }
        }
    }

    private static HashSet<string>? GetRequiredParameterNames(JsonElement schema)
    {
        HashSet<string>? requiredParameterNames = null;
        if (!schema.TryGetProperty("required", out var requiredElement) || requiredElement.ValueKind != JsonValueKind.Array)
        {
            return requiredParameterNames;
        }

        foreach (var node in requiredElement.EnumerateArray())
        {
            requiredParameterNames ??= [];
            requiredParameterNames.Add(node.GetString()!);
        }

        return requiredParameterNames;
    }
}
