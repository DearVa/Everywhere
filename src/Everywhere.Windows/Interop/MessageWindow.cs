using System.Reactive.Disposables;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using System.Collections.Immutable;

namespace Everywhere.Windows.Interop;

// Shared message-only window host on a dedicated STA thread.
// Consumers can add message handlers and reuse HWND for OS APIs (e.g., RegisterHotKey, AddClipboardFormatListener).
internal sealed class MessageWindow
{
    public static MessageWindow Shared { get; } = new();

    public HWND HWnd { get; private set; }

    public delegate void MessageHandler(in MSG msg);

    private readonly ManualResetEventSlim _windowCreatedEvent = new(false);
    private readonly Lock _lock = new();
    // Message delivery vastly outnumbers subscriptions. Mutations publish a new
    // contiguous snapshot so dispatch only copies the ImmutableArray wrapper.
    private readonly Dictionary<uint, ImmutableArray<MessageHandler>> _handlers = new();

    private MessageWindow()
    {
        var thread = new Thread(WindowLoop)
        {
            IsBackground = true,
            Name = "Everywhere.MessageWindow",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        _windowCreatedEvent.Wait();
    }

    public IDisposable AddHandler(uint message, MessageHandler handler)
    {
        lock (_lock)
        {
            var handlers = _handlers.GetValueOrDefault(message, []);
            _handlers[message] = handlers.Add(handler);
        }

        return Disposable.Create(() => RemoveHandler(message, handler));
    }

    private void RemoveHandler(uint message, MessageHandler handler)
    {
        lock (_lock)
        {
            if (!_handlers.TryGetValue(message, out var handlers))
            {
                return;
            }

            handlers = handlers.Remove(handler);
            if (handlers.IsEmpty)
            {
                _handlers.Remove(message);
            }
            else
            {
                _handlers[message] = handlers;
            }
        }
    }

    private unsafe void WindowLoop()
    {
        using var hModule = PInvoke.GetModuleHandle();

        // Create a message-only window (child of HWND_MESSAGE)
        HWnd = PInvoke.CreateWindowEx(
            0,
            "STATIC",
            "Everywhere.MessageWindow",
            0,
            0,
            0,
            0,
            0,
            new HWND(-3), // HWND_MESSAGE
            null,
            hModule,
            null);

        _windowCreatedEvent.Set();

        if (HWnd.IsNull)
            throw new InvalidOperationException("Failed to create message window.");

        MSG msg;
        while (PInvoke.GetMessage(&msg, HWND.Null, 0, 0) != 0)
        {
            ImmutableArray<MessageHandler> handlers;
            lock (_lock)
            {
                handlers = _handlers.GetValueOrDefault(msg.message, []);
            }

            foreach (var handler in handlers)
            {
                try { handler(in msg); }
                catch
                { /* swallow */
                }
            }

            PInvoke.TranslateMessage(&msg);
            PInvoke.DispatchMessage(&msg);
        }
    }
}
