using System.Text.Json.Serialization;

namespace VPULSE.Backend.Core.Models
{
    /// <summary>
    /// Where a game draws its kill feed, and who the player is in it. Calibrated once from a
    /// recording and reused for every later scan of the same game.
    ///
    /// Stored as one JSON file per game rather than inside settings.json — see KillFeedProfileStore.
    /// This is data worth sharing: a profile calibrated once works for anyone playing the same game.
    /// </summary>
    public class KillFeedProfile
    {
        // Carried in the file so a shared profile says what it is for. The file name is a slug and
        // cannot be turned back into the real name ("pubg-battlegrounds" is not "PUBG: BATTLEGROUNDS").
        [JsonPropertyName("gameName")]
        public string GameName { get; set; } = string.Empty;

        // Relative to the frame (0-1), so a profile calibrated on a 1080p recording still applies
        // to a 1440p one of the same game — and to someone else's monitor.
        [JsonPropertyName("regionX")]
        public double RegionX { get; set; }

        [JsonPropertyName("regionY")]
        public double RegionY { get; set; }

        [JsonPropertyName("regionWidth")]
        public double RegionWidth { get; set; }

        [JsonPropertyName("regionHeight")]
        public double RegionHeight { get; set; }

        // The player's in-game name. Its position within a feed row is what separates a kill from a
        // death, so this is required rather than optional.
        //
        // Note this is the one field that does NOT transfer between people: a shared profile carries
        // a usable region, and whoever imports it replaces the name with their own.
        [JsonPropertyName("playerName")]
        public string PlayerName { get; set; } = string.Empty;

        // Frames sampled per second of recording. One per second was the rate at which every known
        // kill in a test session was found; halving it missed two of three on sampling phase alone,
        // because a feed row is only legible for its first couple of seconds.
        [JsonPropertyName("scanFramesPerSecond")]
        public double ScanFramesPerSecond { get; set; } = 1.0;

        // Whether deaths are worth marking for this game, or only kills.
        [JsonPropertyName("includeDeaths")]
        public bool IncludeDeaths { get; set; } = true;
    }
}
