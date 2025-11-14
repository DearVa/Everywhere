using System.ComponentModel;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reflection;
using DynamicData;
using Everywhere.Chat.Permissions;
using Everywhere.Chat.Plugins;
using Everywhere.Common;
using Everywhere.Extensions;
using Everywhere.I18N;
using Lucide.Avalonia;
using Microsoft.Extensions.Logging;
using Microsoft.PowerShell;
using Microsoft.SemanticKernel;

namespace Everywhere.Windows.Chat.Plugins;

public class PowerShellPlugin : BuiltInChatPlugin
{
    public override DynamicResourceKeyBase HeaderKey { get; } = new DynamicResourceKey(LocaleKey.NativeChatPlugin_Shell_Header);

    public override DynamicResourceKeyBase DescriptionKey { get; } = new DynamicResourceKey(LocaleKey.NativeChatPlugin_Shell_Description);

    public override LucideIconKind? Icon => LucideIconKind.SquareTerminal;

    public override string BeautifulIcon => "avares://Everywhere.Windows/Assets/Icons/PowerShell.svg";

    private readonly ILogger<PowerShellPlugin> _logger;
    private readonly InitialSessionState _initialSessionState;

    public PowerShellPlugin(ILogger<PowerShellPlugin> logger) : base("powershell")
    {
        _logger = logger;

        _initialSessionState = InitialSessionState.CreateDefault2();
        _initialSessionState.ExecutionPolicy = ExecutionPolicy.Bypass;

        // Load powershell module
        // from: https://github.com/PowerShell/PowerShell/issues/25793
        var path = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location);
#if NET9_0
        var modulesPath = Path.Combine(path ?? ".", "runtimes", "win", "lib", "net9.0", "Modules");
#else
        #error Target framework not supported
#endif
        Environment.SetEnvironmentVariable(
            "PSModulePath",
            $"{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\Modules")};" +
            $"{modulesPath};" + // Import application auto-contained modules
            Environment.GetEnvironmentVariable("PSModulePath"));

        _functionsSource.Add(
            new NativeChatFunction(
                ExecuteScriptAsync,
                ChatFunctionPermissions.ShellExecute));
    }

    [KernelFunction("execute_script")]
    [Description("Execute PowerShell script and obtain its output.")]
    [DynamicResourceKey(LocaleKey.NativeChatPlugin_PowerShell_ExecuteScript_Header)]
    private async Task<string> ExecuteScriptAsync(
        [FromKernelServices] IChatPluginUserInterface userInterface,
        [Description("A concise description for user, explaining what you are doing")] string description,
        [Description("Single or multi-line")] string script,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Executing PowerShell script with description: {Description}", description);

        if (string.IsNullOrWhiteSpace(script))
        {
            throw new ArgumentException("Script cannot be null or empty.", nameof(script));
        }

        string? consentKey;
        var trimmedScript = script.AsSpan().Trim();
        if (trimmedScript.Count('\n') == 0)
        {
            // single line script, confirm with user
            var command = trimmedScript[trimmedScript.Split(' ').FirstOrDefault(new Range(0, trimmedScript.Length))].ToString();
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
            new ChatPluginCodeBlockDisplayBlock(script, "powershell"),
        };

        var consent = await userInterface.RequestConsentAsync(
            consentKey,
            new DynamicResourceKey(LocaleKey.NativeChatPlugin_PowerShell_ExecuteScript_ScriptConsent_Header),
            detailBlock,
            cancellationToken);
        if (!consent)
        {
            throw new HandledException(
                new UnauthorizedAccessException("User denied consent for PowerShell script execution."),
                new DynamicResourceKey(LocaleKey.NativeChatPlugin_PowerShell_ExecuteScript_DenyMessage),
                showDetails: false);
        }

        userInterface.DisplaySink.AppendBlocks(detailBlock.Children);

        // Create a new runspace and PowerShell instance for each execution
        // using the cached InitialSessionState. This ensures context isolation.
        using var runspace = RunspaceFactory.CreateRunspace(_initialSessionState);
        // Disable ReSharper warning as OpenAsync() cannot be awaited
        // ReSharper disable once MethodHasAsyncOverload
        runspace.Open();

        using var powerShell = PowerShell.Create(runspace);
        powerShell.AddScript($"& {{ {script} }} | Out-String"); // Ensure results are returned as string
        var invokeTask = powerShell.InvokeAsync();

        await using var registration = cancellationToken.Register(() =>
        {
            try
            {
                // ReSharper disable once AccessToDisposedClosure
                powerShell.Stop();
            }
            catch (ObjectDisposedException)
            {
                // ignore
            }
        });

        var results = await invokeTask;
        if (powerShell.HadErrors)
        {
            var errorMessage = string.Join(Environment.NewLine, powerShell.Streams.Error.Select(e => e.ToString()));
            throw new HandledException(
                new SystemException($"PowerShell script execution failed: {errorMessage}"),
                new FormattedDynamicResourceKey(
                    LocaleKey.NativeChatPlugin_PowerShell_ExecuteScript_ErrorMessage,
                    new DirectResourceKey(errorMessage)),
                showDetails: false);
        }

        var result = results.FirstOrDefault()?.ToString() ?? string.Empty;
        if (result.EndsWith(Environment.NewLine)) result = result[..^Environment.NewLine.Length]; // Trim trailing new line
        userInterface.DisplaySink.AppendCodeBlock(result, "log");

        return result;
    }
}