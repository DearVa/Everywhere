using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.Common;
using GnomeStack.Os.Secrets;

namespace Everywhere.Cloud;

public partial class OAuthCloudClient : ObservableObject, ICloudClient, IRecipient<ApplicationCommand>
{
    private const string ServiceName = "com.sylinko.everywhere";
    private const string TokenDataKey = "oauth_token_data";

    private const string ClientId = "2594ff9a1589fa4817fc5156ffaeee13";
    private const string BaseUrl = "https://localhost:3000";
    private const string AuthorizeEndpoint = $"{BaseUrl}/api/auth/oauth2/authorize";
    private const string TokenEndpoint = $"{BaseUrl}/api/auth/oauth2/token";
    private const string UserInfoEndpoint = $"{BaseUrl}/api/auth/oauth2/userinfo";
    private const string RevokeEndpoint = $"{BaseUrl}/api/auth/oauth2/revoke";
    private const string EndSessionEndpoint = $"{BaseUrl}/api/auth/oauth2/end-session";
    private const string RequestRedirectUri = $"{BaseUrl}/oauth2/device-callback?redirect=sylinko-everywhere://callback";
    private const string ResponseRedirectUri = "sylinko-everywhere://callback";
    private const string Scopes = "openid profile email offline_access";

    private string? _accessToken;
    private string? _refreshToken;
    private string? _idToken;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILauncher _launcher;

    private readonly SemaphoreSlim _loginLock = new(1, 1);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    // To coordinate the callback
    private TaskCompletionSource<string>? _authCodeTcs;
    private string? _expectedState;
    private string? _codeVerifier;

    public OAuthCloudClient(ILauncher launcher, IHttpClientFactory httpClientFactory)
    {
        _launcher = launcher;
        _httpClientFactory = httpClientFactory;
        WeakReferenceMessenger.Default.Register(this);
    }

    public bool IsAuthenticated => !string.IsNullOrEmpty(_accessToken);
    public string? IdToken => _idToken;

    private static void SecureStore(string key, string value)
    {
        OsSecretVault.SetSecret(ServiceName, key, value);
    }

    [ObservableProperty]
    public partial UserProfile? CurrentUser { get; set; }

    public async Task<bool> TrySilentLoginAsync()
    {
        try
        {
            // Try to load tokens from secure storage
            var json = OsSecretVault.GetSecret(ServiceName, TokenDataKey);
            if (string.IsNullOrEmpty(json))
            {
                return false;
            }

            var data = JsonSerializer.Deserialize<TokenData>(json);
            if (data?.RefreshToken == null)
            {
                return false;
            }

            _accessToken = data.AccessToken;
            _refreshToken = data.RefreshToken;
            _idToken = data.IdToken;

            // Try to refresh the token to ensure it's still valid
            var parameters = new Dictionary<string, string>
            {
                { "grant_type", "refresh_token" },
                { "refresh_token", _refreshToken },
                { "client_id", ClientId }
            };

            CurrentUser = await RequestTokenAsync(parameters);
            return CurrentUser is not null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Silent login failed: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> LoginAsync()
    {
        if (!await _loginLock.WaitAsync(0)) return false;

        try
        {
            // 1. Prepare for callback
            _authCodeTcs = new TaskCompletionSource<string>();
            _expectedState = Guid.CreateVersion7().ToString();
            _codeVerifier = GenerateCodeVerifier();
            var codeChallenge = GenerateCodeChallenge(_codeVerifier);

            // 2. Construct Authorization URL with PKCE
            // Added: offline_access, profile, email to scopes
            var sb = new StringBuilder(AuthorizeEndpoint);
            sb.Append("?response_type=code");
            sb.Append($"&client_id={ClientId}");
            sb.Append($"&redirect_uri={Uri.EscapeDataString(RequestRedirectUri)}");
            sb.Append($"&state={_expectedState}");
            sb.Append($"&scope={Uri.EscapeDataString(Scopes)}");
            sb.Append($"&code_challenge={Uri.EscapeDataString(codeChallenge)}");
            sb.Append("&code_challenge_method=S256");
            sb.Append($"&audience={Uri.EscapeDataString("https://localhost:4001")}");

            var authorizeUrl = sb.ToString();
            Console.WriteLine($"[AuthService] Starting login flow. Auth URL: {authorizeUrl}");

            // 3. Open the system browser
            await _launcher.LaunchUriAsync(new Uri(authorizeUrl));

            // 4. Wait for the callback
            var completedTask = await Task.WhenAny(_authCodeTcs.Task, Task.Delay(TimeSpan.FromMinutes(5)));

            if (completedTask != _authCodeTcs.Task)
            {
                throw new TimeoutException("Login timed out.");
            }

            var code = await _authCodeTcs.Task;

            // 5. Exchange Authorization Code for Access Token
            CurrentUser = await ExchangeCodeForTokenAsync(code);
            return CurrentUser is not null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Login flow failed: {ex.Message}");
            return false;
        }
        finally
        {
            _authCodeTcs = null;
            _expectedState = null;
            _codeVerifier = null;
            _loginLock.Release();
        }
    }

    private async Task<UserProfile?> ExchangeCodeForTokenAsync(string code)
    {
        if (_codeVerifier == null) return null;

        var parameters = new Dictionary<string, string>
        {
            { "grant_type", "authorization_code" },
            { "code", code },
            { "redirect_uri", RequestRedirectUri },
            { "client_id", ClientId },
            { "code_verifier", _codeVerifier }
        };

        return await RequestTokenAsync(parameters);
    }

    private async Task<UserProfile?> RequestTokenAsync(Dictionary<string, string> parameters)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(parameters)
        };

        try
        {
            // Use the default client (without auth handler) to avoid circular dependency
            using var httpClient = _httpClientFactory.CreateClient();
            var response = await httpClient.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Token exchange failed. Status: {response.StatusCode}, Body: {content}");
                return null;
            }

            var json = JsonSerializer.Deserialize<JsonElement>(content);

            if (json.TryGetProperty("access_token", out var at))
            {
                _accessToken = at.GetString();
                Console.WriteLine($"[AuthService] Access Token received: {_accessToken}");
            }
            if (json.TryGetProperty("refresh_token", out var rt)) _refreshToken = rt.GetString();
            if (json.TryGetProperty("id_token", out var it)) _idToken = it.GetString();

            SaveTokens();

            return await GetUserInfoAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Token request exception: {ex.Message}");
            return null;
        }
    }


    public async Task<UserProfile?> GetUserInfoAsync()
    {
        if (string.IsNullOrEmpty(_accessToken)) return null;

        var request = new HttpRequestMessage(HttpMethod.Get, UserInfoEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

        try
        {
            using var httpClient = _httpClientFactory.CreateClient();
            var response = await httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<UserProfile>();
            }
            else
            {
                Console.WriteLine($"UserInfo failed: {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"User info fetch failed: {ex.Message}");
        }

        return null;
    }

    private void SaveTokens()
    {
        try
        {
            var data = new TokenData
            {
                AccessToken = _accessToken,
                RefreshToken = _refreshToken,
                IdToken = _idToken
            };
            var json = JsonSerializer.Serialize(data);
            SecureStore(TokenDataKey, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AuthService] Failed to save tokens: {ex.Message}");
        }
    }

    // Token Persistence
    private class TokenData
    {
        public string? AccessToken { get; set; }
        public string? RefreshToken { get; set; }
        public string? IdToken { get; set; }
    }

    // PKCE Helper Methods
    private static string GenerateCodeVerifier()
    {
        var bytes = new byte[32];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(bytes);
        }
        return Base64UrlEncode(bytes);
    }

    private static string GenerateCodeChallenge(string codeVerifier)
        => Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(codeVerifier)));

    public async Task LogoutAsync()
    {
        // 1. Revoke Access Token if exists
        if (!string.IsNullOrEmpty(_accessToken))
        {
            await RevokeTokenAsync(_accessToken, "access_token");
        }

        // 2. Revoke Refresh Token if exists
        if (!string.IsNullOrEmpty(_refreshToken))
        {
            await RevokeTokenAsync(_refreshToken, "refresh_token");
        }

        // 3. Open RP-Initiated Logout if id_token exists
        if (!string.IsNullOrEmpty(_idToken))
        {
            var url = $"{EndSessionEndpoint}?id_token_hint={_idToken}&post_logout_redirect_uri={Uri.EscapeDataString(RequestRedirectUri)}";
            try
            {
                await _launcher.LaunchUriAsync(new Uri(url));
            }
            catch
            { /* Ignore browser errors */
            }
        }

        _accessToken = null;
        _refreshToken = null;
        _idToken = null;
        CurrentUser = null;
        OsSecretVault.DeleteSecret(ServiceName, TokenDataKey);
    }

    private async Task RevokeTokenAsync(string token, string tokenTypeHint)
    {
        var parameters = new Dictionary<string, string>
        {
            { "token", token },
            { "token_type_hint", tokenTypeHint },
            { "client_id", ClientId }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, RevokeEndpoint)
        {
            Content = new FormUrlEncodedContent(parameters)
        };

        try
        {
            using var httpClient = _httpClientFactory.CreateClient();
            await httpClient.SendAsync(request);
        }
        catch
        { /* Ignore revocation errors */
        }
    }

    public async Task RefreshUserProfileAsync()
    {
        if (string.IsNullOrEmpty(_refreshToken)) return;

        var parameters = new Dictionary<string, string>
        {
            { "grant_type", "refresh_token" },
            { "refresh_token", _refreshToken },
            { "client_id", ClientId }
        };

        CurrentUser = await RequestTokenAsync(parameters);
    }

    public Task<string?> GetAccessTokenAsync() => Task.FromResult(_accessToken);

    public async Task<bool> TryRefreshTokenAsync()
    {
        if (string.IsNullOrEmpty(_refreshToken)) return false;

        // Prevent concurrent refresh attempts
        if (!await _refreshLock.WaitAsync(0))
        {
            // Another refresh is in progress, wait for it
            await _refreshLock.WaitAsync();
            _refreshLock.Release();
            // Check if the refresh was successful by verifying we have a token
            return !string.IsNullOrEmpty(_accessToken);
        }

        try
        {
            var parameters = new Dictionary<string, string>
            {
                { "grant_type", "refresh_token" },
                { "refresh_token", _refreshToken },
                { "client_id", ClientId }
            };

            var result = await RequestTokenAsync(parameters);
            return result is not null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Token refresh failed: {ex.Message}");
            return false;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public void Receive(ApplicationCommand message)
    {
        if (message is not UrlProtocolCallbackCommand oauth) return;

        var url = oauth.Url;
        Console.WriteLine($"[AuthService] Received URL callback: {url}");

        // Only process if we are expecting a callback
        if (_authCodeTcs == null || _authCodeTcs.Task.IsCompleted)
        {
            Console.WriteLine("[AuthService] Not expecting callback or task already completed. Ignoring.");
            return;
        }

        try
        {
            var uri = new Uri(url);

            // Check if it matches our redirect scheme/path
            // Note: Simplistic check. Better to check Scheme and Path specifically.
            if (!url.StartsWith(ResponseRedirectUri, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("[AuthService] URL does not match expected prefix. Ignoring.");
                return;
            }

            // Parse Query Parameters
            var query = HttpUtility.ParseQueryString(uri.Query);
            var code = query["code"];
            var state = query["state"];
            var error = query["error"];
            var errorDesc = query["error_description"];

            Console.WriteLine($"[AuthService] Extracted - Code: '{code}', State: '{state}', Error: '{error}'");

            if (!string.IsNullOrEmpty(error))
            {
                _authCodeTcs.TrySetException(new Exception($"OAuth Error: {error} - {errorDesc}"));
                return;
            }

            if (state != _expectedState)
            {
                Console.WriteLine("[AuthService] State mismatch!");
                _authCodeTcs.TrySetException(new Exception($"Invalid state received. Expected: {_expectedState}, Received: {state}"));
                return;
            }

            if (!string.IsNullOrEmpty(code))
            {
                _authCodeTcs.TrySetResult(code);
            }
            else
            {
                _authCodeTcs.TrySetException(new Exception("No code found in callback."));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AuthService] Exception in OnUrlDropped: {ex}");
            _authCodeTcs.TrySetException(ex);
        }
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .Replace("=", "");
    }
}