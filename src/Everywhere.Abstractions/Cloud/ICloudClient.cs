using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Everywhere.Cloud;

/// <summary>
/// Represents the user's profile information.
/// </summary>
public partial class UserProfile : ObservableObject
{
    [ObservableProperty]
    [JsonPropertyName("name")]
    public required partial string Name { get; set; }

    [ObservableProperty]
    [JsonPropertyName("email")]
    public required partial string Email { get; set; }

    [ObservableProperty]
    [JsonPropertyName("picture")]
    public partial string? AvatarUrl { get; set; }

    [ObservableProperty]
    public partial SubscriptionPlan SubscriptionPlan { get; set; }

    [ObservableProperty]
    public partial long UsedTokens { get; set; } = 15000;

    [ObservableProperty]
    public partial long TotalTokens { get; set; } = 50000;

    [ObservableProperty]
    public partial string QuotaResetInfo { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool CanUpgradePlan { get; set; } = true;

    public ObservableCollection<string> PlanFeatures { get; } = [];

    [ObservableProperty]
    public partial string? AccessToken { get; set; }
}

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
    /// Attempts silent login using stored credentials (refresh tokens).
    /// This should be called at app startup to restore the user's session without browser interaction.
    /// </summary>
    /// <returns>A task returning true if silent login was successful, otherwise false.</returns>
    Task<bool> TrySilentLoginAsync();

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
    /// Gets the current access token for API requests.
    /// Returns null if not authenticated.
    /// </summary>
    /// <returns>The current access token, or null if not available.</returns>
    Task<string?> GetAccessTokenAsync();

    /// <summary>
    /// Attempts to refresh the access token using the stored refresh token.
    /// This is typically called by the authentication handler when a 401 response is received.
    /// </summary>
    /// <returns>True if the token was successfully refreshed, false otherwise.</returns>
    Task<bool> TryRefreshTokenAsync();
}