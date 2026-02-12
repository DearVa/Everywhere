using System.ComponentModel;

namespace Everywhere.Cloud;

/// <summary>
/// Interface for cloud client operations, handling authentication and user profile management.
/// Implements <see cref="INotifyPropertyChanged"/> to support data binding for the <see cref="CurrentUser"/> property.
/// </summary>
public interface ICloudClient : INotifyPropertyChanged
{
    /// <summary>
    /// Gets the current logged-in user profile. Returns null if not logged in.
    /// This property raises <see cref="INotifyPropertyChanged.PropertyChanged"/> when updated.
    /// </summary>
    UserProfile? CurrentUser { get; }

    /// <summary>
    /// Initiates the OAuth 2.0 (PKCE) login flow.
    /// This process should handle browser interaction, callback capture, token exchange, and initial user profile retrieval.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns>A task returning true if login was successful, otherwise false.</returns>
    Task<bool> LoginAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Logs out the current user, revoking tokens and clearing local storage.
    /// </summary>
    Task LogoutAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Attempts to refresh the access token using the stored refresh token.
    /// This is typically called by the authentication handler when a 401 response is received.
    /// </summary>
    /// <returns>True if the token was successfully refreshed, false otherwise.</returns>
    Task<bool> TryRefreshTokenAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Creates a DelegatingHandler that can be added to the HTTP client pipeline to automatically handle authentication.
    /// It adds the necessary Authorization header to outgoing requests and attempts token refresh on 401 responses.
    /// </summary>
    /// <returns></returns>
    DelegatingHandler CreateAuthenticationHandler();
}