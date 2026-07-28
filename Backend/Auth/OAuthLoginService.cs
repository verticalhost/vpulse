using Serilog;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using VPULSE.Backend.App;
using VPULSE.Backend.Core.Models;
using VPULSE.Backend.Platform;

namespace VPULSE.Backend.Auth
{
    /// <summary>
    /// Authorization code + PKCE (RFC 8252), shared by every provider.
    /// </summary>
    /// <remarks>
    /// Only the authorization code travels through the browser, and it is useless without the
    /// verifier, which never leaves this process. The tokens are fetched by a direct HTTPS call and
    /// are never handed to the frontend. That is the difference from the Discord flow this replaces,
    /// where the tokens themselves arrived as query parameters and passed through the renderer.
    /// </remarks>
    internal static class OAuthLoginService
    {
        private static readonly TimeSpan PendingTimeout = TimeSpan.FromMinutes(5);

        private sealed record Pending(string State, string Verifier, CancellationTokenSource Timeout);

        private static readonly Lock _lock = new();
        private static readonly Dictionary<string, Pending> _pending = [];

        public static void Begin(OAuthProvider provider)
        {
            if (Settings.Instance.AirplaneMode)
            {
                Log.Information("{Provider} sign-in refused: airplane mode is on", provider.DisplayName);
                _ = SendResult(provider, "unavailable");
                return;
            }

            if (!provider.IsConfigured())
            {
                Log.Error("{Provider} sign-in refused: no client id is configured", provider.DisplayName);
                _ = SendResult(provider, "unavailable");
                return;
            }

            var pkce = Pkce.Create();
            string state = Pkce.NewState();

            lock (_lock)
            {
                if (_pending.TryGetValue(provider.Name, out var previous))
                    previous.Timeout.Cancel();

                var timeout = new CancellationTokenSource();
                _pending[provider.Name] = new Pending(state, pkce.Verifier, timeout);
                _ = ExpirePendingAsync(provider, state, timeout.Token);
            }

            var query = new Dictionary<string, string>
            {
                ["response_type"] = "code",
                ["client_id"] = provider.ClientId,
                ["redirect_uri"] = provider.RedirectUri,
                ["scope"] = provider.Scopes,
                ["state"] = state,
                ["code_challenge"] = pkce.Challenge,
                ["code_challenge_method"] = PkcePair.Method,
            };

            string url = provider.AuthorizeUrl + "?" + string.Join("&",
                query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

            // Never log the URL: it carries the state and the challenge.
            Log.Information("Opening {Provider} sign-in in the default browser", provider.DisplayName);
            PlatformServices.Dialogs.OpenUrl(url);
        }

        public static void Cancel(OAuthProvider provider)
        {
            lock (_lock)
            {
                if (!_pending.Remove(provider.Name, out var pending))
                    return;

                pending.Timeout.Cancel();
            }

            Log.Information("{Provider} sign-in cancelled from the app", provider.DisplayName);
        }

        public static async Task HandleCallbackAsync(OAuthProvider provider, HttpListenerContext context)
        {
            var query = HttpUtility.ParseQueryString(context.Request.Url?.Query ?? string.Empty);
            string? state = query["state"];
            string? code = query["code"];
            string? error = query["error"];

            Pending? pending = null;
            lock (_lock)
            {
                if (_pending.TryGetValue(provider.Name, out var candidate) && Matches(candidate.State, state))
                {
                    pending = candidate;
                    _pending.Remove(provider.Name);
                    candidate.Timeout.Cancel();
                }
            }

            if (pending is null)
            {
                // Say the same thing whether the state was wrong, replayed, or absent: this listener
                // is reachable by anything on the machine and should not confirm guesses.
                Log.Warning("{Provider} callback rejected: no matching pending sign-in", provider.DisplayName);
                await RespondAsync(context, "Sign-in link expired",
                    $"Head back to VPULSE and start the {provider.DisplayName} sign-in again.");
                return;
            }

            if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
            {
                // Log the error code, never error_description: it is attacker-influenceable text.
                Log.Information("{Provider} sign-in returned {Error}", provider.DisplayName, error ?? "no code");
                await SendResult(provider, "cancelled");
                await RespondAsync(context, "Sign-in cancelled", "Nothing was changed. You can close this tab.");
                return;
            }

            // Answer the browser before exchanging: the tab must not sit on a network round-trip,
            // and the page never depends on anything the exchange returns.
            await RespondAsync(context, "You're signed in",
                "You can close this tab and return to VPULSE.");
            Program.BringWindowToFront();

            bool exchanged = await OAuthTokenService.ExchangeCodeAsync(provider, code, pending.Verifier);
            await SendResult(provider, exchanged ? "success" : "failed");

            if (exchanged)
                await AuthStateService.RefreshAsync(provider);
        }

        private static bool Matches(string expected, string? actual)
        {
            if (string.IsNullOrEmpty(actual))
                return false;

            return CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(actual));
        }

        private static async Task ExpirePendingAsync(OAuthProvider provider, string state, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(PendingTimeout, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            bool expired = false;
            lock (_lock)
            {
                if (_pending.TryGetValue(provider.Name, out var pending) && pending.State == state)
                {
                    _pending.Remove(provider.Name);
                    expired = true;
                }
            }

            if (expired)
            {
                Log.Information("{Provider} sign-in timed out waiting for the browser", provider.DisplayName);
                await SendResult(provider, "expired");
            }
        }

        private static Task SendResult(OAuthProvider provider, string status) =>
            MessageService.SendFrontendMessage("OAuthLoginResult", new { provider = provider.Name, status });

        // No auto-close: browsers refuse close() for tabs a script did not open.
        private static async Task RespondAsync(HttpListenerContext context, string title, string message)
        {
            string safeTitle = WebUtility.HtmlEncode(title);
            string safeMessage = WebUtility.HtmlEncode(message);

            string html = $$"""
                <!doctype html>
                <html lang="en">
                <head>
                  <meta charset="utf-8">
                  <meta name="viewport" content="width=device-width, initial-scale=1">
                  <title>{{safeTitle}} - VPULSE</title>
                  <style>
                    html, body { height: 100%; margin: 0; }
                    body {
                      display: flex; align-items: center; justify-content: center;
                      background: #17191c; color: #e9eaec;
                      font-family: system-ui, -apple-system, "Segoe UI", sans-serif;
                      text-align: center; padding: 24px;
                    }
                    h1 { font-size: 1.5rem; margin: 0 0 12px; }
                    p { margin: 0; color: #a5a9b0; line-height: 1.5; }
                  </style>
                </head>
                <body>
                  <main>
                    <h1>{{safeTitle}}</h1>
                    <p>{{safeMessage}}</p>
                  </main>
                </body>
                </html>
                """;

            byte[] buffer = Encoding.UTF8.GetBytes(html);
            var response = context.Response;
            response.StatusCode = (int)HttpStatusCode.OK;
            response.ContentType = "text/html; charset=utf-8";
            // The request URL holds the authorization code; keep it out of caches and referrers.
            response.Headers["Cache-Control"] = "no-store";
            response.Headers["Referrer-Policy"] = "no-referrer";
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer);
        }
    }
}
