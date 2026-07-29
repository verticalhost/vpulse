using Serilog;
using System.Text.Json;
using VPULSE.Backend.App;
using VPULSE.Backend.Core;
using VPULSE.Backend.Core.Models;
using VPULSE.Backend.Games;
using VPULSE.Backend.Shared;

namespace VPULSE.Backend.Media
{
    /// <summary>
    /// Drives post-processing kill detection from the UI: grabs frames for calibration, runs a scan
    /// over a recording, and writes the confirmed events back as bookmarks.
    ///
    /// This exists only for games VPULSE has no native integration for. Games that detect live keep
    /// doing so — a scan would just duplicate what they already marked.
    /// </summary>
    internal static class KillFeedScanService
    {
        private static CancellationTokenSource? _activeScan;
        private static readonly SemaphoreSlim _scanLock = new(1, 1);

        /// <summary>
        /// Returns a frame from a recording as a base64 PNG so the user can draw the kill feed
        /// region over an actual frame of their own gameplay rather than guess at coordinates.
        /// </summary>
        public static async Task HandleGetCalibrationFrame(JsonElement message)
        {
            try
            {
                string? filePath = message.TryGetProperty("FilePath", out var p) ? p.GetString() : null;
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                {
                    await SendError("Recording not found.");
                    return;
                }

                double atSeconds = message.TryGetProperty("AtSeconds", out var a) ? a.GetDouble() : 0;

                string? framePath = await KillFeedScanner.ExtractFullFrameAsync(
                    filePath, TimeSpan.FromSeconds(atSeconds));

                if (framePath is null)
                {
                    await SendError("Could not read a frame at that position.");
                    return;
                }

                try
                {
                    byte[] bytes = await File.ReadAllBytesAsync(framePath);
                    await MessageService.SendFrontendMessage("KillFeedCalibrationFrame", new
                    {
                        FilePath = filePath,
                        AtSeconds = atSeconds,
                        ImageBase64 = Convert.ToBase64String(bytes),
                    });
                }
                finally
                {
                    try { File.Delete(framePath); } catch { /* temp file */ }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[KillFeedScanService] Calibration frame failed: {ex.Message}");
                await SendError("Could not read a frame from this recording.");
            }
        }

        /// <summary>
        /// Reads back whatever OCR sees inside the region the user has drawn, so calibration gives
        /// immediate feedback instead of only revealing a bad region at the end of a long scan.
        /// The words returned double as the picker for the player's own name.
        /// </summary>
        public static async Task HandleTestCalibration(JsonElement message)
        {
            try
            {
                string? filePath = message.TryGetProperty("FilePath", out var p) ? p.GetString() : null;
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                {
                    await SendError("Recording not found.");
                    return;
                }

                var region = ReadRegion(message);
                double atSeconds = message.TryGetProperty("AtSeconds", out var a) ? a.GetDouble() : 0;

                var words = await KillFeedScanner.ReadRegionWordsAsync(
                    filePath, TimeSpan.FromSeconds(atSeconds), region);

                await MessageService.SendFrontendMessage("KillFeedCalibrationTest", new
                {
                    AtSeconds = atSeconds,
                    Lines = words,
                });
            }
            catch (Exception ex)
            {
                Log.Error($"[KillFeedScanService] Calibration test failed: {ex.Message}");
                await SendError("Could not read that region.");
            }
        }

        /// <summary>
        /// Scans a recording and reports the events found. Nothing is written to the recording here:
        /// the candidates go back to the UI for the user to confirm, because a misread name would
        /// otherwise silently pollute the timeline.
        /// </summary>
        public static async Task HandleScanContent(JsonElement message)
        {
            if (!await _scanLock.WaitAsync(0))
            {
                await SendError("A scan is already running.");
                return;
            }

            var cancellation = new CancellationTokenSource();
            _activeScan = cancellation;

            try
            {
                string? filePath = message.TryGetProperty("FilePath", out var p) ? p.GetString() : null;
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                {
                    await SendError("Recording not found.");
                    return;
                }

                var region = ReadRegion(message);
                string playerName = message.TryGetProperty("PlayerName", out var n) ? n.GetString() ?? "" : "";
                double fps = message.TryGetProperty("FramesPerSecond", out var f)
                    ? f.GetDouble()
                    : KillFeedScanner.DefaultFramesPerSecond;

                if (string.IsNullOrWhiteSpace(playerName))
                {
                    await SendError("Set your in-game name first — it is what tells a kill from a death.");
                    return;
                }

                // Report at whole percents only; the frontend redraws on every message and a
                // thousand-frame scan would otherwise push a thousand updates through the socket.
                int lastPercent = -1;
                var progress = new Progress<double>(value =>
                {
                    int percent = (int)(value * 100);
                    if (percent == lastPercent) return;
                    lastPercent = percent;
                    _ = MessageService.SendFrontendMessage("KillFeedScanProgress", new
                    {
                        FilePath = filePath,
                        Percent = percent,
                    });
                });

                var result = await KillFeedScanner.ScanAsync(
                    filePath, playerName, region, fps, progress, cancellation.Token);

                // A thumbnail per candidate, so the review list can be checked by eye rather than
                // taken on trust. Pulled in parallel like the scan itself; a candidate whose frame
                // cannot be read simply has none, which is not worth failing the scan over.
                var thumbnails = new string?[result.Candidates.Count];
                await Parallel.ForEachAsync(
                    Enumerable.Range(0, result.Candidates.Count),
                    new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellation.Token },
                    async (index, token) =>
                    {
                        thumbnails[index] = await KillFeedScanner.ExtractThumbnailAsync(
                            filePath, result.Candidates[index].Time, token);
                    });

                await MessageService.SendFrontendMessage("KillFeedScanResult", new
                {
                    FilePath = filePath,
                    FramesScanned = result.FramesScanned,
                    Candidates = result.Candidates.Select((c, i) => new
                    {
                        Time = c.Time.ToString(@"hh\:mm\:ss\.fff"),
                        Role = c.Role.ToString(),
                        c.Opponent,
                        c.FrameCount,
                        ThumbnailBase64 = thumbnails[i],
                    }),
                });

                Log.Information(
                    $"[KillFeedScanService] {result.Candidates.Count} candidates over " +
                    $"{result.FramesScanned} frames of {PathUtils.Normalize(filePath)}");
            }
            catch (OperationCanceledException)
            {
                Log.Information("[KillFeedScanService] Scan cancelled");
                await MessageService.SendFrontendMessage("KillFeedScanCancelled", new { });
            }
            catch (Exception ex)
            {
                Log.Error($"[KillFeedScanService] Scan failed: {ex.Message}");
                await SendError("The scan failed. See the logs for details.");
            }
            finally
            {
                _activeScan = null;
                cancellation.Dispose();
                _scanLock.Release();
            }
        }

        public static void CancelScan() => _activeScan?.Cancel();

        /// <summary>
        /// Writes the confirmed events as bookmarks in one pass.
        ///
        /// Deliberately a batch, not a loop over the single AddBookmark command: each of those does
        /// its own read-modify-write of the metadata file, so firing several at once loses all but
        /// the last. Observed in testing — two confirmed kills, one bookmark on disk.
        /// </summary>
        public static async Task HandleAddBookmarks(JsonElement message)
        {
            try
            {
                string? filePath = message.TryGetProperty("FilePath", out var p) ? p.GetString() : null;
                string? contentTypeText = message.TryGetProperty("ContentType", out var c) ? c.GetString() : null;

                if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(contentTypeText))
                {
                    Log.Error("[KillFeedScanService] AddBookmarks is missing FilePath or ContentType");
                    return;
                }

                if (!Enum.TryParse<Content.ContentType>(contentTypeText, out var contentType))
                {
                    Log.Error($"[KillFeedScanService] Unknown content type: {contentTypeText}");
                    return;
                }

                if (!message.TryGetProperty("Bookmarks", out var entries) || entries.ValueKind != JsonValueKind.Array)
                {
                    Log.Error("[KillFeedScanService] AddBookmarks has no bookmarks");
                    return;
                }

                var bookmarks = new List<Bookmark>();
                foreach (var entry in entries.EnumerateArray())
                {
                    string? typeText = entry.TryGetProperty("Type", out var t) ? t.GetString() : null;
                    string? timeText = entry.TryGetProperty("Time", out var ti) ? ti.GetString() : null;

                    if (string.IsNullOrEmpty(timeText) || !TimeSpan.TryParse(timeText, out var time))
                        continue;

                    var type = Enum.TryParse<BookmarkType>(typeText, out var parsed) ? parsed : BookmarkType.Manual;
                    bookmarks.Add(new Bookmark { Type = type, Time = time });
                }

                if (bookmarks.Count == 0)
                    return;

                string metadataPath = PathUtils.Combine(
                    FolderNames.GetMetadataFolderPath(contentType),
                    $"{Path.GetFileNameWithoutExtension(filePath)}.json");

                var updated = await ContentService.UpdateMetadataFile(metadataPath, content =>
                {
                    content.Bookmarks ??= [];
                    foreach (var bookmark in bookmarks)
                        content.AddBookmark(bookmark);
                });

                if (updated is null)
                    return;

                var item = AppState.Instance.Content.FirstOrDefault(
                    x => x.FilePath == filePath && x.Type == contentType);

                if (item is not null)
                {
                    foreach (var bookmark in bookmarks)
                        item.AddBookmark(bookmark);
                }

                await MessageService.SendStateToFrontend("Added kill feed bookmarks");
                Log.Information($"[KillFeedScanService] Added {bookmarks.Count} bookmarks to {PathUtils.Normalize(filePath)}");
            }
            catch (Exception ex)
            {
                Log.Error($"[KillFeedScanService] Could not add bookmarks: {ex.Message}");
                await SendError("Could not save the bookmarks.");
            }
        }

        /// <summary>
        /// Saves the calibrated region and name against the game, so the next recording of it can be
        /// scanned without going through calibration again.
        /// </summary>
        public static async Task HandleSaveProfile(JsonElement message)
        {
            try
            {
                string? gameName = message.TryGetProperty("GameName", out var g) ? g.GetString() : null;
                if (string.IsNullOrWhiteSpace(gameName))
                {
                    Log.Error("[KillFeedScanService] SaveProfile without a game name");
                    return;
                }

                var region = ReadRegion(message);

                var profile = new KillFeedProfile
                {
                    GameName = gameName,
                    RegionX = region.X,
                    RegionY = region.Y,
                    RegionWidth = region.Width,
                    RegionHeight = region.Height,
                    PlayerName = message.TryGetProperty("PlayerName", out var n) ? n.GetString() ?? "" : "",
                    ScanFramesPerSecond = message.TryGetProperty("FramesPerSecond", out var f)
                        ? f.GetDouble()
                        : KillFeedScanner.DefaultFramesPerSecond,
                    IncludeDeaths = !message.TryGetProperty("IncludeDeaths", out var d) || d.GetBoolean(),
                };

                string? path = KillFeedProfileStore.Save(gameName, profile);

                // Echoed back so the UI can stop asking for calibration, and can show where the
                // file landed — the point of a file per game is that it can be opened and shared.
                await SendProfile(gameName, profile, path);
            }
            catch (Exception ex)
            {
                Log.Error($"[KillFeedScanService] Could not save profile: {ex.Message}");
            }
        }

        /// <summary>
        /// Returns the stored profile for a game, if it has one. Asked for when the scan screen
        /// opens, so a game already calibrated — by this user or by a profile shipped with the app —
        /// goes straight to scanning.
        /// </summary>
        public static async Task HandleGetProfile(JsonElement message)
        {
            string gameName = message.TryGetProperty("GameName", out var g) ? g.GetString() ?? "" : "";
            await SendProfile(gameName, KillFeedProfileStore.Load(gameName), path: null);
        }

        private static Task SendProfile(string gameName, KillFeedProfile? profile, string? path) =>
            MessageService.SendFrontendMessage("KillFeedProfile", new
            {
                GameName = gameName,
                FilePath = path,
                Profile = profile is null ? null : new
                {
                    profile.RegionX,
                    profile.RegionY,
                    profile.RegionWidth,
                    profile.RegionHeight,
                    profile.PlayerName,
                    profile.ScanFramesPerSecond,
                    profile.IncludeDeaths,
                },
            });

        private static KillFeedScanner.Region ReadRegion(JsonElement message) => new(
            message.TryGetProperty("RegionX", out var x) ? x.GetDouble() : 0,
            message.TryGetProperty("RegionY", out var y) ? y.GetDouble() : 0,
            message.TryGetProperty("RegionWidth", out var w) ? w.GetDouble() : 1,
            message.TryGetProperty("RegionHeight", out var h) ? h.GetDouble() : 1);

        private static Task SendError(string reason) =>
            MessageService.SendFrontendMessage("KillFeedScanError", new { Reason = reason });
    }
}
