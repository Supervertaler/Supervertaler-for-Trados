namespace Supervertaler.Trados.Licensing
{
    /// <summary>
    /// Represents the active license tier.
    /// Ordered so that higher tiers have higher numeric values.
    /// </summary>
    public enum LicenseTier
    {
        /// <summary>No valid license — trial expired, subscription lapsed.</summary>
        None = 0,

        /// <summary>14-day free trial — grants full Tier 2 access.</summary>
        Trial = 1,

        /// <summary>TermLens only — terminology features.</summary>
        Tier1 = 2,

        /// <summary>TermLens + Supervertaler Assistant — all features.</summary>
        Tier2 = 3,
    }
}
