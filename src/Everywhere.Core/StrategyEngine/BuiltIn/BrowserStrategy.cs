using Everywhere.StrategyEngine.Conditions;
using Lucide.Avalonia;

namespace Everywhere.StrategyEngine.BuiltIn;

/// <summary>
/// Strategy for browser-related contexts.
/// Provides commands for web pages, articles, and online content.
/// </summary>
public sealed class BrowserStrategy : StrategyBase
{
    public override string Id => "builtin.browser";
    public override DynamicResourceKeyBase Name => "Browser Assistant";
    public override DynamicResourceKeyBase Description => "Commands for web browsing contexts";
    public override int Priority => 50;

    // Common browser process names across platforms
    private static readonly string[] BrowserProcessNames =
    [
        "chrome", "Google Chrome",
        "firefox",
        "msedge", "Microsoft Edge",
        "safari",
        "opera",
        "brave", "Brave Browser",
        "vivaldi",
        "arc",
    ];

    protected override IStrategyCondition Condition =>
        new VisualElementCondition
        {
            ProcessNames = BrowserProcessNames,
            MinCount = 1
        };

    public override IEnumerable<StrategyCommand> GetCommands(StrategyContext context)
    {
        // Translate page
        yield return new StrategyCommand
        {
            Id = "browser.translate",
            Name = "Translate Page",
            Description = "Translate the current page content",
            Icon = LucideIconKind.Languages,
            Priority = 100,
            SystemPrompt = """
                You are a professional translator.
                Translate the provided web page content to the user's preferred language.
                Maintain the original formatting and structure where possible.
                If the content is already in the target language, inform the user.
                """,
            UserMessage = "Please translate this page content.",
            SourceStrategyId = Id
        };

        // Summarize page
        yield return new StrategyCommand
        {
            Id = "browser.summarize-page",
            Name = "Summarize Page",
            Description = "Get a quick summary of this web page",
            Icon = LucideIconKind.FileText,
            Priority = 90,
            SystemPrompt = """
                You are an expert at summarizing web content.
                Provide a clear, structured summary of the web page including:
                - Main topic/purpose
                - Key points or findings
                - Important details or conclusions
                Keep the summary concise but comprehensive.
                """,
            UserMessage = "Please summarize this web page.",
            SourceStrategyId = Id
        };

        // Extract key information
        yield return new StrategyCommand
        {
            Id = "browser.extract-info",
            Name = "Extract Key Information",
            Description = "Extract and organize important information",
            Icon = LucideIconKind.ListChecks,
            Priority = 80,
            SystemPrompt = """
                You are an information extraction specialist.
                Extract and organize the key information from this web page.
                Present the information in a clear, structured format.
                Include: names, dates, numbers, facts, and any actionable items.
                """,
            UserMessage = "Please extract the key information from this page.",
            SourceStrategyId = Id
        };

        // Explain technical content
        yield return new StrategyCommand
        {
            Id = "browser.explain",
            Name = "Explain This",
            Description = "Explain technical or complex content",
            Icon = LucideIconKind.GraduationCap,
            Priority = 70,
            SystemPrompt = """
                You are an expert educator.
                Explain the content on this page in simple, easy-to-understand terms.
                Break down complex concepts into digestible pieces.
                Use analogies and examples where helpful.
                Adjust your explanation to the apparent complexity of the content.
                """,
            UserMessage = "Please explain this content in simple terms.",
            SourceStrategyId = Id
        };

        // Fact check
        yield return new StrategyCommand
        {
            Id = "browser.fact-check",
            Name = "Fact Check",
            Description = "Verify claims made on this page",
            Icon = LucideIconKind.ShieldCheck,
            Priority = 60,
            AllowedTools = ["web_search"],
            SystemPrompt = """
                You are a fact-checking specialist.
                Review the claims made on this page and assess their accuracy.
                For each major claim:
                - State the claim
                - Assess its accuracy (true, false, partially true, unverifiable)
                - Provide context or corrections if needed
                Be objective and cite sources when possible.
                """,
            UserMessage = "Please fact-check the claims on this page.",
            SourceStrategyId = Id
        };
    }
}
