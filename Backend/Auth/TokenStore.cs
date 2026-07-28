using Serilog;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VPULSE.Backend.Platform;
using VPULSE.Backend.Shared;

namespace VPULSE.Backend.Auth
{
    /// <summary>OAuth tokens for one provider. Never serialized anywhere but credentials.dat.</summary>
    internal sealed class ProviderTokens
    {
        [JsonPropertyName("accessToken")]
        public string AccessToken { get; set; } = string.Empty;

        [JsonPropertyName("refreshToken")]
        public string RefreshToken { get; set; } = string.Empty;

        /// <summary>
        /// Derived from the token response's expires_in rather than by decoding the token, so
        /// this works for opaque tokens and does not depend on an unverified JWT payload.
        /// </summary>
        [JsonPropertyName("expiresAtUtc")]
        public DateTimeOffset ExpiresAtUtc { get; set; }

        [JsonPropertyName("scopes")]
        public string[] Scopes { get; set; } = [];

        [JsonIgnore]
        public bool HasCredentials => !string.IsNullOrEmpty(AccessToken);

        // 60s of slack so a token cannot expire between the check and the request landing.
        [JsonIgnore]
        public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAtUtc.AddSeconds(-60);

        public bool HasScope(string scope) => Scopes.Contains(scope, StringComparer.Ordinal);
    }

    /// <summary>
    /// Credentials live here and not in <c>Settings</c> on purpose: the whole Settings object is
    /// broadcast to the frontend over an unauthenticated local WebSocket, so anything stored there
    /// is readable by any process on the machine. Keeping tokens out of that object makes the
    /// broadcast safe by construction rather than by remembering to redact.
    /// </summary>
    internal static class TokenStore
    {
        private static readonly string FilePath = PathUtils.Normalize(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VPULSE", "credentials.dat"));

        private static readonly Lock _lock = new();
        private static Dictionary<string, ProviderTokens> _tokens = [];
        private static bool _loaded;

        public static ProviderTokens Get(string provider)
        {
            EnsureLoaded();
            lock (_lock)
            {
                return _tokens.TryGetValue(provider, out var tokens) ? tokens : new ProviderTokens();
            }
        }

        public static void Set(string provider, ProviderTokens tokens)
        {
            EnsureLoaded();
            lock (_lock)
            {
                _tokens[provider] = tokens;
            }
            Persist();
        }

        public static void Clear(string provider)
        {
            EnsureLoaded();
            lock (_lock)
            {
                _tokens.Remove(provider);
            }
            Persist();
        }

        private static void EnsureLoaded()
        {
            lock (_lock)
            {
                if (_loaded)
                    return;

                _loaded = true;

                try
                {
                    if (!File.Exists(FilePath))
                        return;

                    byte[]? plaintext = PlatformServices.Secrets.Unprotect(File.ReadAllBytes(FilePath));
                    if (plaintext == null)
                    {
                        // Another user's blob, another machine, or corruption. Treat as signed out.
                        Log.Warning("Stored credentials could not be decrypted; signing out");
                        return;
                    }

                    _tokens = JsonSerializer.Deserialize<Dictionary<string, ProviderTokens>>(plaintext) ?? [];
                }
                catch (Exception ex)
                {
                    // Type only: the message can embed the path, and the path embeds the username.
                    Log.Error("Could not read stored credentials: {Type}", ex.GetType().Name);
                    _tokens = [];
                }
            }
        }

        private static void Persist()
        {
            try
            {
                Dictionary<string, ProviderTokens> snapshot;
                lock (_lock)
                {
                    snapshot = new Dictionary<string, ProviderTokens>(_tokens);
                }

                if (snapshot.Count == 0)
                {
                    if (File.Exists(FilePath))
                        File.Delete(FilePath);
                    return;
                }

                byte[] plaintext = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(snapshot));
                byte[]? ciphertext = PlatformServices.Secrets.Protect(plaintext);
                if (ciphertext == null)
                {
                    Log.Error("Credentials were not saved: this platform could not protect them");
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

                // Write then move, so a crash mid-write cannot leave a truncated file that reads
                // as "signed out" and silently drops a working refresh token.
                string temp = FilePath + ".tmp";
                File.WriteAllBytes(temp, ciphertext);
                RestrictToCurrentUser(temp);
                File.Move(temp, FilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Error("Could not save credentials: {Type}", ex.GetType().Name);
            }
        }

        private static void RestrictToCurrentUser(string path)
        {
#if !WINDOWS
            // Without a key store the file mode is the only protection, so it has to be set
            // before the file is moved into place. On Windows DPAPI already covers this.
            try
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (Exception ex)
            {
                Log.Warning("Could not restrict credential file permissions: {Type}", ex.GetType().Name);
            }
#endif
        }
    }
}
