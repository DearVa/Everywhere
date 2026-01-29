using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Chat.Permissions;
using Lucide.Avalonia;
using Microsoft.SemanticKernel;
using ModelContextProtocol.Client;

namespace Everywhere.Chat.Plugins;

[DynamicallyAccessedMembers(
    DynamicallyAccessedMemberTypes.PublicConstructors |
    DynamicallyAccessedMemberTypes.PublicFields |
    DynamicallyAccessedMemberTypes.PublicProperties)]
public abstract partial class ChatFunction : ObservableObject
{
    public virtual DynamicResourceKeyBase HeaderKey => new DirectResourceKey(KernelFunction.Name);

    public virtual DynamicResourceKeyBase DescriptionKey => new DirectResourceKey(KernelFunction.Description);

    public LucideIconKind? Icon { get; set; }

    /// <summary>
    /// The permissions required by this function.
    /// </summary>
    public virtual ChatFunctionPermissions Permissions => ChatFunctionPermissions.AllAccess;

    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = true;

    public bool IsExperimental { get; set; }

    public abstract KernelFunction KernelFunction { get; }

    /// <summary>
    /// Converts the function call content to a user-friendly format for UI display.
    /// </summary>
    /// <param name="content"></param>
    /// <returns></returns>
    public virtual ChatPluginDisplayBlock? GetFriendlyCallContent(FunctionCallContent content) => null;
}

public sealed class NativeChatFunction : ChatFunction
{
    public override DynamicResourceKeyBase HeaderKey { get; }

    public override DynamicResourceKeyBase DescriptionKey => _descriptionKey ?? base.DescriptionKey;

    public override ChatFunctionPermissions Permissions { get; }

    public override KernelFunction KernelFunction { get; }

    private readonly DynamicResourceKey? _descriptionKey;
    private readonly IFriendlyFunctionCallContentRenderer? _renderer;

    public NativeChatFunction(Delegate method, ChatFunctionPermissions permissions, LucideIconKind? icon = null, bool isExperimental = false)
    {
        if (method.Method.GetCustomAttributes<DynamicResourceKeyAttribute>(false).FirstOrDefault() is
            { HeaderKey: { Length: > 0 } headerKey } attribute)
        {
            HeaderKey = new DynamicResourceKey(headerKey);
            if (!attribute.DescriptionKey.IsNullOrWhiteSpace())
            {
                _descriptionKey = new DynamicResourceKey(attribute.DescriptionKey);
            }
        }
        else if (method.Method.GetCustomAttributes<KernelFunctionAttribute>(false).FirstOrDefault() is { Name: { Length: > 0 } name })
        {
            HeaderKey = new DirectResourceKey(name);
        }
        else
        {
            HeaderKey = new DirectResourceKey(method.Method.Name);
        }

        Icon = icon;
        Permissions = permissions;
        KernelFunction = KernelFunctionFactory.CreateFromMethod(method);
        IsExperimental = isExperimental;

        if (method.Method.GetCustomAttributes<FriendlyFunctionCallContentRendererAttribute>(false).FirstOrDefault() is
            { RendererType: { } rendererType })
        {
            if (!typeof(IFriendlyFunctionCallContentRenderer).IsAssignableFrom(rendererType))
            {
                throw new InvalidOperationException(
                    $"The renderer type '{rendererType.FullName}' does not implement {nameof(IFriendlyFunctionCallContentRenderer)}.");
            }

            _renderer = Activator.CreateInstance(rendererType) as IFriendlyFunctionCallContentRenderer;
        }
    }

    public override ChatPluginDisplayBlock? GetFriendlyCallContent(FunctionCallContent content)
    {
        if (content.Arguments is not { Count: > 0 } arguments) return base.GetFriendlyCallContent(content);
        return _renderer?.Render(arguments) ?? base.GetFriendlyCallContent(content);
    }
}

public class McpChatFunction(McpClientTool tool) : ChatFunction
{
    public override ChatFunctionPermissions Permissions => ChatFunctionPermissions.MCP;

    public override KernelFunction KernelFunction { get; } = tool.AsKernelFunction();
}