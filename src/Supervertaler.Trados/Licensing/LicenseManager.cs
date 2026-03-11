using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Supervertaler.Trados.Licensing
{
    /// <summary>
    /// Singleton managing all license state: trial, activation, validation, and caching.
    /// The single source of truth for whether features should be enabled.
    ///
    /// Uses the Lemon Squeezy License API:
    ///   - POST /v1/licenses/activate   — activate a key on this machine
    ///   - POST /v1/licenses/validate   — check if a key is still valid
    ///   - POST /v1/licenses/deactivate — release this machine's activation
    ///
    /// No auth header required — these endpoints only need the license key.
    /// </summary>
    public sealed class LicenseManager
    {
        // ─── Singleton ──────────────────────────────────────────────

        private static readonly Lazy<LicenseManager> _lazy =
            new Lazy<LicenseManager>(() => new LicenseManager());

        public static LicenseManager Instance => _lazy.Value;

        // ─── Constants ──────────────────────────────────────────────

        private const string BaseUrl = "https://api.lemonsqueezy.com/v1/licenses";
        private const int OfflineCacheDays = 30;
        private const int TrialDays = 90;

        /// <summary>
        /// Lemon Squeezy variant names — must match exactly what's configured
        /// in the Lemon Squeezy dashboard.
        /// </summary>
        private const string VariantTier1 = "TermLens";
        private const string VariantTier2 = "TermLens + Supervertaler Assistant";

        // ─── State ──────────────────────────────────────────────────

        private LicenseInfo _info;
        private readonly object _lock = new object();
        private static readonly HttpClient Http = new HttpClient();

        // ─── Events ─────────────────────────────────────────────────

        /// <summary>
        /// Fired when the license state changes (activation, deactivation, validation result).
        /// UI subscribes to this to show/hide features without restarting Trados.
        /// </summary>
        public event EventHandler LicenseStateChanged;

        // ─── Constructor ────────────────────────────────────────────

        private LicenseManager()
        {
            _info = LicenseInfo.Load();
        }

        // ─── Public properties ──────────────────────────────────────

        /// <summary>
        /// The current effective license tier.
        /// </summary>
        public LicenseTier CurrentTier
        {
            get
            {
                lock (_lock)
                {
                    return ResolveTier();
                }
            }
        }

        /// <summary>True if the user has at least Tier 1 (TermLens) access.</summary>
        public bool HasTier1Access
        {
            get
            {
                var tier = CurrentTier;
                return tier == LicenseTier.Trial
                    || tier == LicenseTier.Tier1
                    || tier == LicenseTier.Tier2;
            }
        }

        /// <summary>True if the user has Tier 2 (TermLens + Assistant) access.</summary>
        public bool HasTier2Access
        {
            get
            {
                var tier = CurrentTier;
                return tier == LicenseTier.Trial
                    || tier == LicenseTier.Tier2;
            }
        }

        /// <summary>Days remaining in the trial (0 if expired or licensed).</summary>
        public int TrialDaysRemaining => _info?.TrialDaysRemaining ?? 0;

        /// <summary>Whether the user is currently on a trial (no license key entered).</summary>
        public bool IsOnTrial => CurrentTier == LicenseTier.Trial;

        /// <summary>The variant name for display ("TermLens" or "TermLens + Supervertaler Assistant").</summary>
        public string VariantName => _info?.VariantName ?? "";

        /// <summary>The license status string ("active", "expired", etc.).</summary>
        public string Status => _info?.Status ?? "";

        /// <summary>Whether a license key has been entered.</summary>
        public bool HasLicenseKey => _info?.HasLicenseKey ?? false;

        /// <summary>The masked license key for display (first 8 + last 4 characters).</summary>
        public string MaskedLicenseKey
        {
            get
            {
                var key = _info?.LicenseKey ?? "";
                if (key.Length <= 12) return key;
                return key.Substring(0, 8) + "..." + key.Substring(key.Length - 4);
            }
        }

        /// <summary>Last successful validation time (UTC).</summary>
        public DateTime LastValidatedAt => _info?.LastValidatedAt ?? DateTime.MinValue;

        // ─── Initialization ─────────────────────────────────────────

        /// <summary>
        /// Called from AppInitializer.Execute(). Loads cached state (instant),
        /// then fires a background validation if a license key is present.
        /// Never blocks Trados startup.
        /// </summary>
        public void InitializeAsync()
        {
            // Already loaded in constructor. Kick off background validation.
            if (_info.IsActivated)
            {
                Task.Run(() => ValidateOnlineAsync());
            }
        }

        // ─── Activation ─────────────────────────────────────────────

        /// <summary>
        /// Activates a license key on this machine.
        /// Returns (success, errorMessage).
        /// </summary>
        public async Task<(bool Success, string Message)> ActivateAsync(string licenseKey)
        {
            if (string.IsNullOrWhiteSpace(licenseKey))
                return (false, "Please enter a license key.");

            try
            {
                var fingerprint = MachineId.GetFingerprint();

                var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("license_key", licenseKey.Trim()),
                    new KeyValuePair<string, string>("instance_name", fingerprint),
                });

                var response = await Http.PostAsync(BaseUrl + "/activate", content);
                var json = await response.Content.ReadAsStringAsync();
                var result = ParseLemonSqueezyResponse(json);

                if (result.Activated)
                {
                    lock (_lock)
                    {
                        _info.LicenseKey = licenseKey.Trim();
                        _info.InstanceId = result.InstanceId;
                        _info.VariantName = result.VariantName;
                        _info.Status = result.Status;
                        _info.ActivatedAt = DateTime.UtcNow;
                        _info.LastValidatedAt = DateTime.UtcNow;
                        _info.ExpiresAt = result.ExpiresAt;
                        _info.MachineFingerprint = fingerprint;
                        _info.Save();
                    }

                    OnLicenseStateChanged();
                    return (true, "License activated successfully.");
                }

                // Activation failed — return the error from Lemon Squeezy
                return (false, result.Error ?? "Activation failed. Please check your license key.");
            }
            catch (HttpRequestException ex)
            {
                return (false, "Could not reach the license server. Please check your internet connection.\n\n" + ex.Message);
            }
            catch (Exception ex)
            {
                return (false, "An error occurred during activation: " + ex.Message);
            }
        }

        // ─── Deactivation ───────────────────────────────────────────

        /// <summary>
        /// Deactivates the license on this machine, freeing up the activation slot.
        /// Returns (success, errorMessage).
        /// </summary>
        public async Task<(bool Success, string Message)> DeactivateAsync()
        {
            if (!_info.IsActivated)
                return (false, "No active license to deactivate.");

            try
            {
                var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("license_key", _info.LicenseKey),
                    new KeyValuePair<string, string>("instance_id", _info.InstanceId),
                });

                var response = await Http.PostAsync(BaseUrl + "/deactivate", content);
                // Even if the server call fails, we clear local state
            }
            catch
            {
                // Network error — still clear local state
            }

            lock (_lock)
            {
                _info.Reset();
            }

            OnLicenseStateChanged();
            return (true, "License deactivated. This machine's activation slot has been freed.");
        }

        // ─── Validation ─────────────────────────────────────────────

        /// <summary>
        /// Validates the license online. Called on startup (background) and
        /// can be called manually from the License panel ("Refresh" button).
        /// Returns (success, errorMessage).
        /// </summary>
        public async Task<(bool Success, string Message)> ValidateOnlineAsync()
        {
            if (!_info.IsActivated)
                return (false, "No active license to validate.");

            try
            {
                var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("license_key", _info.LicenseKey),
                    new KeyValuePair<string, string>("instance_id", _info.InstanceId),
                });

                var response = await Http.PostAsync(BaseUrl + "/validate", content);
                var json = await response.Content.ReadAsStringAsync();
                var result = ParseLemonSqueezyResponse(json);

                var previousTier = ResolveTier();

                lock (_lock)
                {
                    _info.Status = result.Status;
                    _info.VariantName = result.VariantName;
                    _info.ExpiresAt = result.ExpiresAt;
                    _info.LastValidatedAt = DateTime.UtcNow;
                    _info.Save();
                }

                var newTier = ResolveTier();
                if (newTier != previousTier)
                    OnLicenseStateChanged();

                if (result.Valid)
                    return (true, "License is valid.");
                else
                    return (false, result.Error ?? "License validation failed.");
            }
            catch (HttpRequestException)
            {
                // Network error — offline mode, trust cached state
                return (false, "Could not reach the license server. Using cached license state.");
            }
            catch (Exception ex)
            {
                return (false, "Validation error: " + ex.Message);
            }
        }

        // ─── Tier Resolution ────────────────────────────────────────

        private LicenseTier ResolveTier()
        {
            // 1. If we have an activated license with valid cached state
            if (_info.IsActivated && IsStatusActive())
            {
                // Check that the cached validation is still within the offline window
                if (IsCacheValid())
                    return MapVariantToTier(_info.VariantName);
            }

            // 2. If we have an activated license but the cache is stale
            //    (online validation hasn't succeeded in 30+ days)
            if (_info.IsActivated && !IsCacheValid())
                return LicenseTier.None;

            // 3. If no license key, check trial
            if (!_info.HasLicenseKey && _info.IsTrialActive)
                return LicenseTier.Trial;

            return LicenseTier.None;
        }

        private bool IsStatusActive()
        {
            return string.Equals(_info.Status, "active", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsCacheValid()
        {
            if (_info.LastValidatedAt == DateTime.MinValue)
                return false;

            return (DateTime.UtcNow - _info.LastValidatedAt).TotalDays < OfflineCacheDays;
        }

        private static LicenseTier MapVariantToTier(string variantName)
        {
            if (string.IsNullOrWhiteSpace(variantName))
                return LicenseTier.Tier1; // Default to Tier 1 if variant is unknown

            // Case-insensitive comparison
            if (variantName.IndexOf("Assistant", StringComparison.OrdinalIgnoreCase) >= 0)
                return LicenseTier.Tier2;

            return LicenseTier.Tier1;
        }

        // ─── Lemon Squeezy Response Parsing ─────────────────────────

        /// <summary>
        /// Parses the JSON response from Lemon Squeezy's License API.
        /// The response structure is:
        /// {
        ///   "valid": true/false,
        ///   "error": "...",
        ///   "license_key": { "status": "active", "expires_at": "..." },
        ///   "meta": { "variant_name": "..." },
        ///   "instance": { "id": "..." }
        /// }
        /// </summary>
        private static LemonSqueezyResult ParseLemonSqueezyResponse(string json)
        {
            var result = new LemonSqueezyResult();

            try
            {
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    var serializer = new DataContractJsonSerializer(typeof(LsResponse));
                    var response = (LsResponse)serializer.ReadObject(stream);

                    result.Valid = response.Valid;
                    result.Activated = response.Activated;
                    result.Error = response.Error;

                    if (response.LicenseKey != null)
                    {
                        result.Status = response.LicenseKey.Status ?? "";

                        if (!string.IsNullOrWhiteSpace(response.LicenseKey.ExpiresAt))
                        {
                            if (DateTime.TryParse(response.LicenseKey.ExpiresAt, null,
                                System.Globalization.DateTimeStyles.RoundtripKind, out var expires))
                            {
                                result.ExpiresAt = expires;
                            }
                        }
                    }

                    if (response.Meta != null)
                    {
                        result.VariantName = response.Meta.VariantName ?? "";
                    }

                    if (response.Instance != null)
                    {
                        result.InstanceId = response.Instance.Id ?? "";
                    }
                }
            }
            catch
            {
                result.Error = "Failed to parse license server response.";
            }

            return result;
        }

        private void OnLicenseStateChanged()
        {
            LicenseStateChanged?.Invoke(this, EventArgs.Empty);
        }

        // ─── Static UI helpers ──────────────────────────────────────

        /// <summary>
        /// Shows a MessageBox informing the user that a license is required.
        /// </summary>
        public static void ShowLicenseRequiredMessage()
        {
            MessageBox.Show(
                "Your trial has expired. Please enter a license key in Settings \u2192 License to continue using Supervertaler for Trados.\n\n" +
                "Visit supervertaler.com/trados/ for pricing and purchase options.",
                "License Required",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        /// <summary>
        /// Shows a MessageBox informing the user that an upgrade is required for AI features.
        /// </summary>
        public static void ShowUpgradeMessage()
        {
            MessageBox.Show(
                "The Supervertaler Assistant requires a \"TermLens + Supervertaler Assistant\" license.\n\n" +
                "You can upgrade your subscription at supervertaler.com/trados/ or in Settings \u2192 License.",
                "Upgrade Required",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        // ─── Lemon Squeezy response DTOs ────────────────────────────

        private class LemonSqueezyResult
        {
            public bool Valid;
            public bool Activated;
            public string Error;
            public string Status;
            public string VariantName;
            public string InstanceId;
            public DateTime? ExpiresAt;
        }

        [DataContract]
        private class LsResponse
        {
            [DataMember(Name = "valid")]
            public bool Valid { get; set; }

            [DataMember(Name = "activated")]
            public bool Activated { get; set; }

            [DataMember(Name = "error")]
            public string Error { get; set; }

            [DataMember(Name = "license_key")]
            public LsLicenseKey LicenseKey { get; set; }

            [DataMember(Name = "meta")]
            public LsMeta Meta { get; set; }

            [DataMember(Name = "instance")]
            public LsInstance Instance { get; set; }
        }

        [DataContract]
        private class LsLicenseKey
        {
            [DataMember(Name = "status")]
            public string Status { get; set; }

            [DataMember(Name = "expires_at")]
            public string ExpiresAt { get; set; }
        }

        [DataContract]
        private class LsMeta
        {
            [DataMember(Name = "variant_name")]
            public string VariantName { get; set; }
        }

        [DataContract]
        private class LsInstance
        {
            [DataMember(Name = "id")]
            public string Id { get; set; }
        }
    }
}
