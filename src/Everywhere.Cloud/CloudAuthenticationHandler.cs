using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.Cloud;

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
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
public sealed class CloudAuthenticationHandler : DelegatingHandler
{
    private readonly IServiceProvider _serviceProvider;
    private static readonly SemaphoreSlim RefreshLock = new(1, 1);

    // Use IServiceProvider to avoid circular dependency:
    // CloudAuthenticationHandler -> ICloudClient -> IHttpClientFactory -> CloudAuthenticationHandler
    public CloudAuthenticationHandler(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var cloudClient = _serviceProvider.GetRequiredService<ICloudClient>();

        // Add authorization header if we have a token
        await AddAuthorizationHeaderAsync(request, cloudClient);

        var response = await base.SendAsync(request, cancellationToken);

        // If we get a 401, try to refresh the token and retry once
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            var refreshed = await TryRefreshTokenWithLockAsync(cloudClient);

            if (refreshed)
            {
                // Clone the request (original request cannot be reused)
                var retryRequest = await CloneRequestAsync(request);
                await AddAuthorizationHeaderAsync(retryRequest, cloudClient);

                // Dispose the original response before retrying
                response.Dispose();
                response = await base.SendAsync(retryRequest, cancellationToken);
            }
        }

        return response;
    }

    private static async Task AddAuthorizationHeaderAsync(HttpRequestMessage request, ICloudClient cloudClient)
    {
        var token = await cloudClient.GetAccessTokenAsync();
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    private static async Task<bool> TryRefreshTokenWithLockAsync(ICloudClient cloudClient)
    {
        // Use a lock to prevent multiple concurrent refresh attempts
        await RefreshLock.WaitAsync();
        try
        {
            return await cloudClient.TryRefreshTokenAsync();
        }
        finally
        {
            RefreshLock.Release();
        }
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version
        };

        // Copy headers
        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        // Copy content if present
        if (request.Content != null)
        {
            var contentBytes = await request.Content.ReadAsByteArrayAsync();
            clone.Content = new ByteArrayContent(contentBytes);

            // Copy content headers
            foreach (var header in request.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        // Copy options
        foreach (var option in request.Options)
        {
            clone.Options.TryAdd(option.Key, option.Value);
        }

        return clone;
    }
}
