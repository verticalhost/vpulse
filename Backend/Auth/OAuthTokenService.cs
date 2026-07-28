using Serilog;
using System.Net.Http.Headers;
using System.Text.Json;

namespace VPULSE.Backend.Auth
{
    /// <summary>
    /// Exchanges authorization codes for tokens, keeps them fresh, and hands out access tokens.
    /// Tokens stay in this process: nothing here returns one to the frontend.
    /// </summary>
    internal static class OAuthTokenService
    {
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

        // One gate per provider so a VPZONE refresh cannot block a Gamefolio upload.
        private static readonly Dictionary<string, SemaphoreSlim> _refreshGates =
            OAuthProviders.All.ToDictionary(p => p.Name, _ => new SemaphoreSlim(1, 1));

        public static bool IsSignedIn(OAuthProvider provider) => TokenStore.Get(provider.Name).HasCredentials;

        public static async Task<bool> ExchangeCodeAsync(OAuthProvider provider, string code, string verifier)
        {
            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                // Must byte-match the value sent to the authorize endpoint.
                ["redirect_uri"] = provider.RedirectUri,
                ["client_id"] = provider.ClientId,
                // No client_secret: this is a public client, and PKCE is what proves the exchange
                // comes from the process that started the sign-in.
                ["code_verifier"] = verifier,
            };

            return await PostTokenRequestAsync(provider, form, "code exchange");
        }

        /// <summary>Returns null when there is no usable token, so callers can prompt a sign-in.</summary>
        public static async Task<string?> GetAccessTokenAsync(OAuthProvider provider)
        {
            var tokens = TokenStore.Get(provider.Name);
            if (!tokens.HasCredentials)
                return null;

            if (!tokens.IsExpired)
                return tokens.AccessToken;

            var gate = _refreshGates[provider.Name];
            await gate.WaitAsync();
            try
            {
                // Another caller may have refreshed while we waited.
                tokens = TokenStore.Get(provider.Name);
                if (!tokens.IsExpired)
                    return tokens.AccessToken;

                if (string.IsNullOrEmpty(tokens.RefreshToken))
                {
                    SignOut(provider);
                    return null;
                }

                var form = new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = tokens.RefreshToken,
                    ["client_id"] = provider.ClientId,
                };

                if (!await PostTokenRequestAsync(provider, form, "token refresh"))
                    return null;

                return TokenStore.Get(provider.Name).AccessToken;
            }
            finally
            {
                gate.Release();
            }
        }

        public static void SignOut(OAuthProvider provider)
        {
            TokenStore.Clear(provider.Name);
            Log.Information("Signed out of {Provider}", provider.DisplayName);
        }

        private static async Task<bool> PostTokenRequestAsync(
            OAuthProvider provider, Dictionary<string, string> form, string operation)
        {
            try
            {
                using var content = new FormUrlEncodedContent(form);
                using var response = await _http.PostAsync(provider.TokenUrl, content);
                string body = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    // Status and the error code only. The body can echo the code or the verifier,
                    // and a proxy may wrap the whole request into the message.
                    Log.Error("{Provider} {Operation} failed ({Status}: {Error})",
                        provider.DisplayName, operation, (int)response.StatusCode, ReadErrorCode(body));

                    // invalid_grant means the refresh token is dead; retrying forever would just
                    // produce a loop of 401s, so drop the session and let the user sign in again.
                    if (form["grant_type"] == "refresh_token" && ReadErrorCode(body) == "invalid_grant")
                        SignOut(provider);

                    return false;
                }

                return Store(provider, body);
            }
            catch (Exception ex)
            {
                Log.Error("{Provider} {Operation} failed: {Type}", provider.DisplayName, operation, ex.GetType().Name);
                return false;
            }
        }

        private static bool Store(OAuthProvider provider, string body)
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;

                string? accessToken = root.TryGetProperty("access_token", out var at) ? at.GetString() : null;
                if (string.IsNullOrEmpty(accessToken))
                {
                    Log.Error("{Provider} returned no access token", provider.DisplayName);
                    return false;
                }

                var existing = TokenStore.Get(provider.Name);

                // Keep the current refresh token when the response omits one: providers that do not
                // rotate simply leave it out, and discarding it would sign the user out for good.
                string refreshToken = root.TryGetProperty("refresh_token", out var rt) && rt.GetString() is { Length: > 0 } value
                    ? value
                    : existing.RefreshToken;

                // Track expiry from expires_in rather than by decoding the token: it works for
                // opaque tokens and does not rely on an unverified JWT payload.
                int expiresIn = root.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out int seconds)
                    ? seconds
                    : 3600;

                string[] scopes = root.TryGetProperty("scope", out var sc) && sc.GetString() is { } granted
                    ? granted.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    : provider.ScopeList;

                TokenStore.Set(provider.Name, new ProviderTokens
                {
                    AccessToken = accessToken,
                    RefreshToken = refreshToken,
                    ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(expiresIn),
                    Scopes = scopes,
                });

                string[] missing = provider.ScopeList.Except(scopes, StringComparer.Ordinal).ToArray();
                if (missing.Length > 0)
                    Log.Warning("{Provider} granted fewer scopes than requested; missing {Missing}",
                        provider.DisplayName, string.Join(", ", missing));

                Log.Information("{Provider} tokens stored, valid for {Seconds}s", provider.DisplayName, expiresIn);
                return true;
            }
            catch (JsonException)
            {
                Log.Error("{Provider} returned a token response that could not be parsed", provider.DisplayName);
                return false;
            }
        }

        private static string ReadErrorCode(string body)
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                return document.RootElement.TryGetProperty("error", out var error)
                    ? error.GetString() ?? "unknown"
                    : "unknown";
            }
            catch (JsonException)
            {
                return "unknown";
            }
        }

        /// <summary>Builds an authenticated request, or returns null when the user is signed out.</summary>
        public static async Task<HttpRequestMessage?> CreateRequestAsync(
            OAuthProvider provider, HttpMethod method, string relativePath)
        {
            string? accessToken = await GetAccessTokenAsync(provider);
            if (accessToken == null)
                return null;

            var request = new HttpRequestMessage(method, $"{provider.ApiBase}{relativePath}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            return request;
        }

        public static Task<HttpResponseMessage> SendAsync(HttpRequestMessage request) => _http.SendAsync(request);

        public static Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption option) =>
            _http.SendAsync(request, option);
    }
}
