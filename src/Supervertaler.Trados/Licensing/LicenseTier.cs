using System;

namespace Supervertaler.Trados.Licensing
{
    /// <summary>
    /// Represents the active license state.
    /// Since v4.18.48, all paid licenses grant full access (single-tier model).
    /// </summary>
    public enum LicenseTier
    {
        /// <summary>No valid license – trial expired, subscription lapsed.</summary>
        None = 0,

        /// <summary>14-day free trial – grants full access.</summary>
        Trial = 1,

        /// <summary>Any active paid license – all features unlocked.</summary>
        Licensed = 2,

        // ── Legacy tiers (kept for backward compatibility with cached license.json) ──

        /// <summary>Obsolete. Previously: TermLens only. Now treated as Licensed.</summary>
        [Obsolete("Use Licensed. Single-tier model since v4.18.48.")]
        Tier1 = 3,

        /// <summary>Obsolete. Previously: TermLens + Assistant bundle. Now treated as Licensed.</summary>
        [Obsolete("Use Licensed. Single-tier model since v4.18.48.")]
        Tier2 = 4,

        /// <summary>Obsolete. Previously: Assistant only. Now treated as Licensed.</summary>
        [Obsolete("Use Licensed. Single-tier model since v4.18.48.")]
        AssistantOnly = 5,

        /// <summary>
        /// The licence could not be read this session. Never a refusal: every
        /// feature stays available, because a paying customer must not be
        /// locked out by an absence of information.
        /// </summary>
        Unknown = 6,
    }
}
