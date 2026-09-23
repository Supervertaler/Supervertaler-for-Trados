using System;
using System.Threading.Tasks;
using System.Windows.Forms;
using Supervertaler.Core;

namespace Supervertaler.Trados.Licensing
{
    /// <summary>
    /// The Trados plugin's view of the Supervertaler licence.
    ///
    /// The licence itself - trial, activation, validation, storage - lives in
    /// core's <see cref="SupervertalerLicence"/>, shared with Supervertaler for
    /// memoQ: one licence per computer, whichever products are on it. This
    /// class keeps the plugin's own vocabulary (<see cref="LicenseTier"/>, the
    /// feature gates) and its UI, which core deliberately has none of.
    /// </summary>
    public sealed class LicenseManager
    {
        // ─── Singleton ──────────────────────────────────────────────

        private static readonly Lazy<LicenseManager> _lazy =
            new Lazy<LicenseManager>(() => new LicenseManager());

        public static LicenseManager Instance => _lazy.Value;

        private readonly SupervertalerLicence _licence;

        // ─── Events ─────────────────────────────────────────────────

        /// <summary>
        /// Fired when the license state changes (activation, deactivation, validation result).
        /// UI subscribes to this to show/hide features without restarting Trados.
        /// Raised on whichever thread made the change.
        /// </summary>
        public event EventHandler LicenseStateChanged;

        // ─── Constructor ────────────────────────────────────────────

        private LicenseManager()
        {
            // Before the first touch of Instance, which reads the licence and
            // may already have something to report.
            SupervertalerLicence.Log = Core.BridgeLog.Write;

            _licence = SupervertalerLicence.Instance;
            _licence.StateChanged += (s, e) => LicenseStateChanged?.Invoke(this, EventArgs.Empty);

            // At every start until a key is activated, not only the first: the
            // session after the damage is the one that can lock a paying
            // customer out, and it would otherwise say nothing about why.
            if (_licence.DamagedFileFound && _licence.State != LicenceState.Licensed)
                ShowDamagedFileMessage(thisSession: _licence.State == LicenceState.Unknown);
        }

        // ─── Public properties ──────────────────────────────────────

        /// <summary>The current effective license tier.</summary>
        public LicenseTier CurrentTier
        {
            get
            {
                switch (_licence.State)
                {
                    case LicenceState.Licensed: return LicenseTier.Licensed;
                    case LicenceState.Trial: return LicenseTier.Trial;
                    case LicenceState.Unknown: return LicenseTier.Unknown;
                    default: return LicenseTier.None;
                }
            }
        }

        /// <summary>
        /// True unless the licence is known to have lapsed – all features unlocked.
        /// An unreadable licence counts: it is never a refusal.
        /// </summary>
        public bool IsLicensed => CurrentTier != LicenseTier.None;

        /// <summary>Backward-compatible alias for IsLicensed. All paid tiers now grant full access.</summary>
        public bool HasTier1Access => IsLicensed;

        /// <summary>Backward-compatible alias for IsLicensed. All paid tiers now grant full access.</summary>
        public bool HasAssistantAccess => IsLicensed;

        /// <summary>Days remaining in the trial (0 when not on trial).</summary>
        public int TrialDaysRemaining => _licence.TrialDaysRemaining;

        /// <summary>The variant name from Lemon Squeezy for display (legacy – all variants now grant full access).</summary>
        public string VariantName => _licence.VariantName;

        /// <summary>Whether a license key has been entered.</summary>
        public bool HasLicenseKey => _licence.HasKey;

        /// <summary>The masked license key for display (first 8 + last 4 characters).</summary>
        public string MaskedLicenseKey => _licence.MaskedKey;

        /// <summary>Last successful validation time (UTC).</summary>
        public DateTime LastValidatedAt => _licence.LastValidatedUtc;

        // ─── Initialization ─────────────────────────────────────────

        /// <summary>
        /// Called from AppInitializer.Execute(). The licence is already loaded
        /// (instant); this fires a background validation if the computer is
        /// activated. Never blocks Trados startup.
        /// </summary>
        public void InitializeAsync()
        {
            if (_licence.IsActivated)
            {
                Task.Run(() => _licence.ValidateOnlineAsync());
            }
            else
            {
                // Trial install: register with the licence server (issue #47).
                // Observe-only in this release – the server records the
                // authoritative start date; local trial behaviour is unchanged
                // and the call fails silently when offline.
                var trialStart = _licence.TrialStartedUtc;
                var trialActive = _licence.State == LicenceState.Trial;
                Task.Run(() => TrialRegistration.RegisterAsync(trialStart, trialActive));
            }
        }

        // ─── Activation, deactivation, validation ───────────────────

        /// <summary>Activates a license key on this machine. Returns (success, message).</summary>
        public Task<(bool Success, string Message)> ActivateAsync(string licenseKey) =>
            _licence.ActivateAsync(licenseKey);

        /// <summary>Deactivates the license on this machine, freeing the activation slot.</summary>
        public Task<(bool Success, string Message)> DeactivateAsync() =>
            _licence.DeactivateAsync();

        /// <summary>Validates the license online. Startup (background) and the panel's "Verify Now".</summary>
        public Task<(bool Success, string Message)> ValidateOnlineAsync() =>
            _licence.ValidateOnlineAsync();

        // ─── Static UI helpers ──────────────────────────────────────

        /// <summary>
        /// Shows a MessageBox informing the user that a license is required.
        /// </summary>
        public static void ShowLicenseRequiredMessage()
        {
            MessageBox.Show(
                "Your trial has expired. Please enter a licence key in Settings → Licence to continue using Supervertaler for Trados.\n\n" +
                "Visit supervertaler.com/trados/ for pricing and purchase options.\n\n" +
                "Cost shouldn’t be a barrier: if the price is a problem for you, get in touch via beijer.uk/contact and we’ll work something out.",
                "Licence Required",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        /// <summary>
        /// Backward-compatible alias for ShowLicenseRequiredMessage.
        /// The single-tier model has no upgrade concept.
        /// </summary>
        public static void ShowUpgradeMessage() => ShowLicenseRequiredMessage();

        private static void ShowDamagedFileMessage(bool thisSession)
        {
            try
            {
                MessageBox.Show(
                    (thisSession
                        ? "Your Supervertaler licence file is damaged and could not be read, so it has been reset.\n\n" +
                          "If you have a licence key, please re-enter it in Settings → Licence. " +
                          "Everything stays available until you next start Trados Studio."
                        : "Your Supervertaler licence file was found damaged and has been reset.\n\n" +
                          "If you have a licence key, please re-enter it in Settings → Licence to restore access."),
                    "Supervertaler – Licence File Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            catch
            {
                // UI not available yet – the log has it.
            }
        }
    }
}
