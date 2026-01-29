using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.System;
using Windows.UI.Composition;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.WinRT;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using WinRT;
using ComObject = SharpGen.Runtime.ComObject;
using Vector = Avalonia.Vector;
using Visual = Windows.UI.Composition.Visual;

namespace Everywhere.Windows.Interop;

public sealed partial class Direct3D11ScreenCapture : IAsyncDisposable
{
    private readonly DispatcherQueueController _dispatcherQueueController;
    private readonly IDCompositionDevice2 _dCompositionDevice2;
    private readonly ID3D11Device _d3d11Device;
    private readonly IDirect3DDevice _direct3dDevice;
    private readonly ID3D11Texture2D _stagingTexture;
    private readonly Direct3D11CaptureFramePool _framePool;
    private readonly GraphicsCaptureSession _session;
    private readonly IDCompositionVisual2 _dCompositionVisual;

    // https://blog.adeltax.com/dwm-thumbnails-but-with-idcompositionvisual/
    // https://gist.github.com/ADeltaX/aea6aac248604d0cb7d423a61b06e247
    private Direct3D11ScreenCapture(nint sourceHWnd, nint targetHWnd, PixelRect relativeRect)
    {
        // 1. Create and hold the DispatcherQueueController
        PInvoke.CreateDispatcherQueueController(
            new DispatcherQueueOptions
            {
                apartmentType = DISPATCHERQUEUE_THREAD_APARTMENTTYPE.DQTAT_COM_STA,
                threadType = DISPATCHERQUEUE_THREAD_TYPE.DQTYPE_THREAD_CURRENT,
                dwSize = (uint)Unsafe.SizeOf<DispatcherQueueOptions>()
            },
            out _dispatcherQueueController).ThrowOnFailure();

        // 2. Create the composition device
        var interopCompositorFactory = Compositor.As<IInteropCompositorFactoryPartner>();
        var pInteropCompositor = interopCompositorFactory.CreateInteropCompositor(0, 0, typeof(IDCompositionDevice2).GUID);
        _dCompositionDevice2 = ComObject.As<IDCompositionDevice2>(pInteropCompositor);

        DwmpQueryWindowThumbnailSourceSize((HWND)sourceHWnd, false, out var srcSize).ThrowOnFailure();
        if (srcSize.Width == 0 || srcSize.Height == 0)
        {
            throw new InvalidOperationException("Failed to query thumbnail source size.");
        }

        // 3. Create and update the DWM thumbnail
        var thumbProperties = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags =
                DwmThumbnailPropertyFlags.RectDestination |
                DwmThumbnailPropertyFlags.RectSource |
                DwmThumbnailPropertyFlags.Opacity |
                DwmThumbnailPropertyFlags.Visible |
                DwmThumbnailPropertyFlags.SourceClientAreaOnly,
            fVisible = true,
            fSourceClientAreaOnly = false,
            opacity = 255,
            rcDestination = new RECT(0, 0, srcSize.Width, srcSize.Height),
            rcSource = new RECT(0, 0, srcSize.Width, srcSize.Height),
        };
        DwmUpdateThumbnailProperties((HWND)sourceHWnd, ref thumbProperties);

        // 4. Create the shared thumbnail visual
        DwmpCreateSharedThumbnailVisual(
            (HWND)targetHWnd,
            (HWND)sourceHWnd,
            2, // Undocumented flag
            ref thumbProperties,
            pInteropCompositor,
            out var pDCompositionVisual,
            out _).ThrowOnFailure();
        _dCompositionVisual = new IDCompositionVisual2(pDCompositionVisual);

        // 5. Transform and crop the visual using relativeRect
        using var containerVisual = _dCompositionDevice2.CreateVisual();
        containerVisual.AddVisual(_dCompositionVisual, true, null);

        // Create a transform matrix for translation
        using var transform = _dCompositionDevice2.CreateMatrixTransform();
        var matrix = Matrix3x2.CreateTranslation(-relativeRect.X, -relativeRect.Y);
        transform.SetMatrix(ref matrix);
        _dCompositionVisual.SetTransform(transform);

        // Set the clip region
        containerVisual.SetClip(new RawRectF(0, 0, relativeRect.Width, relativeRect.Height));

        var visual = Visual.FromAbi(containerVisual.NativePointer);
        visual.Size = new Vector2(relativeRect.Width, relativeRect.Height);

        // 6. Create D3D device and frame pool
        _d3d11Device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
        _stagingTexture = _d3d11Device.CreateTexture2D(
            new Texture2DDescription
            {
                Width = (uint)relativeRect.Width,
                Height = (uint)relativeRect.Height,
                ArraySize = 1,
                BindFlags = BindFlags.None,
                Usage = ResourceUsage.Staging,
                CPUAccessFlags = CpuAccessFlags.Read,
                Format = Format.B8G8R8A8_UNorm,
                MipLevels = 1,
                SampleDescription = new SampleDescription(1, 0),
                MiscFlags = ResourceOptionFlags.None
            });

        using var dxgiDevice = _d3d11Device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var pD3d11Device));
        _direct3dDevice = MarshalInterface<IDirect3DDevice>.FromAbi(pD3d11Device);

        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _direct3dDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2, // Use a buffer of 2 to avoid capture lag
            new SizeInt32(relativeRect.Width, relativeRect.Height));

        // 7. Create the capture session
        var item = GraphicsCaptureItem.CreateFromVisual(visual);
        _session = _framePool.CreateCaptureSession(item);
        _session.IsCursorCaptureEnabled = false;
    }

    private async Task<Bitmap> CaptureFrameAsync(CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<Bitmap>();
        await using var registration = cancellationToken.Register(() => tcs.TrySetCanceled());

        _framePool.FrameArrived += (f, _) =>
        {
            using var frame = f.TryGetNextFrame();
            if (frame is null) return;

            try
            {
                // Get the underlying ID3D11Texture2D from the frame's
                var access = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();

                var textureGuid = typeof(ID3D11Texture2D).GUID;
                var pTexture = access.GetInterface(textureGuid);
                using var sourceTexture = new ID3D11Texture2D(pTexture);

                var immediateContext = _d3d11Device.ImmediateContext;
                immediateContext.CopyResource(_stagingTexture, sourceTexture);
                var mapBox = immediateContext.Map(_stagingTexture, 0);

                try
                {
                    var bitmap = new Bitmap(
                        PixelFormat.Bgra8888,
                        AlphaFormat.Premul,
                        mapBox.DataPointer,
                        new PixelSize(frame.ContentSize.Width, frame.ContentSize.Height),
                        new Vector(96d, 96d),
                        (int)mapBox.RowPitch
                    );

                    tcs.TrySetResult(bitmap);
                }
                finally
                {
                    immediateContext.Unmap(_stagingTexture, 0);
                }
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        };

        _session.StartCapture();
        _dCompositionDevice2.Commit();

        try
        {
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
        }
        finally
        {
            _session.Dispose();
            _framePool.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _session.Dispose();
        _framePool.Dispose();
        _dCompositionVisual.Dispose();
        _dCompositionDevice2.Dispose();
        _direct3dDevice.Dispose();
        await _dispatcherQueueController.ShutdownQueueAsync();
    }

    public static Task<Bitmap> CaptureAsync(nint sourceHWnd, PixelRect relativeRect, CancellationToken cancellationToken = default)
    {
        var targetHWnd = (Application.Current?.ApplicationLifetime as ClassicDesktopStyleApplicationLifetime)
            ?.Windows.FirstOrDefault()
            ?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (targetHWnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to get target window handle.");
        }

        return Dispatcher.UIThread.InvokeAsync(async () =>
        {
            await using var screenCapture = new Direct3D11ScreenCapture(sourceHWnd, targetHWnd, relativeRect);
            return await screenCapture.CaptureFrameAsync(cancellationToken);
        });
    }

    [Flags]
    private enum DwmThumbnailPropertyFlags : uint
    {
        RectDestination = 0x00000001,
        RectSource = 0x00000002,
        Opacity = 0x00000004,
        Visible = 0x00000008,
        SourceClientAreaOnly = 0x00000010
    }

    // ReSharper disable InconsistentNaming
    // ReSharper disable NotAccessedField.Local
    private struct DWM_THUMBNAIL_PROPERTIES
    {
        public DwmThumbnailPropertyFlags dwFlags;
        public RECT rcDestination;
        public RECT rcSource;
        public byte opacity;
        public BOOL fVisible;
        public BOOL fSourceClientAreaOnly;
    }
    // ReSharper restore InconsistentNaming
    // ReSharper restore NotAccessedField.Local

    [LibraryImport("d3d11.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    [DllImport("dwmapi.dll", CallingConvention = CallingConvention.Winapi, PreserveSig = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern HRESULT DwmUpdateThumbnailProperties(
        [In] HWND hWndThumbnail,
        [In] ref DWM_THUMBNAIL_PROPERTIES ptnProperties);

    [DllImport("dwmapi.dll", CallingConvention = CallingConvention.Winapi, PreserveSig = true, EntryPoint = "#162")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern HRESULT DwmpQueryWindowThumbnailSourceSize(
        [In] HWND hWndSource,
        [In] BOOL fSourceClientAreaOnly,
        [Out] out SIZE pSize);

    [DllImport("dwmapi.dll", CallingConvention = CallingConvention.Winapi, PreserveSig = true, EntryPoint = "#147")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern HRESULT DwmpCreateSharedThumbnailVisual(
        [In] HWND hWndDestination,
        [In] HWND hWndSource,
        [In] uint thumbnailFlags,
        [In] ref DWM_THUMBNAIL_PROPERTIES thumbnailProperties,
        [In] nint pDCompositionDesktopDevice,
        [Out] out nint pDCompositionVisual,
        [Out] out nint hThumbnailId);
}