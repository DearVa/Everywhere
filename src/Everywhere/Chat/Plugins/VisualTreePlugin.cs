using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Everywhere.AI;
using Everywhere.Configuration;
using Everywhere.Database;
using Everywhere.Interop;
using Everywhere.Storage;
using Lucide.Avalonia;
using Microsoft.SemanticKernel;

namespace Everywhere.Chat.Plugins;

public class VisualTreePlugin : BuiltInChatPlugin
{
    public override DynamicResourceKeyBase HeaderKey { get; } = new DynamicResourceKey(LocaleKey.NativeChatPlugin_VisualTree_Header);
    public override DynamicResourceKeyBase DescriptionKey { get; } = new DynamicResourceKey(LocaleKey.NativeChatPlugin_VisualTree_Description);
    public override LucideIconKind? Icon => LucideIconKind.Component;

    private readonly IBlobStorage _blobStorage;
    private readonly IVisualElementContext _visualElementContext;
    private readonly Settings _settings;

    public VisualTreePlugin(IBlobStorage blobStorage, IVisualElementContext visualElementContext, Settings settings) : base("visual_tree")
    {
        this._blobStorage = blobStorage;
        this._visualElementContext = visualElementContext;
        this._settings = settings;        
        _functions.Add(
            new AnonymousChatFunction(
                CaptureVisualElementByIdAsync,
                ChatFunctionPermissions.ScreenRead));
        _functions.Add(
            new AnonymousChatFunction(
                CaptureFullScreenAsync,
                ChatFunctionPermissions.ScreenRead));
        _functions.Add(
            new AnonymousChatFunction(
                ExecuteVisualActionQueueAsync,
                ChatFunctionPermissions.ScreenAccess));
    }

    /// <summary>
    /// When LLM does not support image input or image feature is disabled, and there is no visual element in the chat context, hide this plugin.
    /// </summary>
    /// <param name="chatContext"></param>
    /// <param name="customAssistant"></param>
    /// <returns></returns>
    public override IEnumerable<ChatFunction> SnapshotFunctions(ChatContext chatContext, CustomAssistant customAssistant) =>
        customAssistant.IsImageInputSupported.ActualValue is not true ||
        chatContext.VisualElements.Count == 0 ?
            [] :
            base.SnapshotFunctions(chatContext, customAssistant);

    [KernelFunction("capture_visual_element_by_id")]
    [Description("Captures a screenshot of the specified visual element by Id. Use when XML content is inaccessible or element is image-like.")]
    private Task<ChatFileAttachment> CaptureVisualElementByIdAsync([FromKernelServices] ChatContext chatContext, int elementId)
    {
    return CaptureVisualElementAsync(ResolveVisualElement(chatContext, elementId, nameof(elementId)));
    }

    [KernelFunction("capture_full_screen")]
    [Description("Captures a screenshot of the entire screen. Use when no specific visual element is available.")]
    private Task<ChatFileAttachment> CaptureFullScreenAsync()
    {
        var visualElement = _visualElementContext.ElementFromPointer(PickElementMode.Screen);
        if (visualElement is null)
        {
            throw new InvalidOperationException("No screen is available to capture.");
        }

        return CaptureVisualElementAsync(visualElement);
    }

    private async Task<ChatFileAttachment> CaptureVisualElementAsync(IVisualElement visualElement)
    {
        var bitmap = await visualElement.CaptureAsync();

        BlobEntity blob;
        using (var stream = new MemoryStream())
        {
            bitmap.Save(stream, 100);
            blob = await _blobStorage.StorageBlobAsync(stream, "image/png");
        }

        return new ChatFileAttachment(
            new DynamicResourceKey(string.Empty),
            blob.LocalPath,
            blob.Sha256,
            blob.MimeType);
    }

    [KernelFunction("execute_visual_action_queue")]
    [Description("Executes a reliable UI automation action queue. Supports clicking elements, entering text, sending shortcuts (e.g., Ctrl+V), and waiting without simulating pointer input. Useful for automating stable interactions, even when the target window is minimized. Example: [{\"Type\":\"click\",\"ElementId\":1},{\"Type\":\"set_text\",\"ElementId\":2,\"Text\":\"hello\"},{\"Type\":\"send_shortcut\",\"ElementId\":3,\"Key\":\"VK_RETURN\",\"Modifiers\":[\"VK_CONTROL\"]},{\"Type\":\"wait\",\"DelayMs\":500}]")]
    private async Task<string> ExecuteVisualActionQueueAsync(
        [FromKernelServices] ChatContext chatContext,
        [Description("Array of actions to execute in sequence")] VisualActionStep[] actions,
        CancellationToken cancellationToken = default)
    {
        if (actions == null || actions.Length == 0)
        {
            throw new ArgumentException("Action queue cannot be empty.", nameof(actions));
        }

        var index = 0;
        foreach (var step in actions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;

            if (!TryParseActionType(step.Type, out var actionType))
            {
                throw new ArgumentException($"Unsupported action type '{step.Type}' at position {index}.", nameof(actions));
            }

            try
            {
                switch (actionType)
                {
                    case VisualActionType.Click:
                    {
                        var element = ResolveVisualElement(chatContext, step.RequireElementId(), nameof(actions));
                        await element.InvokeAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    }
                    case VisualActionType.SetText:
                    {
                        var element = ResolveVisualElement(chatContext, step.RequireElementId(), nameof(actions));
                        await element.SetTextAsync(step.Text ?? string.Empty, cancellationToken).ConfigureAwait(false);
                        break;
                    }
                    case VisualActionType.SendShortcut:
                    {
                        var element = ResolveVisualElement(chatContext, step.RequireElementId(), nameof(actions));
                        var shortcut = ResolveShortcut(step, nameof(actions));
                        await element.SendShortcutAsync(shortcut, cancellationToken).ConfigureAwait(false);
                        break;
                    }
                    case VisualActionType.Wait:
                    {
                        var delay = step.ResolveDelayMilliseconds();
                        if (delay < 0)
                        {
                            throw new ArgumentException($"Delay must be non-negative for wait actions (step {index}).", nameof(actions));
                        }

                        await Task.Delay(TimeSpan.FromMilliseconds(delay), cancellationToken).ConfigureAwait(false);
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException($"Action #{index} ({actionType}) failed: {ex.Message}", ex);
            }
        }

        return $"Executed {actions.Length} action(s).";
    }

    private static bool TryParseActionType(string? value, out VisualActionType actionType)
    {
        actionType = VisualActionType.Click;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var normalized = value.Trim().ToLowerInvariant().Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty);
        return normalized switch
        {
            "click" or "invoke" or "press" => (actionType = VisualActionType.Click) == VisualActionType.Click,
            "settext" or "input" or "inputtext" or "type" or "entertext" => (actionType = VisualActionType.SetText) == VisualActionType.SetText,
            "sendshortcut" or "shortcut" or "sendkey" or "presskey" => (actionType = VisualActionType.SendShortcut) == VisualActionType.SendShortcut,
            "wait" or "delay" or "sleep" => (actionType = VisualActionType.Wait) == VisualActionType.Wait,
            _ => false
        };
    }

    private static VisualElementShortcut ResolveShortcut(VisualActionStep step, string argumentName)
    {
        if (string.IsNullOrWhiteSpace(step.Key))
        {
            throw new ArgumentException("Key is required for send_shortcut actions. Use standard VK codes (e.g., VK_RETURN, VK_CONTROL, VK_A).", argumentName);
        }

        var key = ParseVirtualKey(step.Key, argumentName);
        var modifiers = VirtualKey.None;

        if (step.Modifiers != null && step.Modifiers.Length > 0)
        {
            foreach (var modifier in step.Modifiers)
            {
                modifiers |= ParseVirtualKey(modifier, argumentName);
            }
        }

        return new VisualElementShortcut(key, modifiers);
    }

    private static VirtualKey ParseVirtualKey(string vkCode, string argumentName)
    {
        if (string.IsNullOrWhiteSpace(vkCode))
        {
            throw new ArgumentException("Virtual key code cannot be empty.", argumentName);
        }

        // Remove VK_ prefix if present
        var normalized = vkCode.Trim();
        if (normalized.StartsWith("VK_", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized.Substring(3);
        }

        // Handle single character keys (letters, digits)
        if (normalized.Length == 1)
        {
            var ch = char.ToUpperInvariant(normalized[0]);
            if (char.IsLetterOrDigit(ch))
            {
                return (VirtualKey)ch;
            }
        }

        // Handle common virtual key names using Windows API constants
        var upperNormalized = normalized.ToUpperInvariant();
        switch (upperNormalized)
        {
            case "RETURN":
            case "ENTER":
                return (VirtualKey)0x0D; // VK_RETURN
            case "CONTROL":
            case "CTRL":
                return (VirtualKey)0x11; // VK_CONTROL
            case "SHIFT":
                return (VirtualKey)0x10; // VK_SHIFT
            case "ALT":
            case "MENU":
                return (VirtualKey)0x12; // VK_MENU
            case "TAB":
                return (VirtualKey)0x09; // VK_TAB
            case "ESCAPE":
            case "ESC":
                return (VirtualKey)0x1B; // VK_ESCAPE
            case "SPACE":
                return (VirtualKey)0x20; // VK_SPACE
            case "BACKSPACE":
            case "BACK":
                return (VirtualKey)0x08; // VK_BACK
            case "DELETE":
            case "DEL":
                return (VirtualKey)0x2E; // VK_DELETE
            case "LEFT":
                return (VirtualKey)0x25; // VK_LEFT
            case "RIGHT":
                return (VirtualKey)0x27; // VK_RIGHT
            case "UP":
                return (VirtualKey)0x26; // VK_UP
            case "DOWN":
                return (VirtualKey)0x28; // VK_DOWN
            case "F1":
                return (VirtualKey)0x70; // VK_F1
            case "F2":
                return (VirtualKey)0x71; // VK_F2
            case "F3":
                return (VirtualKey)0x72; // VK_F3
            case "F4":
                return (VirtualKey)0x73; // VK_F4
            case "F5":
                return (VirtualKey)0x74; // VK_F5
            case "F6":
                return (VirtualKey)0x75; // VK_F6
            case "F7":
                return (VirtualKey)0x76; // VK_F7
            case "F8":
                return (VirtualKey)0x77; // VK_F8
            case "F9":
                return (VirtualKey)0x78; // VK_F9
            case "F10":
                return (VirtualKey)0x79; // VK_F10
            case "F11":
                return (VirtualKey)0x7A; // VK_F11
            case "F12":
                return (VirtualKey)0x7B; // VK_F12
        }

        // Try to parse as VirtualKey enum
        if (Enum.TryParse<VirtualKey>(normalized, ignoreCase: true, out var virtualKey))
        {
            return virtualKey;
        }

        throw new ArgumentException($"Invalid virtual key code '{vkCode}'. Use standard VK codes (e.g., VK_RETURN, VK_CONTROL, VK_A, D).", argumentName);
    }

    private static IVisualElement ResolveVisualElement(ChatContext chatContext, int elementId, string? argumentName = null)
    {
        if (!chatContext.VisualElements.TryGetValue(elementId, out var visualElement))
        {
            throw new ArgumentException($"Visual element with id '{elementId}' is not found or has been destroyed.", argumentName ?? nameof(elementId));
        }

        return visualElement;
    }

    /// <summary>
    /// Represents a single action in the visual automation queue.
    /// </summary>
    public sealed record VisualActionStep
    {
        /// <summary>
        /// The type of action: "click", "set_text", "send_shortcut", or "wait"
        /// </summary>
        [Description("Type of action: click, set_text, send_shortcut, or wait")]
        public string? Type { get; init; }

        /// <summary>
        /// The ID of the target visual element (required for click, set_text, send_shortcut actions; not used for wait actions)
        /// </summary>
        [Description("ID of the target visual element (required for click, set_text, send_shortcut actions)")]
        public int? ElementId { get; init; }

        /// <summary>
        /// The text to input (for set_text actions)
        /// </summary>
        [Description("Text to input (for set_text action)")]
        public string? Text { get; init; }

        /// <summary>
        /// The virtual key code to send (e.g., VK_RETURN, VK_A). Required for send_shortcut actions.
        /// </summary>
        [Description("Virtual key code (e.g., VK_RETURN, VK_A) for send_shortcut action")]
        public string? Key { get; init; }

        /// <summary>
        /// Modifier keys (e.g., VK_CONTROL, VK_SHIFT) for send_shortcut actions
        /// </summary>
        [Description("Modifier virtual key codes (e.g., VK_CONTROL, VK_SHIFT) for send_shortcut action")]
        public string[]? Modifiers { get; init; }

        /// <summary>
        /// Delay in milliseconds (for wait actions)
        /// </summary>
        [Description("Delay in milliseconds (for wait action)")]
        public int? DelayMs { get; init; }

        public int RequireElementId() => ElementId ?? throw new ArgumentException("Element id is required for this action.");

        public int ResolveDelayMilliseconds()
        {
            if (DelayMs is null)
            {
                throw new ArgumentException("Delay must be provided for wait actions.");
            }

            return DelayMs.Value;
        }
    }

    private enum VisualActionType
    {
        Click,
        SetText,
        Wait,
        SendShortcut
    }
}