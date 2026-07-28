namespace VPULSE.Backend.Platform.Linux
{
    /// <summary>
    /// No encryption: the credentials file is protected by its 0600 mode alone, which
    /// <see cref="TokenStore"/> applies. <see cref="IsOsBacked"/> is false so the UI can say so.
    /// </summary>
    /// <remarks>
    /// Deliberately not a homegrown cipher. Any key this process could derive unaided would come
    /// from constants in a public repository, so "encrypting" with it would buy no security while
    /// telling the user they were protected. Real protection here means the Secret Service API
    /// (gnome-keyring / kwallet), which is worth adding but is a keyed store rather than a
    /// protector and does not fit this interface as-is.
    /// </remarks>
    internal sealed class LinuxSecretStore : ISecretStore
    {
        public bool IsOsBacked => false;

        public byte[]? Protect(byte[] plaintext) => plaintext;

        public byte[]? Unprotect(byte[] ciphertext) => ciphertext;
    }
}
