using Everywhere.Chat;
using Everywhere.StrategyEngine.Conditions;
using Lucide.Avalonia;
using ZLinq;

namespace Everywhere.StrategyEngine.BuiltIn;

/// <summary>
/// Strategy for text selection contexts.
/// Provides commands when user has selected text.
/// </summary>
public sealed class TextSelectionStrategy : StrategyBase
{
    public override string Id => "builtin.text-selection";
    public override DynamicResourceKeyBase Name => "Text Selection Assistant";
    public override DynamicResourceKeyBase Description => "Commands for selected text";
    public override int Priority => 60;

    protected override IStrategyCondition Condition =>
        new TextCondition
        {
            TargetType = AttachmentType.TextSelection,
            MinLength = 1,
            MinCount = 1
        };

    public override IEnumerable<StrategyCommand> GetCommands(StrategyContext context)
    {
        // Get the selected text for context-aware commands
        var textSelection = context.Attachments.AsValueEnumerable().OfType<TextSelectionAttachment>().FirstOrDefault();
        var textLength = textSelection?.Text.Length ?? 0;

        // Translate
        yield return new StrategyCommand
        {
            Id = "text.translate",
            Name = "Translate",
            Description = "Translate the selected text",
            Icon = LucideIconKind.Languages,
            Priority = 100,
            SystemPrompt = """
                You are a professional translator.
                Translate the provided text accurately while preserving meaning and tone.
                If the source language is unclear, detect it first.
                Translate to the user's preferred language (typically their system language).
                Provide the translation directly without excessive explanation.
                """,
            UserMessage = "Please translate this text.",
            SourceStrategyId = Id
        };

        // Explain/Define
        yield return new StrategyCommand
        {
            Id = "text.explain",
            Name = "Explain / Define",
            Description = "Explain or define the selected text",
            Icon = LucideIconKind.BookOpen,
            Priority = 95,
            SystemPrompt = """
                You are a knowledgeable assistant.
                Explain or define the selected text clearly:
                - If it's a word or phrase: provide a definition
                - If it's a concept: explain it thoroughly
                - If it's code: explain what it does
                - If it's a name: provide relevant information
                Be concise but comprehensive.
                """,
            UserMessage = "What does this mean? Please explain.",
            SourceStrategyId = Id
        };

        // Summarize (only for longer text)
        if (textLength > 100)
        {
            yield return new StrategyCommand
            {
                Id = "text.summarize",
                Name = "Summarize",
                Description = "Summarize the selected text",
                Icon = LucideIconKind.FileText,
                Priority = 90,
                SystemPrompt = """
                    You are an expert at creating concise summaries.
                    Summarize the provided text, capturing the main points.
                    Be concise but don't miss important details.
                    Use bullet points for multiple distinct points.
                    """,
                UserMessage = "Please summarize this text.",
                SourceStrategyId = Id
            };
        }

        // Rewrite/Rephrase
        yield return new StrategyCommand
        {
            Id = "text.rewrite",
            Name = "Rewrite / Improve",
            Description = "Rewrite for clarity or different tone",
            Icon = LucideIconKind.PenLine,
            Priority = 85,
            SystemPrompt = """
                You are an expert editor and writing assistant.
                Rewrite the provided text to improve it:
                - Fix grammar and spelling errors
                - Improve clarity and flow
                - Make it more concise if needed
                - Maintain the original meaning and intent
                Provide the improved version directly.
                """,
            UserMessage = "Please rewrite this text to improve it.",
            SourceStrategyId = Id
        };

        // Search/Research
        yield return new StrategyCommand
        {
            Id = "text.research",
            Name = "Research This",
            Description = "Search for more information",
            Icon = LucideIconKind.Search,
            Priority = 80,
            AllowedTools = ["web_search"],
            SystemPrompt = """
                You are a research assistant.
                Research the provided text/topic and provide relevant information:
                - Find factual information
                - Provide context and background
                - Include relevant sources when possible
                Be thorough but focused on what's most relevant.
                """,
            UserMessage = "Please research this and tell me more.",
            SourceStrategyId = Id
        };

        // Grammar check
        yield return new StrategyCommand
        {
            Id = "text.grammar",
            Name = "Check Grammar",
            Description = "Check and correct grammar",
            Icon = LucideIconKind.SpellCheck,
            Priority = 75,
            SystemPrompt = """
                You are a grammar and writing expert.
                Check the provided text for:
                - Grammar errors
                - Spelling mistakes
                - Punctuation issues
                - Style improvements
                List each issue found and provide the corrected text.
                """,
            UserMessage = "Please check this text for grammar and spelling errors.",
            SourceStrategyId = Id
        };
    }
}
