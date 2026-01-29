using System.ComponentModel;

namespace Everywhere.Cloud;

/// <summary>
/// Represents the user's profile information.
/// </summary>
/// <param name="Nickname">The user's display name.</param>
/// <param name="AvatarUrl">The URL of the user's avatar image.</param>
/// <param name="PlanType">The type of the subscription plan (e.g., Free, Pro).</param>
/// <param name="TotalPoints">The user's total accumulated points.</param>
/// <param name="RemainingPoints">The user's currently available points.</param>
public record UserProfile(
    string Nickname,
    string? AvatarUrl,
    string PlanType,
    long TotalPoints,
    long RemainingPoints
);

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
    /// <returns>A task returning true if login was successful, otherwise false.</returns>
    Task<bool> LoginAsync();

    /// <summary>
    /// Logs out the current user, revoking tokens and clearing local storage.
    /// </summary>
    Task LogoutAsync();

    /// <summary>
    /// Manually refreshes the user profile data from the server.
    /// Useful for updating points or plan status in response to user actions (e.g., purchase or consumption).
    /// </summary>
    Task RefreshUserProfileAsync();

    /// <summary>
    /// Creates a configured <see cref="HttpClient"/> for making API requests.
    /// The client automatically handles "Bearer" token attachment and silent token refreshing upon 401 Unauthorized responses.
    /// </summary>
    /// <returns>A configured <see cref="HttpClient"/> instance.</returns>
    HttpClient CreateApiClient();

    /// <summary>
    /// Retrieves the current valid Access Token.
    /// <para>
    /// Prefer using <see cref="CreateApiClient"/> for standard HTTP requests.
    /// This method is intended for scenarios where HttpClient cannot be used directly (e.g., establishing SignalR/WebSocket connections, or integration with third-party SDKs).
    /// </para>
    /// </summary>
    /// <returns>The access token string, or null if not authenticated.</returns>
    Task<string?> GetAccessTokenAsync();
}