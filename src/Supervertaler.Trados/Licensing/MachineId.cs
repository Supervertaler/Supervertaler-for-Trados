using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Supervertaler.Trados.Licensing
{
    /// <summary>
    /// Generates a stable machine fingerprint used to bind license activations
    /// to a specific computer. The fingerprint is a SHA256 hash of:
    ///   - Machine name
    ///   - Windows user SID
    ///   - System drive volume serial number
    /// </summary>
    internal static class MachineId
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool GetVolumeInformation(
            string rootPathName,
            StringBuilder volumeNameBuffer, int volumeNameSize,
            out uint volumeSerialNumber,
            out uint maximumComponentLength,
            out uint fileSystemFlags,
            StringBuilder fileSystemNameBuffer, int fileSystemNameSize);

        /// <summary>
        /// Returns a stable SHA256 fingerprint string for this machine + user combination.
        /// </summary>
        public static string GetFingerprint()
        {
            try
            {
                var sb = new StringBuilder();

                // 1. Machine name
                sb.Append(Environment.MachineName ?? "");
                sb.Append("|");

                // 2. Windows user SID (stable across renames)
                try
                {
                    var identity = WindowsIdentity.GetCurrent();
                    sb.Append(identity?.User?.Value ?? "");
                }
                catch
                {
                    sb.Append("no-sid");
                }
                sb.Append("|");

                // 3. System drive volume serial number
                try
                {
                    var systemDrive = Environment.GetFolderPath(Environment.SpecialFolder.System);
                    var root = System.IO.Path.GetPathRoot(systemDrive) ?? @"C:\";

                    if (GetVolumeInformation(root, null, 0, out uint serial, out _, out _, null, 0))
                    {
                        sb.Append(serial.ToString("X8"));
                    }
                    else
                    {
                        sb.Append("no-vol");
                    }
                }
                catch
                {
                    sb.Append("no-vol");
                }

                // SHA256 hash
                using (var sha = SHA256.Create())
                {
                    var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                    var hex = new StringBuilder(64);
                    foreach (var b in bytes)
                        hex.Append(b.ToString("x2"));
                    return hex.ToString();
                }
            }
            catch
            {
                // Absolute fallback — still deterministic per machine name
                return "fallback-" + (Environment.MachineName ?? "unknown").GetHashCode().ToString("x8");
            }
        }
    }
}
