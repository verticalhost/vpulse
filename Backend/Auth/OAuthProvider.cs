using VPULSE.Backend.Api;

namespace VPULSE.Backend.Auth
{
    /// <summary>
    /// Everything that differs between the OAuth services VPULSE talks to. Both use authorization
    /// code + PKCE, so the flow itself lives once in <see cref="OAuthLoginService"/>.
    /// </summary>
    internal sealed record OAuthProvider(
        string Name,
        string DisplayName,
        string AuthorizeUrl,
        string TokenUrl,
        string ApiBase,
        string ClientId,
        string Scopes,
        string CallbackPath,
        // RFC 8252 8.3 prefers the literal loopback address, but registration forms often accept
        // only "localhost", so this follows whatever the provider will register. ContentServer
        // binds both, so either resolves.
        string RedirectHost = "localhost")
    {
        /// <summary>Must byte-match between the authorize request and the token exchange.</summary>
        public string RedirectUri => $"http://{RedirectHost}:{ContentServer.Port}{CallbackPath}";

        public string[] ScopeList => Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    internal static class OAuthProviders
    {
        public const string VPZoneName = "vpzone";
        public const string GamefolioName = "gamefolio";

        // Registered at vpzone.tv/developers/oauth as a public client (OAuth 2.1 + PKCE, no secret).
        // Note the token endpoint sits under /api/oauth, not /api/v1 like the rest of the API.
        public static readonly OAuthProvider VPZone = Configure(new OAuthProvider(
            Name: VPZoneName,
            DisplayName: "VPZONE",
            AuthorizeUrl: "https://vpzone.tv/oauth/authorize",
            TokenUrl: "https://vpzone.tv/api/oauth/token",
            ApiBase: "https://vpzone.tv/api/v1",
            ClientId: "3321677d-71c3-4022-890f-b1e73597a409",
            // /me/vpz-plus reads under profile:read, so membership needs no scope of its own.
            Scopes: "profile:read",
            CallbackPath: "/auth/vpzone/callback"));

        // Registered at developer.gamefolio.com as the "VPULSE" app. The redirect URI is matched
        // exactly and the form only accepts localhost, so it cannot use the literal 127.0.0.1.
        public static readonly OAuthProvider Gamefolio = Configure(new OAuthProvider(
            Name: GamefolioName,
            DisplayName: "Gamefolio",
            AuthorizeUrl: "https://app.gamefolio.com/oauth/authorize",
            TokenUrl: "https://app.gamefolio.com/oauth/token",
            ApiBase: "https://app.gamefolio.com/api/public/v1",
            ClientId: "8a74b779-92ec-473a-95ba-c87af1964a46",
            Scopes: "profile:read clips:write",
            CallbackPath: "/auth/gamefolio/callback"));

        public static IEnumerable<OAuthProvider> All => [VPZone, Gamefolio];

        public static OAuthProvider? ByName(string? name) =>
            All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        public static OAuthProvider? ByCallbackPath(string path) =>
            All.FirstOrDefault(p => path.Equals(p.CallbackPath, StringComparison.OrdinalIgnoreCase));

        /// <summary>True once a client id is configured; the UI hides sign-in until then.</summary>
        public static bool IsConfigured(this OAuthProvider provider) =>
            !string.IsNullOrWhiteSpace(provider.ClientId);

        // Lets a local stub authorization server stand in for the real one during development,
        // so the flow can be exercised before either service is ready.
        private static OAuthProvider Configure(OAuthProvider provider)
        {
            string prefix = $"VPULSE_OAUTH_{provider.Name.ToUpperInvariant()}_";

            return provider with
            {
                AuthorizeUrl = Override(prefix + "AUTHORIZE") ?? provider.AuthorizeUrl,
                TokenUrl = Override(prefix + "TOKEN") ?? provider.TokenUrl,
                ApiBase = Override(prefix + "API") ?? provider.ApiBase,
                ClientId = Override(prefix + "CLIENT_ID") ?? provider.ClientId,
            };

            static string? Override(string variable)
            {
                string? value = Environment.GetEnvironmentVariable(variable);
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }
    }
}
