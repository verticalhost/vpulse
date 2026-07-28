using Serilog;
using System.Text.Json;
using VPULSE.Backend.App;
using VPULSE.Backend.Core.Models;

namespace VPULSE.Backend.Auth
{
    internal sealed record VpzPlusStatus(
        bool Active,
        DateTimeOffset? Since,
        DateTimeOffset? CurrentPeriodEnd,
        bool CancelAtPeriodEnd,
        string? Source,
        DateTimeOffset CheckedAtUtc)
    {
        public static VpzPlusStatus Inactive() =>
            new(false, null, null, false, null, DateTimeOffset.UtcNow);
    }

    internal sealed record AccountProfile(string Username, string? DisplayName, string? AvatarUrl);

    /// <summary>
    /// Owns what the frontend is told about sign-in state. The renderer receives a username, an
    /// avatar URL and booleans, never a token, so a process that connects to the local WebSocket
    /// learns nothing it can authenticate with.
    /// </summary>
    internal static class AuthStateService
    {
        private static readonly Lock _lock = new();
        private static readonly Dictionary<string, AccountProfile> _profiles = [];
        private static VpzPlusStatus _vpzPlus = VpzPlusStatus.Inactive();

        public static VpzPlusStatus VpzPlus
        {
            get { lock (_lock) { return _vpzPlus; } }
        }

        public static AccountProfile? ProfileFor(OAuthProvider provider)
        {
            lock (_lock)
            {
                return _profiles.GetValueOrDefault(provider.Name);
            }
        }

        /// <summary>Refreshes one provider's profile (and VPZ+ for VPZONE), then pushes state.</summary>
        public static async Task RefreshAsync(OAuthProvider provider)
        {
            if (OAuthTokenService.IsSignedIn(provider) && !Settings.Instance.AirplaneMode)
            {
                var profile = await FetchProfileAsync(provider);
                if (profile != null)
                {
                    lock (_lock)
                    {
                        _profiles[provider.Name] = profile;
                    }
                }

                if (provider.Name == OAuthProviders.VPZoneName)
                    await RefreshVpzPlusAsync();
            }

            await SendToFrontendAsync();
        }

        public static async Task RefreshAllAsync()
        {
            foreach (var provider in OAuthProviders.All)
            {
                if (OAuthTokenService.IsSignedIn(provider))
                    await RefreshAsync(provider);
            }

            await SendToFrontendAsync();
        }

        public static async Task SignOutAsync(OAuthProvider provider)
        {
            OAuthTokenService.SignOut(provider);

            lock (_lock)
            {
                _profiles.Remove(provider.Name);
                if (provider.Name == OAuthProviders.VPZoneName)
                    _vpzPlus = VpzPlusStatus.Inactive();
            }

            await SendToFrontendAsync();
        }

        public static async Task SendToFrontendAsync()
        {
            VpzPlusStatus vpzPlus;
            Dictionary<string, AccountProfile> profiles;
            lock (_lock)
            {
                vpzPlus = _vpzPlus;
                profiles = new Dictionary<string, AccountProfile>(_profiles);
            }

            await MessageService.SendFrontendMessage("AuthState", new
            {
                vpzone = Describe(OAuthProviders.VPZone, profiles),
                gamefolio = Describe(OAuthProviders.Gamefolio, profiles),
                vpzPlus = new
                {
                    isActive = vpzPlus.Active,
                    since = vpzPlus.Since,
                    currentPeriodEnd = vpzPlus.CurrentPeriodEnd,
                    cancelAtPeriodEnd = vpzPlus.CancelAtPeriodEnd,
                    source = vpzPlus.Source,
                    capabilities = FeatureGate.ActiveCapabilities(),
                },
            });
        }

        private static object Describe(OAuthProvider provider, Dictionary<string, AccountProfile> profiles)
        {
            var profile = profiles.GetValueOrDefault(provider.Name);
            return new
            {
                isSignedIn = OAuthTokenService.IsSignedIn(provider),
                isConfigured = provider.IsConfigured(),
                username = profile?.Username,
                displayName = profile?.DisplayName,
                avatarUrl = profile?.AvatarUrl,
            };
        }

        private static async Task<AccountProfile?> FetchProfileAsync(OAuthProvider provider)
        {
            var json = await GetJsonAsync(provider, "/me");
            if (json == null)
                return null;

            try
            {
                // VPZONE nests the payload under "data"; Gamefolio returns it at the root. Unwrap
                // when present so one parser covers both.
                var root = json.Value.TryGetProperty("data", out var data) ? data : json.Value;
                if (root.TryGetProperty("profile", out var nested))
                    root = nested;

                string? username = Text(root, "username");
                if (string.IsNullOrEmpty(username))
                    return null;

                return new AccountProfile(
                    username,
                    Text(root, "display_name") ?? Text(root, "displayName"),
                    Text(root, "avatar_url") ?? Text(root, "avatarUrl"));
            }
            catch (Exception ex)
            {
                Log.Error("Could not read the {Provider} profile: {Type}", provider.DisplayName, ex.GetType().Name);
                return null;
            }
        }

        private static async Task RefreshVpzPlusAsync()
        {
            var json = await GetJsonAsync(OAuthProviders.VPZone, "/me/vpz-plus");
            if (json == null)
            {
                // Leave the cached status alone: EntitlementService decides how long an unverified
                // membership stays trusted, so a flaky connection does not revoke a paid feature.
                Log.Debug("VPZ+ status could not be refreshed; keeping the cached value");
                return;
            }

            try
            {
                var root = json.Value.TryGetProperty("data", out var data) ? data : json.Value;

                var status = new VpzPlusStatus(
                    Active: root.TryGetProperty("active", out var active) && active.ValueKind == JsonValueKind.True,
                    Since: Date(root, "since"),
                    CurrentPeriodEnd: Date(root, "current_period_end"),
                    CancelAtPeriodEnd: root.TryGetProperty("cancel_at_period_end", out var cancel)
                        && cancel.ValueKind == JsonValueKind.True,
                    Source: Text(root, "source"),
                    CheckedAtUtc: DateTimeOffset.UtcNow);

                lock (_lock)
                {
                    _vpzPlus = status;
                }

                EntitlementService.Store(status);
                Log.Information("VPZ+ membership is {State}", status.Active ? "active" : "inactive");
            }
            catch (Exception ex)
            {
                Log.Error("Could not read VPZ+ status: {Type}", ex.GetType().Name);
            }
        }

        public static void Restore(VpzPlusStatus status)
        {
            lock (_lock)
            {
                _vpzPlus = status;
            }
        }

        private static async Task<JsonElement?> GetJsonAsync(OAuthProvider provider, string path)
        {
            try
            {
                using var request = await OAuthTokenService.CreateRequestAsync(provider, HttpMethod.Get, path);
                if (request == null)
                    return null;

                using var response = await OAuthTokenService.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    Log.Warning("{Provider} GET {Path} returned {Status}",
                        provider.DisplayName, path, (int)response.StatusCode);
                    return null;
                }

                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                return document.RootElement.Clone();
            }
            catch (Exception ex)
            {
                Log.Warning("{Provider} GET {Path} failed: {Type}", provider.DisplayName, path, ex.GetType().Name);
                return null;
            }
        }

        private static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static DateTimeOffset? Date(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), out var parsed)
                ? parsed
                : null;
    }
}
