using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;
using Avalonia.Input;
using Everywhere.Interop;
using Everywhere.Windows.Extensions;

namespace Everywhere.Windows.Interop;

public unsafe class ShortcutListener : IShortcutListener
{
    private static HWND HWnd => MessageWindow.Shared.HWnd;

    // Unique id generator for RegisterHotKey
    private static int _nextId = 1;

    // OS-registered keyboard hotkeys: shortcut -> registration bundle (id + handlers)
    private static readonly Dictionary<KeyboardShortcut, OsReg> OsRegs = new();
    private static readonly Dictionary<int, OsReg> IdToOsReg = new();

    // Fallback keyboard hotkeys (when RegisterHotKey fails): (mods,key) -> handlers
    private static readonly Dictionary<ShortcutSignature, HandlerList> HookKbHandlers = new();

    // Mouse hotkeys: per-button registrations (each has its own delay/handler/timer)
    private static readonly Dictionary<MouseButton, List<MouseRegistration>> MouseRegs = new()
    {
        { MouseButton.Left, [] },
        { MouseButton.Right, [] },
        { MouseButton.Middle, [] },
        { MouseButton.XButton1, [] },
        { MouseButton.XButton2, [] },
    };

    // Hooks (created on demand)
    private static IDisposable? _keyboardHookSubscription;
    private static IDisposable? _mouseHookSubscription;

    // Inject sentinel to ignore self-injected events
    private const nuint InjectExtra = 0x0d000721;

    // Concurrency: light lock for shortcut maps
    private static readonly Lock SyncLock = new();

    private static IKeyboardShortcutScope? _currentKeyboardShortcutScope;

    static ShortcutListener()
    {
        MessageWindow.Shared.AddHandler(
            (uint)WINDOW_MESSAGE.WM_HOTKEY,
            static (in msg) =>
            {
                var id = (int)msg.wParam.Value;
                OsReg? bundle;
                lock (SyncLock) IdToOsReg.TryGetValue(id, out bundle);
                if (bundle is not null) InvokeHandlers(bundle.Handlers);
            });
    }

    public IKeyboardShortcutScope StartCaptureKeyboardShortcut()
    {
        lock (SyncLock)
        {
            if (_currentKeyboardShortcutScope is not { IsDisposed: false }) _currentKeyboardShortcutScope = new KeyboardShortcutScopeImpl();
            return _currentKeyboardShortcutScope;
        }
    }

    public IDisposable Register(KeyboardShortcut shortcut, Action handler)
    {
        if (shortcut.Key == Key.None || shortcut.Modifiers == KeyModifiers.None)
            throw new ArgumentException("Invalid keyboard shortcut.", nameof(shortcut));
        ArgumentNullException.ThrowIfNull(handler);

        lock (SyncLock)
        {
            // If there is already an OS registration for this shortcut, reuse id and append handler.
            if (OsRegs.TryGetValue(shortcut, out var reg))
            {
                reg.Handlers.Add(handler);
                return new Disposer(() => UnregisterKeyboardHandlerOs(shortcut, handler));
            }

            // Try OS registration (incl. MOD_WIN).
            var mods = HOT_KEY_MODIFIERS.MOD_NOREPEAT;
            if (shortcut.Modifiers.HasFlag(KeyModifiers.Control)) mods |= HOT_KEY_MODIFIERS.MOD_CONTROL;
            if (shortcut.Modifiers.HasFlag(KeyModifiers.Shift)) mods |= HOT_KEY_MODIFIERS.MOD_SHIFT;
            if (shortcut.Modifiers.HasFlag(KeyModifiers.Alt)) mods |= HOT_KEY_MODIFIERS.MOD_ALT;
            if (shortcut.Modifiers.HasFlag(KeyModifiers.Meta)) mods |= HOT_KEY_MODIFIERS.MOD_WIN;

            var id = _nextId++;
            if (PInvoke.RegisterHotKey(HWnd, id, mods, (uint)shortcut.Key.ToVirtualKey()))
            {
                var bundle = new OsReg(id, new List<Action> { handler });
                OsRegs[shortcut] = bundle;
                IdToOsReg[id] = bundle;
                return new Disposer(() => UnregisterKeyboardHandlerOs(shortcut, handler));
            }

            // Fallback to LL keyboard hook with Win suppression/compensation.
            var sig = new ShortcutSignature(shortcut.Modifiers, shortcut.Key);
            if (!HookKbHandlers.TryGetValue(sig, out var list))
            {
                list = new HandlerList();
                HookKbHandlers[sig] = list;
            }
            list.Add(handler);

            EnsureKeyboardHook();
            return new Disposer(() => UnregisterKeyboardHandlerHook(sig, handler));
        }
    }

    public IDisposable Register(MouseShortcut shortcut, Action handler)
    {
        if (handler is null) throw new ArgumentNullException(nameof(handler));

        lock (SyncLock)
        {
            var reg = new MouseRegistration(shortcut, handler);
            MouseRegs[shortcut.Key].Add(reg);
            EnsureMouseHook();
            return new Disposer(() => UnregisterMouseHandler(reg));
        }
    }

    public void Dispose()
    {
        lock (SyncLock)
        {
            // Unregister all OS hotkeys
            foreach (var kv in OsRegs)
            {
                PInvoke.UnregisterHotKey(HWnd, kv.Value.Id);
            }
            OsRegs.Clear();
            IdToOsReg.Clear();

            // Clear fallback handlers
            HookKbHandlers.Clear();

            // Clear mouse regs (and cancel timers)
            foreach (var list in MouseRegs.Values)
            {
                foreach (var r in list) r.CancelTimer();
                list.Clear();
            }

            // Dispose hooks
            _keyboardHookSubscription?.Dispose();
            _keyboardHookSubscription = null;
            _mouseHookSubscription?.Dispose();
            _mouseHookSubscription = null;
        }
    }

    private static void InvokeHandlers(List<Action> handlers)
    {
        // Execute handlers safely; avoid holding locks while invoking.
        foreach (var handler in handlers.ToList())
        {
            try
            {
                handler();
            }
            catch
            {
                /* swallow */
            }
        }
    }

    // ---------- keyboard hook (fallback) ----------
    // Reference: PowerToys
    // https://github.com/microsoft/PowerToys/blob/main/src/modules/cmdpal/CmdPalKeyboardService/KeyboardListener.cpp

    private static void EnsureKeyboardHook()
    {
        if (_keyboardHookSubscription is not null) return;
        _keyboardHookSubscription = LowLevelHook.CreateKeyboardHook(KeyboardHookProc);
    }

    private static void KeyboardHookProc(WINDOW_MESSAGE msg, ref KBDLLHOOKSTRUCT hookStruct, ref bool blockNext)
    {
        // Ignore self-injected events
        if (hookStruct.dwExtraInfo == InjectExtra) return;

        if (msg != WINDOW_MESSAGE.WM_KEYDOWN && msg != WINDOW_MESSAGE.WM_SYSKEYDOWN)
            return;

        // Resolve current modifiers using GetAsyncKeyState (best-effort snapshot)
        var mods = GetAsyncModifiers();

        // Resolve main key from vkCode
        var vk = (VIRTUAL_KEY)hookStruct.vkCode;
        var key = vk.ToAvaloniaKey();

        // Build shortcut signature and lookup handlers
        List<Action>? handlers = null;
        var sig = new ShortcutSignature(mods, key);

        lock (SyncLock)
        {
            if (HookKbHandlers.TryGetValue(sig, out var list))
            {
                handlers = list.Snapshot();
            }
        }

        if (handlers is null || handlers.Count == 0)
            return;

        // Execute handlers outside the lock
        foreach (var h in handlers)
        {
            try { h(); }
            catch
            { /* swallow */
            }
        }

        // Send a dummy key-up to prevent Start menu from activating on Win-key release
        SendDummyKeyUp();

        // Swallow this key press
        blockNext = true;
    }

    private static KeyModifiers GetAsyncModifiers()
    {
        // Note: Using async state to query left/right Win, Ctrl, Shift, Alt.
        // This mirrors the PowerToys approach and avoids maintaining custom state.
        static bool Down(VIRTUAL_KEY v) => (PInvoke.GetAsyncKeyState((int)v) & 0x8000) != 0;

        var mods = KeyModifiers.None;

        if (Down(VIRTUAL_KEY.VK_LWIN) || Down(VIRTUAL_KEY.VK_RWIN))
            mods |= KeyModifiers.Meta;
        if ((PInvoke.GetAsyncKeyState((int)VIRTUAL_KEY.VK_CONTROL) & 0x8000) != 0)
            mods |= KeyModifiers.Control;
        if ((PInvoke.GetAsyncKeyState((int)VIRTUAL_KEY.VK_SHIFT) & 0x8000) != 0)
            mods |= KeyModifiers.Shift;
        if ((PInvoke.GetAsyncKeyState((int)VIRTUAL_KEY.VK_MENU) & 0x8000) != 0)
            mods |= KeyModifiers.Alt;

        return mods;
    }

    /// <summary>
    /// Inject a harmless key-up (VK 0xFF) to cancel Start-menu activation
    /// </summary>
    private static void SendDummyKeyUp()
    {
        var inputs = new INPUT[1];
        inputs[0] = new INPUT
        {
            type = INPUT_TYPE.INPUT_KEYBOARD,
            Anonymous = new INPUT._Anonymous_e__Union
            {
                ki = new KEYBDINPUT
                {
                    wVk = (VIRTUAL_KEY)0xFF,
                    wScan = 0,
                    dwFlags = KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP,
                    time = 0,
                    dwExtraInfo = InjectExtra
                }
            }
        };

        fixed (INPUT* p = inputs)
        {
            PInvoke.SendInput(new ReadOnlySpan<INPUT>(p, 1), sizeof(INPUT));
        }
    }

    // ---------- mouse hook ----------

    private static void EnsureMouseHook()
    {
        if (_mouseHookSubscription is not null) return;
        _mouseHookSubscription = LowLevelHook.CreateMouseHook(MouseHookProc);
    }

    private static void MouseHookProc(WINDOW_MESSAGE msg, ref MSLLHOOKSTRUCT hookStruct, ref bool blockNext)
    {
        if (hookStruct.dwExtraInfo == InjectExtra) return;

        var button = (hookStruct.mouseData >> 16) & 0xFFFF;
        switch (msg)
        {
            case WINDOW_MESSAGE.WM_LBUTTONDOWN:
            case WINDOW_MESSAGE.WM_RBUTTONDOWN:
            case WINDOW_MESSAGE.WM_MBUTTONDOWN:
            case WINDOW_MESSAGE.WM_XBUTTONDOWN:
            {
                if (GetRegistrations() is not { Count: > 0 } registrations) break;

                // Schedule or fire per registration
                foreach (var r in registrations) r.OnDown();
                break;
            }

            case WINDOW_MESSAGE.WM_LBUTTONUP:
            case WINDOW_MESSAGE.WM_RBUTTONUP:
            case WINDOW_MESSAGE.WM_MBUTTONUP:
            case WINDOW_MESSAGE.WM_XBUTTONUP:
            {
                if (GetRegistrations() is not { Count: > 0 } registrations) break;

                // Cancel pending per registration
                foreach (var r in registrations) r.OnUp();
                break;
            }
        }

        List<MouseRegistration>? GetRegistrations()
        {
            var mk = msg switch
            {
                WINDOW_MESSAGE.WM_LBUTTONUP => MouseButton.Left,
                WINDOW_MESSAGE.WM_RBUTTONUP => MouseButton.Right,
                WINDOW_MESSAGE.WM_MBUTTONUP => MouseButton.Middle,
                WINDOW_MESSAGE.WM_XBUTTONUP when button == PInvoke.XBUTTON1 => MouseButton.XButton1,
                WINDOW_MESSAGE.WM_XBUTTONUP when button == PInvoke.XBUTTON2 => MouseButton.XButton2,
                _ => MouseButton.None
            };
            if (mk == MouseButton.None) return null;

            lock (SyncLock) return MouseRegs[mk].Count > 0 ? [..MouseRegs[mk]] : null;
        }
    }

    // ---------- unregister helpers ----------

    private static void UnregisterKeyboardHandlerOs(KeyboardShortcut shortcut, Action handler)
    {
        lock (SyncLock)
        {
            if (!OsRegs.TryGetValue(shortcut, out var bundle)) return;
            bundle.Handlers.Remove(handler);
            if (bundle.Handlers.Count > 0) return;

            OsRegs.Remove(shortcut);
            IdToOsReg.Remove(bundle.Id);
            PInvoke.UnregisterHotKey(HWnd, bundle.Id);
        }
    }

    private static void UnregisterKeyboardHandlerHook(ShortcutSignature sig, Action handler)
    {
        lock (SyncLock)
        {
            if (!HookKbHandlers.TryGetValue(sig, out var list)) return;
            list.Remove(handler);
            if (list.Count == 0) HookKbHandlers.Remove(sig);
            if (HookKbHandlers.Count == 0 && _keyboardHookSubscription is not null)
            {
                _keyboardHookSubscription.Dispose();
                _keyboardHookSubscription = null;
            }
        }
    }

    private static void UnregisterMouseHandler(MouseRegistration reg)
    {
        lock (SyncLock)
        {
            if (MouseRegs.TryGetValue(reg.Shortcut.Key, out var list))
            {
                list.Remove(reg);
                reg.CancelTimer();
            }
            if (MouseRegs.Values.Sum(l => l.Count) == 0 && _mouseHookSubscription is not null)
            {
                _mouseHookSubscription.Dispose();
                _mouseHookSubscription = null;
            }
        }
    }

    // ---------- small types ----------

    private readonly record struct Disposer(Action DisposeAction) : IDisposable
    {
        public void Dispose() => DisposeAction?.Invoke();
    }

    private sealed class OsReg(int id, List<Action> handlers)
    {
        public int Id { get; } = id;
        public List<Action> Handlers { get; } = handlers;
    }

    private readonly struct ShortcutSignature(KeyModifiers modifiers, Key key) : IEquatable<ShortcutSignature>
    {
        public KeyModifiers Modifiers { get; } = modifiers;
        public Key Key { get; } = key;
        public bool Equals(ShortcutSignature other) => Modifiers == other.Modifiers && Key == other.Key;
        public override bool Equals(object? obj) => obj is ShortcutSignature o && Equals(o);
        public override int GetHashCode() => ((int)Modifiers << 16) ^ (int)Key;
    }

    private sealed class HandlerList
    {
        private readonly List<Action> _handlers = [];
        public void Add(Action h) => _handlers.Add(h);
        public void Remove(Action h) => _handlers.Remove(h);
        public int Count => _handlers.Count;
        public List<Action> Snapshot() => [.._handlers];
    }

    // Per-registration mouse state (timer lifecycle)
    private sealed class MouseRegistration(MouseShortcut shortcut, Action handler)
    {
        public MouseShortcut Shortcut { get; } = shortcut;
        public Action Handler { get; } = handler;
        private Timer? _timer;
        private int _armed; // 1 when button is considered pressed/pending

        public void OnDown()
        {
            // mark pressed
            Interlocked.Exchange(ref _armed, 1);

            if (Shortcut.Delay <= TimeSpan.Zero)
            {
                SafeInvoke();
                return;
            }

            CancelTimer(); // defensive
            _timer = new Timer(
                _ =>
                {
                    if (Interlocked.CompareExchange(ref _armed, 1, 1) == 1)
                    {
                        SafeInvoke();
                    }
                },
                null,
                Shortcut.Delay,
                Timeout.InfiniteTimeSpan);
        }

        public void OnUp()
        {
            Interlocked.Exchange(ref _armed, 0);
            CancelTimer();
        }

        public void CancelTimer()
        {
            var t = Interlocked.Exchange(ref _timer, null);
            t?.Dispose();
        }

        private void SafeInvoke()
        {
            try { Handler(); }
            catch
            { /* swallow */
            }
        }
    }

    private sealed class KeyboardShortcutScopeImpl : IKeyboardShortcutScope
    {
        public KeyboardShortcut PressingShortcut { get; private set; }

        public bool IsDisposed { get; private set; }

        public event IKeyboardShortcutScope.PressingShortcutChangedHandler? PressingShortcutChanged;

        public event IKeyboardShortcutScope.ShortcutFinishedHandler? ShortcutFinished;

        private readonly IDisposable _hookSubscription;

        private KeyModifiers _pressedKeyModifiers = KeyModifiers.None;

        public KeyboardShortcutScopeImpl()
        {
            _hookSubscription = LowLevelHook.CreateKeyboardHook(KeyboardHookCallback);
        }

        private void KeyboardHookCallback(WINDOW_MESSAGE msg, ref KBDLLHOOKSTRUCT hookStruct, ref bool blockNext)
        {
            var virtualKey = (VIRTUAL_KEY)hookStruct.vkCode;
            var keyModifiers = virtualKey.ToKeyModifiers();
            bool? isKeyDown = msg switch
            {
                WINDOW_MESSAGE.WM_KEYDOWN => true,
                WINDOW_MESSAGE.WM_SYSKEYDOWN => true,
                WINDOW_MESSAGE.WM_KEYUP => false,
                WINDOW_MESSAGE.WM_SYSKEYUP => false,
                _ => null
            };

            switch (isKeyDown)
            {
                case true when keyModifiers == KeyModifiers.None:
                {
                    PressingShortcut = PressingShortcut with { Key = virtualKey.ToAvaloniaKey() };
                    PressingShortcutChanged?.Invoke(this, PressingShortcut);
                    break;
                }
                case true:
                {
                    _pressedKeyModifiers |= keyModifiers;
                    PressingShortcut = PressingShortcut with { Modifiers = _pressedKeyModifiers };
                    PressingShortcutChanged?.Invoke(this, PressingShortcut);
                    break;
                }
                case false:
                {
                    _pressedKeyModifiers &= ~keyModifiers;
                    if (_pressedKeyModifiers == KeyModifiers.None)
                    {
                        if (PressingShortcut.Modifiers != KeyModifiers.None && PressingShortcut.Key == Key.None)
                        {
                            PressingShortcut = default; // modifiers only shortcut, reset it
                        }

                        if (PressingShortcut.Modifiers == KeyModifiers.None)
                        {
                            PressingShortcut = default; // no modifiers, reset it
                        }

                        // system key is all released, capture is done
                        PressingShortcutChanged?.Invoke(this, PressingShortcut);
                        ShortcutFinished?.Invoke(this, PressingShortcut);
                    }
                    break;
                }
            }

            blockNext = true;
        }

        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;

            _hookSubscription.Dispose();
        }
    }
}