using Serilog;
using System.Net;
using System.Text.Json;
using VPULSE.Backend.App;
using VPULSE.Backend.Auth;
using VPULSE.Backend.Core;
using System.Diagnostics;
using VPULSE.Backend.Shared;
using System.Net.Http.Headers;
using VPULSE.Backend.Core.Models;

namespace VPULSE.Backend.Media
{
    internal static class UploadService
    {
        private static readonly HttpClient _httpClient = new()
        {
            Timeout = TimeSpan.FromMinutes(10)
        };

        private static readonly Dictionary<string, CancellationTokenSource> _activeUploads = new();
        private static readonly object _uploadLock = new();

        public static void CancelUpload(string fileName)
        {
            Log.Information($"[Upload] Cancel requested for: {fileName}");

            lock (_uploadLock)
            {
                if (_activeUploads.TryGetValue(fileName, out var cts))
                {
                    cts.Cancel();
                    Log.Information($"[Upload] Cancelled upload for: {fileName}");
                }
                else
                {
                    Log.Warning($"[Upload] No active upload found for: {fileName}");
                }
            }
        }

        public static async Task HandleUploadContent(JsonElement message)
        {
            string fileName = "";
            string title = "";
            CancellationTokenSource? cts = null;

            try
            {
                string filePath = message.GetProperty("FilePath").GetString()!;
                fileName = Path.GetFileName(filePath);
                string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
                title = message.GetProperty("Title").GetString()!;

                cts = new CancellationTokenSource();
                lock (_uploadLock)
                {
                    _activeUploads[fileName] = cts;
                }

                byte[] fileBytes = await File.ReadAllBytesAsync(filePath, cts.Token);
                using var formData = new MultipartFormDataContent();

                int lastSentProgress = -1;
                void ProgressHandler(long sent, long total)
                {
                    if (total <= 0) return;
                    int progress = (int)(sent / (double)total * 100);

                    if (progress != lastSentProgress)
                    {
                        lastSentProgress = progress;

                        if (progress >= 100)
                        {
                            _ = MessageService.SendFrontendMessage("UploadProgress", new
                            {
                                title,
                                fileName,
                                progress = 100,
                                status = "processing",
                                message = "Processing..."
                            });
                        }
                        else
                        {
                            _ = MessageService.SendFrontendMessage("UploadProgress", new
                            {
                                title,
                                fileName,
                                progress,
                                status = "uploading",
                                message = $"Uploading... {progress}%"
                            });
                        }
                    }
                }

                // Gamefolio rejects anything that is not a declared video type, so the part has to
                // carry a real media type: "application/octet-stream" is refused outright.
                var fileContent = new ProgressableStreamContent(fileBytes, VideoMediaType(fileName), ProgressHandler, cts.Token);
                formData.Add(fileContent, "file", fileName);

                // Gamefolio requires a title; the rest is optional. Names follow its clip API
                // rather than the field names the frontend still uses internally.
                formData.Add(new StringContent(string.IsNullOrWhiteSpace(title) ? fileNameWithoutExtension : title), "title");
                AddOptionalContent(formData, message, "Description", "description");
                // gameId is an identifier, not a label: sending the display name
                // ("PUBG: BATTLEGROUNDS") makes the endpoint answer 500. The frontend already
                // carries the game's IGDB id alongside the name, so that is what goes out; a
                // recording whose game was never resolved simply uploads untagged.
                AddOptionalContent(formData, message, "IgdbId", "gameId");

                await MessageService.SendFrontendMessage("UploadProgress", new
                {
                    title,
                    fileName,
                    progress = 0,
                    status = "uploading",
                    message = "Starting upload..."
                });

                var provider = OAuthProviders.Gamefolio;
                string? accessToken = await OAuthTokenService.GetAccessTokenAsync(provider);
                if (accessToken == null)
                {
                    await FailUpload(title, fileName, "Connect your Gamefolio account to publish clips.");
                    return;
                }

                var request = new HttpRequestMessage(HttpMethod.Post, $"{provider.ApiBase}/clips")
                {
                    Content = formData
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                var response = await _httpClient.SendAsync(request, cts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    // EnsureSuccessStatusCode throws away the body, which is where the endpoint
                    // says what it actually objected to. Without it a rejected field is
                    // indistinguishable from an outage.
                    string error = await response.Content.ReadAsStringAsync(cts.Token);
                    Log.Error("Gamefolio rejected the upload ({Status}): {Body}",
                        (int)response.StatusCode, Truncate(error, 600));
                    throw new HttpRequestException(
                        $"Gamefolio answered {(int)response.StatusCode}. {Truncate(error, 300)}");
                }

                lock (_uploadLock)
                {
                    _activeUploads.Remove(fileName);
                }

                await MessageService.SendFrontendMessage("UploadProgress", new
                {
                    title,
                    fileName,
                    progress = 100,
                    status = "done",
                    message = "Upload completed successfully"
                });

                var responseContent = await response.Content.ReadAsStringAsync();
                Log.Information($"Upload success: {responseContent}");

                // Parse the response to extract the URL and update the content with uploadId
                if (!string.IsNullOrEmpty(responseContent))
                {
                    try
                    {
                        var responseJson = JsonSerializer.Deserialize<JsonElement>(responseContent);
                        // Gamefolio answers 201 with { "clip": { ..., "shareUrl": "..." } }. Only
                        // shareUrl is kept: videoUrl and thumbnailUrl are signed and expire in an
                        // hour, so storing them would leave dead links behind.
                        if (responseJson.TryGetProperty("clip", out var clipElement) &&
                            clipElement.TryGetProperty("shareUrl", out var urlElement))
                        {
                            string url = urlElement.GetString()!;
                            if (!string.IsNullOrEmpty(url))
                            {
                                // The share code is the last path segment.
                                string uploadId = url.Split('/').Last();
                                Log.Information($"Extracted upload ID: {uploadId}");

                                // Update the content with the uploadId
                                var contentList = AppState.Instance.Content.ToList();
                                Log.Information($"File name: {fileName}, without extension: {fileNameWithoutExtension}");

                                var contentToUpdate = contentList.FirstOrDefault(c =>
                                    Path.GetFileNameWithoutExtension(c.FileName) == fileNameWithoutExtension);
                                Log.Information($"Content to update: {contentToUpdate?.FileName ?? "not found"}");

                                if (contentToUpdate != null)
                                {
                                    contentToUpdate.UploadId = uploadId;

                                    // Also update the metadata file
                                    string metadataFolderPath = FolderNames.GetMetadataFolderPath(contentToUpdate.Type);
                                    string metadataFilePath = PathUtils.Combine(metadataFolderPath, $"{fileNameWithoutExtension}.json");

                                    var updatedContent = await ContentService.UpdateMetadataFile(metadataFilePath, content =>
                                    {
                                        content.UploadId = uploadId;
                                    });

                                    if (updatedContent != null)
                                    {
                                        Log.Information($"Updated metadata file with upload ID: {metadataFilePath}");
                                    }

                                    Log.Information($"Updated content with upload ID: {uploadId}");
                                    await SettingsService.LoadContentFromFolderIntoState(true);
                                }

                                // Open browser if setting is enabled
                                if (Settings.Instance.ClipShowInBrowserAfterUpload)
                                {
                                    Log.Information($"Opening URL in browser: {url}");
                                    Process.Start(new ProcessStartInfo
                                    {
                                        FileName = url,
                                        UseShellExecute = true
                                    });
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Failed to parse upload response or update content: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Log.Information($"[Upload] Upload cancelled for: {fileName}");

                lock (_uploadLock)
                {
                    _activeUploads.Remove(fileName);
                }

                await MessageService.SendFrontendMessage("UploadProgress", new
                {
                    title,
                    fileName,
                    progress = 0,
                    status = "error",
                    message = "Upload cancelled"
                });
            }
            catch (Exception ex)
            {
                Log.Error($"Upload failed: {ex.Message}");

                lock (_uploadLock)
                {
                    if (!string.IsNullOrEmpty(fileName))
                        _activeUploads.Remove(fileName);
                }

                await MessageService.ShowModal(
                    "Upload Error",
                    "The upload failed.\n" + ex.Message,
                    "error",
                    "Could not upload clip"
                );

                await MessageService.SendFrontendMessage("UploadProgress", new
                {
                    title,
                    fileName,
                    progress = 0,
                    status = "error",
                    message = ex.Message
                });
            }
            finally
            {
                cts?.Dispose();
            }
        }

        public class ProgressableStreamContent : HttpContent
        {
            private readonly byte[] _content;
            private readonly Action<long, long> _progressCallback;
            private readonly CancellationToken _cancellationToken;

            public ProgressableStreamContent(byte[] content, string mediaType, Action<long, long> progressCallback, CancellationToken cancellationToken = default)
            {
                _content = content ?? throw new ArgumentNullException(nameof(content));
                _progressCallback = progressCallback;
                _cancellationToken = cancellationToken;
                Headers.ContentType = new MediaTypeHeaderValue(mediaType);
            }

            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            {
                long totalBytes = _content.Length;
                long totalWritten = 0;
                int bufferSize = 4096;

                for (int i = 0; i < _content.Length; i += bufferSize)
                {
                    _cancellationToken.ThrowIfCancellationRequested();

                    int toWrite = Math.Min(bufferSize, _content.Length - i);
                    await stream.WriteAsync(_content.AsMemory(i, toWrite), _cancellationToken);
                    totalWritten += toWrite;
                    _progressCallback?.Invoke(totalWritten, totalBytes);
                }
            }

            protected override bool TryComputeLength(out long length)
            {
                length = _content.Length;
                return true;
            }
        }

        /// <param name="formField">
        /// The name the API expects, which differs from the message field the frontend sends.
        /// </param>
        private static void AddOptionalContent(MultipartFormDataContent formData, JsonElement message, string field, string formField)
        {
            if (message.TryGetProperty(field, out JsonElement element)
                && element.ValueKind == JsonValueKind.String
                && element.GetString() is { Length: > 0 } value)
            {
                formData.Add(new StringContent(value), formField);
            }
        }

        /// <summary>
        /// The subset Gamefolio accepts. Anything else falls back to mp4, which is what VPULSE
        /// records; sending a wrong-but-plausible video type gets a clear rejection, while
        /// sending none at all gets the opaque 500 this replaced.
        /// </summary>
        private static string VideoMediaType(string fileName) =>
            Path.GetExtension(fileName).ToLowerInvariant() switch
            {
                ".webm" => "video/webm",
                ".mov" => "video/quicktime",
                ".avi" => "video/x-msvideo",
                _ => "video/mp4",
            };

        private static string Truncate(string value, int max) =>
            string.IsNullOrEmpty(value) ? "(empty body)"
            : value.Length <= max ? value
            : value[..max] + "…";

        private static async Task FailUpload(string title, string fileName, string reason)
        {
            Log.Warning("Upload not started: {Reason}", reason);
            lock (_uploadLock)
            {
                _activeUploads.Remove(fileName);
            }

            await MessageService.SendFrontendMessage("UploadProgress", new
            {
                title,
                fileName,
                progress = 0,
                status = "error",
                message = reason
            });
        }
    }
}
