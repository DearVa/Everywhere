using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using Duende.IdentityModel.OidcClient;
using Duende.IdentityModel.OidcClient.Browser;
using GnomeStack.Os.Secrets;

namespace Everywhere.Cloud;

public sealed partial class OAuthCloudClient(ILauncher launcher) : ObservableObject, ICloudClient
{
    private const string ServiceName = "com.sylinko.everywhere";
    private const string RefreshTokenKey = "auth_refresh_token";

    // TODO: Move these to configuration
    private const string Authority = "https://demo.duendesoftware.com";
    private const string ClientId = "interactive.public";
    private const string RedirectUri = "http://127.0.0.1:49152/callback";
    private const string Scope = "openid profile email offline_access";

    private OidcClient? _oidcClient;

    [ObservableProperty]
    public partial UserProfile? CurrentUser { get; set; }

    public async Task<bool> LoginAsync()
    {
        try
        {
            var result = await EnsureOidcClient().LoginAsync(new LoginRequest());
            if (result.IsError)
            {
                Trace.WriteLine($"Login failed: {result.Error}");
                return false;
            }

            await SecureStoreAsync(RefreshTokenKey, result.RefreshToken);
            UpdateUserProfile(result.User, result.AccessToken);
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Login exception: {ex}");
            return false;
        }
    }

    public async Task LogoutAsync()
    {
        try
        {
            await EnsureOidcClient().LogoutAsync(new LogoutRequest());
            OsSecretVault.DeleteSecret(ServiceName, RefreshTokenKey);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Logout exception: {ex}");
        }
        finally
        {
            CurrentUser = null;
        }
    }

    public async Task RefreshUserProfileAsync()
    {
        var refreshToken = OsSecretVault.GetSecret(ServiceName, RefreshTokenKey);
        if (string.IsNullOrEmpty(refreshToken))
        {
            CurrentUser = null;
            return;
        }

        // Silent refresh using the stored refresh token
        var result = await EnsureOidcClient().RefreshTokenAsync(refreshToken);
        if (!result.IsError)
        {
            // Rotate refresh token if a new one was issued
            if (!string.IsNullOrEmpty(result.RefreshToken) && result.RefreshToken != refreshToken)
            {
                await SecureStoreAsync(RefreshTokenKey, result.RefreshToken);
            }

            // Fetch user info using the new access token
            var userInfo = await _oidcClient.GetUserInfoAsync(result.AccessToken);
            if (!userInfo.IsError)
            {
                UpdateUserProfile(
                    new ClaimsPrincipal(new ClaimsIdentity(userInfo.Claims)),
                    result.AccessToken);
            }
        }
        else
        {
            // If refresh fails (e.g. revoked), clear session
            CurrentUser = null;
        }
    }

    public HttpClient CreateApiClient()
    {
        EnsureOidcClient();
        // Create a handler that automatically refreshes the token when 401 occurs
        var handler = new AutoRefreshTokenHandler(this);
        return new HttpClient(handler)
        {
            BaseAddress = new Uri(Authority) // TODO: Replace with your API base address
        };
    }

    public async Task<string?> GetAccessTokenAsync()
    {
        // Simple implementation: Try to get a valid token by refreshing. 
        // In a real app, cache the AccessToken in memory and check expiration before refreshing.
        var refreshToken = OsSecretVault.GetSecret(ServiceName, RefreshTokenKey);
        if (string.IsNullOrEmpty(refreshToken)) return null;

        var result = await EnsureOidcClient().RefreshTokenAsync(refreshToken);
        if (!result.IsError)
        {
            if (!string.IsNullOrEmpty(result.RefreshToken) && result.RefreshToken != refreshToken)
            {
                await SecureStoreAsync(RefreshTokenKey, result.RefreshToken);
            }
            return result.AccessToken;
        }
        return null;
    }

    [MemberNotNull(nameof(_oidcClient))]
    private OidcClient EnsureOidcClient()
    {
        return _oidcClient ??= new OidcClient(new OidcClientOptions
        {
            Authority = Authority,
            ClientId = ClientId,
            Scope = Scope,
            RedirectUri = RedirectUri,
            Browser = new SystemBrowser(launcher),
            Policy = new Policy { RequireAccessTokenHash = false }
        });
    }

    private void UpdateUserProfile(ClaimsPrincipal user, string accessToken)
    {
        var name = user.FindFirst("name")?.Value ?? user.FindFirst("sub")?.Value ?? "Unknown";
        var picture = user.FindFirst("picture")?.Value;

        // TODO: Map custom claims for points/plan if available in ID Token or UserInfo
        var plan = "Free";
        long points = 0;
        long remaining = 0;

        CurrentUser = new UserProfile(name, picture, plan, points, remaining);
    }

    private Task SecureStoreAsync(string key, string value)
    {
        return Task.Run(() => OsSecretVault.SetSecret(ServiceName, key, value));
    }

    private class SystemBrowser(ILauncher launcher) : IBrowser
    {
        public async Task<BrowserResult> InvokeAsync(BrowserOptions options, CancellationToken cancellationToken = default)
        {
            using var listener = new HttpListener();
            // Listen on the Redirect URI. Note: Must run as Admin on Windows or use netsh to allow non-admin port reservation if not localhost.
            // Localhost usually fine.
            listener.Prefixes.Add(ExtractRedirectUriPrefix(options.StartUrl));
            listener.Start();

            try
            {
                if (!await launcher.LaunchUriAsync(new Uri(options.StartUrl)))
                {
                    return new BrowserResult { ResultType = BrowserResultType.UnknownError, Error = "Failed to launch browser" };
                }

                var context = await listener.GetContextAsync();

                // Return a simple HTML response to the browser
                using (var response = context.Response)
                {
                    var content = "<html><body>You can close this window.</body></html>"u8;
                    response.ContentLength64 = content.Length;
                    await response.OutputStream.WriteAsync(content.ToArray(), cancellationToken);
                }

                return new BrowserResult
                {
                    ResultType = BrowserResultType.Success,
                    Response = context.Request.Url?.ToString() ?? string.Empty
                };
            }
            catch (Exception ex)
            {
                return new BrowserResult { ResultType = BrowserResultType.UnknownError, Error = ex.Message };
            }
            finally
            {
                listener.Stop();
            }
        }

        private static string ExtractRedirectUriPrefix(string startUrl)
        {
            // We use the configured global RedirectUri
            return RedirectUri.EndsWith('/') ? RedirectUri : RedirectUri + "/";
        }
    }

    private class AutoRefreshTokenHandler(OAuthCloudClient client) : DelegatingHandler(new HttpClientHandler())
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var token = await client.GetAccessTokenAsync();
            if (!string.IsNullOrEmpty(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            var response = await base.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                // Refresh logic is already implicitly handled by GetAccessTokenAsync always refreshing first in this simple implementation
                // But if token was valid at start of request and expired during, we might want to retry effectively.
                // For now, this is a basic implementation.
            }
            return response;
        }
    }
}