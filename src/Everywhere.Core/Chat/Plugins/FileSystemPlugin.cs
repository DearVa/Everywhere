using System.Buffers;
using System.ComponentModel;
using System.IO.Enumeration;
using System.Text;
using System.Text.RegularExpressions;
using Everywhere.Chat.Permissions;
using Everywhere.Common;
using Everywhere.Utilities;
using Lucide.Avalonia;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace Everywhere.Chat.Plugins;

public class FileSystemPlugin : BuiltInChatPlugin
{
    private static TimeSpan RegexTimeout => TimeSpan.FromSeconds(3);

    public override DynamicResourceKeyBase HeaderKey { get; } = new DynamicResourceKey(LocaleKey.BuiltInChatPlugin_FileSystem_Header);
    public override DynamicResourceKeyBase DescriptionKey { get; } = new DynamicResourceKey(LocaleKey.BuiltInChatPlugin_FileSystem_Description);
    public override LucideIconKind? Icon => LucideIconKind.FileBox;

    private readonly ILogger<FileSystemPlugin> _logger;

    public FileSystemPlugin(ILogger<FileSystemPlugin> logger) : base("file_system")
    {
        _logger = logger;

        _functionsSource.Edit(list =>
        {
            list.Add(
                new NativeChatFunction(
                    SearchFiles,
                    ChatFunctionPermissions.FileRead));
            list.Add(
                new NativeChatFunction(
                    GetFileInformation,
                    ChatFunctionPermissions.FileRead));
            list.Add(
                new NativeChatFunction(
                    SearchFileContentAsync,
                    ChatFunctionPermissions.FileRead));
            list.Add(
                new NativeChatFunction(
                    ReadFileAsync,
                    ChatFunctionPermissions.FileRead));
            list.Add(
                new NativeChatFunction(
                    MoveFile,
                    ChatFunctionPermissions.FileAccess));
            list.Add(
                new NativeChatFunction(
                    DeleteFilesAsync,
                    ChatFunctionPermissions.FileAccess));
            list.Add(
                new NativeChatFunction(
                    CreateDirectory,
                    ChatFunctionPermissions.FileAccess));
            list.Add(
                new NativeChatFunction(
                    WriteToFileAsync,
                    ChatFunctionPermissions.FileAccess));
            list.Add(
                new NativeChatFunction(
                    ReplaceFileContentAsync,
                    ChatFunctionPermissions.FileAccess));
        });
    }

    [KernelFunction("search_files")]
    [Description(
        "Search for files and directories in a specified path matching the given search pattern. " +
        "This tool may slow; avoid using it to enumerate large numbers of files. " +
        "DO NOT specify the value of `orderBy` when dealing with a large number of files.")]
    [DynamicResourceKey(LocaleKey.BuiltInChatPlugin_FileSystem_SearchFiles_Header, LocaleKey.BuiltInChatPlugin_FileSystem_SearchFiles_Description)]
    [FriendlyFunctionCallContentRenderer(typeof(FileRenderer))]
    private string SearchFiles(
        [FromKernelServices] IChatPluginUserInterface userInterface,
        [FromKernelServices] IChatContextManager chatContextManager,
        [FromKernelServices] ChatContext chatContext,
        string path,
        [Description("Regex search pattern to match file and directory names.")] string filePattern = ".*",
        int skip = 0,
        [Description("Maximum number of results to return. Max is 1000.")] int maxCount = 100,
        FilesOrderBy orderBy = FilesOrderBy.Default,
        CancellationToken cancellationToken = default)
    {
        if (skip < 0) skip = 0;
        if (maxCount < 0) maxCount = 0;
        if (maxCount > 1000) maxCount = 1000;

        _logger.LogDebug(
            "Searching files in path: {Path} with pattern: {SearchPattern}, skip: {Skip}, maxCount: {MaxCount}, orderBy: {OrderBy}",
            path,
            filePattern,
            skip,
            maxCount,
            orderBy);

        ExpandFullPath(chatContextManager, chatContext, ref path);
        userInterface.DisplaySink.AppendFileReferences(new ChatPluginFileReference(path));

        var regex = new Regex(filePattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout);
        var query = new RegexFileSystemInfoEnumerable(EnsureDirectoryInfo(path).FullName, regex, true)
            .WithCancellation(cancellationToken)
            .OfType<FileSystemInfo>()
            .Select(i => new FileRecord(
                i.FullName,
                i is FileInfo file ? file.Length : -1,
                i.CreationTime,
                i.LastWriteTime,
                i.Attributes));

        query = orderBy switch
        {
            FilesOrderBy.Name => query.OrderBy(item => item.FullPath),
            FilesOrderBy.Size => query.OrderBy(item => item.BytesSize),
            FilesOrderBy.Created => query.OrderBy(item => item.Created),
            FilesOrderBy.LastModified => query.OrderBy(item => item.Modified),
            _ => query
        };

        return new FileRecords(query.Skip(skip).Take(maxCount)).ToString();
    }

    [KernelFunction("get_file_info")]
    [Description("Get information about a file or directory at the specified path.")]
    [DynamicResourceKey(
        LocaleKey.BuiltInChatPlugin_FileSystem_GetFileInformation_Header,
        LocaleKey.BuiltInChatPlugin_FileSystem_GetFileInformation_Description)]
    [FriendlyFunctionCallContentRenderer(typeof(FileRenderer))]
    private string GetFileInformation(
        [FromKernelServices] IChatPluginUserInterface userInterface,
        [FromKernelServices] IChatContextManager chatContextManager,
        [FromKernelServices] ChatContext chatContext,
        string path)
    {
        _logger.LogDebug("Getting file information for path: {Path}", path);

        ExpandFullPath(chatContextManager, chatContext, ref path);
        userInterface.DisplaySink.AppendFileReferences(new ChatPluginFileReference(path));

        var info = EnsureFileSystemInfo(path);
        var sb = new StringBuilder();
        return sb.AppendLine(FileRecord.Header).Append(
            new FileRecord(
                info.FullName,
                info is FileInfo file ? file.Length : -1,
                info.CreationTime,
                info.LastWriteTime,
                info.Attributes)).ToString();
    }

    [KernelFunction("search_file_content")]
    [Description("Searches for a specific text pattern within file(s) and returns matching lines.")]
    [DynamicResourceKey(
        LocaleKey.BuiltInChatPlugin_FileSystem_SearchFileContent_Header,
        LocaleKey.BuiltInChatPlugin_FileSystem_SearchFileContent_Description)]
    [FriendlyFunctionCallContentRenderer(typeof(FileRenderer))]
    private async Task<string> SearchFileContentAsync(
        [FromKernelServices] IChatPluginUserInterface userInterface,
        [FromKernelServices] IChatContextManager chatContextManager,
        [FromKernelServices] ChatContext chatContext,
        [Description("File or directory path to search.")] string path,
        [Description("Regex pattern to search for within the file.")] string pattern,
        bool ignoreCase = true,
        [Description("Regex pattern to include files to search. Effective when path is a folder.")]
        string filePattern = ".*",
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(
            "Searching file content in path: {Path} with pattern: {SearchPattern}, ignoreCase: {IgnoreCase}, filePattern: {FilePattern}",
            path,
            pattern,
            ignoreCase,
            filePattern);

        ExpandFullPath(chatContextManager, chatContext, ref path);
        userInterface.DisplaySink.AppendFileReferences(new ChatPluginFileReference(path));

        var regexOptions = RegexOptions.Compiled | RegexOptions.Multiline;
        if (ignoreCase)
        {
            regexOptions |= RegexOptions.IgnoreCase;
        }

        var searchRegex = new Regex(pattern, regexOptions, RegexTimeout);
        var fileRegex = new Regex(filePattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);

        var fileSystemInfo = EnsureFileSystemInfo(path);

        var resultLines = new List<string>(capacity: 256);
        const int maxCollectedLines = 2000;
        const long maxSearchFileSize = 10 * 1024 * 1024; // 10 MB

        var filesToSearch = fileSystemInfo switch
        {
            FileInfo fileInfo when fileRegex.IsMatch(fileInfo.Name) => [fileInfo],
            DirectoryInfo directoryInfo => new RegexFileSystemInfoEnumerable(directoryInfo.FullName, fileRegex, true)
                .WithCancellation(cancellationToken)
                .OfType<FileInfo>(),
            _ => []
        };

        foreach (var file in filesToSearch)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // skip big or binary files
            if (file.Length > maxSearchFileSize) continue;

            await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (await EncodingDetector.DetectEncodingAsync(stream, cancellationToken: cancellationToken) is not { } encoding)
            {
                continue;
            }

            stream.Seek(0, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);
            var lineNumber = 0;

            while (resultLines.Count < maxCollectedLines && await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                lineNumber++;

                if (!searchRegex.IsMatch(line)) continue;

                // format: full path:line: content
                resultLines.Add($"----\n{file.FullName}\n{lineNumber}\t{line}");

                if (resultLines.Count >= maxCollectedLines)
                {
                    break; // stop early, enough lines collected
                }
            }
        }

        if (resultLines.Count == 0)
        {
            return "No matching lines found.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Found {resultLines.Count} matching line(s).");
        foreach (var resultLine in resultLines) sb.AppendLine(resultLine);
        return sb.ToString();
    }

    [KernelFunction("read_file")]
    [Description(
        "Reads lines from a text file at the specified path. Supports reading from a specific line and limiting the number of lines." +
        "Binary files will read as hex string, 32 bytes per line.")]
    [DynamicResourceKey(LocaleKey.BuiltInChatPlugin_FileSystem_ReadFile_Header, LocaleKey.BuiltInChatPlugin_FileSystem_ReadFile_Description)]
    [FriendlyFunctionCallContentRenderer(typeof(FileRenderer))]
    private async Task<string> ReadFileAsync(
        [FromKernelServices] IChatPluginUserInterface userInterface,
        [FromKernelServices] IChatContextManager chatContextManager,
        [FromKernelServices] ChatContext chatContext,
        string path,
        long startBytes = 0L,
        long maxReadBytes = 10240L,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(
            "Reading text file at path: {Path}, startBytes: {StartBytes}, maxReadBytes: {MaxReadBytes}",
            path,
            startBytes,
            maxReadBytes);

        ExpandFullPath(chatContextManager, chatContext, ref path);
        userInterface.DisplaySink.AppendFileReferences(new ChatPluginFileReference(path));

        var fileInfo = EnsureFileInfo(path);
        if (fileInfo.Length > 10 * 1024 * 1024)
        {
            throw new HandledException(
                new NotSupportedException("File size is larger than 10 MB, read operation is not supported."),
                new DynamicResourceKey(LocaleKey.BuiltInChatPlugin_FileSystem_ReadFile_FileTooLarge_ErrorMessage),
                showDetails: false);
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (fileInfo.Length == 0)
        {
            return new FileContent(string.Empty, false, 0, 0).ToString();
        }

        var stringBuilder = new StringBuilder();
        if (await EncodingDetector.DetectEncodingAsync(stream, cancellationToken: cancellationToken) is not { } encoding)
        {
            stream.Seek(startBytes, SeekOrigin.Begin);

            // Read binary file as hex string, 32 bytes per line
            var buffer = new byte[32];
            int bytesRead;

            while ((bytesRead = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                var hexString = BitConverter.ToString(buffer, 0, bytesRead);
                stringBuilder.AppendLine(hexString);

                if (stringBuilder.Length >= maxReadBytes)
                {
                    break;
                }
            }

            return new FileContent(stringBuilder.ToString(), true, stream.Position, fileInfo.Length - stream.Position).ToString();
        }

        stream.Seek(startBytes, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, encoding);
        var readBuffer = ArrayPool<char>.Shared.Rent(1024);
        try
        {
            int charsRead;
            long totalBytesRead = 0;
            while ((charsRead = await reader.ReadAsync(readBuffer.AsMemory(), cancellationToken)) > 0)
            {
                var bytesRead = encoding.GetByteCount(readBuffer, 0, charsRead);
                if (totalBytesRead + bytesRead > maxReadBytes)
                {
                    var allowedChars = encoding.GetMaxCharCount((int)(maxReadBytes - totalBytesRead));
                    stringBuilder.Append(readBuffer, 0, allowedChars);
                    break;
                }

                stringBuilder.Append(readBuffer, 0, charsRead);
                totalBytesRead += bytesRead;
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(readBuffer);
        }

        return new FileContent(stringBuilder.ToString(), false, stream.Position, fileInfo.Length - stream.Position).ToString();
    }

    [KernelFunction("move_file")]
    [Description("Moves or renames a file or directory.")]
    [DynamicResourceKey(LocaleKey.BuiltInChatPlugin_FileSystem_MoveFile_Header, LocaleKey.BuiltInChatPlugin_FileSystem_MoveFile_Description)]
    [FriendlyFunctionCallContentRenderer(typeof(FileRenderer))]
    private bool MoveFile(
        [FromKernelServices] IChatPluginUserInterface userInterface,
        [FromKernelServices] IChatContextManager chatContextManager,
        [FromKernelServices] ChatContext chatContext,
        [Description("Source file or directory path.")] string source,
        [Description("Destination file or directory path. Type must match the source.")] string destination)
    {
        _logger.LogDebug("Moving file from {Source} to {Destination}", source, destination);

        ExpandFullPath(chatContextManager, chatContext, ref source);
        ExpandFullPath(chatContextManager, chatContext, ref destination);
        userInterface.DisplaySink.AppendFileReferences(
            new ChatPluginFileReference(source),
            new ChatPluginFileReference(destination));

        var isFile = File.Exists(source);
        if (!isFile && !Directory.Exists(source))
        {
            throw new HandledSystemException(
                new FileNotFoundException($"{nameof(source)} does not exist."),
                HandledSystemExceptionType.FileNotFound);
        }

        var destinationDirectory = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            throw new HandledSystemException(
                new DirectoryNotFoundException($"{nameof(destination)} directory is invalid."),
                HandledSystemExceptionType.DirectoryNotFound);
        }

        try
        {
            Directory.CreateDirectory(destinationDirectory);
        }
        catch (Exception ex)
        {
            throw new HandledSystemException(
                new IOException("Failed to create destination directory.", ex),
                HandledSystemExceptionType.IOException,
                new DynamicResourceKey(LocaleKey.BuiltInChatPlugin_FileSystem_MoveFile_CreateDirectory_ErrorMessage));
        }

        if (isFile)
        {
            File.Move(source, destination, overwrite: false);
        }
        else
        {
            Directory.Move(source, destination);
        }

        return true;
    }

    [KernelFunction("delete_files")]
    [Description(
        "Delete files and directories at the specified path matching the given pattern.")]
    [DynamicResourceKey(
        LocaleKey.BuiltInChatPlugin_FileSystem_DeleteFiles_Header,
        LocaleKey.BuiltInChatPlugin_FileSystem_DeleteFiles_Description)]
    [FriendlyFunctionCallContentRenderer(typeof(FileRenderer))]
    private async Task<string> DeleteFilesAsync(
        [FromKernelServices] IChatPluginUserInterface userInterface,
        [FromKernelServices] IChatContextManager chatContextManager,
        [FromKernelServices] ChatContext chatContext,
        [Description("File or directory path to delete.")] string path,
        [Description(
            "Regex search pattern to match file and directory names (not full path). " +
            "Effective when path is a folder. " +
            "Warn that this will delete all matching files and directories recursively.")]
        string filePattern = ".*",
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Deleting file at {Path}", path);

        ExpandFullPath(chatContextManager, chatContext, ref path);
        userInterface.DisplaySink.AppendFileReferences(new ChatPluginFileReference(path));

        if (Path.GetDirectoryName(path) is null)
        {
            throw new HandledException(
                new UnauthorizedAccessException("Cannot delete root directory."),
                new DynamicResourceKey(LocaleKey.BuiltInChatPlugin_FileSystem_DeleteFiles_RootDirectory_Deletion_ErrorMessage),
                showDetails: false);
        }

        var fileSystemInfo = EnsureFileSystemInfo(path);
        if (fileSystemInfo.Attributes.HasFlag(FileAttributes.System))
        {
            throw new HandledException(
                new UnauthorizedAccessException("Cannot delete system files or directories."),
                new DynamicResourceKey(LocaleKey.BuiltInChatPlugin_FileSystem_DeleteFiles_SystemFile_Deletion_ErrorMessage),
                showDetails: false);
        }

        IEnumerable<FileSystemInfo> infosToDelete;
        switch (fileSystemInfo)
        {
            case FileInfo fileInfo:
            {
                infosToDelete = [fileInfo];
                break;
            }
            case DirectoryInfo directoryInfo:
            {
                var regex = new Regex(filePattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout);
                infosToDelete = new RegexFileSystemInfoEnumerable(directoryInfo.FullName, regex, true)
                    .WithCancellation(cancellationToken)
                    .OfType<FileSystemInfo>();
                break;
            }
            default:
            {
                return "No files or directories to delete.";
            }
        }

        var successCount = 0;
        var errorCount = 0;
        foreach (var info in infosToDelete)
        {
            if (info.Attributes.HasFlag(FileAttributes.System))
            {
                var consent = await userInterface.RequestConsentAsync(
                    "system",
                    new DynamicResourceKey(LocaleKey.BuiltInChatPlugin_FileSystem_DeleteFiles_SystemFile_DeletionConsent_Header),
                    new ChatPluginFileReferencesDisplayBlock(new ChatPluginFileReference(info.FullName)),
                    cancellationToken);
                if (!consent)
                {
                    continue;
                }
            }

            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return $"User cancelled the deletion operation. " +
                        $"{successCount} files/directories were deleted successfully, {errorCount} errors occurred.";
                }

                if (info.Exists)
                {
                    if (info is DirectoryInfo directoryInfo) directoryInfo.Delete(true);
                    else info.Delete();
                }

                successCount++;
            }
            catch
            {
                errorCount++;
            }
        }

        return errorCount == 0 ?
            $"{successCount} files/directories were deleted successfully." :
            $"{successCount} files/directories were deleted successfully, {errorCount} errors occurred.";
    }

    [KernelFunction("create_directory")]
    [Description("Creates a new directory at the specified path.")]
    [DynamicResourceKey(
        LocaleKey.BuiltInChatPlugin_FileSystem_CreateDirectory_Header,
        LocaleKey.BuiltInChatPlugin_FileSystem_CreateDirectory_Description)]
    [FriendlyFunctionCallContentRenderer(typeof(FileRenderer))]
    private void CreateDirectory(
        [FromKernelServices] IChatPluginUserInterface userInterface,
        [FromKernelServices] IChatContextManager chatContextManager,
        [FromKernelServices] ChatContext chatContext,
        string path)
    {
        _logger.LogDebug("Creating directory at {Path}", path);

        ExpandFullPath(chatContextManager, chatContext, ref path);
        userInterface.DisplaySink.AppendFileReferences(new ChatPluginFileReference(path));

        Directory.CreateDirectory(path);
    }

    [KernelFunction("write_to_file")]
    [Description("Writes content to a text file at the specified path. Binary files are not supported.")]
    [DynamicResourceKey(
        LocaleKey.BuiltInChatPlugin_FileSystem_WriteToFile_Header,
        LocaleKey.BuiltInChatPlugin_FileSystem_WriteToFile_Description)]
    [FriendlyFunctionCallContentRenderer(typeof(FileRenderer))]
    private async Task WriteToFileAsync(
        [FromKernelServices] IChatPluginUserInterface userInterface,
        [FromKernelServices] IChatContextManager chatContextManager,
        [FromKernelServices] ChatContext chatContext,
        string path,
        string? content,
        bool append = false)
    {
        _logger.LogDebug("Writing text file at {Path}, append: {Append}", path, append);

        ExpandFullPath(chatContextManager, chatContext, ref path);
        userInterface.DisplaySink.AppendFileReferences(new ChatPluginFileReference(path));

        await using var stream = new FileStream(path, append ? FileMode.Append : FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        if (await EncodingDetector.DetectEncodingAsync(stream) is not { } encoding)
        {
            throw new HandledException(
                new UnauthorizedAccessException("Cannot write to a binary file."),
                new DynamicResourceKey(LocaleKey.BuiltInChatPlugin_FileSystem_WriteToFile_BinaryFile_Write_ErrorMessage),
                showDetails: false);
        }

        await using var writer = new StreamWriter(stream, encoding);
        await writer.WriteAsync(content);
        await writer.FlushAsync();
    }

    [KernelFunction("replace_file_content")]
    [Description("Replaces content in a single text file at the specified path with regex. Binary files are not supported.")]
    [DynamicResourceKey(
        LocaleKey.BuiltInChatPlugin_FileSystem_ReplaceFileContent_Header,
        LocaleKey.BuiltInChatPlugin_FileSystem_ReplaceFileContent_Description)]
    [FriendlyFunctionCallContentRenderer(typeof(FileRenderer))]
    private async Task<string> ReplaceFileContentAsync(
        [FromKernelServices] IChatPluginUserInterface userInterface,
        [FromKernelServices] IChatContextManager chatContextManager,
        [FromKernelServices] ChatContext chatContext,
        string path,
        [Description("Regex patterns to search for within the file.")] IReadOnlyList<string> patterns,
        [Description("Replacement strings that march patterns.")] IReadOnlyList<string> replacements,
        bool ignoreCase = false,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(
            "Replacing file content at {Path} with patterns: {Patterns}, replacements: {Replacements}, ignoreCase: {IgnoreCase}",
            path,
            patterns,
            replacements,
            ignoreCase);

        if (patterns.Count == 0)
        {
            throw new HandledFunctionInvokingException(
                HandledFunctionInvokingExceptionType.ArgumentError,
                nameof(patterns),
                new ArgumentException("At least one pattern must be provided.", nameof(patterns)));
        }

        if (replacements.Count != patterns.Count)
        {
            throw new HandledFunctionInvokingException(
                HandledFunctionInvokingExceptionType.ArgumentError,
                nameof(replacements),
                new ArgumentException("Replacements count must match patterns count.", nameof(replacements)));
        }

        ExpandFullPath(chatContextManager, chatContext, ref path);
        userInterface.DisplaySink.AppendFileReferences(new ChatPluginFileReference(path));

        var fileInfo = EnsureFileInfo(path);
        if (fileInfo.Length > 10 * 1024 * 1024)
        {
            throw new HandledException(
                new NotSupportedException("File size is larger than 10 MB, replace operation is not supported."),
                new DynamicResourceKey(LocaleKey.BuiltInChatPlugin_FileSystem_ReplaceFileContent_FileTooLarge_ErrorMessage),
                showDetails: false);
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        if (await EncodingDetector.DetectEncodingAsync(stream, cancellationToken: cancellationToken) is not { } encoding)
        {
            throw new HandledException(
                new InvalidOperationException("Cannot replace content in a binary file."),
                new DynamicResourceKey(LocaleKey.BuiltInChatPlugin_FileSystem_ReplaceFileContent_BinaryFile_ErrorMessage),
                showDetails: false);
        }

        stream.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, encoding);
        var fileContent = await reader.ReadToEndAsync(cancellationToken);

        var regexOptions = RegexOptions.Compiled | RegexOptions.Multiline;
        if (ignoreCase)
        {
            regexOptions |= RegexOptions.IgnoreCase;
        }

        var replacedContent = fileContent;
        for (var i = 0; i < patterns.Count; i++)
        {
            var pattern = patterns[i];
            var replacement = i < replacements.Count ? replacements[i] : string.Empty;
            var regex = new Regex(pattern, regexOptions);
            replacedContent = regex.Replace(replacedContent, replacement);
        }

        var difference = new TextDifference(path);
        TextDifferenceBuilder.BuildLineDiff(difference, fileContent, replacedContent);

        userInterface.DisplaySink.AppendFileDifference(difference, fileContent);
        await difference.WaitForAcceptanceAsync(cancellationToken);

        // Apply all accepted changes
        if (!difference.Changes.Any(t => t.Accepted is true))
        {
            return "All changes were rejected by user.";
        }

        replacedContent = difference.Apply(fileContent);
        stream.SetLength(0);
        stream.Seek(0, SeekOrigin.Begin);
        await using var writer = new StreamWriter(stream, encoding);
        await writer.WriteAsync(replacedContent);
        await writer.FlushAsync(cancellationToken);

        return difference.ToModelSummary(fileContent, default);
    }

    private static void ExpandFullPath(IChatContextManager chatContextManager, ChatContext chatContext, ref string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new HandledFunctionInvokingException(
                HandledFunctionInvokingExceptionType.ArgumentError,
                nameof(path),
                new ArgumentException("Path cannot be null or empty.", nameof(path)));
        }

        var originalWorkingDirectory = Environment.CurrentDirectory;
        Environment.CurrentDirectory = chatContextManager.EnsureWorkingDirectory(chatContext);
        try
        {
            path = Environment.ExpandEnvironmentVariables(path);
            path = Path.GetFullPath(path);
        }
        finally
        {
            Environment.CurrentDirectory = originalWorkingDirectory;
        }
    }

    /// <summary>
    /// Ensures the specified path is a valid directory and returns its DirectoryInfo.
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    /// <exception cref="DirectoryNotFoundException"></exception>
    private static DirectoryInfo EnsureDirectoryInfo(string path)
    {
        var directoryInfo = new DirectoryInfo(path);
        if (directoryInfo.Exists) return directoryInfo;

        if (File.Exists(path))
        {
            throw new HandledException(
                new InvalidOperationException("The specified path is a file, not a directory."),
                new DynamicResourceKey(LocaleKey.BuiltInChatPlugin_FileSystem_EnsureDirectoryInfo_PathIsFile_ErrorMessage),
                showDetails: false);
        }

        throw new HandledException(
            new DirectoryNotFoundException("The specified path is not a directory or a file."),
            new DynamicResourceKey(LocaleKey.BuiltInChatPlugin_FileSystem_EnsureDirectoryInfo_PathNotExist_ErrorMessage),
            showDetails: false);
    }

    /// <summary>
    /// Ensures the specified path is a valid file and returns its FileInfo.
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    /// <exception cref="FileNotFoundException"></exception>
    private static FileInfo EnsureFileInfo(string path)
    {
        var fileInfo = new FileInfo(path);
        if (fileInfo.Exists) return fileInfo;

        if (Directory.Exists(path))
        {
            throw new HandledException(
                new InvalidOperationException("The specified path is a directory, not a file."),
                new DynamicResourceKey(LocaleKey.BuiltInChatPlugin_FileSystem_EnsureFileInfo_PathIsDirectory_ErrorMessage),
                showDetails: false);
        }

        throw new HandledException(
            new FileNotFoundException("The specified path is not a file or a directory."),
            new DynamicResourceKey(LocaleKey.BuiltInChatPlugin_FileSystem_EnsureFileInfo_PathNotExist_ErrorMessage),
            showDetails: false);

    }

    private static FileSystemInfo EnsureFileSystemInfo(string path)
    {
        if (File.Exists(path))
        {
            return new FileInfo(path);
        }

        if (Directory.Exists(path))
        {
            return new DirectoryInfo(path);
        }

        throw new HandledException(
            new FileNotFoundException("The specified path does not exist as a file or directory."),
            new DynamicResourceKey(LocaleKey.BuiltInChatPlugin_FileSystem_EnsureFileSystemInfo_PathNotExist_ErrorMessage),
            showDetails: false);
    }

    /// <summary>
    /// An enumerable that filters FileSystemInfo objects based on a regex pattern.
    /// </summary>
    /// <param name="directory"></param>
    /// <param name="regex"></param>
    /// <param name="recurseSubdirectories"></param>
    private class RegexFileSystemInfoEnumerable(string directory, Regex regex, bool recurseSubdirectories) : FileSystemEnumerable<FileSystemInfo?>(
        directory,
        (ref entry) =>
        {
            try
            {
                return !regex.IsMatch(entry.FileName) ? null : entry.ToFileSystemInfo();
            }
            catch
            {
                return null;
            }
        },
        new EnumerationOptions
        {
            IgnoreInaccessible = true,
            MatchType = MatchType.Simple,
            ReturnSpecialDirectories = false,
            MaxRecursionDepth = 32,
            RecurseSubdirectories = recurseSubdirectories
        });

    private sealed class FileRenderer : IFriendlyFunctionCallContentRenderer
    {
        public ChatPluginDisplayBlock? Render(KernelArguments arguments)
        {
            if (!arguments.TryGetValue("path", out var pathObj) || pathObj is not string path) return null;

            // arguments.TryGetValue("filePattern", out var filePatternObj);
            return new ChatPluginFileReferencesDisplayBlock(new ChatPluginFileReference(path));
        }
    }
}