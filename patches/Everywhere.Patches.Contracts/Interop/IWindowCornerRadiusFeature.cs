namespace Everywhere.Patches.Contracts.Interop;

/// <summary>
/// Runtime contract injected into Avalonia's Windows platform implementation by MonoMod.
/// </summary>
public interface IWindowCornerRadiusFeature
{
    /// <summary>
    /// Sets the requested corner radius in Avalonia logical pixels.
    /// </summary>
    /// <param name="radius">The requested radius. Values are clamped by the platform implementation.</param>
    void SetCornerRadius(double radius);

    /// <summary>
    /// Temporarily replaces the requested radius with zero for native window states that require square corners.
    /// </summary>
    /// <param name="suppressed">Whether the effective radius should be zero.</param>
    void SetCornerRadiusSuppressed(bool suppressed);

    /// <summary>
    /// Gets the effective radius for the current native window state.
    /// </summary>
    /// <param name="radius">The effective radius in Avalonia logical pixels.</param>
    /// <returns><see langword="true" /> when a radius has been configured for the window.</returns>
    bool TryGetEffectiveCornerRadius(out double radius);
}
