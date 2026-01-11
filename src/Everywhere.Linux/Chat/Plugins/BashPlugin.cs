using System.ComponentModel;
using System.Diagnostics;
using DynamicData;
using Everywhere.Chat;
using Everywhere.Chat.Permissions;
using Everywhere.Chat.Plugins;
using Everywhere.Common;
using Everywhere.I18N;
using Lucide.Avalonia;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace Everywhere.Linux.Chat.Plugin;

public class BashPlugin : BuiltInChatPlugin
{
    public override DynamicResourceKeyBase HeaderKey { get; } = new DynamicResourceKey(LocaleKey.Linux_BuiltInChatPlugin_Bash_Header);

    public override DynamicResourceKeyBase DescriptionKey { get; } = new DynamicResourceKey(LocaleKey.Linux_BuiltInChatPlugin_Bash_Description);

    public override LucideIconKind? Icon => LucideIconKind.SquareTerminal;

    public override bool IsDefaultEnabled => false;

    private readonly ILogger<BashPlugin> _logger;

    public BashPlugin(ILogger<BashPlugin> logger) : base("bash")
    {
        _logger = logger;

        _functionsSource.Add(
            new NativeChatFunction(
                ExecuteScriptAsync,
                ChatFunctionPermissions.ShellExecute));
    }

    [KernelFunction("execute_script")]
    [Description("Execute Bash script and obtain its output.")]
    [DynamicResourceKey(LocaleKey.Linux_BuiltInChatPlugin_Bash_ExecuteScript_Header)]
    private async Task<string> ExecuteScriptAsync(
        [FromKernelServices] IChatPluginUserInterface userInterface,
        [FromKernelServices] IChatContextManager chatContextManager,
        [FromKernelServices] ChatContext chatContext,
        [Description("A concise description for user, explaining what you are doing")] string description,
        [Description("Single or multi-line")] string script,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Executing Bash script with description: {Description}", description);

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
            new DynamicResourceKey(LocaleKey.Linux_BuiltInChatPlugin_Bash_ExecuteScript_ScriptConsent_Header),
            detailBlock,
            cancellationToken);
        if (!consent)
        {
            throw new HandledException(
                new UnauthorizedAccessException("User denied consent for Bash script execution."),
                new DynamicResourceKey(LocaleKey.Linux_BuiltInChatPlugin_Bash_ExecuteScript_DenyMessage),
                showDetails: false);
        }

        userInterface.DisplaySink.AppendBlocks(detailBlock);

        var workingDirectory = chatContextManager.EnsureWorkingDirectory(chatContext);
        var scopeName = $"everywhere-bash-{Guid.NewGuid():N}";
        
        // Use systemd-run to create a scope for bash process
        var psi = new ProcessStartInfo
        {
            FileName = "systemd-run",
            Arguments = $"--user --scope --quiet --unit={scopeName} /bin/bash -s",
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };

        string result;
        using (var process = Process.Start(psi))
        {
            if (process is null) throw new SystemException("Failed to start Bash process.");
            await using var registration = cancellationToken.Register(() =>
            {
                _ = Task.Run(() =>
                {
                    // Stop the scope gracefully
                    Process.Start("systemctl", $"--user stop {scopeName}.scope")?.WaitForExit(2000);
                });
            });

            await process.StandardInput.WriteAsync(script);
            process.StandardInput.Close();
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

            await Task.WhenAll(outputTask, errorTask);
            await process.WaitForExitAsync(cancellationToken);

            result = await outputTask;
            var errorOutput = await errorTask;

            if (process.ExitCode != 0)
            {
                throw new HandledException(
                    new SystemException($"Bash failed: {errorOutput}"),
                    new FormattedDynamicResourceKey(
                        LocaleKey.Linux_BuiltInChatPlugin_Bash_ExecuteScript_ErrorMessage,
                        new DirectResourceKey(errorOutput.Trim())),
                    showDetails: false);
            }
        }

        userInterface.DisplaySink.AppendCodeBlock(result.Trim(), "log");
        return result;
    }
}
