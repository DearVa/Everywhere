using CommunityToolkit.Mvvm.ComponentModel;
using MessagePack;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using OpenAI.Chat;

namespace Everywhere.Chat;

[MessagePackObject]
public partial class ChatUsageDetails : ObservableObject
{
    /// <remarks>
    /// This flag describes only the current model invocation. It is ignored during serialization,
    /// so it remains false after deserialization even when persisted token counts are present.
    /// </remarks>
    [IgnoreMember]
    public bool HasUsage { get; private set; }

    /// <summary>
    /// Gets the number of input tokens used. Including <see cref="CachedInputTokenCount"/>
    /// </summary>
    [Key(0)]
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CachedInputHitRate))]
    public partial long InputTokenCount { get; set; }

    /// <summary>
    /// Gets the number of cached input tokens used.
    /// </summary>
    [Key(1)]
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CachedInputHitRate))]
    public partial long CachedInputTokenCount { get; set; }

    /// <summary>
    /// Gets the number of output tokens used. Including <see cref="ReasoningTokenCount"/>
    /// </summary>
    [Key(2)]
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TokensPerSecond))]
    public partial long OutputTokenCount { get; set; }

    /// <summary>
    /// Gets the number of reasoning tokens used.
    /// </summary>
    [Key(3)]
    [ObservableProperty]
    public partial long ReasoningTokenCount { get; set; }

    /// <summary>
    /// Gets the total number of tokens used.
    /// </summary>
    [Key(4)]
    [ObservableProperty]
    public partial long TotalTokenCount { get; set; }

    /// <summary>
    /// Gets the total generation time in seconds. Time before first token and function invoking are not included.
    /// </summary>
    [Key(5)]
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TokensPerSecond))]
    public partial double TotalGenerationSeconds { get; set; }

    /// <summary>
    /// Gets whether the model API explicitly reported the cached input token count.
    /// This distinguishes an actual zero cache hit rate from providers that omit cache usage data.
    /// </summary>
    [Key(6)]
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CachedInputHitRate))]
    public partial bool HasCachedInputTokenCount { get; set; }

    [IgnoreMember]
    public double TokensPerSecond => TotalGenerationSeconds > 0d ? OutputTokenCount / TotalGenerationSeconds : 0d;

    /// <summary>
    /// Gets the proportion of input tokens served from cache, or <see langword="null"/> when it is unavailable.
    /// A positive cached count remains available for chat histories saved before
    /// <see cref="HasCachedInputTokenCount"/> was persisted.
    /// </summary>
    [IgnoreMember]
    public double? CachedInputHitRate => InputTokenCount > 0 && (CachedInputTokenCount > 0 || HasCachedInputTokenCount)
        ? Math.Clamp((double)CachedInputTokenCount / InputTokenCount, 0d, 1d)
        : null;

    /// <summary>
    /// Updates the usage details from the given <see cref="StreamingKernelContent"/>. Used in streaming scenarios.
    /// </summary>
    /// <param name="streamingKernelContent"></param>
    public void Update(StreamingKernelContent streamingKernelContent)
    {
        if (streamingKernelContent.Metadata?.TryGetValue("Usage", out var usage) is true && usage is not null)
        {
            Update(usage);
        }
    }

    /// <summary>
    /// Updates the usage details from the given <see cref="KernelContent"/>. Used in non-streaming scenarios.
    /// </summary>
    /// <param name="kernelContent"></param>
    public void Update(KernelContent kernelContent)
    {
        if (kernelContent.Metadata?.TryGetValue("Usage", out var usage) is true && usage is not null)
        {
            Update(usage);
        }
    }

    /// <summary>
    /// Accumulates the maximum token counts from another <see cref="ChatUsageDetails"/> instance. Used to aggregate usage across multiple calls.
    /// </summary>
    /// <param name="other"></param>
    /// <param name="generationSeconds"></param>
    public void Accumulate(ChatUsageDetails other, double generationSeconds)
    {
        InputTokenCount += other.InputTokenCount;
        CachedInputTokenCount += other.CachedInputTokenCount;
        OutputTokenCount += other.OutputTokenCount;
        ReasoningTokenCount += other.ReasoningTokenCount;
        TotalTokenCount += other.TotalTokenCount;

        TotalGenerationSeconds += generationSeconds;
        HasUsage |= other.HasUsage;
        HasCachedInputTokenCount |= other.HasCachedInputTokenCount;
    }

    private void Update(object? usage)
    {
        switch (usage)
        {
            case UsageContent usageContent:
            {
                HasUsage = true;
                HasCachedInputTokenCount |= usageContent.Details.CachedInputTokenCount.HasValue;
                InputTokenCount = Max(InputTokenCount, usageContent.Details.InputTokenCount);
                CachedInputTokenCount = Max(CachedInputTokenCount, usageContent.Details.CachedInputTokenCount);
                OutputTokenCount = Max(OutputTokenCount, usageContent.Details.OutputTokenCount);
                ReasoningTokenCount = Max(ReasoningTokenCount, usageContent.Details.ReasoningTokenCount);
                TotalTokenCount = Max(TotalTokenCount, usageContent.Details.TotalTokenCount);
                break;
            }
            case UsageDetails usageDetails:
            {
                HasUsage = true;
                HasCachedInputTokenCount |= usageDetails.CachedInputTokenCount.HasValue;
                InputTokenCount = Max(InputTokenCount, usageDetails.InputTokenCount);
                CachedInputTokenCount = Max(CachedInputTokenCount, usageDetails.CachedInputTokenCount);
                OutputTokenCount = Max(OutputTokenCount, usageDetails.OutputTokenCount);
                ReasoningTokenCount = Max(ReasoningTokenCount, usageDetails.ReasoningTokenCount);
                TotalTokenCount = Max(TotalTokenCount, usageDetails.TotalTokenCount);
                break;
            }
            case ChatTokenUsage chatTokenUsage: // OpenAI
            {
                HasUsage = true;
                HasCachedInputTokenCount = true;
                InputTokenCount = Max(InputTokenCount, chatTokenUsage.InputTokenCount);
                CachedInputTokenCount = Max(CachedInputTokenCount, chatTokenUsage.InputTokenDetails.CachedTokenCount);
                OutputTokenCount = Max(OutputTokenCount, chatTokenUsage.OutputTokenCount);
                ReasoningTokenCount = Max(ReasoningTokenCount, chatTokenUsage.OutputTokenDetails.ReasoningTokenCount);
                TotalTokenCount = Max(TotalTokenCount, chatTokenUsage.TotalTokenCount);
                break;
            }
        }
    }

    private static long Max(long field, long? value) => Math.Max(field, value ?? 0);
}