using Serilog;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;

namespace VPULSE.Backend.Recorder
{
    /// <summary>
    /// Talks to the obs-websocket 5 server built into OBS Studio, so VPULSE can drive the user's
    /// own OBS instead of running a second capture and encoder alongside it.
    /// </summary>
    internal sealed class ObsWebSocketClient : IDisposable
    {
        private ClientWebSocket? _ws;
        private CancellationTokenSource? _pump;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();

        public bool IsConnected => _ws?.State == WebSocketState.Open;

        /// <summary>Raised with the saved file path each time OBS writes a replay.</summary>
        public event Action<string>? ReplaySaved;

        /// <summary>
        /// obs-websocket stores its port and password in OBS's own config, so the user never has to
        /// copy a password across. Returns null when OBS has never written that config.
        /// </summary>
        public static (int Port, string Password, bool ServerEnabled)? ReadObsConfig()
        {
            try
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "obs-studio", "plugin_config", "obs-websocket", "config.json");

                if (!File.Exists(path))
                    return null;

                var cfg = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
                return (
                    cfg.TryGetProperty("server_port", out var p) ? p.GetInt32() : 4455,
                    cfg.TryGetProperty("server_password", out var w) ? w.GetString() ?? "" : "",
                    cfg.TryGetProperty("server_enabled", out var e) && e.GetBoolean());
            }
            catch (Exception ex)
            {
                Log.Warning("Could not read the obs-websocket config: {Type}", ex.GetType().Name);
                return null;
            }
        }

        public async Task<bool> ConnectAsync()
        {
            var cfg = ReadObsConfig();
            if (cfg == null)
                return false;

            try
            {
                _ws = new ClientWebSocket();
                await _ws.ConnectAsync(new Uri($"ws://127.0.0.1:{cfg.Value.Port}"), CancellationToken.None);

                var hello = await ReceiveAsync();
                var d = hello.GetProperty("d");

                object identify;
                if (d.TryGetProperty("authentication", out var auth))
                {
                    // RPC 5: base64(sha256(base64(sha256(password + salt)) + challenge)).
                    string secret = Convert.ToBase64String(SHA256.HashData(
                        Encoding.UTF8.GetBytes(cfg.Value.Password + auth.GetProperty("salt").GetString())));
                    string response = Convert.ToBase64String(SHA256.HashData(
                        Encoding.UTF8.GetBytes(secret + auth.GetProperty("challenge").GetString())));
                    identify = new { op = 1, d = new { rpcVersion = 1, authentication = response, eventSubscriptions = OutputEvents } };
                }
                else
                {
                    identify = new { op = 1, d = new { rpcVersion = 1, eventSubscriptions = OutputEvents } };
                }

                await SendAsync(identify);
                var identified = await ReceiveAsync();
                if (identified.GetProperty("op").GetInt32() != 2)
                {
                    Log.Warning("OBS rejected the websocket handshake");
                    Dispose();
                    return false;
                }

                _pump = new CancellationTokenSource();
                _ = Task.Run(() => PumpAsync(_pump.Token));

                Log.Information("Connected to OBS ({Version})",
                    d.TryGetProperty("obsWebSocketVersion", out var v) ? v.GetString() : "unknown");
                return true;
            }
            catch (Exception ex)
            {
                // Not being able to reach OBS is normal, so this stays quiet.
                Log.Debug("Could not connect to obs-websocket: {Type}", ex.GetType().Name);
                Dispose();
                return false;
            }
        }

        // Outputs (bit 6) carries ReplayBufferSaved, which is the only event we need.
        private const int OutputEvents = 1 << 6;

        /// <summary>Returns null when the request failed; the caller decides how loud to be.</summary>
        public async Task<JsonElement?> RequestAsync(string requestType, object? data = null, int timeoutSeconds = 15)
        {
            if (!IsConnected)
                return null;

            string id = Guid.NewGuid().ToString("N")[..8];
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;

            try
            {
                await SendAsync(new { op = 6, d = new { requestType, requestId = id, requestData = data ?? new { } } });

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                var completed = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, timeout.Token));
                if (completed != tcs.Task)
                {
                    Log.Warning("OBS request {Request} timed out", requestType);
                    return null;
                }

                var response = tcs.Task.Result;
                var status = response.GetProperty("requestStatus");
                if (!status.GetProperty("result").GetBoolean())
                {
                    Log.Information("OBS refused {Request}: {Code} {Comment}", requestType,
                        status.GetProperty("code").GetInt32(),
                        status.TryGetProperty("comment", out var c) ? c.GetString() : "");
                    return null;
                }

                return response.TryGetProperty("responseData", out var rd) ? rd : default(JsonElement);
            }
            catch (Exception ex)
            {
                Log.Warning("OBS request {Request} failed: {Type}", requestType, ex.GetType().Name);
                return null;
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }
        }

        private async Task PumpAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested && IsConnected)
                {
                    var message = await ReceiveAsync();
                    int op = message.GetProperty("op").GetInt32();
                    var d = message.GetProperty("d");

                    if (op == 7 && d.TryGetProperty("requestId", out var id)
                        && _pending.TryGetValue(id.GetString()!, out var tcs))
                    {
                        tcs.TrySetResult(d);
                    }
                    else if (op == 5 && d.GetProperty("eventType").GetString() == "ReplayBufferSaved")
                    {
                        string? path = d.GetProperty("eventData").GetProperty("savedReplayPath").GetString();
                        if (!string.IsNullOrEmpty(path))
                            ReplaySaved?.Invoke(path);
                    }
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                Log.Information("OBS websocket closed");
            }
            catch (Exception ex)
            {
                Log.Warning("OBS websocket pump stopped: {Type}", ex.GetType().Name);
            }
        }

        private async Task<JsonElement> ReceiveAsync()
        {
            var buffer = new byte[64 * 1024];
            var text = new StringBuilder();
            WebSocketReceiveResult result;
            do
            {
                result = await _ws!.ReceiveAsync(buffer, CancellationToken.None);
                text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            } while (!result.EndOfMessage);

            return JsonDocument.Parse(text.ToString()).RootElement.Clone();
        }

        private Task SendAsync(object payload) =>
            _ws!.SendAsync(JsonSerializer.SerializeToUtf8Bytes(payload), WebSocketMessageType.Text, true, CancellationToken.None);

        public void Dispose()
        {
            _pump?.Cancel();
            _pump?.Dispose();
            _pump = null;
            try { _ws?.Dispose(); } catch { }
            _ws = null;
        }
    }
}
