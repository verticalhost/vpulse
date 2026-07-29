using Serilog;
using System.Text;
using System.Text.Json;
using VPULSE.Backend.Core.Models;
using VPULSE.Backend.Shared;

namespace VPULSE.Backend.Games
{
    /// <summary>
    /// Kill feed profiles, one JSON file per game.
    ///
    /// Two layers, in priority order:
    ///
    ///   1. The user's own calibrations, in %APPDATA%/VPULSE/game-profiles (or the XDG equivalent).
    ///   2. Profiles shipped with the app, from Backend/Games/Profiles in the source tree.
    ///
    /// A file per game rather than a blob inside settings.json, because these are worth reading,
    /// diffing and above all sharing: one person calibrates a game, the file can be dropped into
    /// anyone else's profile folder, or contributed to the repo so it ships as a default.
    ///
    /// Shipped profiles are never written to — a user's calibration always lands in their own
    /// folder, so an app update cannot overwrite it and reinstalling cannot lose it.
    /// </summary>
    internal static class KillFeedProfileStore
    {
        private const string UserFolderName = "game-profiles";

        /// Copied next to the executable at build time; see the Content item in VPULSE.csproj.
        private const string ShippedFolderName = "GameProfiles";

        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        // Beside settings.json, so a user's calibrations travel with the rest of their configuration.
        public static string UserProfileFolder =>
            PathUtils.Combine(
                Path.GetDirectoryName(Core.SettingsService.SettingsFilePath) ?? string.Empty,
                UserFolderName);

        private static string ShippedProfileFolder =>
            PathUtils.Combine(AppContext.BaseDirectory, ShippedFolderName);

        /// <summary>
        /// Turns a game name into a stable file name. Games carry characters a path cannot
        /// ("PUBG: BATTLEGROUNDS"), and the result has to survive being typed, mailed and committed.
        /// </summary>
        public static string ToSlug(string gameName)
        {
            if (string.IsNullOrWhiteSpace(gameName))
                return "unknown";

            var builder = new StringBuilder(gameName.Length);
            bool lastWasSeparator = false;

            foreach (char c in gameName.Trim().ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c))
                {
                    builder.Append(c);
                    lastWasSeparator = false;
                }
                else if (!lastWasSeparator && builder.Length > 0)
                {
                    builder.Append('-');
                    lastWasSeparator = true;
                }
            }

            string slug = builder.ToString().Trim('-');
            return slug.Length == 0 ? "unknown" : slug;
        }

        /// <summary>
        /// Returns the profile for a game, preferring the user's own calibration over the shipped
        /// one, or null if the game has never been calibrated.
        /// </summary>
        public static KillFeedProfile? Load(string gameName)
        {
            string slug = ToSlug(gameName);

            foreach (string folder in new[] { UserProfileFolder, ShippedProfileFolder })
            {
                string path = PathUtils.Combine(folder, $"{slug}.json");
                if (!File.Exists(path))
                    continue;

                try
                {
                    var profile = JsonSerializer.Deserialize<KillFeedProfile>(
                        File.ReadAllText(path), SerializerOptions);

                    // The region is what makes a profile worth anything; a zero-sized one cannot be
                    // scanned. An empty player name is NOT a defect — shipped profiles deliberately
                    // omit it, since it is the one field that cannot transfer between people. Such
                    // a profile is a region-only starting point, and the caller asks for the name.
                    if (profile is null || profile.RegionWidth <= 0 || profile.RegionHeight <= 0)
                    {
                        Log.Warning($"[KillFeedProfileStore] Ignoring incomplete profile {PathUtils.Normalize(path)}");
                        continue;
                    }

                    Log.Information($"[KillFeedProfileStore] Loaded profile for '{gameName}' from {PathUtils.Normalize(path)}");
                    return profile;
                }
                catch (Exception ex)
                {
                    Log.Warning($"[KillFeedProfileStore] Could not read {PathUtils.Normalize(path)}: {ex.Message}");
                }
            }

            return null;
        }

        /// <summary>
        /// Writes the user's calibration for a game. Returns the file path, so the UI can point at
        /// something the user can actually open and send to someone else.
        /// </summary>
        public static string? Save(string gameName, KillFeedProfile profile)
        {
            try
            {
                Directory.CreateDirectory(UserProfileFolder);

                string path = PathUtils.Combine(UserProfileFolder, $"{ToSlug(gameName)}.json");

                // The game name is not derivable from the slug ("pubg-battlegrounds" is not
                // "PUBG: BATTLEGROUNDS"), and a shared file is worthless without it.
                profile.GameName = gameName;

                // Written to a temp file and moved into place, so a crash mid-write cannot leave a
                // truncated profile that would silently fail to load later.
                string tempPath = path + ".tmp";
                File.WriteAllText(tempPath, JsonSerializer.Serialize(profile, SerializerOptions));
                File.Move(tempPath, path, overwrite: true);

                Log.Information($"[KillFeedProfileStore] Saved profile for '{gameName}' to {PathUtils.Normalize(path)}");
                return path;
            }
            catch (Exception ex)
            {
                Log.Error($"[KillFeedProfileStore] Could not save profile for '{gameName}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Every game that has a profile, from either layer, deduplicated by slug with the user's
        /// own winning. Used to show which games are ready to scan without calibrating first.
        /// </summary>
        public static IReadOnlyList<string> ListCalibratedGames()
        {
            var bySlug = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Shipped first so the user's own overwrite them.
            foreach (string folder in new[] { ShippedProfileFolder, UserProfileFolder })
            {
                if (!Directory.Exists(folder))
                    continue;

                foreach (string path in Directory.EnumerateFiles(folder, "*.json"))
                {
                    string slug = Path.GetFileNameWithoutExtension(path);
                    try
                    {
                        var profile = JsonSerializer.Deserialize<KillFeedProfile>(
                            File.ReadAllText(path), SerializerOptions);

                        if (!string.IsNullOrWhiteSpace(profile?.GameName))
                            bySlug[slug] = profile.GameName;
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"[KillFeedProfileStore] Could not read {PathUtils.Normalize(path)}: {ex.Message}");
                    }
                }
            }

            return bySlug.Values.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
