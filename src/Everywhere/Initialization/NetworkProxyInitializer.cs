using System.ComponentModel;
using Everywhere.Common;
using Everywhere.Configuration;
using Microsoft.Extensions.Logging;
using ShadUI;

namespace Everywhere.Initialization;

/// <summary>
/// Applies the configured network proxy to global HTTP handlers and keeps it in sync with user settings.
/// </summary>
public sealed class NetworkProxyInitializer : IAsyncInitializer, IDisposable
{
    public AsyncInitializerPriority Priority => AsyncInitializerPriority.AfterSettings;

    private readonly NetworkSettings _networkSettings;
    private readonly ToastManager _toastManager;
    private readonly ILogger<NetworkProxyInitializer> _logger;

    private string? lastErrorMessage;

    public NetworkProxyInitializer(
        Settings settings,
        ToastManager toastManager,
        ILogger<NetworkProxyInitializer> logger)
    {
        _networkSettings = settings.Network;
        this._toastManager = toastManager;
        this._logger = logger;

        _networkSettings.PropertyChanged += HandleNetworkSettingsPropertyChanged;
    }

    public Task InitializeAsync()
    {
        ApplyProxySettings(showNotification: false);
        return Task.CompletedTask;
    }

    private void HandleNetworkSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!NetworkProxyConfigurator.IsProxyProperty(e.PropertyName)) return;
        ApplyProxySettings(showNotification: true);
    }

    private void ApplyProxySettings(bool showNotification)
    {
        if (NetworkProxyConfigurator.TryApply(_networkSettings, out var errorMessage))
        {
            lastErrorMessage = null;
            return;
        }

        if (errorMessage is null)
        {
            errorMessage = "Failed to apply proxy settings.";
        }

        _logger.LogWarning("Failed to apply proxy settings: {Message}", errorMessage);

        if (!showNotification) return;

        if (!string.Equals(lastErrorMessage, errorMessage, StringComparison.Ordinal))
        {
            _toastManager
                .CreateToast(LocaleKey.Common_Error.I18N())
                .WithContent(errorMessage)
                .DismissOnClick()
                .OnBottomRight()
                .ShowError();
        }

        lastErrorMessage = errorMessage;
    }

    public void Dispose()
    {
        _networkSettings.PropertyChanged -= HandleNetworkSettingsPropertyChanged;
    }
}