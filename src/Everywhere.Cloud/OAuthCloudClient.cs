using System.Buffers.Text;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.Common;
using Everywhere.Extensions;
using GnomeStack.Os.Secrets;
using Microsoft.Extensions.Logging;

namespace Everywhere.Cloud;

public partial class OAuthCloudClient : ObservableObject, ICloudClient, IAsyncInitializer, IRecipient<ApplicationCommand>
{
    /// <summary>
    /// Record for holding token data for secure storage. Using a record for easy JSON serialization and immutability.
    /// </summary>
    /// <param name="RefreshToken"></param>
    /// <param name="AccessToken"></param>
    /// <param name="IdToken"></param>
    private partial record TokenData(
        [property: JsonPropertyName("refresh_token")] string RefreshToken,
        [property: JsonPropertyName("access_token")] string? AccessToken = null,
        [property: JsonPropertyName("id_token")] string? IdToken = null
    )
    {
        [JsonSerializable(typeof(TokenData))]
        private partial class TokenDataJsonSerializerContext : JsonSerializerContext;

        public static TokenData? FromJson(string json)
        {
            try
            {
                return JsonSerializer.Deserialize(json, TokenDataJsonSerializerContext.Default.TokenData);
            }
            catch
            {
                return null;
            }
        }

        public string ToJson()
        {
            return JsonSerializer.Serialize(this, TokenDataJsonSerializerContext.Default.TokenData);
        }
    }

    [ObservableProperty]
    public partial UserProfile? CurrentUser { get; set; }

    private const string ServiceName = "com.sylinko.everywhere";
    private const string TokenDataKey = "oauth_token_data";

    private const string AuthorizeEndpoint = $"{CloudConstants.OAuthBaseUrl}/api/auth/oauth2/authorize";
    private const string TokenEndpoint = $"{CloudConstants.OAuthBaseUrl}/api/auth/oauth2/token";
    private const string UserInfoEndpoint = $"{CloudConstants.OAuthBaseUrl}/api/auth/oauth2/userinfo";
    private const string RevokeEndpoint = $"{CloudConstants.OAuthBaseUrl}/api/auth/oauth2/revoke";
    private const string RedirectUri = "sylinko-everywhere://callback";
    private const string Scopes = "openid profile email offline_access";

    private TokenData? _tokenData;

    private readonly ILauncher _launcher;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OAuthCloudClient> _logger;

    private readonly SemaphoreSlim _loginLock = new(1, 1);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    // To coordinate the callback
    private TaskCompletionSource<string>? _authCodeTcs;
    private volatile string? _expectedState;

    public OAuthCloudClient(ILauncher launcher, IHttpClientFactory httpClientFactory, ILogger<OAuthCloudClient> logger)
    {
        _launcher = launcher;
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        WeakReferenceMessenger.Default.Register(this);
    }

    public async Task<bool> LoginAsync(CancellationToken cancellationToken)
    {
        if (!await _loginLock.WaitAsync(0, cancellationToken)) return false;

        try
        {
            _authCodeTcs = new TaskCompletionSource<string>();
            _expectedState = Guid.NewGuid().ToString();
            var codeVerifier = GenerateCodeVerifier();
            var codeChallenge = GenerateCodeChallenge(codeVerifier);

            // Construct Authorization URL with PKCE
            var sb = new StringBuilder(AuthorizeEndpoint);
            sb.Append($"?response_type=code");
            sb.Append($"&client_id={CloudConstants.ClientId}");
            sb.Append($"&redirect_uri={Uri.EscapeDataString(RedirectUri)}");
            sb.Append($"&state={_expectedState}");
            sb.Append($"&scope={Uri.EscapeDataString(Scopes)}");
            sb.Append($"&code_challenge={Uri.EscapeDataString(codeChallenge)}");
            sb.Append($"&code_challenge_method=S256");
            sb.Append($"&audience=sylinko-everywhere-api");

            var authorizeUrl = sb.ToString();
            _logger.LogDebug("[AuthService] Starting login flow. Auth URL: {AuthorizeUrl}", authorizeUrl);
            await _launcher.LaunchUriAsync(new Uri(authorizeUrl));

            // Wait for the callback
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)); // No interaction required, so we can have a reasonable timeout
            string code;
            try
            {
                code = await _authCodeTcs.Task.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("Login timed out.");
            }

            // Exchange Authorization Code for Access Token
            var parameters = new Dictionary<string, string>
            {
                { "grant_type", "authorization_code" },
                { "code", code },
                { "redirect_uri", RedirectUri },
                { "client_id", CloudConstants.ClientId },
                { "code_verifier", codeVerifier }
            };
            await RequestTokenAsync(parameters, cancellationToken);

            // Refresh UserProfile
            await RefreshUserProfileAsync(cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to login flow.");
            _tokenData = null;
            return false;
        }
        finally
        {
            _authCodeTcs = null;
            _expectedState = null;
            _loginLock.Release();
        }
    }

    /// <summary>
    /// Requests an access token using the provided parameters.
    /// This method is used for both exchanging the authorization code and refreshing the token.
    /// </summary>
    /// <param name="parameters"></param>
    /// <param name="cancellationToken"></param>
    /// <exception cref="HttpRequestException"></exception>
    private async Task RequestTokenAsync(Dictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        using var httpClient = _httpClientFactory.CreateClient();

        // OAuth 2.0 token endpoint requires application/x-www-form-urlencoded (RFC 6749 Section 4.1.3)
        var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(parameters)
        };
        var response = await httpClient.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        _tokenData = TokenData.FromJson(content);
        if (_tokenData is null)
        {
            throw new HttpRequestException("Failed to parse token response. Invalid format.", null, response.StatusCode);
        }

        try
        {
            OsSecretVault.SetSecret(ServiceName, TokenDataKey, _tokenData.ToJson());
        }
        catch (Exception ex)
        {
            // Not a critical failure, we can still proceed with the obtained tokens
            _logger.LogWarning(ex, "Failed to save token data to secure storage");
        }
    }

    public async Task LogoutAsync(CancellationToken cancellationToken)
    {
        if (!await _loginLock.WaitAsync(0, cancellationToken)) return; // Prevent logout during login flow

        try
        {
            if (_tokenData is null) return; // Already logged out

            using var httpClient = _httpClientFactory.CreateClient();

            // Revoke Access Token if exists
            if (!_tokenData.AccessToken.IsNullOrEmpty())
            {
                await RevokeTokenAsync(_tokenData.AccessToken, "access_token");
            }

            // Revoke Refresh Token if exists
            if (!_tokenData.RefreshToken.IsNullOrEmpty())
            {
                await RevokeTokenAsync(_tokenData.RefreshToken, "refresh_token");
            }

            _tokenData = null;
            CurrentUser = null;

            try
            {
                OsSecretVault.DeleteSecret(ServiceName, TokenDataKey);
            }
            catch (Exception ex)
            {
                // Not a critical failure, just log it
                _logger.LogWarning(ex, "Failed to delete token data from secure storage");
            }

            async Task RevokeTokenAsync(string token, string tokenTypeHint)
            {
                var parameters = new Dictionary<string, string>
                {
                    { "token", token },
                    { "token_type_hint", tokenTypeHint },
                    { "client_id", CloudConstants.ClientId }
                };

                var request = new HttpRequestMessage(HttpMethod.Post, RevokeEndpoint)
                {
                    Content = new FormUrlEncodedContent(parameters)
                };

                try
                {
                    // ReSharper disable once AccessToDisposedClosure
                    await httpClient.SendAsync(request, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to revoke {TokenType}", tokenTypeHint);
                }
            }
        }
        finally
        {
            _loginLock.Release();
        }
    }

    public async Task RefreshUserProfileAsync(CancellationToken cancellationToken)
    {
        using var httpClient = _httpClientFactory.CreateClient(nameof(ICloudClient));

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, UserInfoEndpoint);
            var response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            CurrentUser = await response.Content.ReadFromJsonAsync<UserProfile>(cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get user profile");
        }
    }

    public async Task<bool> TryRefreshTokenAsync(CancellationToken cancellationToken)
    {
        if (_tokenData is null) return false;
        if (_tokenData.RefreshToken.IsNullOrEmpty()) return false;

        // Prevent concurrent refresh attempts
        if (!await _refreshLock.WaitAsync(0, cancellationToken))
        {
            // Another refresh is in progress, wait for it
            await _refreshLock.WaitAsync(cancellationToken);
            _refreshLock.Release();

            // Check if the refresh was successful by verifying we have a token
            return !string.IsNullOrEmpty(_tokenData.AccessToken);
        }

        try
        {
            var parameters = new Dictionary<string, string>
            {
                { "grant_type", "refresh_token" },
                { "refresh_token", _tokenData.RefreshToken },
                { "client_id", CloudConstants.ClientId }
            };

            await RequestTokenAsync(parameters, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Token refresh failed");
            return false;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public DelegatingHandler CreateAuthenticationHandler() => new CloudAuthenticationHandler(this);

    public void Receive(ApplicationCommand message)
    {
        if (message is not UrlProtocolCallbackCommand oauth) return;

        var url = oauth.Url;
        _logger.LogDebug("[AuthService] Received URL callback: {url}", url);

        // Only process if we are expecting a callback
        if (_authCodeTcs == null || _authCodeTcs.Task.IsCompleted)
        {
            _logger.LogDebug("[AuthService] Not expecting callback or task already completed. Ignoring.");
            return;
        }

        try
        {
            var uri = new Uri(url);

            // Parse Query Parameters
            var query = HttpUtility.ParseQueryString(uri.Query);
            var code = query["code"];
            var state = query["state"];
            var error = query["error"];
            var errorDesc = query["error_description"];

            _logger.LogDebug("[AuthService] Extracted - Code: '{code}', State: '{state}', Error: '{error}'", code, state, error);

            if (!string.IsNullOrEmpty(error))
            {
                _authCodeTcs.TrySetException(new Exception($"OAuth Error: {error} - {errorDesc}"));
                return;
            }

            if (state != _expectedState)
            {
                _logger.LogDebug("[AuthService] State mismatch!");
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
            _logger.LogDebug("[AuthService] Exception in OnUrlDropped: {exception}", ex);
            _authCodeTcs.TrySetException(ex);
        }
    }

    // PKCE Helper
    private static string GenerateCodeVerifier() =>
        Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    private static unsafe string GenerateCodeChallenge(string codeVerifier)
    {
        Span<byte> verifierBytes = stackalloc byte[codeVerifier.Length];
        var bytesWritten = Encoding.ASCII.GetBytes(codeVerifier, verifierBytes);
        verifierBytes = verifierBytes[..bytesWritten];
        Span<byte> hashBytes = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(verifierBytes, hashBytes);
        return Base64Url.EncodeToString(hashBytes);
    }

    #region IAsyncInitializer Implementation

    public AsyncInitializerPriority Priority => AsyncInitializerPriority.Highest;

    /// <summary>
    /// Initializes the client by attempting a silent login using stored tokens.
    /// This allows the app to restore the user's session without requiring them to log in again, providing a smoother user experience.
    /// If valid tokens are found and the refresh is successful, the CurrentUser will be populated.
    /// Otherwise, it will remain null, indicating that the user needs to log in interactively.
    /// </summary>
    public async Task InitializeAsync()
    {
        Debug.Assert(_tokenData is null, "Token data should be null before initialization");
        Debug.Assert(CurrentUser is null, "CurrentUser should be null before initialization");

        if (!await _loginLock.WaitAsync(0)) return; // Prevent reentry for initialization

        try
        {
            // Try to load tokens from secure storage
            var json = OsSecretVault.GetSecret(ServiceName, TokenDataKey);
            if (string.IsNullOrEmpty(json))
            {
                return;
            }

            var data = TokenData.FromJson(json);
            if (data?.RefreshToken == null)
            {
                return;
            }

            _tokenData = data;

            // Try to refresh the token to ensure it's still valid
            var parameters = new Dictionary<string, string>
            {
                { "grant_type", "refresh_token" },
                { "refresh_token", _tokenData.RefreshToken },
                { "client_id", CloudConstants.ClientId }
            };

            await RequestTokenAsync(parameters, CancellationToken.None);
            await RefreshUserProfileAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // This exception is expected to happen if the user has never logged in before or if the stored tokens are invalid/expired.
            // Log it for debugging but don't treat it as an error.
            _logger.LogInformation(ex, "Silent login failed");
        }
        finally
        {
            _loginLock.Release();
        }
    }

    #endregion

    /// <summary>
    /// A delegating handler that automatically adds authentication headers to outgoing requests
    /// and handles token refresh on 401 Unauthorized responses.
    /// </summary>
    /// <remarks>
    /// This handler:
    /// <list type="bullet">
    ///   <item>Adds Bearer token to Authorization header before each request</item>
    ///   <item>Catches 401 responses and attempts to refresh the token</item>
    ///   <item>Retries the request once with the new token after successful refresh</item>
    ///   <item>Uses a semaphore to prevent concurrent token refresh operations</item>
    /// </list>
    /// </remarks>
    private sealed class CloudAuthenticationHandler(OAuthCloudClient cloudClient) : DelegatingHandler
    {
        private static readonly SemaphoreSlim RefreshLock = new(1, 1);

        // Use IServiceProvider to avoid circular dependency:
        // CloudAuthenticationHandler -> ICloudClient -> IHttpClientFactory -> CloudAuthenticationHandler
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (cloudClient._tokenData?.AccessToken is not { Length: > 0 } token)
            {
                // No token available, proceed without adding Authorization header
                return await base.SendAsync(request, cancellationToken);
            }

            // Add authorization header if we have a token
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            // Before first send, ensure content is buffered for potential retry
            if (request.Content != null)
            {
                await request.Content.LoadIntoBufferAsync(cancellationToken);
            }

            var response = await base.SendAsync(request, cancellationToken);

            // If the response is not 401, return it as is
            if (response.StatusCode != HttpStatusCode.Unauthorized) return response;

            // If we get a 401, try to refresh the token and retry once
            var refreshed = await TryRefreshTokenWithLockAsync(cloudClient, cancellationToken);
            if (!refreshed) return response;

            if (cloudClient._tokenData?.AccessToken is not { Length: > 0 } newToken)
            {
                return response; // Refresh succeeded, but we don't have a new token, give up
            }

            // Dispose the original response before retrying
            response.Dispose();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", newToken);
            response = await base.SendAsync(request, cancellationToken);

            return response;
        }

        private static async Task<bool> TryRefreshTokenWithLockAsync(OAuthCloudClient cloudClient, CancellationToken cancellationToken)
        {
            // Use a lock to prevent multiple concurrent refresh attempts
            await RefreshLock.WaitAsync(cancellationToken);
            try
            {
                return await cloudClient.TryRefreshTokenAsync(cancellationToken);
            }
            finally
            {
                RefreshLock.Release();
            }
        }
    }
}