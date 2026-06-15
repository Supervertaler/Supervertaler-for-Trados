using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sdl.ProjectAutomation.Core;
using Sdl.ProjectAutomation.FileBased;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using Supervertaler.Trados.Models;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Detects file-based termbases attached to the active Trados project
    /// and resolves language index mappings. Handles both legacy MultiTerm
    /// .sdltb files (Trados Studio 2024) and the new SQLite-based .ttb files
    /// (Trados Studio 2026). Downstream code dispatches on the file extension
    /// to choose between MultiTermReader (OleDb) and TtbReader (SQLite).
    /// </summary>
    public static class MultiTermProjectDetector
    {
        /// <summary>
        /// Termbase file extensions recognised by Trados Studio:
        ///   .sdltb — legacy MultiTerm format (JET 4.0 / ACE OleDb, Trados Studio 2024 and earlier)
        ///   .ttb   — new file-based termbase (SQLite 3 + FTS5, Trados Studio 2026)
        /// </summary>
        private static readonly string[] SupportedTermbaseExtensions = { ".sdltb", ".ttb" };

        /// <summary>
        /// Enumerates file-based termbases from the active project's termbase
        /// configuration and resolves source/target language index names.
        /// Returns empty list if no project is open or no termbases are configured.
        /// </summary>
        public static List<MultiTermTermbaseConfig> DetectTermbases(
            IStudioDocument activeDocument)
        {
            var result = new List<MultiTermTermbaseConfig>();
            if (activeDocument == null)
            {
                DiagnosticLog.Log("MultiTerm", "DetectTermbases: activeDocument is null → 0 termbases.");
                return result;
            }

            try
            {
                var project = activeDocument.Project as FileBasedProject;
                if (project == null)
                {
                    DiagnosticLog.Log("MultiTerm", "DetectTermbases: active document's Project is not a FileBasedProject → 0 termbases.");
                    return result;
                }

                var tbConfig = project.GetTermbaseConfiguration();
                if (tbConfig?.Termbases == null || tbConfig.Termbases.Count == 0)
                {
                    DiagnosticLog.Log("MultiTerm", $"DetectTermbases: project termbase configuration has no termbases (tbConfig null={tbConfig == null}) → 0 termbases.");
                    return result;
                }
                DiagnosticLog.Log("MultiTerm", $"DetectTermbases: project has {tbConfig.Termbases.Count} termbase(s) in its configuration, {tbConfig.LanguageIndexes?.Count ?? 0} language index(es).");

                // Build language index mapping: project language code → termbase index name
                // e.g. "en-US" → "English", "nl-NL" → "Dutch"
                var langIndexMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (tbConfig.LanguageIndexes != null)
                {
                    foreach (var idx in tbConfig.LanguageIndexes)
                    {
                        var cultureName = idx.ProjectLanguage?.CultureInfo?.Name;
                        var tbIndex = idx.TermbaseIndex;
                        if (cultureName != null && !string.IsNullOrEmpty(tbIndex))
                            langIndexMap[cultureName] = tbIndex;
                    }
                }

                // Determine source and target language from the active document
                string sourceLocale = null;
                string targetLocale = null;

                try
                {
                    var activeFile = activeDocument.ActiveFile;
                    sourceLocale = activeFile?.SourceFile?.Language?.CultureInfo?.Name;
                    targetLocale = activeFile?.Language?.CultureInfo?.Name;
                }
                catch { }

                if (string.IsNullOrEmpty(sourceLocale) || string.IsNullOrEmpty(targetLocale))
                {
                    DiagnosticLog.Log("MultiTerm", $"DetectTermbases: could not read active-file locales (source='{sourceLocale}', target='{targetLocale}') → 0 termbases.");
                    return result;
                }

                // Resolve source and target index names from the mapping
                string sourceIndexName = null;
                string targetIndexName = null;

                langIndexMap.TryGetValue(sourceLocale, out sourceIndexName);
                langIndexMap.TryGetValue(targetLocale, out targetIndexName);

                // If exact match failed, try 2-letter language code prefix
                if (sourceIndexName == null)
                    sourceIndexName = FindIndexByPrefix(langIndexMap, sourceLocale);
                if (targetIndexName == null)
                    targetIndexName = FindIndexByPrefix(langIndexMap, targetLocale);

                if (string.IsNullOrEmpty(sourceIndexName) || string.IsNullOrEmpty(targetIndexName))
                {
                    DiagnosticLog.Log("MultiTerm",
                        $"DetectTermbases: could not map project locales to termbase index names " +
                        $"(source '{sourceLocale}'→'{sourceIndexName}', target '{targetLocale}'→'{targetIndexName}'). " +
                        $"Available indexes: [{string.Join(", ", langIndexMap.Keys)}] → [{string.Join(", ", langIndexMap.Values)}]. → 0 termbases.");
                    return result;
                }

                // Enumerate each termbase in the project configuration
                int ordinal = 0;
                foreach (var tb in tbConfig.Termbases)
                {
                    try
                    {
                        // Try to capture SettingsXml via reflection (property name varies)
                        string settingsXml = null;
                        try
                        {
                            var tbType = tb.GetType();
                            var flags = System.Reflection.BindingFlags.Instance |
                                        System.Reflection.BindingFlags.Public |
                                        System.Reflection.BindingFlags.NonPublic;

                            foreach (var prop in tbType.GetProperties(flags))
                            {
                                if ((prop.Name == "SettingsXml" || prop.Name == "SettingsXML") &&
                                    prop.PropertyType == typeof(string))
                                {
                                    settingsXml = prop.GetValue(tb) as string;
                                    break;
                                }
                            }
                        }
                        catch { }

                        // NB: we DO NOT skip termbases that the user has unchecked
                        // in Trados Project Settings → Termbases. Many users want to
                        // keep a termbase reference in the project but suppress
                        // Trados's own native term-recognition (the red overlines
                        // and Term Recognition pane), while still having the
                        // termbase contribute to TermLens chips and AI prompts.
                        // The plugin's own disable/enable lives in Supervertaler's
                        // Termbases settings (`DisabledMultiTermIds`).

                        var localTb = tb as LocalTermbase;
                        if (localTb == null) continue;

                        var filePath = localTb.FilePath;
                        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                            continue;

                        if (!SupportedTermbaseExtensions.Any(ext =>
                            filePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                            continue;

                        var name = !string.IsNullOrEmpty(localTb.Name)
                            ? localTb.Name
                            : Path.GetFileNameWithoutExtension(filePath);

                        // Generate stable negative synthetic ID from file path
                        long syntheticId = -(Math.Abs((long)filePath.ToLowerInvariant().GetHashCode()) * 1000 + ordinal + 1);

                        result.Add(new MultiTermTermbaseConfig
                        {
                            FilePath = filePath,
                            TermbaseName = name,
                            SourceIndexName = sourceIndexName,
                            TargetIndexName = targetIndexName,
                            SyntheticId = syntheticId,
                            TradosEnabled = tb.Enabled,
                            SettingsXml = settingsXml
                        });

                        ordinal++;
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Log("MultiTerm", "DetectTermbases threw: " + ex);
            }

            DiagnosticLog.Log("MultiTerm",
                $"DetectTermbases: returning {result.Count} usable .sdltb/.ttb termbase(s)" +
                (result.Count > 0 ? " — " + string.Join(", ", result.ConvertAll(c => c.TermbaseName)) : "."));
            return result;
        }

        /// <summary>
        /// Tries to find a matching index name by the 2-letter language code prefix.
        /// e.g. "nl-BE" matches the entry for "nl-NL" → "Dutch".
        /// </summary>
        private static string FindIndexByPrefix(Dictionary<string, string> map, string locale)
        {
            if (string.IsNullOrEmpty(locale) || locale.Length < 2) return null;
            var prefix = locale.Substring(0, 2).ToLowerInvariant();

            foreach (var kvp in map)
            {
                if (kvp.Key.Length >= 2 &&
                    kvp.Key.Substring(0, 2).Equals(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return kvp.Value;
                }
            }
            return null;
        }
    }
}
