using System.Text.Json.Nodes;
using Everywhere.AI;

namespace Everywhere.Configuration.Migrations;

/// <summary>
/// This migration handles 0.7.0 settings changes.
/// It migrates to the new model definition structure. For each CustomAssistant,
/// 1. Rename "MaxTokens"  to "ContextLimit"
/// 2. Rename "IsDeepThinkingSupported" to "SupportsReasoning"
/// 3. Rename "IsFunctionCallingSupported" to "SupportsToolCall"
/// 4. Process "Modalities" node to extract "InputModalities" and "OutputModalities".
/// </summary>
public class _20260208160256_0_7_0 : SettingsMigration
{
    public override Version Version => new(0, 7, 0);

    protected override IEnumerable<Func<JsonObject, bool>> MigrationTasks =>
    [
        MigrateTask1
    ];

    private static bool MigrateTask1(JsonObject root)
    {
        var customAssistantsNode = GetPathNode(root, "Model.CustomAssistants");
        if (customAssistantsNode is not JsonArray customAssistantsArray) return false;

        var modified = false;
        foreach (var assistantNode in customAssistantsArray)
        {
            if (assistantNode is not JsonObject assistantObj) continue;

            // Rename "MaxTokens" to "ContextLimit"
            if (assistantObj.TryGetPropertyValue("MaxTokens", out var maxTokensNode))
            {
                assistantObj["ContextLimit"] = maxTokensNode?.DeepClone();
                assistantObj.Remove("MaxTokens");
                modified = true;
            }

            // Rename "IsDeepThinkingSupported" to "SupportsReasoning"
            if (assistantObj.TryGetPropertyValue("IsDeepThinkingSupported", out var reasoningNode))
            {
                assistantObj["SupportsReasoning"] = reasoningNode?.DeepClone();
                assistantObj.Remove("IsDeepThinkingSupported");
                modified = true;
            }

            // Rename "IsFunctionCallingSupported" to "SupportsToolCall"
            if (assistantObj.TryGetPropertyValue("IsFunctionCallingSupported", out var toolCallNode))
            {
                assistantObj["SupportsToolCall"] = toolCallNode?.DeepClone();
                assistantObj.Remove("IsFunctionCallingSupported");
                modified = true;
            }

            var inputModalities = Modalities.Text;
            if (assistantObj.TryGetPropertyValue("IsImageInputSupported", out var imageSupportedNode))
            {
                if (imageSupportedNode?.GetValue<bool>() == true) inputModalities |= Modalities.Image;
                assistantObj.Remove("IsImageInputSupported");
                modified = true;
            }

            assistantObj["InputModalities"] = JsonValue.Create(inputModalities);
            assistantObj["OutputModalities"] = JsonValue.Create(Modalities.Text);
        }

        return modified;
    }
}