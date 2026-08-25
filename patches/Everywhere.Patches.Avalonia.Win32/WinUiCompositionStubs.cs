using System.Numerics;
using Avalonia.OpenGL.Egl;
using Everywhere.Patches.Contracts.Interop;
using MonoMod;

// Avalonia's WinRT bindings are internal by design. These declarations provide the
// compile-time shape required by the patch; MonoMod relinks them to the matching
// internal types in Avalonia.Win32 when weaving the assembly.
namespace Avalonia.Win32.WinRT
{
    [MonoModIgnore]
    internal interface ICompositor;

    [MonoModIgnore]
    internal interface IVisual;

    [MonoModIgnore]
    internal interface ICompositionRoundedRectangleGeometry : IDisposable
    {
        void SetCornerRadius(Vector2 value);

        void SetOffset(Vector2 value);

        void SetSize(Vector2 value);
    }
}

namespace Avalonia.Win32.WinRT.Composition
{
    [MonoModIgnore]
    // ReSharper disable once ClassNeverInstantiated.Global
    internal class WinUiCompositionShared
    {
        // This initializer only satisfies nullable analysis while compiling the patch donor.
        // MonoMod relinks the property access to Avalonia.Win32's live compositor instance.
        public ICompositor Compositor { get; } = null!;

        // This is likewise a compile-time shape declaration. After weaving, it resolves to
        // Avalonia.Win32's shared synchronization object rather than this null initializer.
        public object SyncRoot { get; } = null!;
    }

    [MonoModPatch("Avalonia.Win32.WinRT.Composition.WinUiCompositedWindow")]
    internal class patch_WinUiCompositedWindow : IDisposable
    {
        private const float BackdropClipInset = 2;

        [MonoModIgnore]
        private readonly WinUiCompositionShared _shared = null!;

        [MonoModIgnore]
        private readonly IVisual? _micaLight = null;

        [MonoModIgnore]
        private readonly IVisual? _micaDark = null;

        [MonoModIgnore]
        private readonly IVisual _blur = null!;

        [MonoModIgnore]
        private readonly IVisual _visual = null!;

        [MonoModIgnore]
        public extern EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfo WindowInfo { get; }

        private ICompositionRoundedRectangleGeometry? _everywhereCornerGeometry;
        private ICompositionRoundedRectangleGeometry? _everywhereBackdropCornerGeometry;
        private PixelSize _everywhereCornerSize;
        private double _everywhereCornerScaling;
        private float _everywhereCornerRadius = float.NaN;

        [MonoModLinkTo("Avalonia.Win32.WinRT.Composition.WinUiCompositionUtils", "ClipVisual")]
        private static extern ICompositionRoundedRectangleGeometry? ClipVisual(
            ICompositor compositor,
            float? cornerRadius,
            params IVisual?[] visuals);

        private static bool TryGetCornerRadius(
            EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfo windowInfo,
            out double radius)
        {
            // ReSharper disable once SuspiciousTypeConversion.Global
            // Avalonia's Win32 platform implementation is extended at runtime to implement IWindowCornerRadiusFeature.
            if (windowInfo is IWindowCornerRadiusFeature feature)
                return feature.TryGetEffectiveCornerRadius(out radius);

            radius = 0;
            return false;
        }

        // The wrapper below must not use MonoModReplace: MonoMod needs to preserve
        // Avalonia's implementation under this orig_ name before applying the wrapper.
        public extern void orig_ResizeIfNeeded(PixelSize size);

        public void ResizeIfNeeded(PixelSize size)
        {
            orig_ResizeIfNeeded(size);

            lock (_shared.SyncRoot)
            {
                UpdateEverywhereCornerClip(size);
            }
        }

        private void UpdateEverywhereCornerClip(PixelSize size)
        {
            if (!TryGetCornerRadius(WindowInfo, out var logicalRadius))
                return;

            var scaling = WindowInfo.Scaling;
            var radius = (float)(logicalRadius * scaling);
            var needsCreation = _everywhereCornerGeometry is null;
            var sizeChanged = _everywhereCornerSize != size;
            var scaleChanged = Math.Abs(_everywhereCornerScaling - scaling) > double.Epsilon;
            var radiusChanged = float.IsNaN(_everywhereCornerRadius) || Math.Abs(_everywhereCornerRadius - radius) > float.Epsilon;

            if (needsCreation)
            {
                // Keep the rendered content on the requested outer boundary. The backdrop uses
                // a separate, slightly inset clip so its bright edge sampling cannot appear as
                // a second border outside the Avalonia-rendered border.
                _everywhereCornerGeometry = ClipVisual(
                    _shared.Compositor,
                    radius,
                    _visual);
                _everywhereBackdropCornerGeometry = ClipVisual(
                    _shared.Compositor,
                    Math.Max(0, radius - BackdropClipInset),
                    _blur,
                    _micaLight,
                    _micaDark);
            }
            var geometry = _everywhereCornerGeometry;
            if (geometry is null)
                return;

            if (!needsCreation && radiusChanged)
                geometry.SetCornerRadius(new Vector2(radius, radius));

            if (needsCreation || sizeChanged || scaleChanged)
                geometry.SetSize(new Vector2(size.Width, size.Height));

            var backdropGeometry = _everywhereBackdropCornerGeometry;
            if (backdropGeometry is not null)
            {
                // Maximized and snapped windows suppress their radius. Do not leave a one-pixel
                // backdrop gap along their square edges in that state.
                var backdropInset = radius > 0 ? BackdropClipInset : 0;
                if (needsCreation || radiusChanged)
                {
                    backdropGeometry.SetCornerRadius(new Vector2(
                        Math.Max(0, radius - backdropInset),
                        Math.Max(0, radius - backdropInset)));
                    backdropGeometry.SetOffset(new Vector2(backdropInset, backdropInset));
                }

                if (needsCreation || sizeChanged || scaleChanged || radiusChanged)
                {
                    backdropGeometry.SetSize(new Vector2(
                        Math.Max(0, size.Width - backdropInset * 2),
                        Math.Max(0, size.Height - backdropInset * 2)));
                }
            }

            _everywhereCornerSize = size;
            _everywhereCornerScaling = scaling;
            _everywhereCornerRadius = radius;
        }

        // Keep Avalonia's original disposal path so all of its COM objects are released.
        public extern void orig_Dispose();

        public void Dispose()
        {
            lock (_shared.SyncRoot)
            {
                _everywhereCornerGeometry?.Dispose();
                _everywhereCornerGeometry = null;
                _everywhereBackdropCornerGeometry?.Dispose();
                _everywhereBackdropCornerGeometry = null;
                orig_Dispose();
            }
        }
    }
}
