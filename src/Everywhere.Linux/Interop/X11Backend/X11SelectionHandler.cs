using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using Everywhere.Interop;
using Microsoft.Extensions.Logging;
using X11;
using X11Window = X11.Window;

namespace Everywhere.Linux.Interop.X11Backend;

public sealed class X11SelectionHandler : IObservable<string>, IDisposable
{
    private readonly ILogger _logger;
    private readonly X11Context _context;
    private readonly X11CoreServices _coreServices;
    private readonly Subject<string> _subject = new();

    private int _xFixesEventBase;
    private int _xiOpcode;
    private bool _isLeftMouseDown;
    private bool _dirtyFlag;
    private string? _pendingContents;

    public X11SelectionHandler(ILogger logger, X11Context context, X11CoreServices coreServices)
    {
        _logger = logger;
        _context = context;
        _coreServices = coreServices;

        Initialize();
    }

    private void Initialize()
    {
        _context.InvokeSync(() =>
        {
            // Initialize XFixes
            if (X11Native.XFixesQueryExtension(_context.Display, out _xFixesEventBase, out _) != 0)
            {
                var primary = _context.GetAtom("PRIMARY");
                X11Native.XFixesSelectSelectionInput(_context.Display, _context.RootWindow, primary, X11Native.XFixesSetSelectionOwnerNotifyMask);
            }

            // Initialize XInput2
            if (X11Native.XIQueryExtension(_context.Display, out _xiOpcode, out _, out _) != 0)
            {
                int major = 2, minor = 0;
                if (X11Native.XIQueryVersion(_context.Display, ref major, ref minor) == 0)
                {
                    var mask = new byte[4]; // Enough for XI_ButtonRelease (5)
                    mask[XI_ButtonPress / 8] |= (byte)(1 << (XI_ButtonPress % 8));
                    mask[XI_ButtonRelease / 8] |= (byte)(1 << (XI_ButtonRelease % 8));

                    unsafe
                    {
                        fixed (byte* pMask = mask)
                        {
                            var evMask = new X11Native.XIEventMask
                            {
                                deviceid = X11Native.XI_AllMasterDevices,
                                mask_len = mask.Length,
                                mask = (IntPtr)pMask
                            };
                            X11Native.XISelectEvents(_context.Display, _context.RootWindow, new[] { evMask }, 1);
                        }
                    }
                }
            }

            _context.XEventReceived += OnXEvent;
        });
    }

    private const int XI_ButtonPress = 4;
    private const int XI_ButtonRelease = 5;

    private const int SelectionNotify = 31;
    private const int GenericEvent = 35;

    private void OnXEvent(IntPtr evPtr)
    {
        var type = Marshal.ReadInt32(evPtr);

        if (type == _xFixesEventBase + X11Native.XFixesSelectionNotify)
        {
            var ev = Marshal.PtrToStructure<X11Native.XFixesSelectionNotifyEvent>(evPtr);
            if (ev.selection == _context.GetAtom("PRIMARY"))
            {
                RequestSelection();
            }
        }
        else if (type == SelectionNotify)
        {
            var ev = Marshal.PtrToStructure<X11Native.XSelectionEvent>(evPtr);
            if (ev.selection == _context.GetAtom("PRIMARY") && ev.property != Atom.None)
            {
                HandleSelectionNotify(ev);
            }
        }
        else if (type == GenericEvent)
        {
            var cookie = Marshal.PtrToStructure<X11Native.XGenericEventCookie>(evPtr);
            if (cookie.extension == _xiOpcode && X11Native.XIGetEventData(_context.Display, evPtr) != 0)
            {
                try
                {
                    if (cookie.evtype == XI_ButtonPress || cookie.evtype == XI_ButtonRelease)
                    {
                        var xiev = Marshal.PtrToStructure<X11Native.XIDeviceEvent>(cookie.data);
                        if (xiev.detail == 1) // Left button
                        {
                            if (cookie.evtype == XI_ButtonPress)
                            {
                                _isLeftMouseDown = true;
                            }
                            else
                            {
                                _isLeftMouseDown = false;
                                if (_dirtyFlag)
                                {
                                    PendMessage();
                                }
                                else
                                {
                                    _dirtyFlag = false;
                                }
                            }
                        }
                    }
                }
                finally
                {
                    X11Native.XIFreeEventData(_context.Display, evPtr);
                }
            }
        }
    }

    private void RequestSelection()
    {
        var primary = _context.GetAtom("PRIMARY");
        var utf8String = _context.GetAtom("UTF8_STRING");
        var prop = _context.GetAtom("EVERYWHERE_SELECTION");

        X11Native.XConvertSelection(_context.Display, primary, utf8String, prop, _context.MessageWindow, X11Native.CurrentTime);
    }

    private void HandleSelectionNotify(X11Native.XSelectionEvent ev)
    {
        _coreServices.GetProperty(ev.requestor, "EVERYWHERE_SELECTION", 1024 * 1024, ev.target, (actualType, actualFormat, nItems, bytesAfter, prop) =>
        {
            if (prop != IntPtr.Zero && nItems > 0)
            {
                var bytes = new byte[(int)nItems];
                Marshal.Copy(prop, bytes, 0, (int)nItems);
                var text = System.Text.Encoding.UTF8.GetString(bytes);
                if (!string.IsNullOrEmpty(text))
                {
                    _pendingContents = text;
                    if (_isLeftMouseDown)
                    {
                        _dirtyFlag = true;
                    }
                    else
                    {
                        PendMessage();
                    }
                }
            }
        });
    }

    private void PendMessage()
    {
        var text = _pendingContents;
        _pendingContents = null;
        _dirtyFlag = false;

        if (text != null)
        {
            _subject.OnNext(text);
        }
    }

    public IDisposable Subscribe(IObserver<string> observer)
    {
        return _subject.Subscribe(observer);
    }

    public void Dispose()
    {
        _context.XEventReceived -= OnXEvent;
        _subject.Dispose();
    }
}
