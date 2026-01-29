using System.Text.Json;
using Anthropic.Exceptions;
using Anthropic.Models.Beta;
using Anthropic.Models.Messages;
using Batches = Anthropic.Models.Messages.Batches;
using Files = Anthropic.Models.Beta.Files;
using Messages = Anthropic.Models.Beta.Messages;
using MessagesBatches = Anthropic.Models.Beta.Messages.Batches;
using Type = Anthropic.Models.Messages.Type;

namespace Anthropic.Core;

/// <summary>
/// The base class for all API objects with properties.
///
/// <para>API objects such as enums do not inherit from this class.</para>
/// </summary>
public abstract record class ModelBase
{
    protected ModelBase(ModelBase modelBase)
    {
        // Nothing to copy. Just so that subclasses can define copy constructors.
    }

    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters =
        {
            new FrozenDictionaryConverterFactory(),
            new ApiEnumConverter<string, MediaType>(),
            new ApiEnumConverter<string, Ttl>(),
            new ApiEnumConverter<string, Role>(),
            new ApiEnumConverter<string, Model>(),
            new ApiEnumConverter<string, StopReason>(),
            new ApiEnumConverter<string, Type>(),
            new ApiEnumConverter<string, UsageServiceTier>(),
            new ApiEnumConverter<string, ErrorCode>(),
            new ApiEnumConverter<string, WebSearchToolResultErrorErrorCode>(),
            new ApiEnumConverter<string, ServiceTier>(),
            new ApiEnumConverter<string, Batches::ProcessingStatus>(),
            new ApiEnumConverter<string, Batches::ServiceTier>(),
            new ApiEnumConverter<string, AnthropicBeta>(),
            new ApiEnumConverter<string, Messages::MediaType>(),
            new ApiEnumConverter<string, Messages::ErrorCode>(),
            new ApiEnumConverter<
                string,
                Messages::BetaBashCodeExecutionToolResultErrorParamErrorCode
            >(),
            new ApiEnumConverter<string, Messages::Ttl>(),
            new ApiEnumConverter<string, Messages::AllowedCaller>(),
            new ApiEnumConverter<string, Messages::BetaCodeExecutionTool20250825AllowedCaller>(),
            new ApiEnumConverter<string, Messages::BetaCodeExecutionToolResultErrorCode>(),
            new ApiEnumConverter<string, Messages::BetaMemoryTool20250818AllowedCaller>(),
            new ApiEnumConverter<string, Messages::Role>(),
            new ApiEnumConverter<string, Messages::Effort>(),
            new ApiEnumConverter<string, Messages::Name>(),
            new ApiEnumConverter<string, Messages::BetaServerToolUseBlockParamName>(),
            new ApiEnumConverter<string, Messages::Type>(),
            new ApiEnumConverter<string, Messages::BetaSkillParamsType>(),
            new ApiEnumConverter<string, Messages::BetaStopReason>(),
            new ApiEnumConverter<
                string,
                Messages::BetaTextEditorCodeExecutionToolResultErrorErrorCode
            >(),
            new ApiEnumConverter<
                string,
                Messages::BetaTextEditorCodeExecutionToolResultErrorParamErrorCode
            >(),
            new ApiEnumConverter<string, Messages::FileType>(),
            new ApiEnumConverter<
                string,
                Messages::BetaTextEditorCodeExecutionViewResultBlockParamFileType
            >(),
            new ApiEnumConverter<string, Messages::BetaToolAllowedCaller>(),
            new ApiEnumConverter<string, Messages::BetaToolType>(),
            new ApiEnumConverter<string, Messages::BetaToolBash20241022AllowedCaller>(),
            new ApiEnumConverter<string, Messages::BetaToolBash20250124AllowedCaller>(),
            new ApiEnumConverter<string, Messages::BetaToolComputerUse20241022AllowedCaller>(),
            new ApiEnumConverter<string, Messages::BetaToolComputerUse20250124AllowedCaller>(),
            new ApiEnumConverter<string, Messages::BetaToolComputerUse20251124AllowedCaller>(),
            new ApiEnumConverter<string, Messages::BetaToolSearchToolBm25_20251119Type>(),
            new ApiEnumConverter<string, Messages::BetaToolSearchToolBm25_20251119AllowedCaller>(),
            new ApiEnumConverter<string, Messages::BetaToolSearchToolRegex20251119Type>(),
            new ApiEnumConverter<string, Messages::BetaToolSearchToolRegex20251119AllowedCaller>(),
            new ApiEnumConverter<string, Messages::BetaToolSearchToolResultErrorErrorCode>(),
            new ApiEnumConverter<string, Messages::BetaToolSearchToolResultErrorParamErrorCode>(),
            new ApiEnumConverter<string, Messages::BetaToolTextEditor20241022AllowedCaller>(),
            new ApiEnumConverter<string, Messages::BetaToolTextEditor20250124AllowedCaller>(),
            new ApiEnumConverter<string, Messages::BetaToolTextEditor20250429AllowedCaller>(),
            new ApiEnumConverter<string, Messages::BetaToolTextEditor20250728AllowedCaller>(),
            new ApiEnumConverter<string, Messages::BetaUsageServiceTier>(),
            new ApiEnumConverter<string, Messages::BetaWebFetchTool20250910AllowedCaller>(),
            new ApiEnumConverter<string, Messages::BetaWebFetchToolResultErrorCode>(),
            new ApiEnumConverter<string, Messages::BetaWebSearchTool20250305AllowedCaller>(),
            new ApiEnumConverter<string, Messages::BetaWebSearchToolResultErrorCode>(),
            new ApiEnumConverter<string, Messages::ServiceTier>(),
            new ApiEnumConverter<string, MessagesBatches::ProcessingStatus>(),
            new ApiEnumConverter<string, MessagesBatches::ServiceTier>(),
            new ApiEnumConverter<string, Files::Type>(),
        },
    };

    internal static readonly JsonSerializerOptions ToStringSerializerOptions = new(
        SerializerOptions
    )
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Validates that all required fields are set and that each field's value is of the expected type.
    ///
    /// <para>This is useful for instances constructed from raw JSON data (e.g. deserialized from an API response).</para>
    ///
    /// <exception cref="AnthropicInvalidDataException">
    /// Thrown when the instance does not pass validation.
    /// </exception>
    /// </summary>
    public abstract void Validate();
}
