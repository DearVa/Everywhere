using Avalonia.Threading;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.Views;
using Microsoft.Extensions.Logging;

namespace Everywhere.Initialization;

/// <summary>
/// Initializes the chat window hotkey listener and preloads the chat window.
/// </summary>
/// <param name="settings"></param>
/// <param name="shortcutListener"></param>
/// <param name="visualElementContext"></param>
/// <param name="logger"></param>
public class ChatWindowInitializer(
    Settings settings,
    IShortcutListener shortcutListener,
    IVisualElementContext visualElementContext,
    ILogger<ChatWindowInitializer> logger
) : IAsyncInitializer
{
    public AsyncInitializerPriority Priority => AsyncInitializerPriority.Startup;

    private readonly Lock _syncLock = new();

    private IDisposable? _chatShortcutSubscription;
    private IDisposable? _textSelectionSubscription;

    public Task InitializeAsync()
    {
        var chatWindow = ServiceLocator.Resolve<ChatWindow>();
        var chatWindowViewModel = chatWindow.ViewModel;
        var chatWindowHandle = chatWindow.TryGetPlatformHandle()?.Handle ?? 0;

        // Preload ChatWindow to avoid delay on first open
        chatWindow.Initialize();

        // initialize hotkey listener
        settings.ChatWindow.PropertyChanged += (_, args) =>
        {
            switch (args.PropertyName)
            {
                case nameof(ChatWindowSettings.Shortcut):
                {
                    HandleChatShortcutChanged(chatWindow, chatWindowHandle, settings.ChatWindow.Shortcut);
                    break;
                }
                case nameof(ChatWindowSettings.AutomaticallyAddTextSelection):
                {
                    HandleTextSelectionChanged(chatWindowViewModel, settings.ChatWindow.AutomaticallyAddTextSelection);
                    break;
                }
            }
        };

        HandleChatShortcutChanged(chatWindow, chatWindowHandle, settings.ChatWindow.Shortcut);
        HandleTextSelectionChanged(chatWindowViewModel, settings.ChatWindow.AutomaticallyAddTextSelection);

        return Task.CompletedTask;
    }

    private void HandleChatShortcutChanged(ChatWindow chatWindow, nint chatWindowHandle, KeyboardShortcut shortcut)
    {
        using var _ = _syncLock.EnterScope();

        _chatShortcutSubscription?.Dispose();
        if (!shortcut.IsValid) return;

        _chatShortcutSubscription = shortcutListener.Register(
            shortcut,
            () => ThreadPool.QueueUserWorkItem(_ =>
            {
                IVisualElement? element;
                nint? hWnd;
                try
                {
                    element = visualElementContext.FocusedElement ??
                        visualElementContext.ElementFromPointer()?
                            .GetAncestors(true)
                            .LastOrDefault();
                    hWnd = element?.NativeWindowHandle;
                }
                catch
                {
                    element = null;
                    hWnd = null;
                }

                Dispatcher.UIThread.Invoke(() =>
                {
                    if (chatWindow.IsFocused || chatWindowHandle == hWnd)
                    {
                        chatWindow.ViewModel.IsOpened = false; // Hide chat window if it's already focused
                    }
                    else
                    {
                        chatWindow.ViewModel.ShowAsync(element).Detach(logger.ToExceptionHandler());
                    }
                });
            }));
    }

    private void HandleTextSelectionChanged(ChatWindowViewModel chatWindowViewModel, bool isEnabled)
    {
        using var _ = _syncLock.EnterScope();

        _textSelectionSubscription?.Dispose();
        if (isEnabled) _textSelectionSubscription = visualElementContext.Subscribe(chatWindowViewModel);
    }
}