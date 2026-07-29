using Serilog;
using System.Diagnostics;
using System.Globalization;
using VPULSE.Backend.Core.Models;

#if WINDOWS
using System.Drawing;
using System.Drawing.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
#endif

namespace VPULSE.Backend.Media
{
    /// <summary>
    /// Post-processing kill detection for games VPULSE has no native integration for.
    ///
    /// Native integrations (PUBG, GTA, Rocket League) read the game's own files or overlay and stay
    /// the preferred path — they are exact and cost nothing. This is the fallback for everything
    /// else: it reads the recorded video's kill feed after the fact. Nothing is required from the
    /// game, so it works for titles like Delta Force that expose no local match data.
    ///
    /// The output is plain bookmarks, so the timeline, highlight generation and clip creation all
    /// keep working unchanged.
    /// </summary>
    internal static class KillFeedScanner
    {
        /// <summary>
        /// A region of the frame in relative (0-1) coordinates, so a profile calibrated at 1080p
        /// still applies to a 1440p recording of the same game.
        /// </summary>
        public sealed record Region(double X, double Y, double Width, double Height);

        public enum Role
        {
            /// The player's name is first in the row: they got the kill.
            Kill,
            /// The player's name is last in the row: they died.
            Death,
            /// Neither end. Almost always OCR noise from surrounding HUD elements — see ScanAsync.
            Ambiguous,
        }

        public sealed record Candidate(TimeSpan Time, Role Role, string Opponent, int FrameCount);

        public sealed record ScanResult(
            IReadOnlyList<Candidate> Candidates,
            int FramesScanned,
            TimeSpan ExtractionTime,
            TimeSpan OcrTime);

        /// <summary>
        /// One frame per second of recording.
        ///
        /// A feed row lingers on screen for several seconds, but only the first two or three are
        /// legible — after that it fades to a low-contrast tint the OCR cannot read. Sampling every
        /// two seconds was measured missing two of three known kills purely on sampling phase, so
        /// the usable rate is set by how long a row stays *readable*, not how long it stays visible.
        /// </summary>
        public const double DefaultFramesPerSecond = 1.0;

        /// <summary>
        /// Detections closer together than this that name the same opponent are the same event seen
        /// on consecutive frames, not two kills.
        /// </summary>
        private static readonly TimeSpan EventWindow = TimeSpan.FromSeconds(12);

        /// <summary>
        /// The feed row is drawn a moment after the kill lands, and a scan only sees it at the next
        /// sample point. Measured against a session with known kill times, detections trailed the
        /// real event by about a second, so bookmarks are pulled back by that much to sit on the
        /// action rather than just after it.
        /// </summary>
        private static readonly TimeSpan DetectionLag = TimeSpan.FromSeconds(1);

        /// <summary>
        /// The OCR reads names, and names come back with occasional character errors ('NerdyWaffles'
        /// was read as 'NerdyVlaffles' at low scale). Allowing roughly one error per four characters
        /// absorbs that without matching unrelated players.
        /// </summary>
        private static int MatchTolerance(string name) => Math.Max(2, name.Length / 4);

        /// <summary>
        /// The OCR engine reads the cropped frame far better at 3x than at native size. This is not
        /// monotonic — measured on a PUBG feed, 1x misread the name and 2x lost it entirely while 3x
        /// read it cleanly — so do not "optimize" this down without re-measuring.
        /// </summary>
        private const int UpscaleFactor = 3;

#if WINDOWS
        public static async Task<ScanResult> ScanAsync(
            string videoPath,
            string playerName,
            Region region,
            double framesPerSecond = DefaultFramesPerSecond,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(playerName))
                throw new ArgumentException("A player name is required to tell kills from deaths.", nameof(playerName));

            var engine = OcrEngine.TryCreateFromUserProfileLanguages()
                         ?? OcrEngine.TryCreateFromLanguage(new global::Windows.Globalization.Language("en-US"))
                         ?? throw new InvalidOperationException("No OCR language pack is available on this system.");

            string workDir = Path.Combine(Path.GetTempPath(), "vpulse_killscan_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(workDir);

            try
            {
                var duration = await FFmpegService.GetVideoDuration(videoPath);
                int frameCount = Math.Max(1, (int)(duration.TotalSeconds * framesPerSecond));

                var detections = new List<(TimeSpan Time, Role Role, string Opponent)>();
                var detectionLock = new object();
                // One OCR at a time. It costs ~18 ms against ~100 ms to pull a frame, so serializing
                // it hides entirely behind extraction and avoids sharing the engine across threads.
                using var ocrGate = new SemaphoreSlim(1, 1);

                long extractionTicks = 0;
                long ocrTicks = 0;
                int completed = 0;

                var totalTimer = Stopwatch.StartNew();

                await Parallel.ForEachAsync(
                    Enumerable.Range(0, frameCount),
                    new ParallelOptions { MaxDegreeOfParallelism = WorkerCount, CancellationToken = cancellationToken },
                    async (index, ct) =>
                    {
                        var time = TimeSpan.FromSeconds(index / framesPerSecond);
                        string framePath = Path.Combine(workDir, $"f_{index:D6}.png");

                        var extractionTimer = Stopwatch.StartNew();
                        bool extracted = await ExtractFrameAsync(videoPath, time, region, framePath, ct);
                        Interlocked.Add(ref extractionTicks, extractionTimer.ElapsedTicks);

                        if (extracted)
                        {
                            await ocrGate.WaitAsync(ct);
                            try
                            {
                                var ocrTimer = Stopwatch.StartNew();
                                var found = await ReadFrameAsync(engine, framePath, playerName);
                                Interlocked.Add(ref ocrTicks, ocrTimer.ElapsedTicks);

                                if (found.Count > 0)
                                {
                                    lock (detectionLock)
                                    {
                                        foreach (var detection in found)
                                            detections.Add((time, detection.Role, detection.Opponent));
                                    }
                                }
                            }
                            finally { ocrGate.Release(); }

                            try { File.Delete(framePath); } catch { /* cleaned up with the directory */ }
                        }

                        progress?.Report(Interlocked.Increment(ref completed) / (double)frameCount);
                    });

                totalTimer.Stop();
                Log.Information(
                    $"[KillFeedScanner] Scanned {frameCount} frames of {duration:hh\\:mm\\:ss} " +
                    $"in {totalTimer.Elapsed.TotalSeconds:F1}s");

                return new ScanResult(
                    GroupIntoEvents(detections),
                    frameCount,
                    TimeSpan.FromTicks(extractionTicks),
                    TimeSpan.FromTicks(ocrTicks));
            }
            finally
            {
                try { Directory.Delete(workDir, recursive: true); }
                catch (Exception ex) { Log.Warning($"[KillFeedScanner] Could not clean up {workDir}: {ex.Message}"); }
            }
        }

        /// <summary>
        /// Pulls one uncropped frame to a temp file for the calibration screen, so the user draws
        /// the kill feed region over real footage of their own game. The caller deletes it.
        /// Returns null if there is no frame at that position.
        /// </summary>
        public static async Task<string?> ExtractFullFrameAsync(string videoPath, TimeSpan time)
        {
            string path = Path.Combine(
                Path.GetTempPath(), $"vpulse_calib_{Guid.NewGuid():N}.png");

            bool ok = await ExtractFrameAsync(
                videoPath, time, region: null, outputPath: path, CancellationToken.None);

            return ok ? path : null;
        }

        /// <summary>
        /// A small JPEG of the moment a candidate was found, as a base64 data payload.
        ///
        /// The review list otherwise asks the user to confirm events from a timestamp and a name the
        /// OCR may have misread. A picture of the moment is the only thing that lets them actually
        /// check. Kept narrow — these travel over the local socket, several at a time.
        /// </summary>
        public static async Task<string?> ExtractThumbnailAsync(
            string videoPath, TimeSpan time, CancellationToken cancellationToken = default)
        {
            string path = Path.Combine(Path.GetTempPath(), $"vpulse_thumb_{Guid.NewGuid():N}.jpg");

            try
            {
                if (!await ExtractFrameAsync(videoPath, time, region: null, path, cancellationToken, ThumbnailWidth))
                    return null;

                return Convert.ToBase64String(await File.ReadAllBytesAsync(path, cancellationToken));
            }
            catch (Exception ex)
            {
                Log.Debug($"[KillFeedScanner] No thumbnail at {time:hh\\:mm\\:ss}: {ex.Message}");
                return null;
            }
            finally
            {
                try { File.Delete(path); } catch { /* temp file */ }
            }
        }

        private const int ThumbnailWidth = 240;

        /// <summary>
        /// What OCR reads inside a candidate region, one entry per row, with each word's horizontal
        /// position relative to the row. The calibration screen shows this so a badly placed region
        /// is obvious immediately rather than after a full scan, and lets the user pick their own
        /// name from what was actually recognised instead of typing it and hoping it matches.
        /// </summary>
        public sealed record RecognisedWord(string Text, double RelativeX);
        public sealed record RecognisedLine(string Text, IReadOnlyList<RecognisedWord> Words);

        public static async Task<IReadOnlyList<RecognisedLine>> ReadRegionWordsAsync(
            string videoPath, TimeSpan time, Region region)
        {
            var engine = OcrEngine.TryCreateFromUserProfileLanguages()
                         ?? OcrEngine.TryCreateFromLanguage(new global::Windows.Globalization.Language("en-US"))
                         ?? throw new InvalidOperationException("No OCR language pack is available on this system.");

            string path = Path.Combine(Path.GetTempPath(), $"vpulse_calibtest_{Guid.NewGuid():N}.png");

            try
            {
                if (!await ExtractFrameAsync(videoPath, time, region, path, CancellationToken.None))
                    return [];

                OcrResult ocr;
                using (var bitmap = new Bitmap(path))
                {
                    using var softwareBitmap = await ToSoftwareBitmapAsync(bitmap);
                    ocr = await engine.RecognizeAsync(softwareBitmap);
                }

                var lines = new List<RecognisedLine>();
                foreach (var line in ocr.Lines)
                {
                    if (line.Words.Count == 0)
                        continue;

                    double left = line.Words.Min(w => w.BoundingRect.Left);
                    double right = line.Words.Max(w => w.BoundingRect.Right);
                    double span = Math.Max(1, right - left);

                    lines.Add(new RecognisedLine(
                        line.Text,
                        line.Words
                            .OrderBy(w => w.BoundingRect.Left)
                            .Select(w => new RecognisedWord(w.Text, (w.BoundingRect.Left - left) / span))
                            .ToList()));
                }

                return lines;
            }
            finally
            {
                try { File.Delete(path); } catch { /* temp file */ }
            }
        }

        /// <summary>
        /// How many frames to pull at once. A scan is bound by reading the recording off disk, not
        /// by decoding — hardware decoding only cut a measured full-file pass from 125s to 107s,
        /// while seeking to each sample point across four processes cut it to about 50s. Past four
        /// the requests just queue on the drive, and a mechanical disk starts thrashing.
        /// </summary>
        private static readonly int WorkerCount = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);

        /// <summary>
        /// Pulls the calibrated region of a single frame at the given timestamp. Seeking before the
        /// input lets ffmpeg jump to the nearest keyframe and read only a small window of the file,
        /// rather than streaming the whole recording — which is what makes scanning a multi-gigabyte
        /// session practical.
        /// </summary>
        private static async Task<bool> ExtractFrameAsync(
            string videoPath,
            TimeSpan time,
            Region? region,
            string outputPath,
            CancellationToken cancellationToken,
            int? scaleToWidth = null)
        {
            string N(double v) => v.ToString(CultureInfo.InvariantCulture);

            // A null region means the whole frame — the calibration screen needs it at native size
            // so the region the user draws maps straight onto the recording, while thumbnails ask
            // for a width and get the frame scaled down to it.
            string? filter = region is null
                ? (scaleToWidth is null ? null : $"scale={scaleToWidth}:-2")
                : $"crop=iw*{N(region.Width)}:ih*{N(region.Height)}:iw*{N(region.X)}:ih*{N(region.Y)}," +
                  $"scale=iw*{UpscaleFactor}:ih*{UpscaleFactor}:flags=lanczos";

            var psi = new ProcessStartInfo(FFmpegService.GetFFmpegPath())
            {
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            var arguments = new List<string>
            {
                "-hide_banner", "-loglevel", "error",
                "-ss", N(time.TotalSeconds),
                "-i", videoPath,
                "-frames:v", "1",
                "-an",
            };

            if (filter is not null)
            {
                arguments.Add("-vf");
                arguments.Add(filter);
            }

            arguments.Add("-y");
            arguments.Add(outputPath);

            foreach (var argument in arguments)
                psi.ArgumentList.Add(argument);

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Could not start ffmpeg.");

            string stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0 || !File.Exists(outputPath))
            {
                // A seek past the last frame is expected at the tail of a scan, not a failure.
                if (!string.IsNullOrWhiteSpace(stderr))
                    Log.Debug($"[KillFeedScanner] No frame at {time:hh\\:mm\\:ss}: {stderr.Trim()}");
                return false;
            }

            return true;
        }

        private static async Task<List<(Role Role, string Opponent)>> ReadFrameAsync(
            OcrEngine engine, string framePath, string playerName)
        {
            var results = new List<(Role, string)>();

            OcrResult ocr;
            // Deliberately no thresholding or grayscale conversion. Windows OCR reads the raw game
            // frame better than a preprocessed one: on a PUBG feed the luminance threshold used by
            // the live OcrIntegration erased the player's own (tinted) row while keeping other
            // players' white rows — exactly backwards for this purpose.
            using (var bitmap = new Bitmap(framePath))
            {
                using var softwareBitmap = await ToSoftwareBitmapAsync(bitmap);
                ocr = await engine.RecognizeAsync(softwareBitmap);
            }

            int tolerance = MatchTolerance(playerName);

            foreach (var row in GroupIntoRows(ocr))
            {
                int bestIndex = -1;
                int bestDistance = int.MaxValue;
                for (int i = 0; i < row.Count; i++)
                {
                    int distance = LevenshteinDistance(row[i].Text.ToLowerInvariant(), playerName.ToLowerInvariant());
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestIndex = i;
                    }
                }

                if (bestDistance > tolerance)
                    continue;

                // Kill feeds put the killer on the left and the victim on the right, in every
                // shooter checked. Position in the row is what separates a kill from a death — and
                // it doubles as a noise filter, since a stray HUD word matching the player name
                // lands in the middle of a row far more often than at either end.
                //
                // With a single name on the row there is no position to read: the player is both
                // first and last. That happens when the calibrated region clips the row, and
                // calling it a kill (or a death) would be a coin flip presented as a fact. Observed
                // in testing with a hand-drawn region too narrow for the feed.
                Role role = row.Count < 2 ? Role.Ambiguous
                          : bestIndex == 0 ? Role.Kill
                          : bestIndex == row.Count - 1 ? Role.Death
                          : Role.Ambiguous;

                string opponent = role switch
                {
                    Role.Kill => row[^1].Text,
                    Role.Death => row[0].Text,
                    _ => string.Empty,
                };

                results.Add((role, opponent));
            }

            return results;
        }

        /// <summary>
        /// Whether two readings name genuinely different opponents, used to decide when consecutive
        /// detections are two events rather than one seen twice.
        ///
        /// The opponent name is the least reliable thing the scan produces — the same row read on
        /// two frames gave "porky2rules" and "or". Treating any difference as a new event turned one
        /// kill into five. So the name only splits an event when both readings look substantial
        /// enough to trust; a short or empty one defers to the timing instead.
        /// </summary>
        private static bool AreDifferentOpponents(string first, string second)
        {
            const int MinimumTrustedLength = 4;

            if (first.Length < MinimumTrustedLength || second.Length < MinimumTrustedLength)
                return false;

            int allowed = Math.Max(2, Math.Min(first.Length, second.Length) / 3);
            return LevenshteinDistance(first.ToLowerInvariant(), second.ToLowerInvariant()) > allowed;
        }

        private sealed record PlacedWord(string Text, double Left, double Top, double Height);

        /// <summary>
        /// Rebuilds the feed's visual rows from the recognised words, by vertical position.
        ///
        /// The OCR engine's own line grouping cannot be used for this. A feed row is
        /// "killer [weapon icon] victim", and the icon leaves a gap wide enough that the engine
        /// reports the two names as separate lines — measured on Battlefield 6, where every row came
        /// back split. Reading position within an OCR line then compares a name against nothing, and
        /// the kill/death distinction collapses.
        ///
        /// Words that share a vertical band belong to the same row whatever the engine decided.
        /// </summary>
        private static List<List<PlacedWord>> GroupIntoRows(OcrResult ocr)
        {
            var words = new List<PlacedWord>();

            foreach (var line in ocr.Lines)
            {
                foreach (var word in line.Words)
                {
                    // Names contain letters. Everything else in a feed row is decoration: kill
                    // counters, team numbers, and the separator between killer and victim, which the
                    // engine reads as '>' and would otherwise be treated as a participant — it was
                    // being reported as the opponent's name.
                    if (!word.Text.Any(char.IsLetter))
                        continue;

                    words.Add(new PlacedWord(
                        word.Text,
                        word.BoundingRect.Left,
                        word.BoundingRect.Top,
                        word.BoundingRect.Height));
                }
            }

            if (words.Count == 0)
                return [];

            // Rows are separated by more than a line height; words within one never are. Using the
            // median word height rather than a fixed pixel count keeps this working across
            // resolutions and upscale factors.
            var heights = words.Select(w => w.Height).OrderBy(h => h).ToList();
            double medianHeight = heights[heights.Count / 2];
            double tolerance = Math.Max(1, medianHeight * 0.6);

            var rows = new List<List<PlacedWord>>();

            foreach (var word in words.OrderBy(w => w.Top))
            {
                double center = word.Top + word.Height / 2;
                var row = rows.FirstOrDefault(r =>
                {
                    double rowCenter = r.Average(w => w.Top + w.Height / 2);
                    return Math.Abs(rowCenter - center) <= tolerance;
                });

                if (row is null)
                    rows.Add([word]);
                else
                    row.Add(word);
            }

            foreach (var row in rows)
                row.Sort((a, b) => a.Left.CompareTo(b.Left));

            return rows;
        }

        /// <summary>
        /// Collapses the several frames that show one feed row into a single event, timed at the
        /// first sighting. A clearly different opponent starts a new event even inside the window,
        /// so two kills in quick succession are not merged into one.
        /// </summary>
        private static List<Candidate> GroupIntoEvents(List<(TimeSpan Time, Role Role, string Opponent)> detections)
        {
            var events = new List<Candidate>();

            foreach (var detection in detections.OrderBy(d => d.Time))
            {
                // Matched against the most recent event of the same kind, not simply the last one
                // added. The feed routinely shows a kill and a death at once, so the two interleave
                // frame by frame; comparing only against the previous entry meant every repeat
                // landed on the other kind and opened a new event. One kill became five.
                int index = -1;
                for (int i = events.Count - 1; i >= 0; i--)
                {
                    if (detection.Time - events[i].Time >= EventWindow)
                        break;

                    if (events[i].Role == detection.Role)
                    {
                        index = i;
                        break;
                    }
                }

                if (index >= 0 && !AreDifferentOpponents(events[index].Opponent, detection.Opponent))
                    events[index] = events[index] with { FrameCount = events[index].FrameCount + 1 };
                else
                    events.Add(new Candidate(detection.Time, detection.Role, detection.Opponent, 1));
            }

            // Applied only once grouping is done, so the window comparison above stays in raw
            // detection time rather than mixing compensated and uncompensated values.
            return events
                .Select(e => e with { Time = e.Time > DetectionLag ? e.Time - DetectionLag : TimeSpan.Zero })
                .ToList();
        }

        private static async Task<SoftwareBitmap> ToSoftwareBitmapAsync(Bitmap bitmap)
        {
            using var stream = new global::Windows.Storage.Streams.InMemoryRandomAccessStream();
            using var memoryStream = new MemoryStream();

            bitmap.Save(memoryStream, ImageFormat.Bmp);
            memoryStream.Position = 0;

            await memoryStream.CopyToAsync(stream.AsStreamForWrite());
            stream.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(stream);
            return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        }
#else
        public static Task<ScanResult> ScanAsync(
            string videoPath,
            string playerName,
            Region region,
            double framesPerSecond = DefaultFramesPerSecond,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
            => throw new PlatformNotSupportedException("Kill feed scanning requires the Windows OCR engine.");
#endif

        /// <summary>
        /// Converts scan candidates into bookmarks. Ambiguous rows are dropped: measured against a
        /// session with known kills, every false positive fell in that bucket and every real event
        /// fell outside it.
        /// </summary>
        public static List<Bookmark> ToBookmarks(IEnumerable<Candidate> candidates, bool includeDeaths)
        {
            var bookmarks = new List<Bookmark>();

            foreach (var candidate in candidates)
            {
                BookmarkType? type = candidate.Role switch
                {
                    Role.Kill => BookmarkType.Kill,
                    Role.Death when includeDeaths => BookmarkType.Death,
                    _ => null,
                };

                if (type is null)
                    continue;

                bookmarks.Add(new Bookmark { Type = type.Value, Time = candidate.Time });
            }

            return bookmarks;
        }

        private static int LevenshteinDistance(string s, string t)
        {
            if (s == t) return 0;
            if (s.Length == 0) return t.Length;
            if (t.Length == 0) return s.Length;

            var previous = new int[t.Length + 1];
            var current = new int[t.Length + 1];

            for (int j = 0; j <= t.Length; j++)
                previous[j] = j;

            for (int i = 1; i <= s.Length; i++)
            {
                current[0] = i;
                for (int j = 1; j <= t.Length; j++)
                {
                    current[j] = Math.Min(
                        Math.Min(current[j - 1] + 1, previous[j] + 1),
                        previous[j - 1] + (s[i - 1] == t[j - 1] ? 0 : 1));
                }
                (previous, current) = (current, previous);
            }

            return previous[t.Length];
        }
    }
}
