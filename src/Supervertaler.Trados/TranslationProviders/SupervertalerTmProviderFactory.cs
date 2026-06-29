using System;
using System.IO;
using Sdl.LanguagePlatform.TranslationMemoryApi;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados.TranslationProviders
{
    /// <summary>
    /// Discovery factory that lets Trados Studio recognise
    /// <c>supervertaler-tm:</c> URIs and instantiate the matching
    /// <see cref="SupervertalerTmProvider"/>. Registered with Trados via
    /// the <c>[TranslationProviderFactory]</c> attribute below; Studio
    /// scans every loaded plugin for these at startup and uses them to
    /// resolve project-stored TM URIs back to live provider objects.
    /// </summary>
    [TranslationProviderFactory(
        Id = "SupervertalerTmProviderFactory",
        Name = "Supervertaler TM Bridge",
        Description = "Reads translation memories from a Supervertaler Workbench user-data folder")]
    public class SupervertalerTmProviderFactory : ITranslationProviderFactory
    {
        public bool SupportsTranslationProviderUri(Uri translationProviderUri)
        {
            if (translationProviderUri == null) return false;
            return string.Equals(
                translationProviderUri.Scheme,
                SupervertalerTmProvider.ProviderScheme,
                StringComparison.OrdinalIgnoreCase);
        }

        public ITranslationProvider CreateTranslationProvider(
            Uri translationProviderUri,
            string translationProviderState,
            ITranslationProviderCredentialStore credentialStore)
        {
            TranslationProviders.TmBridgeLog.Info(
                "Factory.CreateTranslationProvider: uri=" + (translationProviderUri == null ? "(null)" : translationProviderUri.ToString()));

            if (!SupportsTranslationProviderUri(translationProviderUri))
                throw new ArgumentException(
                    "URI does not match the Supervertaler TM scheme.",
                    nameof(translationProviderUri));

            var tmId = SupervertalerTmProvider.ExtractTmIdFromUri(translationProviderUri);
            var dbPath = ResolveDbPath();
            var tmInfo = ResolveTmInfo(dbPath, tmId);

            TranslationProviders.TmBridgeLog.Info(
                "Factory.CreateTranslationProvider: resolved tmId=" + (tmId ?? "(null)") +
                ", dbPath=" + (dbPath ?? "(null)") +
                ", tmInfo=" + (tmInfo == null ? "NOT FOUND in bridged TMs" : "OK"));

            // We deliberately return the provider even when tmInfo is null
            // (TM no longer bridged / missing). The provider then surfaces
            // its unavailable state via StatusInfo so Studio shows a clear
            // "this TM is offline" pill instead of failing to open the
            // project entirely.
            var provider = new SupervertalerTmProvider(translationProviderUri, tmInfo, dbPath);
            if (!string.IsNullOrEmpty(translationProviderState))
            {
                provider.LoadState(translationProviderState);
            }
            return provider;
        }

        public TranslationProviderInfo GetTranslationProviderInfo(
            Uri translationProviderUri,
            string translationProviderState)
        {
            var info = new TranslationProviderInfo
            {
                TranslationMethod = Sdl.LanguagePlatform.TranslationMemoryApi.TranslationMethod.TranslationMemory,
            };

            var tmId = SupervertalerTmProvider.ExtractTmIdFromUri(translationProviderUri);
            var dbPath = ResolveDbPath();
            var tmInfo = ResolveTmInfo(dbPath, tmId);

            info.Name = tmInfo != null
                ? "Supervertaler Workbench TM: " + tmInfo.Name
                : "Supervertaler Workbench TM: " + (tmId ?? "(unknown)");
            return info;
        }

        // ─── Helpers ──────────────────────────────────────────────────

        internal static string ResolveDbPath()
        {
            try
            {
                return Path.Combine(UserDataPath.ResourcesDir, "supervertaler.db");
            }
            catch
            {
                return null;
            }
        }

        internal static TmInfo ResolveTmInfo(string dbPath, string tmId)
        {
            if (string.IsNullOrEmpty(dbPath) || string.IsNullOrEmpty(tmId)) return null;
            if (!File.Exists(dbPath)) return null;

            try
            {
                using (var reader = new TmReader(dbPath))
                {
                    if (!reader.Open()) return null;
                    return reader.GetBridgedTmByTmId(tmId);
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
