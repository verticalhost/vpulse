using Serilog;
using System.Net;
using System.Net.Sockets;
using System.Web;
using VPULSE.Backend.Auth;
using VPULSE.Backend.Media;
using VPULSE.Backend.Shared;
using VPULSE.Backend.Core.Models;

namespace VPULSE.Backend.Api
{
    internal class ContentServer
    {
        // Own port, for the same reason as MessageService.WebSocketPort: sharing Segra's 2222 means
        // whichever app starts first wins, and the loser's frontend asks the wrong server for its
        // files and gets a 404 (video and thumbnails render black).
        // Tried in order. Anything past the first is for machines where something already holds
        // 2322; every one of these is registered as a redirect URI with the OAuth providers, so a
        // fallback does not break sign-in. Adding one here means registering it there too.
        internal static readonly int[] CandidatePorts = [2322, 2323, 2324, 2325];

        private static int _activePort = CandidatePorts[0];

        /// <summary>The port actually bound. Only meaningful after <see cref="StartServer"/>.</summary>
        internal static int Port => _activePort;

        internal static string Prefix => $"http://localhost:{_activePort}/";

        // Replaced per bind attempt: a listener whose Start() threw cannot be reused.
        private static HttpListener _httpListener = new();
        private static CancellationTokenSource? _cancellationTokenSource;

        public static void StartServer()
        {
            foreach (int port in CandidatePorts)
            {
                // A fresh listener each time: reusing one whose Start() failed throws on the next
                // attempt, which turned the fallback into a hard failure on the first busy port.
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://localhost:{port}/");

                // HttpListener routes on the Host header, so a "localhost" prefix answers 400 to a
                // request addressed to 127.0.0.1. OAuth redirect URIs may use either form, so both
                // have to be bound or the callback fails with no visible error. Neither needs
                // elevation.
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");

                try
                {
                    listener.Start();
                }
                catch (Exception ex) when (ex is HttpListenerException or SocketException)
                {
                    Log.Warning("Content server port {Port} is in use; trying the next one", port);
                    listener.Close();
                    continue;
                }

                _httpListener = listener;
                _activePort = port;
                break;
            }

            if (!_httpListener.IsListening)
            {
                // Media playback and OAuth callbacks both ride this listener, so there is no
                // degraded mode worth pretending to offer.
                Log.Error("Content server could not bind any of {Ports}; video playback and sign-in will not work",
                    string.Join(", ", CandidatePorts));
                return;
            }

            Log.Information("Server started at {Prefix}", Prefix);

            _cancellationTokenSource = new();
            _ = Task.Run(() => AcceptRequestsAsync(_cancellationTokenSource.Token));
        }

        public static void StopServer()
        {
            try
            {
                _cancellationTokenSource?.Cancel();
                _httpListener.Stop();
                _httpListener.Close();
                Log.Information("ContentServer stopped");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error stopping ContentServer");
            }
            finally
            {
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
            }
        }

        private static async Task AcceptRequestsAsync(CancellationToken cancellationToken)
        {
            Log.Information("ContentServer now accepting requests");

            while (!cancellationToken.IsCancellationRequested && _httpListener.IsListening)
            {
                try
                {
                    var context = await _httpListener.GetContextAsync();
                    _ = ProcessRequestAsync(context);
                }
                catch (HttpListenerException ex) when (ex.ErrorCode == 995)
                {
                    Log.Information("ContentServer listener stopped");
                    break;
                }
                catch (ObjectDisposedException)
                {
                    Log.Information("ContentServer listener disposed");
                    break;
                }

                catch (Exception ex)
                {
                    Log.Error(ex, "Error accepting request");
                }
            }

            Log.Information("ContentServer stopped accepting requests");
        }

        private static async Task ProcessRequestAsync(HttpListenerContext context)
        {
            var response = context.Response;

            try
            {
                var rawUrl = context.Request.RawUrl ?? "";
                var path = context.Request.Url?.AbsolutePath ?? "";

                if (rawUrl.StartsWith("/api/thumbnail"))
                {
                    await HandleThumbnailRequest(context);
                }
                else if (rawUrl.StartsWith("/api/content"))
                {
                    await HandleContentRequest(context);
                }
                else if (OAuthProviders.ByCallbackPath(path) is { } provider)
                {
                    // The listener binds to localhost, so this should hold already; assert it anyway
                    // because everything downstream trusts that the code came from a local browser.
                    if (context.Request.RemoteEndPoint?.Address is not { } address || !IPAddress.IsLoopback(address))
                    {
                        Log.Warning("Rejected a non-loopback request to an OAuth callback");
                        response.StatusCode = (int)HttpStatusCode.Forbidden;
                        response.Close();
                        return;
                    }

                    await OAuthLoginService.HandleCallbackAsync(provider, context);
                }
                else if (DiscordLoginService.IsCallbackPath(path))
                {
                    await DiscordLoginService.HandleCallbackAsync(context);
                }
                else
                {
                    response.StatusCode = (int)HttpStatusCode.NotFound;
                    response.ContentType = "text/plain";
                    using (var writer = new StreamWriter(response.OutputStream))
                    {
                        await writer.WriteAsync("Invalid endpoint.");
                    }
                }
            }
            catch (HttpListenerException)
            {
            }
            catch (Exception ex)
            {
                // Path only: auth callback query strings carry session tokens.
                Log.Error(ex, "Error processing request for {Path}", context.Request.Url?.AbsolutePath);
                try
                {
                    if (!response.OutputStream.CanWrite)
                        return;

                    response.StatusCode = (int)HttpStatusCode.InternalServerError;
                    response.ContentType = "text/plain";
                    using (var writer = new StreamWriter(response.OutputStream))
                    {
                        await writer.WriteAsync("Internal server error");
                    }
                }
                catch
                {
                }
            }
            finally
            {
                try
                {
                    response.Close();
                }
                catch
                {
                }
            }
        }

        private static async Task HandleThumbnailRequest(HttpListenerContext context)
        {
            var query = HttpUtility.ParseQueryString(context.Request?.Url?.Query ?? "");
            string rawInput = query["input"] ?? "";
            string timeParam = query["time"] ?? "";
            var response = context.Response;

            response.AddHeader("Access-Control-Allow-Origin", "*");

            string? input = ValidateUserPath(rawInput);
            if (input == null || !File.Exists(input))
            {
                Log.Warning("Thumbnail request file not found or invalid: {Input}", rawInput);
                response.StatusCode = (int)HttpStatusCode.NotFound;
                response.ContentType = "text/plain";
                using (var writer = new StreamWriter(response.OutputStream))
                {
                    await writer.WriteAsync("File not found.");
                }
                return;
            }

            if (string.IsNullOrEmpty(timeParam))
            {
                response.ContentType = "image/jpeg";
                response.AddHeader("Cache-Control", "public, max-age=86400");
                response.AddHeader("Expires", DateTime.UtcNow.AddDays(7).ToString("R"));

                try
                {
                    var lastModified = File.GetLastWriteTimeUtc(input);
                    response.AddHeader("Last-Modified", lastModified.ToString("R"));
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Could not get last modified time for {Input}", input);
                }

                using (var fs = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, useAsync: true))
                {
                    response.ContentLength64 = fs.Length;
                    await fs.CopyToAsync(response.OutputStream);
                }
            }
            else
            {
                if (!double.TryParse(timeParam, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out double timeSeconds))
                {
                    Log.Warning("Could not parse timeParam={TimeParam}, using 0.0", timeParam);
                    timeSeconds = 0.0;
                }

                if (!FFmpegService.FFmpegExists())
                {
                    Log.Error("FFmpeg executable not found");
                    response.StatusCode = (int)HttpStatusCode.InternalServerError;
                    response.ContentType = "text/plain";
                    using (var writer = new StreamWriter(response.OutputStream))
                    {
                        await writer.WriteAsync("FFmpeg not found on server.");
                    }
                    return;
                }

                byte[] jpegBytes = await FFmpegService.GenerateThumbnail(input, timeSeconds);

                if (jpegBytes != null && jpegBytes.Length > 0)
                {
                    response.ContentType = "image/jpeg";
                    response.AddHeader("Cache-Control", "no-cache, no-store, must-revalidate");
                    response.AddHeader("Pragma", "no-cache");
                    response.AddHeader("Expires", "0");
                    response.ContentLength64 = jpegBytes.Length;
                    await response.OutputStream.WriteAsync(jpegBytes, 0, jpegBytes.Length);
                }
                else
                {
                    Log.Error("No thumbnail data received from FFmpeg");
                    response.StatusCode = (int)HttpStatusCode.InternalServerError;
                    response.ContentType = "text/plain";
                    using (var writer = new StreamWriter(response.OutputStream))
                    {
                        await writer.WriteAsync("Failed to generate thumbnail.");
                    }
                }
            }
        }

        private static async Task HandleContentRequest(HttpListenerContext context)
        {
            var query = HttpUtility.ParseQueryString(context.Request?.Url?.Query ?? "");
            string rawInput = query["input"] ?? "";
            var response = context.Response;

            response.AddHeader("Access-Control-Allow-Origin", "*");

            string? fileName = ValidateUserPath(rawInput);
            if (fileName == null || !File.Exists(fileName))
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                response.ContentType = "text/plain";
                using (var writer = new StreamWriter(response.OutputStream))
                {
                    await writer.WriteAsync("File not found.");
                }
                return;
            }

            if (fileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            {
                await StreamVideoFile(fileName, context);
            }
            else if (fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                await StreamJsonFile(fileName, response);
            }
            else
            {
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                response.ContentType = "text/plain";
                using (var writer = new StreamWriter(response.OutputStream))
                {
                    await writer.WriteAsync("Unsupported file type.");
                }
            }
        }

        private static async Task StreamVideoFile(string fileName, HttpListenerContext context)
        {
            var response = context.Response;

            string rangeHeader = context.Request.Headers["Range"] ?? "";
            long start = 0;
            long end;

            using (var fs = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 262144,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                long fileLength = fs.Length;
                end = fileLength - 1;

                if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes="))
                {
                    string[] rangeParts = rangeHeader.Substring(6).Split('-');
                    if (rangeParts.Length > 0 && !string.IsNullOrEmpty(rangeParts[0]))
                    {
                        long.TryParse(rangeParts[0], out start);
                    }
                    if (rangeParts.Length > 1 && !string.IsNullOrEmpty(rangeParts[1]))
                    {
                        long.TryParse(rangeParts[1], out end);
                    }
                }

                if (start > end || start < 0 || end >= fileLength)
                {
                    response.StatusCode = (int)HttpStatusCode.RequestedRangeNotSatisfiable;
                    response.AddHeader("Content-Range", $"bytes */{fileLength}");
                    return;
                }

                long contentLength = end - start + 1;

                response.StatusCode = string.IsNullOrEmpty(rangeHeader) ? (int)HttpStatusCode.OK : (int)HttpStatusCode.PartialContent;
                response.ContentType = "video/mp4";
                response.AddHeader("Accept-Ranges", "bytes");
                // Content-Range is not on the CORS response-header safelist, so the
                // browser hides it from fetch() unless we explicitly expose it.
                // The frontend reads it to determine the full file size from a small
                // probe request (useAudioTracks.ts).
                response.AddHeader("Access-Control-Expose-Headers", "Content-Range, Accept-Ranges");

                if (!string.IsNullOrEmpty(rangeHeader))
                {
                    response.AddHeader("Content-Range", $"bytes {start}-{end}/{fileLength}");
                }

                response.ContentLength64 = contentLength;

                if (start > 0)
                {
                    fs.Seek(start, SeekOrigin.Begin);
                }

                byte[] buffer = new byte[262144];
                long bytesRemaining = contentLength;

                while (bytesRemaining > 0)
                {
                    int bytesToRead = (int)Math.Min(buffer.Length, bytesRemaining);
                    int bytesRead = await fs.ReadAsync(buffer, 0, bytesToRead);

                    if (bytesRead == 0)
                        break;

                    await response.OutputStream.WriteAsync(buffer, 0, bytesRead);
                    bytesRemaining -= bytesRead;
                }
            }
        }

        private static async Task StreamJsonFile(string fileName, HttpListenerResponse response)
        {
            var fileInfo = new FileInfo(fileName);

            response.StatusCode = (int)HttpStatusCode.OK;
            response.ContentType = "application/json";
            response.AddHeader("Accept-Ranges", "bytes");
            response.ContentLength64 = fileInfo.Length;

            using (var fs = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, useAsync: true))
            {
                await fs.CopyToAsync(response.OutputStream);
            }
        }

        private static string? ValidateUserPath(string userPath)
        {
            if (string.IsNullOrWhiteSpace(userPath))
                return null;

            string canonical;
            try
            {
                canonical = Path.GetFullPath(userPath);
            }
            catch
            {
                return null;
            }

            var allowedRoots = new[]
            {
                Settings.Instance.ContentFolder,
                FolderNames.CacheFolder
            };

            foreach (var root in allowedRoots)
            {
                if (string.IsNullOrEmpty(root))
                    continue;

                string rootCanonical;
                try
                {
                    rootCanonical = Path.GetFullPath(root);
                }
                catch
                {
                    continue;
                }

                if (!rootCanonical.EndsWith(Path.DirectorySeparatorChar) &&
                    !rootCanonical.EndsWith(Path.AltDirectorySeparatorChar))
                {
                    rootCanonical += Path.DirectorySeparatorChar;
                }

                if (canonical.StartsWith(rootCanonical, StringComparison.OrdinalIgnoreCase))
                    return canonical;
            }

            return null;
        }
    }
}
