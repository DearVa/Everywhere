using System.ComponentModel;
using System.Diagnostics;
using DynamicData;
using Everywhere.Chat.Permissions;
using Everywhere.Chat.Plugins;
using Everywhere.Common;
using Everywhere.I18N;
using Everywhere.Interop;
using Lucide.Avalonia;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace Everywhere.Mac.Chat.Plugin;

public class ZshPlugin : BuiltInChatPlugin
{
    public override DynamicResourceKeyBase HeaderKey { get; } = new DynamicResourceKey(LocaleKey.MacOS_BuiltInChatPlugin_Zsh_Header);

    public override DynamicResourceKeyBase DescriptionKey { get; } = new DynamicResourceKey(LocaleKey.MacOS_BuiltInChatPlugin_Zsh_Description);

    public override LucideIconKind? Icon => LucideIconKind.SquareTerminal;

    private readonly IWatchdogManager _watchdogManager;
    private readonly ILogger<ZshPlugin> _logger;

    public ZshPlugin(IWatchdogManager watchdogManager, ILogger<ZshPlugin> logger) : base("zsh")
    {
        _watchdogManager = watchdogManager;
        _logger = logger;

        _functionsSource.Add(
            new NativeChatFunction(
                ExecuteScriptAsync,
                ChatFunctionPermissions.ShellExecute,
                isExperimental: true));
    }

    [KernelFunction("execute_script")]
    [Description("Execute Zsh script and obtain its output.")]
    [DynamicResourceKey(LocaleKey.MacOS_BuiltInChatPlugin_Zsh_ExecuteScript_Header)]
    private async Task<string> ExecuteScriptAsync(
        [FromKernelServices] IChatPluginUserInterface userInterface,
        [Description("A concise description for user, explaining what you are doing")] string description,
        [Description("Single or multi-line")] string script,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Executing Zsh script with description: {Description}", description);

        if (string.IsNullOrWhiteSpace(script))
        {
            throw new ArgumentException("Script cannot be null or empty.", nameof(script));
        }

        string? consentKey;
        var trimmedScript = script.AsSpan().Trim();
        if (!trimmedScript.Contains('\n'))
        {
            // single line script, confirm with user
            var command = trimmedScript.ToString().Split(' ')[0];
            consentKey = $"single.{command}";
        }
        else
        {
            // multi-line script, ask every time
            consentKey = null;
        }

        var detailBlock = new ChatPluginContainerDisplayBlock
        {
            new ChatPluginTextDisplayBlock(description),
            new ChatPluginCodeBlockDisplayBlock(script, "bash"),
        };

        var consent = await userInterface.RequestConsentAsync(
            consentKey,
            new DynamicResourceKey(LocaleKey.MacOS_BuiltInChatPlugin_Zsh_ExecuteScript_ScriptConsent_Header),
            detailBlock,
            cancellationToken);
        if (!consent)
        {
            throw new HandledException(
                new UnauthorizedAccessException("User denied consent for Zsh script execution."),
                new DynamicResourceKey(LocaleKey.MacOS_BuiltInChatPlugin_Zsh_ExecuteScript_DenyMessage),
                showDetails: false);
        }

        userInterface.DisplaySink.AppendBlocks(detailBlock);

        var psi = new ProcessStartInfo
        {
            FileName = "/bin/zsh",
            Arguments = "-s",
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        string result;
        using (var process = Process.Start(psi))
        {
            if (process is null)
            {
                throw new SystemException("Failed to start Zsh script execution process.");
            }

            var processId = process.Id;
            await _watchdogManager.RegisterProcessAsync(processId);
            cancellationToken.Register(() => _watchdogManager.UnregisterProcessAsync(processId));

            try
            {
                await process.StandardInput.WriteAsync(script);
                process.StandardInput.Close();

                result = await process.StandardOutput.ReadToEndAsync(cancellationToken);
                var errorOutput = await process.StandardError.ReadToEndAsync(cancellationToken);

                await process.WaitForExitAsync(cancellationToken);
                if (process.ExitCode != 0)
                {
                    throw new HandledException(
                        new SystemException($"Zsh script execution failed: {errorOutput}"),
                        new FormattedDynamicResourceKey(
                            LocaleKey.MacOS_BuiltInChatPlugin_Zsh_ExecuteScript_ErrorMessage,
                            new DirectResourceKey(errorOutput)),
                        showDetails: false);
                }
            }
            finally
            {
                await _watchdogManager.UnregisterProcessAsync(processId);
            }
        }

        userInterface.DisplaySink.AppendCodeBlock(result, "log");
        return result;
    }
}
