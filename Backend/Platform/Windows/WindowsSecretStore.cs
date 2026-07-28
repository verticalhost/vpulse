using Serilog;
using System.Runtime.InteropServices;
using System.Text;

namespace VPULSE.Backend.Platform.Windows
{
    /// <summary>DPAPI, scoped to the current Windows user.</summary>
    /// <remarks>
    /// Calls crypt32 directly instead of System.Security.Cryptography.ProtectedData: on .NET 10 that
    /// wrapper compiles but throws "the specified procedure could not be found" at runtime, which
    /// would only have surfaced the first time a user signed in. The native calls below are verified
    /// working on the same machine.
    /// </remarks>
    internal sealed class WindowsSecretStore : ISecretStore
    {
        public bool IsOsBacked => true;

        // Optional entropy ties the blob to VPULSE, so another app running as the same user cannot
        // unprotect it by accident. It is compiled into a public GPLv2 binary and is therefore not
        // a secret: it prevents accidental cross-app decryption, not a deliberate local attacker.
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("VPULSE.Credentials.v1");

        // Never prompt: this runs on background threads with no window to parent a dialog to.
        private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

        public byte[]? Protect(byte[] plaintext) => Transform(plaintext, protect: true);

        public byte[]? Unprotect(byte[] ciphertext) => Transform(ciphertext, protect: false);

        private static byte[]? Transform(byte[] input, bool protect)
        {
            GCHandle inputPin = default, entropyPin = default;
            var output = new DATA_BLOB();

            try
            {
                inputPin = GCHandle.Alloc(input, GCHandleType.Pinned);
                entropyPin = GCHandle.Alloc(Entropy, GCHandleType.Pinned);

                var inputBlob = new DATA_BLOB { cbData = input.Length, pbData = inputPin.AddrOfPinnedObject() };
                var entropyBlob = new DATA_BLOB { cbData = Entropy.Length, pbData = entropyPin.AddrOfPinnedObject() };

                bool ok = protect
                    ? CryptProtectData(ref inputBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out output)
                    : CryptUnprotectData(ref inputBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out output);

                if (!ok)
                {
                    // Unprotect failing is expected for a blob from another user or machine, so the
                    // caller decides how loud to be; just record why.
                    Log.Debug("DPAPI {Op} failed with Win32 error {Error}",
                        protect ? "protect" : "unprotect", Marshal.GetLastWin32Error());
                    return null;
                }

                byte[] result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return result;
            }
            catch (Exception ex)
            {
                Log.Error("DPAPI call failed: {Type}", ex.GetType().Name);
                return null;
            }
            finally
            {
                if (output.pbData != IntPtr.Zero)
                    LocalFree(output.pbData);
                if (inputPin.IsAllocated)
                    inputPin.Free();
                if (entropyPin.IsAllocated)
                    entropyPin.Free();
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DATA_BLOB
        {
            public int cbData;
            public IntPtr pbData;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr,
            ref DATA_BLOB pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags,
            out DATA_BLOB pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr,
            ref DATA_BLOB pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags,
            out DATA_BLOB pDataOut);

        // CryptProtectData/CryptUnprotectData allocate their output with LocalAlloc.
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr hMem);
    }
}
