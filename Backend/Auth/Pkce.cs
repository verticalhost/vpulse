using System.Security.Cryptography;
using System.Text;

namespace VPULSE.Backend.Auth
{
    // RFC 7636. In a public-client flow the verifier is the only secret: it is what stops a
    // process that intercepts the authorization code from redeeming it. It must stay in this
    // process and must never reach a log, the frontend, or disk.
    internal sealed record PkcePair(string Verifier, string Challenge)
    {
        public const string Method = "S256";
    }

    internal static class Pkce
    {
        // RFC 7636 4.1: the verifier is 43-128 characters of unreserved ASCII.
        // 32 random bytes encode to 43 base64url characters, so this is the shortest
        // legal verifier that still carries a full 256 bits.
        private const int VerifierEntropyBytes = 32;

        public static PkcePair Create()
        {
            string verifier = Base64Url(RandomNumberGenerator.GetBytes(VerifierEntropyBytes));

            // RFC 7636 4.2: hash the ASCII bytes of the *encoded verifier string*, not the raw
            // random bytes it was built from. Hashing the raw bytes yields a challenge the
            // server can never reproduce, and the only symptom is an opaque invalid_grant.
            byte[] hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));

            return new PkcePair(verifier, Base64Url(hash));
        }

        public static string NewState() => Base64Url(RandomNumberGenerator.GetBytes(32));

        // Base64url without padding (RFC 4648 5), which is what RFC 7636 requires.
        internal static string Base64Url(ReadOnlySpan<byte> bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        // Exposed so the challenge derivation can be checked against the RFC 7636 Appendix B
        // vector; Create() generates its own verifier and so cannot be tested directly.
        internal static string ChallengeFor(string verifier) =>
            Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    }
}
