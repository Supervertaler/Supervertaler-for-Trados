using System;
using System.Security.Cryptography;
using System.Text;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Small helper for encrypting secrets at rest with Windows DPAPI
    /// (CurrentUser scope). Used for the GroupShare server password stored in
    /// settings.json (issue #35) so it is never written in clear text. The
    /// ciphertext is bound to the current Windows user, so it can only be
    /// decrypted by the same user on the same machine.
    /// </summary>
    public static class DpapiSecret
    {
        /// <summary>Encrypts <paramref name="plain"/> and returns base64, or "" on empty/failure.</summary>
        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            try
            {
                var enc = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(enc);
            }
            catch { return ""; }
        }

        /// <summary>Decrypts a base64 value produced by <see cref="Protect"/>, or "" on failure.</summary>
        public static string Unprotect(string protectedBase64)
        {
            if (string.IsNullOrEmpty(protectedBase64)) return "";
            try
            {
                var enc = Convert.FromBase64String(protectedBase64);
                var dec = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(dec);
            }
            catch { return ""; }
        }
    }
}
