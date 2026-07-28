namespace VPULSE.Backend.Auth
{
    /// <summary>
    /// The single place that answers "is this allowed on the current plan".
    /// </summary>
    /// <remarks>
    /// These are honour gates, not security boundaries. VPULSE is GPLv2 and its source is public,
    /// so recording quality, replay buffer length and highlight generation are all local work that
    /// anyone can restore by deleting a condition and rebuilding. They exist to make the paid tier
    /// legible, not to enforce it. The only genuinely enforceable gates are server-side, on the
    /// APIs VPULSE calls. Do not build a security assumption on anything in this file.
    ///
    /// VPZONE reports membership as a single boolean rather than a capability list, so the named
    /// capabilities below all resolve to it today. Keeping them named means introducing a second
    /// tier later is a change here rather than at every call site.
    /// </remarks>
    internal static class FeatureGate
    {
        public const string CapAiHighlights = "ai_highlights";
        public const string CapUnlimitedQuality = "quality_unlimited";
        public const string CapExtendedReplayBuffer = "replay_buffer_extended";

        // Free-tier ceilings. Applied when settings are resolved, never written back to them.
        public const string FreeMaxResolution = "1080p";
        public const int FreeMaxFrameRate = 60;
        public const int FreeMaxReplayBufferSeconds = 30;

        public static bool Allows(string capability) => EntitlementService.IsActive;

        public static string[] ActiveCapabilities() => EntitlementService.IsActive
            ? [CapAiHighlights, CapUnlimitedQuality, CapExtendedReplayBuffer]
            : [];
    }
}
