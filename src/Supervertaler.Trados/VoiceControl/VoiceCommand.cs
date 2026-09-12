using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados.VoiceControl
{
    /// <summary>
    /// A single voice command: a spoken phrase (plus aliases) mapped to an
    /// action. Mirrors the Workbench VoiceCommand dataclass and its JSON
    /// shape (phrase / aliases / action_type / action / description /
    /// category / enabled) so command files can be exchanged between the
    /// two products. Workbench's AHK tiers ("ahk_script"/"ahk_inline") are
    /// not supported here – we run inside Studio, so "internal" +
    /// "keystroke" cover everything; AHK commands import as disabled.
    /// </summary>
    [DataContract]
    public class VoiceCommand
    {
        [DataMember(Name = "phrase")] public string Phrase { get; set; } = "";
        [DataMember(Name = "aliases")] public List<string> Aliases { get; set; } = new List<string>();
        /// <summary>"internal" or "keystroke" (AHK types load but stay disabled).</summary>
        [DataMember(Name = "action_type")] public string ActionType { get; set; } = "internal";
        /// <summary>Internal action id, or a keystroke chord like "ctrl+enter".</summary>
        [DataMember(Name = "action")] public string Action { get; set; } = "";
        [DataMember(Name = "description")] public string Description { get; set; } = "";
        [DataMember(Name = "category")] public string Category { get; set; } = "general";
        [DataMember(Name = "enabled")] public bool Enabled { get; set; } = true;

        /// <summary>
        /// The placeholder that marks an open slot in a spoken form: "select {phrase}".
        /// Everything before it is what the recogniser must hear; everything after it
        /// in the utterance is the argument handed to the action.
        ///
        /// <para>Until #125 every command was an exact phrase, which is why
        /// "select &lt;whatever the translator said&gt;" could not be expressed at all.
        /// A slot is deliberately only allowed at the END of a phrase: the words that
        /// fill it come from the document and can be anything, so a literal tail after
        /// them could not be told apart from the argument itself.</para>
        /// </summary>
        public const string Slot = "{phrase}";

        /// <summary>Whether any spoken form of this command ends in an open slot.</summary>
        public bool HasSlot()
        {
            return AllForms().Any(p => p.EndsWith(Slot, StringComparison.Ordinal));
        }

        /// <summary>
        /// The literal words in front of the slot, for every spoken form that has one -
        /// "select" for "select {phrase}". These are what go into the recogniser's
        /// grammar; the words that can fill the slot are added separately, per segment.
        /// </summary>
        public IEnumerable<string> SlotPrefixes()
        {
            foreach (var form in AllForms())
            {
                if (!form.EndsWith(Slot, StringComparison.Ordinal)) continue;
                var prefix = form.Substring(0, form.Length - Slot.Length).Trim();
                if (prefix.Length > 0) yield return prefix;
            }
        }

        /// <summary>Spoken forms with the slot placeholder still in them.</summary>
        private IEnumerable<string> AllForms()
        {
            if (!string.IsNullOrWhiteSpace(Phrase))
                yield return Phrase.Trim().ToLowerInvariant();
            if (Aliases == null) yield break;
            foreach (var a in Aliases)
                if (!string.IsNullOrWhiteSpace(a))
                    yield return a.Trim().ToLowerInvariant();
        }

        /// <summary>
        /// All spoken forms (phrase + aliases), lower-cased and trimmed. Forms with an
        /// open slot are excluded - they never match literally, and putting
        /// "select {phrase}" in the exact-match table would mean the recogniser had to
        /// hear those braces.
        /// </summary>
        public IEnumerable<string> AllPhrases()
        {
            foreach (var form in AllForms())
                if (!form.EndsWith(Slot, StringComparison.Ordinal))
                    yield return form;
        }
    }

    [DataContract]
    internal class VoiceCommandFile
    {
        [DataMember(Name = "commands")] public List<VoiceCommand> Commands { get; set; }

        /// <summary>
        /// The defaults generation the file was last synced with. When the
        /// built-in default set grows, files saved under an older generation
        /// get the NEW defaults merged in on load – without touching the
        /// user's customisations. Absent in old files → 0 → merge runs.
        /// </summary>
        [DataMember(Name = "defaults_version")] public int DefaultsVersion { get; set; }
    }

    /// <summary>
    /// The user's command set: built-in defaults merged with (or replaced by)
    /// trados/settings/voice_commands.json. The Vosk recogniser runs in
    /// grammar mode, so this list IS the recognition vocabulary – the engine
    /// can only ever hear these phrases (plus [unk] for everything else).
    /// </summary>
    public static class VoiceCommandSet
    {
        /// <summary>
        /// Bump whenever commands are ADDED to Defaults(). Saved files from an
        /// older generation get the new commands merged in on load, so users
        /// with customised command sets still receive new defaults.
        /// History: 1 = initial set (20.126), 2 = match 1–9 / escape /
        /// top+bottom / add-term split (20.127), 3 = zoom in/out (20.128),
        /// 4 = undo (20.191), 5 = select / delete that / dictate (20.191),
        /// 6 = select source (20.191), 7 = dictate split into start/stop (20.191).
        /// </summary>
        internal const int CurrentDefaultsVersion = 7;

        public static string CommandsFilePath =>
            Path.Combine(UserDataPath.TradosSettingsDir, "voice_commands.json");

        /// <summary>
        /// Default commands – ready to roll with zero configuration.
        /// Keystrokes use Trados Studio's stock shortcuts (or this plugin's
        /// own registered shortcuts, which Studio dispatches the same way);
        /// internal actions call plugin code directly.
        /// </summary>
        public static List<VoiceCommand> Defaults()
        {
            return new List<VoiceCommand>
            {
                // Segment flow
                // "scratch that" is Dragon's, and decades of dictation users' fingers
                // and mouths are trained on it. "undo that" for everyone else.
                // #125. "select" carries an open slot: the words after it are matched
                // against the segment's own target text, which is also what the
                // recogniser's grammar is built from while that segment is open.
                new VoiceCommand { Phrase = "select {phrase}", Aliases = new List<string> { "choose {phrase}" }, ActionType = "internal", Action = "select_phrase", Description = "Select words in the target: say \"select\" and the words", Category = "editing" },
                new VoiceCommand { Phrase = "delete that", Aliases = new List<string> { "delete this", "remove that" }, ActionType = "internal", Action = "delete_selection", Description = "Delete whatever is selected in the target", Category = "editing" },

                // #127. The words after "select source" are in the SOURCE language,
                // which the command model cannot pronounce - they are re-heard against
                // a model for that language, downloaded on first use. Off by default
                // for the same reason "dictate" is: it costs a 40 MB download, and an
                // installation whose source language has no model would only find out
                // by trying.
                new VoiceCommand { Phrase = "select source {phrase}", Aliases = new List<string> { "source select {phrase}", "source {phrase}" }, ActionType = "internal", Action = "select_source_phrase", Description = "Select words in the SOURCE segment. Downloads a voice model for the source language on first use. Read-only: \"delete that\" will refuse a source selection.", Category = "editing", Enabled = false },

                // Off by default: it drives an EXTERNAL dictation tool, which most
                // installations will not have. Enabled on a machine without one, it
                // would send a chord nobody is listening for and then gate every
                // other command until it was said again - a trap for anyone who
                // tried it out of curiosity. Shipped visible and off, so it can be
                // found and read before it is switched on.
                // Two commands, not one toggle with a "stop" alias. The toggle was a
                // trap: "stop now" toggled, so saying it when dictation was already
                // off STARTED it, and every command after that was silently
                // suppressed by the dictation gate. Saying either of these twice is
                // now harmless.
                new VoiceCommand { Phrase = "dictate", Aliases = new List<string> { "start dictating" }, ActionType = "internal", Action = "dictate_on:ctrl+win+space:ZZEND", Description = "Hand over to an external dictation tool. Needs that tool set to start/stop on Ctrl+Win+Space.", Category = "editing", Enabled = false },
                new VoiceCommand { Phrase = "stop now", Aliases = new List<string> { "stop dictating" }, ActionType = "internal", Action = "dictate_off:ctrl+win+space:ZZEND", Description = "Take dictation back from the external tool. Map this spoken phrase to ZZEND in that tool's dictionary and the marker is removed automatically.", Category = "editing", Enabled = false },
                new VoiceCommand { Phrase = "undo that", Aliases = new List<string> { "scratch that", "undo" }, ActionType = "keystroke", Action = "ctrl+z", Description = "Undo the last change (Ctrl+Z)", Category = "editing" },
                new VoiceCommand { Phrase = "confirm", Aliases = new List<string> { "confirm segment" }, ActionType = "keystroke", Action = "ctrl+enter", Description = "Confirm segment and move to next unconfirmed", Category = "editing" },
                new VoiceCommand { Phrase = "next segment", Aliases = new List<string> { "go down" }, ActionType = "internal", Action = "navigate_next", Description = "Move to the next segment (without confirming)", Category = "navigation" },
                new VoiceCommand { Phrase = "previous segment", Aliases = new List<string> { "go up" }, ActionType = "internal", Action = "navigate_previous", Description = "Move to the previous segment", Category = "navigation" },
                new VoiceCommand { Phrase = "go to the top", Aliases = new List<string> { "go to top" }, ActionType = "keystroke", Action = "ctrl+home", Description = "Jump to the first segment (Ctrl+Home)", Category = "navigation" },
                new VoiceCommand { Phrase = "go to the bottom", Aliases = new List<string> { "go to bottom" }, ActionType = "keystroke", Action = "ctrl+end", Description = "Jump to the last segment (Ctrl+End)", Category = "navigation" },
                new VoiceCommand { Phrase = "copy source", Aliases = new List<string> { "copy from source" }, ActionType = "keystroke", Action = "ctrl+insert", Description = "Copy source to target", Category = "editing" },
                new VoiceCommand { Phrase = "clear target", Aliases = new List<string>(), ActionType = "keystroke", Action = "alt+delete", Description = "Clear the target segment", Category = "editing" },

                // TermLens – direct plugin calls (case-adapted insertion)
                new VoiceCommand { Phrase = "term one",   ActionType = "internal", Action = "insert_term_1", Description = "Insert TermLens match 1", Category = "termlens" },
                new VoiceCommand { Phrase = "term two",   ActionType = "internal", Action = "insert_term_2", Description = "Insert TermLens match 2", Category = "termlens" },
                new VoiceCommand { Phrase = "term three", ActionType = "internal", Action = "insert_term_3", Description = "Insert TermLens match 3", Category = "termlens" },
                new VoiceCommand { Phrase = "term four",  ActionType = "internal", Action = "insert_term_4", Description = "Insert TermLens match 4", Category = "termlens" },
                new VoiceCommand { Phrase = "term five",  ActionType = "internal", Action = "insert_term_5", Description = "Insert TermLens match 5", Category = "termlens" },
                new VoiceCommand { Phrase = "term six",   ActionType = "internal", Action = "insert_term_6", Description = "Insert TermLens match 6", Category = "termlens" },
                new VoiceCommand { Phrase = "term seven", ActionType = "internal", Action = "insert_term_7", Description = "Insert TermLens match 7", Category = "termlens" },
                new VoiceCommand { Phrase = "term eight", ActionType = "internal", Action = "insert_term_8", Description = "Insert TermLens match 8", Category = "termlens" },
                new VoiceCommand { Phrase = "term nine",  ActionType = "internal", Action = "insert_term_9", Description = "Insert TermLens match 9", Category = "termlens" },
                // Translation Results – Studio applies match N with Ctrl+N
                new VoiceCommand { Phrase = "match one",   ActionType = "keystroke", Action = "ctrl+1", Description = "Apply translation result 1", Category = "matches" },
                new VoiceCommand { Phrase = "match two",   ActionType = "keystroke", Action = "ctrl+2", Description = "Apply translation result 2", Category = "matches" },
                new VoiceCommand { Phrase = "match three", ActionType = "keystroke", Action = "ctrl+3", Description = "Apply translation result 3", Category = "matches" },
                new VoiceCommand { Phrase = "match four",  ActionType = "keystroke", Action = "ctrl+4", Description = "Apply translation result 4", Category = "matches" },
                new VoiceCommand { Phrase = "match five",  ActionType = "keystroke", Action = "ctrl+5", Description = "Apply translation result 5", Category = "matches" },
                new VoiceCommand { Phrase = "match six",   ActionType = "keystroke", Action = "ctrl+6", Description = "Apply translation result 6", Category = "matches" },
                new VoiceCommand { Phrase = "match seven", ActionType = "keystroke", Action = "ctrl+7", Description = "Apply translation result 7", Category = "matches" },
                new VoiceCommand { Phrase = "match eight", ActionType = "keystroke", Action = "ctrl+8", Description = "Apply translation result 8", Category = "matches" },
                new VoiceCommand { Phrase = "match nine",  ActionType = "keystroke", Action = "ctrl+9", Description = "Apply translation result 9", Category = "matches" },

                new VoiceCommand { Phrase = "term picker", Aliases = new List<string> { "pick term" }, ActionType = "internal", Action = "term_picker", Description = "Open the TermPicker dialog", Category = "termlens" },
                new VoiceCommand { Phrase = "term popup", Aliases = new List<string> { "show terms" }, ActionType = "internal", Action = "termlens_popup", Description = "Open the floating TermLens popup", Category = "termlens" },
                new VoiceCommand { Phrase = "add term", Aliases = new List<string> { "new term" }, ActionType = "keystroke", Action = "alt+down", Description = "Quick-add selection to the write termbases (Alt+Down)", Category = "termlens" },
                new VoiceCommand { Phrase = "add project term", Aliases = new List<string> { "project term" }, ActionType = "keystroke", Action = "alt+up", Description = "Quick-add selection to the project termbase (Alt+Up)", Category = "termlens" },

                // AI / search
                new VoiceCommand { Phrase = "translate", Aliases = new List<string> { "translate segment" }, ActionType = "keystroke", Action = "alt+t", Description = "AI-translate the active segment", Category = "translation" },
                new VoiceCommand { Phrase = "concordance", Aliases = new List<string> { "search memory" }, ActionType = "keystroke", Action = "f3", Description = "Concordance search on the selection", Category = "lookup" },

                // Editor font size. Studio's font-adaptation actions ship with NO
                // default shortcut. One-time setup: File > Options > Keyboard
                // Shortcuts > Editor, scroll to the actions named simply
                // "Increase" / "Decrease" (there is no search box on that
                // page) and bind Ctrl+Alt+PgUp / Ctrl+Alt+PgDn.
                new VoiceCommand { Phrase = "zoom in", Aliases = new List<string> { "bigger font" }, ActionType = "keystroke", Action = "ctrl+alt+pgup", Description = "Increase editor font size (bind Ctrl+Alt+PgUp to Keyboard Shortcuts > Editor > ‘Increase’ once)", Category = "view" },
                new VoiceCommand { Phrase = "zoom out", Aliases = new List<string> { "smaller font" }, ActionType = "keystroke", Action = "ctrl+alt+pgdn", Description = "Decrease editor font size (bind Ctrl+Alt+PgDn to Keyboard Shortcuts > Editor > ‘Decrease’ once)", Category = "view" },

                // Control
                new VoiceCommand { Phrase = "escape", Aliases = new List<string> { "close window" }, ActionType = "keystroke", Action = "escape", Description = "Close the focused popup/dialog (term popup, TermPicker…)", Category = "control" },
                new VoiceCommand { Phrase = "stop listening", Aliases = new List<string> { "voice off" }, ActionType = "internal", Action = "stop_listening", Description = "Turn voice commands off", Category = "control" },
            };
        }

        /// <summary>Loads the user's commands, falling back to the defaults.</summary>
        public static List<VoiceCommand> Load()
        {
            try
            {
                if (File.Exists(CommandsFilePath))
                {
                    VoiceCommandFile data;
                    using (var fs = File.OpenRead(CommandsFilePath))
                    {
                        var ser = new DataContractJsonSerializer(typeof(VoiceCommandFile));
                        data = (VoiceCommandFile)ser.ReadObject(fs);
                    }
                    if (data?.Commands != null && data.Commands.Count > 0)
                    {
                        // AHK-tier commands from a Workbench export can't run here
                        foreach (var c in data.Commands)
                        {
                            if (c.ActionType != null && c.ActionType.StartsWith("ahk", StringComparison.OrdinalIgnoreCase))
                                c.Enabled = false;
                            if (c.Aliases == null) c.Aliases = new List<string>();
                        }

                        // File saved under an older defaults generation: merge
                        // in any default command whose phrase the user doesn't
                        // have (deliberate deletions of OLD defaults stay
                        // deleted – only newly shipped defaults are added).
                        // Persist so the merge runs once per generation.
                        if (data.DefaultsVersion < CurrentDefaultsVersion)
                        {
                            var known = new HashSet<string>(
                                data.Commands.Select(c => (c.Phrase ?? "").Trim()),
                                StringComparer.OrdinalIgnoreCase);
                            foreach (var def in Defaults())
                                if (!known.Contains((def.Phrase ?? "").Trim()))
                                    data.Commands.Add(def);
                            Save(data.Commands);
                        }
                        return data.Commands;
                    }
                }
            }
            catch { /* fall through to defaults */ }
            return Defaults();
        }

        public static void Save(List<VoiceCommand> commands)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CommandsFilePath));
                using (var ms = new MemoryStream())
                {
                    var ser = new DataContractJsonSerializer(typeof(VoiceCommandFile));
                    ser.WriteObject(ms, new VoiceCommandFile
                    {
                        Commands = commands,
                        DefaultsVersion = CurrentDefaultsVersion
                    });
                    File.WriteAllBytes(CommandsFilePath, ms.ToArray());
                }
            }
            catch { /* non-fatal – commands just aren't persisted */ }
        }

        /// <summary>
        /// Grammar phrases for the Vosk recogniser: every enabled spoken form,
        /// deduplicated. "[unk]" is appended by the engine.
        /// </summary>
        public static List<string> GrammarPhrases(List<VoiceCommand> commands)
        {
            // #125: a slot command contributes its PREFIX ("select"), not its
            // spoken form - the words that fill the slot come from the open
            // segment and are added by the caller, because they change as the
            // translator moves through the document.
            return commands.Where(c => c.Enabled)
                           .SelectMany(c => c.AllPhrases().Concat(c.SlotPrefixes()))
                           .Distinct()
                           .ToList();
        }
    }
}
