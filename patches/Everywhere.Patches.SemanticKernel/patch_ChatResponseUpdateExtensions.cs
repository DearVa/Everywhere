// ReSharper disable UnusedType.Global
// ReSharper disable UnusedMember.Global

using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using MonoMod;
using FunctionCallContent = Microsoft.Extensions.AI.FunctionCallContent;
using TextContent = Microsoft.Extensions.AI.TextContent;

namespace Everywhere.Patches.SemanticKernel;

[MonoModPatch("Microsoft.Extensions.AI.ChatResponseUpdateExtensions")]
internal static class patch_ChatResponseUpdateExtensions
{
    [MonoModReplace]
    internal static StreamingChatMessageContent ToStreamingChatMessageContent(this ChatResponseUpdate update)
    {
        StreamingChatMessageContent content = new(
            update.Role is not null ? new AuthorRole(update.Role.Value.Value) : null,
            null)
        {
            InnerContent = update.RawRepresentation,
            Metadata = update.AdditionalProperties,
            ModelId = update.ModelId
        };

        foreach (var item in update.Contents)
        {
            StreamingKernelContent? resultContent =
                item switch
                {
                    TextContent tc => new StreamingTextContent(tc.Text),
                    FunctionCallContent fcc => new StreamingFunctionCallUpdateContent(
                        fcc.CallId,
                        fcc.Name,
                        fcc.Arguments is not null ?
                            JsonSerializer.Serialize(fcc.Arguments, AbstractionsJsonContext.Default.IDictionaryStringObject) :
                            null),
                    TextReasoningContent trc => new StreamingReasoningContent(trc.Text),
                    _ => null
                };

            if (resultContent is not null)
            {
                resultContent.Metadata = item.AdditionalProperties;
                resultContent.InnerContent = item.RawRepresentation;
                resultContent.ModelId = update.ModelId;
                content.Items.Add(resultContent);
            }

            if (item is UsageContent uc)
            {
                content.Metadata = new Dictionary<string, object?>(update.AdditionalProperties ?? [])
                {
                    ["Usage"] = uc
                };
            }
        }

        return content;
    }
}