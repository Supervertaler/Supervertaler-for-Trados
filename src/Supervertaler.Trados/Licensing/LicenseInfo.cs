using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados.Licensing
{
    /// <summary>
    /// Persisted license state, stored in the shared Supervertaler data folder
    /// under trados/license.json (resolved via UserDataPath).
    /// Separate from settings.json – license state has a different lifecycle than user preferences.
    /// </summary>
    [DataContract]
    public class LicenseInfo
    {
        private static string StorageDir  => UserDataPath.TradosDir;
        private static string LicenseFile => UserDataPath.LicenseFilePath;

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
        /// Variant name from Lemon Squeezy (meta.variant_name). With the
        /// single-product layout introduced in v4.18.48 (and the product
        /// rename to "Supervertaler for Trados" in v4.20.23), this field
        /// is captured for display in the License panel — it tells the
        /// user what they bought — but no longer drives feature gating.
        /// Any valid key returns <see cref="LicenseTier.Licensed"/>; see
        /// <c>LicenseManager.MapVariantToTier</c>. Old multi-tier license
        /// cache files ("TermLens", "TermLens + Supervertaler Assistant",
        /// "Supervertaler Assistant") still deserialise cleanly into this
        /// field and unlock everything.
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

        // ─── Anti-rollback ──────────────────────────────────────────

        /// <summary>
        /// Effective "now" used for all trial-window maths. Set by
        /// <see cref="Load"/> from the registry anchor's high-water mark, so a
        /// system clock wound backwards cannot extend the trial. Not persisted –
        /// it is recomputed on every load. Defaults to the real clock so any
        /// code path that constructs a <see cref="LicenseInfo"/> directly still
        /// behaves correctly.
        /// </summary>
        public DateTime EffectiveNow { get; set; } = DateTime.UtcNow;

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
                return (EffectiveNow - TrialStartedAt).TotalDays < TrialDays;
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
                var remaining = TrialDays - (EffectiveNow - TrialStartedAt).TotalDays;
                return remaining > 0 ? (int)Math.Ceiling(remaining) : 0;
            }
        }

        /// <summary>
        /// Trial duration in days. Single source of truth – referenced by LicenseManager.
        /// </summary>
        internal const int TrialDays = 14;

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
                    // No license.json. Normally this is a genuine first launch,
                    // but it is also what we see after a re-install, a moved
                    // data folder, or a deleted file – all of which must NOT
                    // hand out a fresh 14-day trial. TrialAnchor.Reconcile
                    // returns the original start when this machine has seen the
                    // trial before, and only "now" for a true first run.
                    var fp = MachineId.GetFingerprint();
                    var anchor = TrialAnchor.Reconcile(fp, DateTime.UtcNow, DateTime.UtcNow);
                    var fresh = new LicenseInfo
                    {
                        TrialStartedAt = anchor.Start,
                        EffectiveNow = anchor.EffectiveNow,
                        MachineFingerprint = fp
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

                    // Reconcile against the registry anchor. This seeds the
                    // anchor for existing trial users on first run of an
                    // anchor-aware build, pulls TrialStartedAt back to the
                    // earliest known start if license.json was edited to a later
                    // date, and derives an EffectiveNow that ignores a system
                    // clock wound backwards. The clock can only move earlier,
                    // never reset, and the trial can only ever count forward.
                    if (info.TrialStartedAt != DateTime.MinValue)
                    {
                        var anchored = TrialAnchor.Reconcile(
                            MachineId.GetFingerprint(), info.TrialStartedAt, DateTime.UtcNow);
                        info.EffectiveNow = anchored.EffectiveNow;
                        if (anchored.Start < info.TrialStartedAt)
                        {
                            info.TrialStartedAt = anchored.Start;
                            info.Save();
                        }
                    }

                    return info;
                }
            }
            catch (Exception ex)
            {
                // File exists but is corrupt – warn the user instead of silently
                // resetting to trial (which would lock out a paid user).
                try
                {
                    System.Windows.Forms.MessageBox.Show(
                        "Your Supervertaler licence file appears to be corrupt and could not be read. " +
                        "Please re-enter your licence key in Settings \u2192 Licence to restore access.\n\n" +
                        "If you do not have a licence key, a new 14-day trial will start.\n\n" +
                        "Technical details: " + ex.Message,
                        "Supervertaler \u2013 Licence File Error",
                        System.Windows.Forms.MessageBoxButtons.OK,
                        System.Windows.Forms.MessageBoxIcon.Warning);
                }
                catch
                {
                    // UI not available yet – continue silently
                }

                // A corrupt license.json must not reset the trial either – fall
                // back to the registry anchor for the original start date.
                var fpFallback = MachineId.GetFingerprint();
                var anchorFallback = TrialAnchor.Reconcile(fpFallback, DateTime.UtcNow, DateTime.UtcNow);
                var fresh = new LicenseInfo
                {
                    TrialStartedAt = anchorFallback.Start,
                    EffectiveNow = anchorFallback.EffectiveNow,
                    MachineFingerprint = fpFallback
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
            catch (Exception ex)
            {
                // A silently-failing Save() is dangerous: if license.json never
                // persists, every launch re-seeds a fresh trial, so the trial
                // never counts down and the user gets unlimited free use without
                // anyone noticing (observed in the field on an older build). Log
                // it loudly. BridgeLog mirrors to %TEMP%, which stays writable
                // even when the data folder itself is the thing that's broken.
                try
                {
                    Core.BridgeLog.Write(
                        "LICENCE WARNING: failed to save license.json to '" + LicenseFile +
                        "'. The trial start may not persist across launches. " +
                        ex.GetType().Name + ": " + ex.Message);
                }
                catch
                {
                    // Logging must never throw out of Save().
                }
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
