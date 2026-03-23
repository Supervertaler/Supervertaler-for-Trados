using System;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Global UI scale factor for Supervertaler controls.
    /// Multiplies font sizes and key pixel dimensions so the entire UI
    /// can be scaled independently of Windows DPI settings.
    /// Set once at plugin startup from <see cref="Settings.TermLensSettings.UiScaleFactor"/>.
    /// </summary>
    public static class UiScale
    {
        /// <summary>
        /// Current scale factor. 1.0 = 100% (default), 1.25 = 125%, etc.
        /// </summary>
        public static float Factor { get; set; } = 1.0f;

        /// <summary>
        /// Returns a font size scaled by the current factor.
        /// </summary>
        public static float FontSize(float baseSize) => baseSize * Factor;

        /// <summary>
        /// Returns a pixel dimension scaled by the current factor, rounded to nearest int.
        /// </summary>
        public static int Pixels(int basePixels) => (int)Math.Round(basePixels * Factor);
    }
}
