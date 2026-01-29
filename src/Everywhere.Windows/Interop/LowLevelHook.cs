using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using Serilog;

namespace Everywhere.Windows.Interop;

/// <summary>
/// Callback for LowLevelHook. Return true to block the message.
/// </summary>
internal delegate void LowLevelHookHandler<T>(WINDOW_MESSAGE msg, ref T hookStruct, ref bool blockNext) where T : unmanaged;

/// <summary>
/// Manages low-level Windows hooks (Keyboard/Mouse) on a dedicated background thread to avoid blocking the UI thread.
/// </summary>
internal static class LowLevelHook
{
    public static IDisposable CreateMouseHook(LowLevelHookHandler<MSLLHOOKSTRUCT> callback, bool runOnDedicatedThread = true)
    {
        return new HookRunner<MSLLHOOKSTRUCT>(WINDOWS_HOOK_ID.WH_MOUSE_LL, callback, runOnDedicatedThread);
    }

    public static IDisposable CreateKeyboardHook(LowLevelHookHandler<KBDLLHOOKSTRUCT> callback, bool runOnDedicatedThread = true)
    {
        return new HookRunner<KBDLLHOOKSTRUCT>(WINDOWS_HOOK_ID.WH_KEYBOARD_LL, callback, runOnDedicatedThread);
    }

    /// <summary>
    /// The actual generic implementation of the hook.
    /// </summary>
    private class HookRunner<T> : IDisposable where T : unmanaged
    {
        private UnhookWindowsHookExSafeHandle? _hookHandle;
        private GCHandle _hookProcHandle;
        private uint _threadId;
        private bool _disposed;

        private readonly WINDOWS_HOOK_ID _id;
        private readonly LowLevelHookHandler<T> _callback;
        private readonly Thread? _thread;

        public HookRunner(WINDOWS_HOOK_ID id, LowLevelHookHandler<T> callback, bool runOnDedicatedThread)
        {
            _id = id;
            _callback = callback;

            if (runOnDedicatedThread)
            {
                _thread = new Thread(ThreadProc)
                {
                    IsBackground = true,
                    Name = "LowLevelHookThread",
                    Priority = ThreadPriority.Highest // Reduce latency for input hooks
                };
                _thread.SetApartmentState(ApartmentState.STA); // Hooks often work best in STA
                _thread.Start();
            }
            else
            {
                Install();
            }
        }

        private void ThreadProc()
        {
            _threadId = PInvoke.GetCurrentThreadId();
            Install();

            while (true)
            {
                // GetMessage blocks until a message arrives
                // It also creates the message queue if one doesn't exist.
                var result = PInvoke.GetMessage(out var msg, HWND.Null, 0, 0);
                if (result <= 0 || result == (uint)WINDOW_MESSAGE.WM_QUIT) break; // Error or WM_QUIT

                PInvoke.TranslateMessage(msg);
                PInvoke.DispatchMessage(msg);
            }

            Uninstall();
        }

        private void Install()
        {
            if (_disposed) return;

            using var hModule = PInvoke.GetModuleHandle(null);
            var hookProc = new HOOKPROC(HookProc);
            _hookProcHandle = GCHandle.Alloc(hookProc);

            _hookHandle = PInvoke.SetWindowsHookEx(
                _id,
                hookProc,
                hModule,
                0);
        }

        private unsafe LRESULT HookProc(int code, WPARAM wParam, LPARAM lParam)
        {
            if (code < 0) return PInvoke.CallNextHookEx(null, code, wParam, lParam);

            ref var hookStruct = ref Unsafe.AsRef<T>(lParam.Value.ToPointer());
            var blockNext = false;

            // Note: This callback runs on the background HookThread!
            // Users should dispatch to UI thread if they need to update UI.
            _callback.Invoke((WINDOW_MESSAGE)wParam.Value, ref hookStruct, ref blockNext);

            return blockNext ? (LRESULT)1 : PInvoke.CallNextHookEx(null, code, wParam, lParam);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_thread is not null)
            {
                // Signal the thread to exit by posting a WM_QUIT message. It will uninstall the hook itself.
                var success = PInvoke.PostThreadMessage(_threadId, (uint)WINDOW_MESSAGE.WM_QUIT, 0, 0);
                if (!success)
                {
                    Log.ForContext<HookRunner<T>>().Error(
                        "Failed to post message to hook thread. Error: {ErrorCode}",
                        Marshal.GetLastWin32Error());
                }
            }
            else
            {
                Uninstall();
            }

            GC.SuppressFinalize(this);
        }

        private void Uninstall()
        {
            _hookHandle?.Dispose();
            if (_hookProcHandle.IsAllocated)
            {
                _hookProcHandle.Free();
            }
        }

        ~HookRunner()
        {
            Dispose();
        }
    }
}