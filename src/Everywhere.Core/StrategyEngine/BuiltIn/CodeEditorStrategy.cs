using Everywhere.StrategyEngine.Conditions;
using Lucide.Avalonia;

namespace Everywhere.StrategyEngine.BuiltIn;

/// <summary>
/// Strategy for code editor contexts.
/// Provides commands for code review, explanation, and refactoring.
/// </summary>
public sealed class CodeEditorStrategy : StrategyBase
{
    public override string Id => "builtin.code-editor";
    public override DynamicResourceKeyBase Name => "Code Editor Assistant";
    public override DynamicResourceKeyBase Description => "Commands for code editing contexts";
    public override int Priority => 50;

    // Common code editor process names
    private static readonly string[] EditorProcessNames =
    [
        "code", "Visual Studio Code",
        "cursor",
        "devenv", "Visual Studio",
        // "idea", "idea64", "IntelliJ IDEA",
        // "pycharm", "pycharm64", "PyCharm",
        // "webstorm", "webstorm64", "WebStorm",
        // "rider", "rider64", "Rider",
        // "clion", "clion64", "CLion",
        // "goland", "goland64", "GoLand",
        // "rustrover", "RustRover",
        "sublime_text", "Sublime Text",
        "atom",
        "nvim", "vim",
    ];

    protected override IStrategyCondition? Condition =>
        CompositeCondition.Or(
            new VisualElementCondition
            {
                ProcessNames = EditorProcessNames,
                MinCount = 1
            },
            new FileCondition
            {
                Extensions =
                [
                    ".py", ".js", ".ts", ".jsx", ".tsx",
                    ".cs", ".java", ".kt", ".go", ".rs",
                    ".cpp", ".c", ".h", ".hpp",
                    ".rb", ".php", ".swift", ".scala",
                    ".vue", ".svelte", ".astro",
                    ".json", ".yaml", ".yml", ".toml",
                    ".html", ".css", ".scss", ".less",
                    ".sql", ".sh", ".bash", ".zsh", ".ps1",
                    ".md", ".markdown"
                ],
                MinCount = 1
            }
        );

    public override IEnumerable<StrategyCommand> GetCommands(StrategyContext context)
    {
        // Explain code
        yield return new StrategyCommand
        {
            Id = "code.explain",
            Name = "Explain Code",
            Description = "Explain what this code does",
            Icon = LucideIconKind.MessageSquareCode,
            Priority = 100,
            SystemPrompt = """
                You are an expert programmer and code educator.
                Explain the provided code clearly and thoroughly:
                - What does it do overall?
                - How does it work step by step?
                - What are the key concepts used?
                - Are there any notable patterns or techniques?
                Adjust your explanation to the complexity of the code.
                """,
            UserMessage = "Please explain this code.",
            SourceStrategyId = Id
        };

        // Review code
        yield return new StrategyCommand
        {
            Id = "code.review",
            Name = "Review Code",
            Description = "Get a code review with suggestions",
            Icon = LucideIconKind.SearchCode,
            Priority = 90,
            SystemPrompt = """
                You are a senior software engineer conducting a code review.
                Review the provided code and provide constructive feedback on:
                - Code quality and readability
                - Potential bugs or issues
                - Performance considerations
                - Best practices and conventions
                - Suggestions for improvement
                Be specific and provide examples where helpful.
                """,
            UserMessage = "Please review this code and suggest improvements.",
            SourceStrategyId = Id
        };

        // Refactor code
        yield return new StrategyCommand
        {
            Id = "code.refactor",
            Name = "Refactor Code",
            Description = "Suggest refactoring improvements",
            Icon = LucideIconKind.RefreshCw,
            Priority = 80,
            SystemPrompt = """
                You are an expert at code refactoring.
                Analyze the provided code and suggest refactoring improvements:
                - Identify code smells or anti-patterns
                - Suggest cleaner, more maintainable alternatives
                - Apply SOLID principles where appropriate
                - Improve naming and structure
                Provide the refactored code with explanations for each change.
                """,
            UserMessage = "Please suggest how to refactor this code.",
            SourceStrategyId = Id
        };

        // Add documentation
        yield return new StrategyCommand
        {
            Id = "code.document",
            Name = "Add Documentation",
            Description = "Generate documentation comments",
            Icon = LucideIconKind.FileCode,
            Priority = 70,
            SystemPrompt = """
                You are a technical documentation specialist.
                Generate appropriate documentation for the provided code:
                - Add doc comments (JSDoc, docstrings, XML docs, etc.)
                - Document parameters, return values, and exceptions
                - Explain complex logic with inline comments
                - Follow the conventions of the programming language
                Return the code with documentation added.
                """,
            UserMessage = "Please add documentation to this code.",
            SourceStrategyId = Id
        };

        // Find bugs
        yield return new StrategyCommand
        {
            Id = "code.find-bugs",
            Name = "Find Potential Bugs",
            Description = "Identify potential bugs or issues",
            Icon = LucideIconKind.Bug,
            Priority = 85,
            SystemPrompt = """
                You are a bug-hunting expert and static analysis specialist.
                Analyze the provided code for potential bugs and issues:
                - Logic errors
                - Edge cases not handled
                - Null/undefined issues
                - Resource leaks
                - Race conditions or concurrency issues
                - Security vulnerabilities
                For each issue found, explain the problem and suggest a fix.
                """,
            UserMessage = "Please analyze this code for potential bugs.",
            SourceStrategyId = Id
        };

        // Write tests
        yield return new StrategyCommand
        {
            Id = "code.write-tests",
            Name = "Write Tests",
            Description = "Generate unit tests for this code",
            Icon = LucideIconKind.FlaskConical,
            Priority = 65,
            SystemPrompt = """
                You are a test-driven development expert.
                Generate comprehensive unit tests for the provided code:
                - Cover happy paths and edge cases
                - Test error handling
                - Use appropriate testing framework for the language
                - Follow testing best practices (Arrange-Act-Assert, etc.)
                - Include descriptive test names
                Provide the complete test code.
                """,
            UserMessage = "Please write unit tests for this code.",
            SourceStrategyId = Id
        };

        // Optimize performance
        yield return new StrategyCommand
        {
            Id = "code.optimize",
            Name = "Optimize Performance",
            Description = "Suggest performance improvements",
            Icon = LucideIconKind.Zap,
            Priority = 60,
            SystemPrompt = """
                You are a performance optimization expert.
                Analyze the provided code for performance issues:
                - Identify inefficient algorithms or data structures
                - Find unnecessary operations or redundant code
                - Suggest caching or memoization opportunities
                - Consider memory usage and allocation
                Provide optimized code with explanations of the improvements.
                """,
            UserMessage = "Please optimize this code for better performance.",
            SourceStrategyId = Id
        };
    }
}
