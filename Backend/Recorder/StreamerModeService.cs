using Serilog;
using VPULSE.Backend.App;
using VPULSE.Backend.Core;
using VPULSE.Backend.Core.Models;
using VPULSE.Backend.Media;
using VPULSE.Backend.Shared;
using VPULSE.Backend.Windows.Storage;

namespace VPULSE.Backend.Recorder
{
    /// <summary>
    /// Rides the user's own OBS instead of capturing alongside it.
    /// </summary>
    /// <remarks>
    /// A streamer already pays for one capture and one encode; running a second set costs about
    /// two thirds of the first encoder session again, measured. Here VPULSE encodes nothing at all:
    /// it asks OBS to save from the scene already on air, so clips also arrive with the overlays
    /// and camera the viewers see, which raw gameplay capture cannot produce.
    /// </remarks>
    internal static class StreamerModeService
    {
        private static readonly ObsWebSocketClient _obs = new();
        private static readonly SemaphoreSlim _gate = new(1, 1);

        /// <summary>Guards against two kills seconds apart producing near-duplicate clips.</summary>
        private static DateTimeOffset _lastSaveUtc = DateTimeOffset.MinValue;

        private static string _pendingGame = "Unknown";

        public static bool IsConnected => _obs.IsConnected;

        /// <summary>Cached by the watcher so callers on the recording path stay synchronous.</summary>
        public static bool IsStreaming { get; private set; }

        /// <summary>
        /// True when the user is live in their own OBS, which is when riding it beats capturing
        /// alongside it. Merely having OBS open is not enough: its replay buffer may be off, and
        /// VPULSE's own capture is the better default then.
        /// </summary>
        public static bool ShouldRideObs => _obs.IsConnected && IsStreaming;

        static StreamerModeService()
        {
            _obs.ReplaySaved += path => _ = ImportAsync(path, _pendingGame);
        }

        /// <summary>Reports what stops streamer mode from working, for the settings UI.</summary>
        public static (bool Ready, string Reason) Probe()
        {
            var cfg = ObsWebSocketClient.ReadObsConfig();
            if (cfg == null)
                return (false, "OBS Studio isn't installed, or has never been opened.");
            if (!cfg.Value.ServerEnabled)
                return (false, "Enable the WebSocket server in OBS (Tools > WebSocket Server Settings), then restart OBS.");
            return (true, string.Empty);
        }

        public static async Task<bool> ConnectAsync()
        {
            await _gate.WaitAsync();
            try
            {
                if (_obs.IsConnected)
                    return true;

                if (!await _obs.ConnectAsync())
                    return false;

                var version = await _obs.RequestAsync("GetVersion");
                if (version is { } v && v.TryGetProperty("obsVersion", out var ov))
                    Log.Information("Streamer mode: OBS {Version}", ov.GetString());

                return true;
            }
            finally
            {
                _gate.Release();
            }
        }

        public static async Task<bool> IsStreamingAsync()
        {
            var status = await _obs.RequestAsync("GetStreamStatus");
            return status is { } s && s.TryGetProperty("outputActive", out var a) && a.GetBoolean();
        }

        /// <summary>
        /// Starts OBS's replay buffer if it isn't already running. Fails when the buffer is turned
        /// off in OBS's output settings, which cannot be changed over the websocket.
        /// </summary>
        public static async Task<bool> EnsureReplayBufferAsync()
        {
            var status = await _obs.RequestAsync("GetReplayBufferStatus");
            if (status is { } s && s.TryGetProperty("outputActive", out var active) && active.GetBoolean())
                return true;

            if (await _obs.RequestAsync("StartReplayBuffer") == null)
            {
                Log.Warning("OBS would not start its replay buffer; it is likely disabled in Settings > Output");
                return false;
            }

            // OBS reports the output active a moment after accepting the request.
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(300);
                var again = await _obs.RequestAsync("GetReplayBufferStatus");
                if (again is { } a2 && a2.TryGetProperty("outputActive", out var ok) && ok.GetBoolean())
                    return true;
            }

            return false;
        }

        /// <summary>Asks OBS to write the last N seconds. The clip arrives via the ReplaySaved event.</summary>
        public static async Task<bool> SaveClipAsync(string game)
        {
            if (!_obs.IsConnected && !await ConnectAsync())
                return false;

            // OBS's buffer is a rolling window, so two saves inside it return overlapping footage.
            if (DateTimeOffset.UtcNow - _lastSaveUtc < TimeSpan.FromSeconds(10))
            {
                Log.Information("Skipping replay save: one was taken {Seconds:F0}s ago",
                    (DateTimeOffset.UtcNow - _lastSaveUtc).TotalSeconds);
                return false;
            }

            if (!await EnsureReplayBufferAsync())
                return false;

            _pendingGame = string.IsNullOrWhiteSpace(game) ? "Unknown" : game;
            if (await _obs.RequestAsync("SaveReplayBuffer") == null)
                return false;

            _lastSaveUtc = DateTimeOffset.UtcNow;
            Log.Information("Asked OBS to save a replay for {Game}", _pendingGame);
            return true;
        }

        /// <summary>
        /// Copies OBS's replay into the VPULSE library so it appears beside everything else, with a
        /// thumbnail and the game VPULSE detected rather than the "Unknown" a manual import gets.
        /// </summary>
        private static async Task ImportAsync(string obsPath, string game)
        {
            try
            {
                // OBS reports the path the instant it starts writing; wait for the file to settle.
                for (int i = 0; i < 20 && !IsReady(obsPath); i++)
                    await Task.Delay(500);

                if (!IsReady(obsPath))
                {
                    Log.Warning("OBS replay never finished writing; skipping import");
                    return;
                }

                const Content.ContentType type = Content.ContentType.Buffer;

                // Game names carry characters Windows rejects in a path ("PUBG: BATTLEGROUNDS"),
                // so the folder has to go through the same sanitiser the recorder uses.
                string folder = PathUtils.Combine(
                    Settings.Instance.ContentFolder,
                    FolderNames.GetVideoFolderName(type),
                    StorageService.SanitizeGameNameForFolder(game));
                Directory.CreateDirectory(folder);

                string target = PathUtils.Combine(folder,
                    $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}{Path.GetExtension(obsPath)}");

                File.Copy(obsPath, target);
                await ContentService.CreateMetadataFile(target, type, game, createdAt: DateTime.Now);
                await ContentService.CreateThumbnail(target, type);
                Log.Information("Imported an OBS replay into the library: {Path}", target);

                // The library rescan walks the whole content folder and can trip over directories
                // it may not read (a content folder set to a drive root hits System Volume
                // Information). The clip is already on disk with its metadata, so a failure here
                // must not be reported as a failed import.
                try
                {
                    await SettingsService.LoadContentFromFolderIntoState(true);
                }
                catch (Exception ex)
                {
                    Log.Warning("Clip imported, but refreshing the library failed: {Type}", ex.GetType().Name);
                }
            }
            catch (Exception ex)
            {
                // Include the message: these are filesystem faults, not anything sensitive, and the
                // type alone ("IOException") says nothing about which path or why.
                Log.Error("Could not import the OBS replay: {Type}: {Message}", ex.GetType().Name, ex.Message);
            }
        }

        private static bool IsReady(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return false;

                // OBS keeps the file open while muxing; an exclusive open is the readiness signal.
                using var _ = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
                return new FileInfo(path).Length > 0;
            }
            catch (IOException)
            {
                return false;
            }
        }

        /// <summary>True while a session is being recorded by OBS rather than by VPULSE.</summary>
        public static bool IsRidingSession { get; private set; }

        /// <summary>
        /// Records the session through OBS instead of VPULSE's own pipeline.
        /// </summary>
        /// <remarks>
        /// Not the replay buffer: PUBG only writes its replay data at the end of a match, so kills
        /// are discovered around two minutes after they happen — measured at 2m09s on a real match.
        /// A 20-second buffer asked for that late holds the wrong footage entirely. Recording the
        /// whole session and cutting by timestamp is what VPULSE already does well; this just uses
        /// OBS's file. It costs nothing extra when the user streams with "same as stream" output,
        /// because OBS then reuses the encoder the stream is already running.
        /// </remarks>
        public static async Task<bool> BeginObsSessionAsync(string game, string exePath, int? pid)
        {
            if (await _obs.RequestAsync("StartRecord") == null)
            {
                Log.Warning("OBS refused to start recording; falling back to VPULSE's own capture");
                return false;
            }

            IsRidingSession = true;
            AppState.Instance.Recording = new Recording
            {
                // Anchors every bookmark offset, so it must be when OBS actually began.
                StartTime = DateTime.Now,
                Game = game,
                ExePath = exePath,
                Pid = pid,
                FilePath = null,
                FileName = string.Empty,
                CoverImageId = Games.GameUtils.GetCoverImageIdFromExePath(exePath),
            };
            AppState.Instance.PreRecording = null;

            Log.Information("Recording {Game} through OBS; VPULSE is not encoding", game);
            await MessageService.SendStateToFrontend("Streamer mode recording started");
            return true;
        }

        /// <summary>Stops OBS's recording and files the result as a normal VPULSE session.</summary>
        public static async Task EndObsSessionAsync()
        {
            var recording = AppState.Instance.Recording;
            IsRidingSession = false;

            var stopped = await _obs.RequestAsync("StopRecord");
            string? obsPath = stopped is { } s && s.TryGetProperty("outputPath", out var p) ? p.GetString() : null;

            AppState.Instance.Recording = null;
            await MessageService.SendStateToFrontend("Streamer mode recording stopped");

            if (recording == null || string.IsNullOrEmpty(obsPath))
            {
                Log.Warning("OBS did not return a recording path; nothing to import");
                return;
            }

            try
            {
                for (int i = 0; i < 40 && !IsReady(obsPath); i++)
                    await Task.Delay(500);

                if (!IsReady(obsPath))
                {
                    Log.Warning("OBS recording never finished writing: {Path}", obsPath);
                    return;
                }

                const Content.ContentType type = Content.ContentType.Session;
                string game = string.IsNullOrWhiteSpace(recording.Game) ? "Unknown" : recording.Game;
                string folder = PathUtils.Combine(
                    Settings.Instance.ContentFolder,
                    FolderNames.GetVideoFolderName(type),
                    StorageService.SanitizeGameNameForFolder(game));
                Directory.CreateDirectory(folder);

                string target = PathUtils.Combine(folder,
                    $"{recording.StartTime:yyyy-MM-dd_HH-mm-ss}{Path.GetExtension(obsPath)}");

                // Move rather than copy: these files run to gigabytes, and OBS produced this one
                // only because VPULSE asked it to.
                File.Move(obsPath, target, overwrite: true);

                // Carrying the bookmarks over is the whole point: the highlight and clip tools then
                // work on this file exactly as they would on a session VPULSE recorded itself.
                await ContentService.CreateMetadataFile(target, type, game, recording.Bookmarks,
                    createdAt: recording.StartTime);
                await ContentService.CreateThumbnail(target, type);
                Log.Information("Filed OBS recording as a session with {Count} bookmarks: {Path}",
                    recording.Bookmarks.Count, target);

                try
                {
                    await SettingsService.LoadContentFromFolderIntoState(true);
                }
                catch (Exception ex)
                {
                    Log.Warning("Session filed, but refreshing the library failed: {Type}", ex.GetType().Name);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Could not file the OBS recording: {Type}: {Message}", ex.GetType().Name, ex.Message);
            }
        }

        public static void Disconnect() => _obs.Dispose();

        private static bool _lastReportedConnected;

        /// <summary>
        /// Polls OBS so the UI can show whether streamer mode is live. Cheap: it only opens a
        /// socket when OBS is actually reachable, and reports to the frontend on change.
        /// </summary>
        public static async Task WatchAsync()
        {
            while (true)
            {
                try
                {
                    if (!_obs.IsConnected && Probe().Ready)
                        await ConnectAsync();

                    IsStreaming = _obs.IsConnected && await IsStreamingAsync();

                    // Republish every cycle rather than only on change. The indicator mounts after
                    // the connection push and would otherwise miss it and never be told again;
                    // one small message every ten seconds is cheaper than that class of bug.
                    _lastReportedConnected = _obs.IsConnected;
                    await SendStateAsync();
                }
                catch (Exception ex)
                {
                    Log.Debug("Streamer mode watcher: {Type}", ex.GetType().Name);
                }

                await Task.Delay(TimeSpan.FromSeconds(10));
            }
        }

        public static Task SendStateAsync()
        {
            var (ready, reason) = Probe();
            return MessageService.SendFrontendMessage("StreamerModeState", new
            {
                isConnected = _obs.IsConnected,
                isReady = ready,
                reason,
            });
        }
    }
}
