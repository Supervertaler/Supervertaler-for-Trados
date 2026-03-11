using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace Supervertaler.Trados.Licensing
{
    /// <summary>
    /// Persisted license state, stored at %LocalAppData%\Supervertaler.Trados\license.json.
    /// Separate from settings.json — license state has a different lifecycle than user preferences.
    /// </summary>
    [DataContract]
    public class LicenseInfo
    {
        private static readonly string StorageDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Supervertaler.Trados");

        private static readonly string LicenseFile = Path.Combine(StorageDir, "license.json");

        // ─── License key fields ─────────────────────────────────────

        /// <summary>
        /// The Lemon Squeezy license key entered by the user.
        /// </summary>
        [DataMember(Name = "licenseKey")]
        public string LicenseKey { get; set; } = "";

        /// <summary>
        /// Instance ID returned by the /activate endpoint.
        /// Ties this machine activation to the license key.
        /// </summary>
        [DataMember(Name = "instanceId")]
        public string InstanceId { get; set; } = "";

        /// <summary>
        /// Variant name from Lemon Squeezy (meta.variant_name).
        /// Maps to a <see cref="LicenseTier"/>: "TermLens" → Tier1, "TermLens + Supervertaler Assistant" → Tier2.
        /// </summary>
        [DataMember(Name = "variantName")]
        public string VariantName { get; set; } = "";

        // ─── Timestamps ─────────────────────────────────────────────

        /// <summary>
        /// When the license was first activated on this machine (UTC).
        /// </summary>
        [DataMember(Name = "activatedAt")]
        public DateTime ActivatedAt { get; set; } = DateTime.MinValue;

        /// <summary>
        /// Last successful online validation timestamp (UTC).
        /// Used for the 30-day offline cache window.
        /// </summary>
        [DataMember(Name = "lastValidatedAt")]
        public DateTime LastValidatedAt { get; set; } = DateTime.MinValue;

        /// <summary>
        /// Subscription expiry date from Lemon Squeezy (license_key.expires_at).
        /// Null if no expiry is set (lifetime or not yet activated).
        /// </summary>
        [DataMember(Name = "expiresAt")]
        public DateTime? ExpiresAt { get; set; }

        /// <summary>
        /// License status from Lemon Squeezy: "active", "expired", "disabled".
        /// </summary>
        [DataMember(Name = "status")]
        public string Status { get; set; } = "";

        // ─── Trial ──────────────────────────────────────────────────

        /// <summary>
        /// When the 14-day trial started (UTC). Set on first-ever plugin launch.
        /// DateTime.MinValue means the trial hasn't started yet (shouldn't happen in practice).
        /// </summary>
        [DataMember(Name = "trialStartedAt")]
        public DateTime TrialStartedAt { get; set; } = DateTime.MinValue;

        // ─── Machine binding ────────────────────────────────────────

        /// <summary>
        /// SHA256 hash of machine-specific identifiers. Used as instance_name on activation
        /// and to detect if license.json was copied to another machine.
        /// </summary>
        [DataMember(Name = "machineFingerprint")]
        public string MachineFingerprint { get; set; } = "";

        // ─── Helpers ────────────────────────────────────────────────

        /// <summary>
        /// Whether a license key has been entered (not necessarily activated or valid).
        /// </summary>
        public bool HasLicenseKey => !string.IsNullOrWhiteSpace(LicenseKey);

        /// <summary>
        /// Whether this machine has been activated (has an instance ID).
        /// </summary>
        public bool IsActivated => !string.IsNullOrWhiteSpace(InstanceId);

        /// <summary>
        /// Whether the trial is still within the 14-day window.
        /// </summary>
        public bool IsTrialActive
        {
            get
            {
                if (TrialStartedAt == DateTime.MinValue) return false;
                return (DateTime.UtcNow - TrialStartedAt).TotalDays < 14;
            }
        }

        /// <summary>
        /// Days remaining in the trial period (0 if expired or not started).
        /// </summary>
        public int TrialDaysRemaining
        {
            get
            {
                if (TrialStartedAt == DateTime.MinValue) return 0;
                var remaining = 14 - (DateTime.UtcNow - TrialStartedAt).TotalDays;
                return remaining > 0 ? (int)Math.Ceiling(remaining) : 0;
            }
        }

        // ─── Persistence ────────────────────────────────────────────

        /// <summary>
        /// Loads license info from disk. Returns a new trial-initialized instance
        /// if the file doesn't exist yet (first-ever launch).
        /// </summary>
        public static LicenseInfo Load()
        {
            try
            {
                if (!File.Exists(LicenseFile))
                {
                    // First launch — start the trial
                    var fresh = new LicenseInfo
                    {
                        TrialStartedAt = DateTime.UtcNow,
                        MachineFingerprint = MachineId.GetFingerprint()
                    };
                    fresh.Save();
                    return fresh;
                }

                var json = File.ReadAllText(LicenseFile, Encoding.UTF8);
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    var serializer = new DataContractJsonSerializer(typeof(LicenseInfo));
                    var info = (LicenseInfo)serializer.ReadObject(stream);

                    // Anti-tamper: if TrialStartedAt is in the future, snap to now
                    if (info.TrialStartedAt > DateTime.UtcNow)
                        info.TrialStartedAt = DateTime.UtcNow;

                    // Ensure fingerprint is set (migration from older license.json)
                    if (string.IsNullOrWhiteSpace(info.MachineFingerprint))
                        info.MachineFingerprint = MachineId.GetFingerprint();

                    return info;
                }
            }
            catch
            {
                // Corrupted file — start fresh with a new trial
                var fresh = new LicenseInfo
                {
                    TrialStartedAt = DateTime.UtcNow,
                    MachineFingerprint = MachineId.GetFingerprint()
                };
                fresh.Save();
                return fresh;
            }
        }

        /// <summary>
        /// Saves license info to disk.
        /// </summary>
        public void Save()
        {
            try
            {
                Directory.CreateDirectory(StorageDir);

                using (var stream = new MemoryStream())
                {
                    var settings = new DataContractJsonSerializerSettings
                    {
                        UseSimpleDictionaryFormat = true
                    };
                    var serializer = new DataContractJsonSerializer(typeof(LicenseInfo), settings);
                    serializer.WriteObject(stream, this);

                    var json = Encoding.UTF8.GetString(stream.ToArray());
                    File.WriteAllText(LicenseFile, json, Encoding.UTF8);
                }
            }
            catch
            {
                // Silently ignore save failures
            }
        }

        /// <summary>
        /// Resets the license info (deactivation). Preserves the trial start date.
        /// </summary>
        public void Reset()
        {
            var trialStart = TrialStartedAt;
            var fingerprint = MachineFingerprint;

            LicenseKey = "";
            InstanceId = "";
            VariantName = "";
            ActivatedAt = DateTime.MinValue;
            LastValidatedAt = DateTime.MinValue;
            ExpiresAt = null;
            Status = "";
            TrialStartedAt = trialStart;
            MachineFingerprint = fingerprint;

            Save();
        }
    }
}
