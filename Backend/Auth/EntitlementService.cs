using Serilog;
using System.Text.Json;
using VPULSE.Backend.Core.Models;

namespace VPULSE.Backend.Auth
{
    /// <summary>
    /// Decides how long a VPZ+ membership stays trusted when it cannot be re-checked.
    /// </summary>
    /// <remarks>
    /// Deliberately fails soft. The local gates are an honour system anyway (see
    /// <see cref="FeatureGate"/>), so revoking a paying member's features because their wifi
    /// dropped would only punish the people who paid, while doing nothing to anyone determined to
    /// bypass the check.
    /// </remarks>
    internal static class EntitlementService
    {
        /// <summary>How long a cached membership is used without question.</summary>
        private static readonly TimeSpan FreshFor = TimeSpan.FromHours(6);

        /// <summary>How long past that a membership still applies while the API is unreachable.</summary>
        private static readonly TimeSpan GracePeriod = TimeSpan.FromDays(7);

        private const string CacheProviderKey = "vpzplus-cache";

        public static bool IsActive => Evaluate().Active;

        /// <summary>True when the cached membership is being used past its refresh window.</summary>
        public static bool IsStale => Evaluate() is { Active: true, Stale: true };

        private static (bool Active, bool Stale) Evaluate()
        {
            var status = AuthStateService.VpzPlus;
            if (!status.Active)
                return (false, false);

            // Airplane mode is an explicit choice to make no network calls, so it must not quietly
            // downgrade the user for failing to phone home.
            if (Settings.Instance.AirplaneMode)
                return (true, false);

            TimeSpan age = DateTimeOffset.UtcNow - status.CheckedAtUtc;
            if (age <= FreshFor)
                return (true, false);

            // Never trust a cached membership past the period it was sold for.
            if (status.CurrentPeriodEnd is { } end && DateTimeOffset.UtcNow > end)
                return (false, false);

            if (age <= FreshFor + GracePeriod)
                return (true, true);

            return (false, false);
        }

        /// <summary>Kept in the encrypted credentials file, not settings.json, so it is not a text field to edit.</summary>
        public static void Store(VpzPlusStatus status)
        {
            TokenStore.Set(CacheProviderKey, new ProviderTokens
            {
                AccessToken = JsonSerializer.Serialize(status),
                ExpiresAtUtc = status.CurrentPeriodEnd ?? DateTimeOffset.UtcNow.Add(FreshFor + GracePeriod),
            });
        }

        public static void RestoreFromCache()
        {
            var cached = TokenStore.Get(CacheProviderKey);
            if (!cached.HasCredentials)
                return;

            try
            {
                var status = JsonSerializer.Deserialize<VpzPlusStatus>(cached.AccessToken);
                if (status != null)
                {
                    AuthStateService.Restore(status);
                    Log.Information("Restored cached VPZ+ status (active: {Active}, checked {Checked:u})",
                        status.Active, status.CheckedAtUtc);
                }
            }
            catch (JsonException)
            {
                TokenStore.Clear(CacheProviderKey);
            }
        }

        public static void Clear() => TokenStore.Clear(CacheProviderKey);
    }
}
