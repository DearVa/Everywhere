using System.ComponentModel;
using DynamicData;
using EverythingNet.Core;
using EverythingNet.Interfaces;
using Everywhere.Chat.Permissions;
using Everywhere.Chat.Plugins;
using Everywhere.I18N;
using Everywhere.Interop;
using Lucide.Avalonia;
using Microsoft.SemanticKernel;

namespace Everywhere.Windows.Chat.Plugins;

/// <summary>
/// A plugin that integrates with the `Everything` search engine to provide file search capabilities within the chat application.
/// </summary>
public class EverythingPlugin : BuiltInChatPlugin
{
    public override DynamicResourceKeyBase HeaderKey { get; } = new DynamicResourceKey(LocaleKey.Windows_BuiltInChatPlugin_Everything_Header);
    public override DynamicResourceKeyBase DescriptionKey { get; } = new DynamicResourceKey(LocaleKey.Windows_BuiltInChatPlugin_Everything_Description);
    public override LucideIconKind? Icon => LucideIconKind.Search;
    public override string BeautifulIcon => "avares://Everywhere/Assets/Icons/Everything.svg";

    private readonly INativeHelper _nativeHelper;
    private readonly IWatchdogManager _watchdogManager;

    public EverythingPlugin(INativeHelper nativeHelper, IWatchdogManager watchdogManager) : base("everything")
    {
        _nativeHelper = nativeHelper;
        _watchdogManager = watchdogManager;

        _functionsSource.Add(
            new NativeChatFunction(
                SearchFilesAsync,
                ChatFunctionPermissions.FileRead));
    }

    private async ValueTask EnsureEverythingRunningAsync()
    {
        if (EverythingState.IsStarted()) return;

        EverythingState.StartService(_nativeHelper.IsAdministrator, EverythingState.StartMode.Service);
        if (EverythingState.Process is { } process)
        {
            await _watchdogManager.RegisterProcessAsync(process.Id);
        }

        var maxAttempts = 5;
        do
        {
            await Task.Delay(300);
        }
        while (!EverythingState.IsReady() && maxAttempts-- > 0);
    }

    [KernelFunction("search_files")]
    [Description("Search files using Everything search engine.")]
    [DynamicResourceKey(LocaleKey.Windows_BuiltInChatPlugin_Everything_SearchFiles_Header)]
    private async Task<string> SearchFilesAsync(
        [FromKernelServices] IChatPluginUserInterface userInterface,
        [Description("Standard search pattern in Everything search engine.")] string searchPattern,
        [Description("Maximum number of results to return. Default is 50 and will be limited to 1000.")]
        int maxResults = 50,
        CancellationToken cancellationToken = default)
    {
        if (maxResults <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxResults), "maxResults must be greater than 0.");
        }

        await EnsureEverythingRunningAsync();

        return await Task.Run(
                () =>
                {
                    using var everything = new Everything();
                    var results = everything
                        .SendSearch(searchPattern, default)
                        .Take(Math.Min(maxResults, 1000))
                        .Select(CreateFileRecord);
                    userInterface.DisplaySink.AppendDynamicResourceKey(
                        new FormattedDynamicResourceKey(
                            LocaleKey.Windows_BuiltInChatPlugin_Everything_SearchFiles_DetailMessage,
                            new DirectResourceKey(everything.Count.ToString())));
                    return new FileRecords(results, everything.Count).ToString();
                },
                cancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        static FileRecord CreateFileRecord(ISearchResult result)
        {
            var fullPath = result.FullPath;
            var bytesSize = result.IsFile ? result.Size : -1;

            try
            {
                var created = result.Created;
                var modified = result.Modified;
                var attributes = (int)result.Attributes == -1 ? FileAttributes.None : (FileAttributes)result.Attributes;
                return new FileRecord(fullPath, bytesSize, created, modified, attributes);
            }
            catch
            {
                // use default values if any exception occurs
                return new FileRecord(fullPath, bytesSize, null, null, FileAttributes.None);
            }
        }
    }
}