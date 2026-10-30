using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Supervertaler.Trados.Core;

namespace Supervertaler.Trados.Settings
{
    /// <summary>
    /// Where Trados gets the AI key for an action, and step 1 of locking API keys
    /// to the computer for the keys only Trados keeps
    /// (core/docs/design/api-key-protection.md, §4 and §5).
    ///
    /// <para><b>Every key an action uses comes through <see cref="For(string, AiSettings, out string)"/>
    /// or <see cref="For(CustomOpenAiProfile, out string)"/>.</b> Both return null
    /// together with the sentence to show: that the key cannot be read on this
    /// computer (design note §7, M2), or that none is set. An action that gets null
    /// shows that sentence and does not call the provider, so a provider is never
    /// sent a blank key in place of one this Windows account cannot unlock.</para>
    ///
    /// <para><b>OpenAI-compatible profiles.</b> A profile keeps its key in
    /// settings.json as before and, from step 1, also in the locked file under
    /// <see cref="ProfileId"/>. In step 1 the profile's own copy decides; the locked
    /// one is read only when that is empty, which is what step 2 will leave
    /// behind - on this computer, or on another one sharing the data folder.</para>
    /// </summary>
    internal static class TradosApiKeys
    {
        /// <summary>Where a missing or unreadable key is entered, as the messages name it.</summary>
        internal const string SettingsPlace = "Settings → AI Settings";

        private const string LogCategory = "API keys";

        /// <summary>
        /// The locked file's id for a profile's key: <c>trados:custom_openai:Acme server</c>.
        /// Case-insensitive, like every id in that file.
        /// </summary>
        internal static string ProfileId(string profileName)
            => Supervertaler.Core.ApiKeyStore.PrivateId("trados", LlmModels.ProviderCustomOpenAi, profileName);

        /// <summary>
        /// The key for a built-in provider, or null with <paramref name="problem"/>
        /// the sentence to show instead of calling it.
        /// </summary>
        internal static string For(string provider, AiSettings ai, out string problem)
            => LlmClient.ResolveApiKey(provider, ai?.ApiKeys, SettingsPlace, out problem);

        /// <summary>
        /// The key for an OpenAI-compatible profile, or null with
        /// <paramref name="problem"/> the sentence to show: the profile's own key
        /// when it has one (step 1: settings.json decides), else the locked file's.
        /// </summary>
        internal static string For(CustomOpenAiProfile profile, out string problem)
        {
            problem = null;
            if (profile == null)
            {
                problem = "No custom OpenAI profile configured. Add one in " + SettingsPlace + ".";
                return null;
            }
            if (!string.IsNullOrWhiteSpace(profile.ApiKey)) return profile.ApiKey;

            var id = ProfileId(profile.Name);
            var key = Supervertaler.Core.ApiKeyStore.Get(id);
            if (key != null) return key;

            // Null from MissingKeyMessage means the key arrived since the line above;
            // asking again next time will find it.
            problem = Supervertaler.Core.ApiKeyStore.MissingKeyMessage(id, SettingsPlace)
                   ?? "No API key set for " + profile.Name + ". Add one in " + SettingsPlace + ".";
            return null;
        }

        /// <summary>
        /// Set by the start-up sync when it had to set aside a damaged locked file;
        /// null otherwise. Shown in AI Settings for the rest of the session, and
        /// already in the log.
        /// </summary>
        internal static string StartupNotice => _startupNotice;
        private static volatile string _startupNotice;

        /// <summary>
        /// The profiles' names and keys as they are now, for
        /// <see cref="SyncAtStartup"/>. Taken under the settings lock; the sync
        /// itself runs outside it.
        /// </summary>
        internal static List<(string Name, string Key)> ProfileKeys(AiSettings ai)
            => (ai?.CustomOpenAiProfiles ?? new List<CustomOpenAiProfile>())
                .Where(p => p != null)
                .Select(p => (Name: p.Name, Key: p.ApiKey))
                .ToList();

        /// <summary>
        /// Step 1 at start-up, once per session, with the settings just loaded:
        /// <list type="number">
        /// <item><description>Trados's own copies of the provider keys go into the
        /// shared plain file where neither file has that key yet (#108,
        /// <c>MigrateFrom</c>, which never overwrites).</description></item>
        /// <item><description>Every key in the shared plain file is locked into the
        /// locked file where that lacks it or holds another (<c>SyncLocked</c>, which
        /// never removes).</description></item>
        /// <item><description>Each profile's key is locked under
        /// <see cref="ProfileId"/> the same way. A profile with no key of its own
        /// leaves its locked entry alone.</description></item>
        /// </list>
        ///
        /// <para>Never throws. A failure is logged and the next start tries again;
        /// meanwhile reads still take the plain copies, so nothing stops working.
        /// Cost: one unlock per key to compare, under a millisecond each, and a
        /// write only when something changed.</para>
        /// </summary>
        internal static void SyncAtStartup(AiApiKeys ownKeys, IList<(string Name, string Key)> profiles)
        {
            Supervertaler.Core.ApiKeyStore.Log = message => DiagnosticLog.WriteAlways(LogCategory, message);
            try
            {
                var added = Supervertaler.Core.ApiKeyStore.MigrateFrom(ownKeys);
                if (added > 0)
                    DiagnosticLog.Log(LogCategory, $"Copied {added} API key(s) into {Supervertaler.Core.ApiKeyStore.FilePath}");

                var sync = Supervertaler.Core.ApiKeyStore.SyncLocked();
                if (sync.Locked > 0)
                    DiagnosticLog.Log(LogCategory, $"Locked {sync.Locked} API key(s) to this computer");

                var failed = sync.Failed;
                foreach (var profile in profiles ?? new List<(string Name, string Key)>())
                {
                    if (string.IsNullOrWhiteSpace(profile.Key)) continue;
                    if (!Supervertaler.Core.ApiKeyStore.Set(ProfileId(profile.Name), profile.Key))
                    {
                        failed = true;
                        DiagnosticLog.WriteAlways(LogCategory, "The key of endpoint profile \"" + profile.Name
                            + "\" could not be locked to this computer; the next start tries again.");
                    }
                }

                if (sync.Notice != null)
                {
                    _startupNotice = DamagedNotice(failed);
                    DiagnosticLog.WriteAlways(LogCategory, _startupNotice);
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteAlways(LogCategory, "API keys not synced at start-up: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Design note §7, M6, as it reads in step 1. Core's sentence asks for every
        /// key again; in step 1 the plain file still has them, the sync has just
        /// locked them again from it, and the profiles' keys from settings.json.
        /// </summary>
        private static string DamagedNotice(bool failed)
        {
            var folder = Path.GetDirectoryName(Supervertaler.Core.ApiKeyStore.LockedFilePath);
            var plain = Supervertaler.Core.ApiKeyStore.FilePath;
            var first = "The file holding your locked API keys was damaged; a copy has been kept in " + folder + ". ";
            return failed
                ? first + "Your keys still work from " + plain + ", and locking them to this computer will be tried again at the next start."
                : first + "Your keys were locked again from " + plain + ", so nothing needs doing. "
                        + "If an AI action says a key is missing, paste it in again in " + SettingsPlace + ".";
        }

        /// <summary>
        /// After AI Settings has saved settings.json: each profile's key into the
        /// locked file - an empty one removes the entry, except one this account
        /// cannot read, which stays until a new key replaces it - and then the
        /// entries of profiles that are gone, renamed or removed. Renaming a
        /// profile therefore moves its key.
        ///
        /// <para>Two orders matter. settings.json first, then the locked file, as
        /// for every key (§5): a crash in between leaves settings.json the newer,
        /// which reads take and the next start locks. And the new entries before
        /// the old ones are removed, so a rename stopped halfway leaves the key
        /// under both names, never under neither.</para>
        /// </summary>
        /// <param name="profiles">Every profile now: name and key.</param>
        /// <param name="namesBefore">The profiles' names when AI Settings opened.</param>
        internal static void SaveProfileKeys(IEnumerable<(string Name, string Key)> profiles, IEnumerable<string> namesBefore)
        {
            try
            {
                var now = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var profile in profiles)
                {
                    var id = ProfileId(profile.Name);
                    now.Add(id);
                    if (!Supervertaler.Core.ApiKeyStore.Set(id, profile.Key))
                        DiagnosticLog.WriteAlways(LogCategory, "The key of endpoint profile \"" + profile.Name
                            + "\" could not be locked to this computer; the next start tries again.");
                }

                foreach (var name in namesBefore ?? Enumerable.Empty<string>())
                {
                    var id = ProfileId(name);
                    if (!now.Contains(id)) Supervertaler.Core.ApiKeyStore.Set(id, null);
                }
            }
            catch (Exception ex)
            {
                // Never takes the settings dialog down: settings.json is saved, and
                // reads take the profile's key from there.
                DiagnosticLog.WriteAlways(LogCategory, "Endpoint profile keys not locked: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
