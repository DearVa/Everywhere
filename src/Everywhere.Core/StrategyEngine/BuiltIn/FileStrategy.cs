using Everywhere.Chat;
using Everywhere.StrategyEngine.Conditions;
using Lucide.Avalonia;
using ZLinq;

namespace Everywhere.StrategyEngine.BuiltIn;

/// <summary>
/// Strategy for file attachment contexts.
/// Provides commands based on file types.
/// </summary>
public sealed class FileStrategy : StrategyBase
{
    public override string Id => "builtin.file";
    public override DynamicResourceKeyBase Name => "File Assistant";
    public override DynamicResourceKeyBase Description => "Commands for file attachments";
    public override int Priority => 40;

    protected override IStrategyCondition Condition =>
        new FileCondition { MinCount = 1 };

    public override IEnumerable<StrategyCommand> GetCommands(StrategyContext context)
    {
        var files = context.Attachments.AsValueEnumerable().OfType<FileAttachment>().ToList();
        if (files.Count == 0)
        {
            yield break;
        }

        // Analyze file type distribution
        var extensions = files
            .Select(f => Path.GetExtension(f.FilePath).ToLowerInvariant())
            .Where(e => !string.IsNullOrEmpty(e))
            .ToHashSet();

        var hasDocuments = extensions.Any(e => DocumentExtensions.Contains(e));
        var hasImages = extensions.Any(e => ImageExtensions.Contains(e));
        var hasData = extensions.Any(e => DataExtensions.Contains(e));
        var hasCode = extensions.Any(e => CodeExtensions.Contains(e));

        // Universal: Summarize file(s)
        yield return new StrategyCommand
        {
            Id = "file.summarize",
            Name = files.Count > 1 ? "Summarize Files" : "Summarize File",
            Description = "Summarize the content of the file(s)",
            Icon = LucideIconKind.FileText,
            Priority = 100,
            SystemPrompt = """
                You are an expert at analyzing and summarizing files.
                Summarize the provided file(s), highlighting:
                - Main content and purpose
                - Key information or findings
                - Notable sections or structure
                Adjust your summary based on the file type.
                """,
            UserMessage = "Please summarize this file.",
            SourceStrategyId = Id
        };

        // Document-specific commands
        if (hasDocuments)
        {
            yield return new StrategyCommand
            {
                Id = "file.extract-key-points",
                Name = "Extract Key Points",
                Description = "Extract and list the main points",
                Icon = LucideIconKind.ListChecks,
                Priority = 90,
                SystemPrompt = """
                    You are a document analysis expert.
                    Extract the key points from the document(s):
                    - Main arguments or findings
                    - Important facts and figures
                    - Conclusions or recommendations
                    Present them as a clear, organized list.
                    """,
                UserMessage = "Please extract the key points from this document.",
                SourceStrategyId = Id
            };

            yield return new StrategyCommand
            {
                Id = "file.translate-document",
                Name = "Translate Document",
                Description = "Translate the document content",
                Icon = LucideIconKind.Languages,
                Priority = 85,
                SystemPrompt = """
                    You are a professional document translator.
                    Translate the document content while:
                    - Preserving formatting and structure
                    - Maintaining technical accuracy
                    - Keeping the original tone
                    """,
                UserMessage = "Please translate this document.",
                SourceStrategyId = Id
            };
        }

        // Image-specific commands
        if (hasImages)
        {
            yield return new StrategyCommand
            {
                Id = "file.describe-image",
                Name = "Describe Image",
                Description = "Describe what's in the image",
                Icon = LucideIconKind.Image,
                Priority = 95,
                SystemPrompt = """
                    You are an expert at image analysis and description.
                    Describe the image in detail:
                    - Main subjects and objects
                    - Colors, composition, and style
                    - Any text visible in the image
                    - Context or setting
                    """,
                UserMessage = "Please describe this image.",
                SourceStrategyId = Id
            };

            yield return new StrategyCommand
            {
                Id = "file.extract-text-ocr",
                Name = "Extract Text (OCR)",
                Description = "Extract text from the image",
                Icon = LucideIconKind.ScanText,
                Priority = 90,
                SystemPrompt = """
                    You are an OCR specialist.
                    Extract all visible text from the image.
                    Preserve the layout and structure as much as possible.
                    If text is unclear, indicate uncertainty.
                    """,
                UserMessage = "Please extract the text from this image.",
                SourceStrategyId = Id
            };
        }

        // Data file commands
        if (hasData)
        {
            yield return new StrategyCommand
            {
                Id = "file.analyze-data",
                Name = "Analyze Data",
                Description = "Analyze the data and provide insights",
                Icon = LucideIconKind.ChartBar,
                Priority = 95,
                SystemPrompt = """
                    You are a data analysis expert.
                    Analyze the provided data and provide:
                    - Overview of the data structure
                    - Key statistics and patterns
                    - Notable trends or anomalies
                    - Actionable insights
                    """,
                UserMessage = "Please analyze this data.",
                SourceStrategyId = Id
            };

            yield return new StrategyCommand
            {
                Id = "file.visualize-data",
                Name = "Suggest Visualizations",
                Description = "Suggest how to visualize this data",
                Icon = LucideIconKind.ChartLine,
                Priority = 85,
                SystemPrompt = """
                    You are a data visualization expert.
                    Based on the data provided:
                    - Suggest appropriate chart types
                    - Explain what each visualization would show
                    - Provide code snippets for creating them (Python/JavaScript)
                    """,
                UserMessage = "How should I visualize this data?",
                SourceStrategyId = Id
            };
        }

        // Multiple files: Compare
        if (files.Count > 1)
        {
            yield return new StrategyCommand
            {
                Id = "file.compare",
                Name = "Compare Files",
                Description = "Compare the contents of these files",
                Icon = LucideIconKind.GitCompare,
                Priority = 95,
                SystemPrompt = """
                    You are a file comparison expert.
                    Compare the provided files and highlight:
                    - Similarities and differences
                    - Structural changes
                    - Content additions or removals
                    Present the comparison in a clear, organized format.
                    """,
                UserMessage = "Please compare these files.",
                SourceStrategyId = Id
            };
        }
    }

    private static readonly HashSet<string> DocumentExtensions =
    [
        ".pdf", ".doc", ".docx", ".odt", ".rtf",
        ".txt", ".md", ".markdown",
        ".ppt", ".pptx", ".odp"
    ];

    private static readonly HashSet<string> ImageExtensions =
    [
        ".png", ".jpg", ".jpeg", ".gif", ".bmp",
        ".webp", ".svg", ".ico", ".tiff", ".tif"
    ];

    private static readonly HashSet<string> DataExtensions =
    [
        ".csv", ".xlsx", ".xls", ".json", ".xml",
        ".parquet", ".sqlite", ".db"
    ];

    private static readonly HashSet<string> CodeExtensions =
    [
        ".py", ".js", ".ts", ".jsx", ".tsx",
        ".cs", ".java", ".kt", ".go", ".rs",
        ".cpp", ".c", ".h", ".hpp",
        ".rb", ".php", ".swift", ".scala"
    ];
}
