using System;
using System.Linq;
using System.Net;
using System.Net.Http;

namespace Everywhere.Configuration;

internal static class NetworkProxyConfigurator
{
    private static readonly object SyncRoot = new();

    private static bool _systemProxyCaptured;
    private static IWebProxy _systemHttpProxy = HttpClient.DefaultProxy;
    private static IWebProxy? _systemWebRequestProxy = WebRequest.DefaultWebProxy;

    public static event EventHandler<ProxyConfigurationChangedEventArgs>? ProxyConfigurationChanged;

    public static IWebProxy? CurrentProxy { get; private set; } = HttpClient.DefaultProxy ?? WebRequest.DefaultWebProxy;

    private static readonly char[] BypassSeparators =
    [
        '\r',
        '\n',
        ';',
        ','
    ];

    public static bool TryApply(NetworkSettings settings, out string? errorMessage)
    {
        lock (SyncRoot)
        {
            CaptureSystemProxy();

            if (!settings.IsProxyEnabled)
            {
                HttpClient.DefaultProxy = _systemHttpProxy;
                WebRequest.DefaultWebProxy = _systemWebRequestProxy;
                CurrentProxy = _systemHttpProxy;
                OnProxyConfigurationChanged(CurrentProxy);
                errorMessage = null;
                return true;
            }

            if (string.IsNullOrWhiteSpace(settings.ProxyAddress))
            {
                errorMessage = "Proxy server address is required.";
                return false;
            }

            if (!TryCreateProxy(settings, out var proxy, out errorMessage)) return false;

            HttpClient.DefaultProxy = proxy;
            WebRequest.DefaultWebProxy = proxy;
            CurrentProxy = proxy;
            OnProxyConfigurationChanged(CurrentProxy);
            return true;
        }
    }

    public static bool IsProxyProperty(string? propertyName) => propertyName is
        nameof(NetworkSettings.IsProxyEnabled) or
        nameof(NetworkSettings.ProxyAddress) or
        nameof(NetworkSettings.BypassProxyOnLocal) or
        nameof(NetworkSettings.ProxyBypassList) or
        nameof(NetworkSettings.UseProxyAuthentication) or
        nameof(NetworkSettings.ProxyUsername) or
        nameof(NetworkSettings.ProxyPassword);

    private static bool TryCreateProxy(NetworkSettings settings, out WebProxy proxy, out string? errorMessage)
    {
        var normalizedAddress = NormalizeAddress(settings.ProxyAddress);
        if (!Uri.TryCreate(normalizedAddress, UriKind.Absolute, out var proxyUri))
        {
            proxy = default!;
            errorMessage = "Proxy server address is invalid.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(proxyUri.Host))
        {
            proxy = default!;
            errorMessage = "Proxy host is required.";
            return false;
        }

        if (proxyUri.Scheme is not "http" and not "https")
        {
            proxy = default!;
            errorMessage = $"Proxy scheme '{proxyUri.Scheme}' is not supported.";
            return false;
        }

        proxy = new WebProxy(proxyUri)
        {
            BypassProxyOnLocal = settings.BypassProxyOnLocal,
            UseDefaultCredentials = false
        };

        var bypassEntries = ParseBypassList(settings.ProxyBypassList);
        proxy.BypassList = bypassEntries.Length > 0 ? bypassEntries : Array.Empty<string>();

        if (!settings.UseProxyAuthentication)
        {
            proxy.Credentials = null;
            errorMessage = null;
            return true;
        }

        if (string.IsNullOrWhiteSpace(settings.ProxyUsername))
        {
            proxy = default!;
            errorMessage = "Proxy username is required when authentication is enabled.";
            return false;
        }

        proxy.Credentials = new NetworkCredential(settings.ProxyUsername.Trim(), settings.ProxyPassword ?? string.Empty);

        errorMessage = null;
        return true;
    }

    private static void CaptureSystemProxy()
    {
    if (_systemProxyCaptured) return;

    _systemHttpProxy = HttpClient.DefaultProxy;
    _systemWebRequestProxy = WebRequest.DefaultWebProxy;
        CurrentProxy ??= _systemHttpProxy;
    _systemProxyCaptured = true;
    }

    private static void OnProxyConfigurationChanged(IWebProxy? proxy)
    {
        ProxyConfigurationChanged?.Invoke(null, new ProxyConfigurationChangedEventArgs(proxy));
    }

    private static string NormalizeAddress(string address)
    {
        address = address.Trim();

        if (!address.Contains("://", StringComparison.Ordinal))
        {
            address = $"http://{address}";
        }

        return address;
    }

    private static string[] ParseBypassList(string? bypassList)
    {
        if (string.IsNullOrWhiteSpace(bypassList)) return Array.Empty<string>();

        return bypassList
            .Split(BypassSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

public sealed class ProxyConfigurationChangedEventArgs(IWebProxy? proxy) : EventArgs
{
    public IWebProxy? Proxy { get; } = proxy;

    public bool IsEnabled => Proxy is not null;
}