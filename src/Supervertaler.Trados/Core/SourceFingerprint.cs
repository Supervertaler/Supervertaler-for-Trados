using System;
using System.Security.Cryptography;
using System.Text;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// A short hash of a segment's source text, so a write can prove it is
    /// landing on the segment its translation was actually made from.
    ///
    /// Why this exists: update_segments addresses segments by POSITION
    /// ("puId:segId"), and a position is not an identity. Three ways that goes
    /// wrong, none of which the plugin could previously detect:
    ///
    ///   * The caller misaligns a long batch. Hand a model 200 segments and it
    ///     may return 200 perfectly good translations attached to ids shifted
    ///     by one. Every write succeeds; every one is in the wrong row.
    ///   * A stale conversation is resumed. Ids read from a document closed
    ///     yesterday still exist in the document open today, so yesterday's
    ///     translations land in today's file.
    ///   * The document moves under a long batch - a split, a merge, a
    ///     re-imported source file - and everything after it shifts.
    ///
    /// In all three the write reports success, which is the worst outcome
    /// available: the user reads "applied: 200, failed: 0" over a document that
    /// is now quietly wrong. Comparing the source at the addressed position
    /// against the source the caller actually read turns each of them into a
    /// refusal with a reason.
    ///
    /// What is hashed is the source string EXACTLY as get_segments emitted it -
    /// after tag serialisation and semantic naming - so both ends compare the
    /// same bytes without either having to re-derive the other's normalisation.
    /// Both paths call this method; that is the entire contract. It follows
    /// that a change confined to a segment's inline tags also invalidates the
    /// fingerprint, which is the safe direction to err in.
    ///
    /// Eight hex characters (32 bits). This is a pairwise comparison of one
    /// segment against one expectation, not a lookup across a corpus, so the
    /// birthday bound does not apply: a false match needs unrelated text to
    /// collide with this exact text, about 1 in 4 billion. It is not a security
    /// boundary - it defends against accident, not against an attacker - and it
    /// is short because every byte is multiplied by the segment count in a
    /// get_segments response the caller pays for on every subsequent turn (see
    /// <see cref="BridgePayloadLedger"/>).
    ///
    /// Scale: SHA-256 over a sentence is microseconds, and this allocates one
    /// short-lived hasher per call. A 10,000-segment document costs single-digit
    /// milliseconds in total, immaterial beside enumerating those segments
    /// through the Studio API in the first place. Per-call rather than a cached
    /// instance because the bridge dispatches concurrently and SHA256 instances
    /// are not thread-safe; a lock or a [ThreadStatic] would buy nothing
    /// measurable and cost the reader something real.
    /// </summary>
    internal static class SourceFingerprint
    {
        /// <summary>
        /// The fingerprint of a source string, or null when there is nothing to
        /// fingerprint. Null in, null out, so a segment with no source carries
        /// no 'fp' field at all rather than the fingerprint of "".
        /// </summary>
        public static string Of(string source)
        {
            if (string.IsNullOrEmpty(source)) return null;

            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(source));
                var hex = new StringBuilder(8);
                for (int i = 0; i < 4; i++) hex.Append(hash[i].ToString("x2"));
                return hex.ToString();
            }
        }

        /// <summary>
        /// Whether a caller-supplied fingerprint matches this source.
        ///
        /// An absent or blank expectation matches anything. The check is opt-in
        /// by design: a caller that never learned about it - an older MCP
        /// client, a hand-made curl, a status-only update carrying no
        /// translation - keeps working exactly as it did before. Only a caller
        /// that sends an 'fp' is held to it.
        /// </summary>
        public static bool Matches(string expected, string source)
        {
            if (string.IsNullOrWhiteSpace(expected)) return true;
            return string.Equals(expected.Trim(), Of(source), StringComparison.OrdinalIgnoreCase);
        }
    }
}
