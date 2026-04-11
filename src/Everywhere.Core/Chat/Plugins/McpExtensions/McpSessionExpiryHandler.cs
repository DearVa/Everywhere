using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace Everywhere.Chat.Plugins.McpExtensions;

/// <summary>
/// A delegating handler that intercepts non-404 4xx responses from MCP servers
/// and converts them to 404 if the response body indicates a session expired error.
/// This allows the SDK's standard <c>SetSessionExpired</c> path to handle it.
/// </summary>
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
internal sealed class McpSessionExpiryHandler : DelegatingHandler
{
    private static readonly string[] SessionExpiredKeywords =
    [
        "session expired",
        "session_expired",
        "session has expired",
        "session not found",
        "session_not_found",
        "invalid session",
        "unknown session",
        "SessionExpired",
        "SessionNotFound",
    ];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        // Only intercept non-404 4xx responses.
        if (response.StatusCode is not HttpStatusCode.NotFound &&
            (int)response.StatusCode is >= 400 and < 500 &&
            response.Content is not null)
        {
            // Buffer the response content so we can read it and still return it if no match.
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (ContainsSessionExpiredKeyword(body))
            {
                // Replace with 404 so the SDK triggers SetSessionExpired.
                var newResponse = new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    RequestMessage = response.RequestMessage,
                    ReasonPhrase = "Session Expired (rewritten by McpSessionExpiryHandler)",
                    Version = response.Version,
                    Content = new StringContent(body),
                };

                // Copy headers.
                foreach (var header in response.Headers)
                {
                    newResponse.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                response.Dispose();
                return newResponse;
            }
        }

        return response;
    }

    private static bool ContainsSessionExpiredKeyword(string body)
    {
        foreach (var keyword in SessionExpiredKeywords)
        {
            if (body.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
