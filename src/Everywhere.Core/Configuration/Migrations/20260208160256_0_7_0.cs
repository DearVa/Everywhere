using System.Text.Json.Nodes;

namespace Everywhere.Configuration.Migrations;

/// <summary>
/// This migration handles 0.7.0 settings changes.
/// It has 1 change:
/// 1. Rename "MaxTokens" property of CustomAssistant to "ContextLimit"
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
        }

        return modified;
    }
}