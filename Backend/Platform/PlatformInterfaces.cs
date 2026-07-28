using VPULSE.Backend.Core.Models;

namespace VPULSE.Backend.Platform
{
    /// <summary>
    /// A disposable watcher that raises <see cref="Changed"/> when the watched system
    /// resource (audio devices, displays) changes.
    /// </summary>
    internal interface IPlatformWatcher : IDisposable
    {
        event Action Changed;
    }

    /// <summary>System tray / notification-area icon. No-op on platforms without a tray.</summary>
    internal interface ITrayIcon
    {
        void Initialize(Action onOpen, Action onExit);
        void SetRecording(bool recording);
    }

    /// <summary>Enumerates audio input/output devices and watches for device changes.</summary>
    internal interface IAudioDeviceService
    {
        List<AudioDevice> GetInputDevices();
        List<AudioDevice> GetOutputDevices();
        IPlatformWatcher CreateWatcher();
    }

    /// <summary>Enumerates displays and watches for display changes.</summary>
    internal interface IDisplayService
    {
        bool LoadAvailableMonitorsIntoState();
        bool GetPrimaryMonitorPhysicalResolution(out uint width, out uint height);
        bool HasDisplayWithMinHeight(int minHeight);
        IPlatformWatcher CreateWatcher();
    }

    /// <summary>Native file/folder pickers and shell integration (open location, open URL, clipboard).</summary>
    internal interface INativeDialogs
    {
        Task<string?> PickFolderAsync(string description);
        Task<string?> PickFileAsync(string title, string filterDescription, string extension);
        Task<string[]?> PickFilesAsync(string title, string filterDescription, string extension);
        void OpenFileLocation(string filePath);
        void OpenUrl(string url);
        void CopyFileToClipboard(string filePath);
    }

    /// <summary>Manages launch-on-startup registration.</summary>
    internal interface IStartupManager
    {
        void SetStartupStatus(bool enable);
        bool GetStartupStatus();
    }

    /// <summary>Plays a short WAV sound effect (from raw bytes) at the given volume.</summary>
    internal interface ISoundPlayer
    {
        void Play(byte[] wavData, float volume);
    }

    /// <summary>
    /// Encrypts small secrets (OAuth tokens) at rest, bound to the signed-in OS user.
    /// Protects against another user on the machine and against the file being copied off it;
    /// it does not protect against code already running as this user.
    /// </summary>
    internal interface ISecretStore
    {
        /// <summary>False when the platform has no key store and only file permissions apply.</summary>
        bool IsOsBacked { get; }

        /// <summary>Returns null when protection is unavailable, so callers can refuse to persist.</summary>
        byte[]? Protect(byte[] plaintext);

        /// <summary>Returns null for a blob written by another user, another machine, or a corrupt file.</summary>
        byte[]? Unprotect(byte[] ciphertext);
    }
}
