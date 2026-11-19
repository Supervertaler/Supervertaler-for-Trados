using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados.Core
{
    /// <summary>One Supervertaler termbase as this computer's termbase database knows it.</summary>
    public sealed class TermbaseIdentity
    {
        public long Id { get; }
        public string Name { get; }
        /// <summary>The database's <c>created_date</c> for the termbase, as stored; "" when unknown.</summary>
        public string Created { get; }

        public TermbaseIdentity(long id, string name, string created)
        {
            Id = id;
            Name = name ?? "";
            Created = created ?? "";
        }
    }

    /// <summary>
    /// Supervertaler's part of a project's settings bundle (<see cref="SupervertalerProjectSettingsGroup"/>), as the
    /// raw strings stored there. "" means no value: the sidecar decides.
    /// </summary>
    public sealed class ProjectProfileValues
    {
        public string MemoryBankName { get; set; } = "";
        public string TranslatePromptPath { get; set; } = "";
        public string ProofreadPromptPath { get; set; } = "";
        public string ClientName { get; set; } = "";
        public string ProfileName { get; set; } = "";
        public string Termbases { get; set; } = "";
        public string DisabledMultiTerm { get; set; } = "";
        public string AiEnabledMultiTerm { get; set; } = "";

        public bool SameAs(ProjectProfileValues o)
        {
            if (o == null) return false;
            return Eq(MemoryBankName, o.MemoryBankName) && Eq(TranslatePromptPath, o.TranslatePromptPath)
                && Eq(ProofreadPromptPath, o.ProofreadPromptPath) && Eq(ClientName, o.ClientName)
                && Eq(ProfileName, o.ProfileName) && Eq(Termbases, o.Termbases)
                && Eq(DisabledMultiTerm, o.DisabledMultiTerm) && Eq(AiEnabledMultiTerm, o.AiEnabledMultiTerm);
        }

        /// <summary>For the diagnostic log: which values are present, never the termbase lines themselves.</summary>
        public string Describe()
        {
            var parts = new List<string>();
            if (Has(MemoryBankName)) parts.Add("bank='" + MemoryBankName + "'");
            if (Has(TranslatePromptPath)) parts.Add("prompt='" + TranslatePromptPath + "'");
            if (Has(ProofreadPromptPath)) parts.Add("proofread='" + ProofreadPromptPath + "'");
            if (Has(ClientName)) parts.Add("client set");
            if (Has(ProfileName)) parts.Add("profile='" + ProfileName + "'");
            if (Has(Termbases)) parts.Add(ProjectProfile.Lines(Termbases).Count + " termbase line(s)");
            if (Has(DisabledMultiTerm)) parts.Add("MultiTerm off: " + ProjectProfile.Lines(DisabledMultiTerm).Count);
            if (Has(AiEnabledMultiTerm)) parts.Add("MultiTerm AI: " + ProjectProfile.Lines(AiEnabledMultiTerm).Count);
            return parts.Count == 0 ? "(nothing)" : string.Join(", ", parts);
        }

        static bool Eq(string a, string b) => string.Equals(a ?? "", b ?? "", StringComparison.Ordinal);
        internal static bool Has(string s) => !string.IsNullOrWhiteSpace(s);
    }

    /// <summary>
    /// The rules between a project's settings bundle (which a Studio project template carries into every new
    /// project, and "Create project template" copies back) and Supervertaler's per-project sidecar file. No Studio
    /// types: everything here is tested outside Studio (tools\project_profile_test.ps1).
    ///
    /// <para><b>Read:</b> the bundle wins wherever it has a value; for anything it lacks, the sidecar, as before
    /// (<see cref="Overlay"/>). <b>Write:</b> the effective values go to both (<see cref="FromSidecar"/> computes
    /// the bundle's). A stored name that does not exist on this computer (a memory bank, a prompt, a termbase from
    /// another translator) is kept as stored: the readers treat a missing bank as none and a missing prompt as the
    /// mode's default, as they always have.</para>
    ///
    /// <para><b>Supervertaler termbases</b> are stored by NAME, the database's own unique key, plus the local id and
    /// creation time, which only serve to recognise a termbase renamed in the same database. A local id alone means
    /// nothing on another computer or after a database is rebuilt. Only termbases with a choice switched on are
    /// listed, so a template names no more of a translator's termbases than it uses; anything not listed is off,
    /// which is also the rule for a termbase created later (#103). A listed termbase this computer lacks is
    /// reported and kept in the stored value, never dropped.</para>
    /// </summary>
    public static class ProjectProfile
    {
        const string Flags = "RWPA";

        // ─── termbase lines ─────────────────────────────────────────

        /// <summary>One line of <see cref="ProjectProfileValues.Termbases"/>.</summary>
        public sealed class TermbaseChoice
        {
            public bool Read, Write, Project, Ai;
            public string Name = "";
            public long Id = -1;
            public string Created = "";
            /// <summary>The line as stored.</summary>
            public string Line = "";
            /// <summary>False for a line that could not be read; such a line is kept as it is and otherwise ignored.</summary>
            public bool Parsed;

            internal string FlagText() => (Read ? "R" : "") + (Write ? "W" : "") + (Project ? "P" : "") + (Ai ? "A" : "");
            internal bool Any => Read || Write || Project || Ai;
        }

        internal static List<string> Lines(string raw) =>
            (raw ?? "").Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(l => l.Trim().Length > 0).ToList();

        public static List<TermbaseChoice> ParseTermbases(string raw)
        {
            var list = new List<TermbaseChoice>();
            foreach (var line in Lines(raw))
            {
                var c = new TermbaseChoice { Line = line };
                var parts = line.Split('\t');
                if (parts.Length >= 2 && parts[0].Trim().Length > 0 && parts[0].Trim().All(ch => Flags.IndexOf(ch) >= 0))
                {
                    var f = parts[0].Trim();
                    c.Read = f.Contains('R'); c.Write = f.Contains('W'); c.Project = f.Contains('P'); c.Ai = f.Contains('A');
                    c.Name = Unescape(parts[1]).Trim();
                    if (parts.Length >= 3 && long.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) c.Id = id;
                    if (parts.Length >= 4) c.Created = parts[3].Trim();
                    c.Parsed = c.Name.Length > 0;
                }
                list.Add(c);
            }
            return list;
        }

        public static string FormatTermbase(TermbaseIdentity tb, bool read, bool write, bool project, bool ai)
        {
            var c = new TermbaseChoice { Read = read, Write = write, Project = project, Ai = ai };
            return c.FlagText() + "\t" + Escape(tb.Name) + "\t" + tb.Id.ToString(CultureInfo.InvariantCulture) + "\t" + (tb.Created ?? "");
        }

        /// <summary>
        /// The termbase on this computer that <paramref name="c"/> names: the same name (exactly, else ignoring
        /// case when only one fits), else - a termbase renamed in this same database - the same id with the same
        /// creation time. Null when there is none.
        /// </summary>
        public static TermbaseIdentity Resolve(TermbaseChoice c, IList<TermbaseIdentity> local)
        {
            if (c == null || !c.Parsed || local == null) return null;
            var exact = local.FirstOrDefault(t => string.Equals(t.Name, c.Name, StringComparison.Ordinal));
            if (exact != null) return exact;
            var folded = local.Where(t => string.Equals(t.Name, c.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (folded.Count == 1) return folded[0];
            if (c.Id > 0 && c.Created.Length > 0)
                return local.FirstOrDefault(t => t.Id == c.Id && string.Equals(t.Created, c.Created, StringComparison.Ordinal));
            return null;
        }

        static string Escape(string s) =>
            (s ?? "").Replace("%", "%25").Replace("\t", "%09").Replace("\r", "%0D").Replace("\n", "%0A");

        static string Unescape(string s) =>
            (s ?? "").Replace("%09", "\t").Replace("%0D", "\r").Replace("%0A", "\n").Replace("%25", "%");

        // ─── MultiTerm lines ────────────────────────────────────────

        /// <summary>
        /// Synthetic ids from a MultiTerm value: each line an absolute termbase path (hashed the way
        /// <see cref="MultiTermProjectDetector"/> does) or, as written up to 4.20.65, a bare synthetic id.
        /// </summary>
        public static List<long> ParseMultiTermIds(string raw)
        {
            var ids = new List<long>();
            foreach (var line in Lines(raw))
            {
                var entry = line.Trim();
                long id = long.TryParse(entry, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : MultiTermProjectDetector.MakeStableSyntheticId(entry);
                if (!ids.Contains(id)) ids.Add(id);
            }
            return ids;
        }

        /// <summary>
        /// Id → path for MultiTerm termbases: those attached to the project, plus any path a stored value already
        /// names (so a termbase not attached right now keeps its readable path rather than turning into a number).
        /// </summary>
        public static Dictionary<long, string> MultiTermPaths(IEnumerable<string> attachedPaths, ProjectProfileValues stored)
        {
            var map = new Dictionary<long, string>();
            void Add(string p)
            {
                p = (p ?? "").Trim();
                if (p.Length == 0 || long.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) return;
                var id = MultiTermProjectDetector.MakeStableSyntheticId(p);
                if (!map.ContainsKey(id)) map[id] = p;
            }
            foreach (var p in attachedPaths ?? Enumerable.Empty<string>()) Add(p);
            if (stored != null)
                foreach (var p in Lines(stored.DisabledMultiTerm).Concat(Lines(stored.AiEnabledMultiTerm))) Add(p);
            return map;
        }

        static string FormatMultiTerm(IEnumerable<long> ids, IDictionary<long, string> paths, string existing)
        {
            var wanted = (ids ?? Enumerable.Empty<long>()).Distinct().ToList();
            // The same termbases as stored (however written): keep the stored text, so nothing is rewritten for nothing.
            var stored = ParseMultiTermIds(existing);
            if (stored.Count == wanted.Count && !wanted.Except(stored).Any()) return existing ?? "";
            return string.Join("\n", wanted
                .Select(id => paths != null && paths.TryGetValue(id, out var p) ? p : id.ToString(CultureInfo.InvariantCulture))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
        }

        // ─── read: bundle over sidecar ──────────────────────────────

        /// <summary>
        /// Puts the bundle's values into <paramref name="ps"/> wherever the bundle has one. Termbase choices are
        /// applied only when this computer's termbase list is known (<paramref name="local"/> not empty): without it
        /// every choice would read as "off", and a later save would make that permanent. <paramref name="report"/>
        /// hears of every stored termbase this computer lacks and every line that cannot be read.
        /// </summary>
        public static void Overlay(ProjectSettings ps, ProjectProfileValues group, IList<TermbaseIdentity> local, Action<string> report)
        {
            if (ps == null || group == null) return;

            if (ProjectProfileValues.Has(group.MemoryBankName)) ps.MemoryBankName = group.MemoryBankName.Trim();
            if (ProjectProfileValues.Has(group.TranslatePromptPath)) ps.ActivePromptPath = group.TranslatePromptPath.Trim();
            if (ProjectProfileValues.Has(group.ProofreadPromptPath)) ps.ActiveProofreadPromptPath = group.ProofreadPromptPath.Trim();
            if (ProjectProfileValues.Has(group.ClientName)) ps.Client = group.ClientName.Trim();

            var choices = ParseTermbases(group.Termbases);
            if (choices.Count > 0 && !choices.Any(c => c.Parsed))
                report?.Invoke("the termbase choices in the project settings cannot be read; Supervertaler's own record for the project is used instead");
            else if (choices.Count > 0)
            {
                var resolved = new List<(TermbaseChoice c, TermbaseIdentity tb)>();
                foreach (var c in choices)
                {
                    if (!c.Parsed) { report?.Invoke("unreadable termbase line in the project settings, kept as it is: '" + c.Line + "'"); continue; }
                    var tb = Resolve(c, local);
                    if (tb == null) report?.Invoke("termbase '" + c.Name + "' (" + c.FlagText() + ") is named in the project settings but not in this computer's termbase database; kept in the project, off here");
                    else resolved.Add((c, tb));
                }

                if (local != null && local.Count > 0)
                {
                    var read = new HashSet<long>(resolved.Where(r => r.c.Read).Select(r => r.tb.Id));
                    var ai = new HashSet<long>(resolved.Where(r => r.c.Ai).Select(r => r.tb.Id));
                    ps.DisabledTermbaseIds = local.Select(t => t.Id).Where(id => !read.Contains(id)).Distinct().ToList();
                    ps.DisabledAiTermbaseIds = local.Select(t => t.Id).Where(id => !ai.Contains(id)).Distinct().ToList();
                    ps.WriteTermbaseIds = resolved.Where(r => r.c.Write).Select(r => r.tb.Id).Distinct().ToList();
                    ps.ProjectTermbaseId = resolved.Where(r => r.c.Project).Select(r => r.tb.Id).DefaultIfEmpty(-1).First();
                    ps.AiTermbaseIdsInitialized = true;
                    // Every local termbase has been decided above, the highest-numbered included, whether or not it
                    // appears in a list: ApplyProjectOverlay must not take it for one created after this file (#103).
                    ps.KnownTermbaseCeiling = local.Max(t => t.Id);
                }
            }

            if (ProjectProfileValues.Has(group.DisabledMultiTerm)) ps.DisabledMultiTermIds = ParseMultiTermIds(group.DisabledMultiTerm);
            if (ProjectProfileValues.Has(group.AiEnabledMultiTerm)) ps.EnabledAiMultiTermIds = ParseMultiTermIds(group.AiEnabledMultiTerm);
        }

        // ─── write: sidecar values into the bundle ──────────────────

        /// <summary>
        /// The bundle values for <paramref name="ps"/> (the effective settings being saved), given what the bundle
        /// holds now (<paramref name="existing"/>, may be null). The profile name, which the sidecar does not hold,
        /// is kept. Termbase lines are rewritten only when a choice changed, or a termbase's stored name is no longer
        /// its name here; lines this computer cannot resolve are kept as they are. Without a termbase list
        /// (<paramref name="local"/> empty) the stored termbase value is left alone.
        /// </summary>
        public static ProjectProfileValues FromSidecar(ProjectSettings ps, ProjectProfileValues existing,
            IList<TermbaseIdentity> local, IDictionary<long, string> multiTermPaths)
        {
            existing = existing ?? new ProjectProfileValues();
            var v = new ProjectProfileValues
            {
                MemoryBankName = ps?.MemoryBankName ?? "",
                TranslatePromptPath = ps?.ActivePromptPath ?? "",
                ProofreadPromptPath = ps?.ActiveProofreadPromptPath ?? "",
                ClientName = ps?.Client ?? "",
                ProfileName = existing.ProfileName ?? "",
                Termbases = existing.Termbases ?? "",
                DisabledMultiTerm = FormatMultiTerm(ps?.DisabledMultiTermIds, multiTermPaths, existing.DisabledMultiTerm),
                AiEnabledMultiTerm = FormatMultiTerm(ps?.EnabledAiMultiTermIds, multiTermPaths, existing.AiEnabledMultiTerm),
            };
            if (ps == null || local == null || local.Count == 0) return v;

            // What ps says for each local termbase, with ApplyProjectOverlay's reading of the lists: one above
            // everything the file mentions is off (#103).
            var knownMax = TermLensSettings.HighestKnownTermbaseId(ps);
            var disabled = new HashSet<long>(ps.DisabledTermbaseIds ?? new List<long>());
            var disabledAi = new HashSet<long>(ps.DisabledAiTermbaseIds ?? new List<long>());
            var write = new HashSet<long>(ps.WriteTermbaseIds ?? new List<long>());
            bool aiNeverChosen = !ps.AiTermbaseIdsInitialized && disabledAi.Count == 0;   // a file from before the opt-in default
            var wanted = new Dictionary<long, string>();
            var lines = new List<string>();
            foreach (var tb in local)
            {
                bool known = tb.Id <= knownMax;
                bool r = known && !disabled.Contains(tb.Id);
                bool w = write.Contains(tb.Id);
                bool p = ps.ProjectTermbaseId == tb.Id;
                bool a = known && !aiNeverChosen && !disabledAi.Contains(tb.Id);
                if (!(r || w || p || a)) continue;
                var line = FormatTermbase(tb, r, w, p, a);
                wanted[tb.Id] = line.Substring(0, line.IndexOf('\t'));
                lines.Add(line);
            }

            // What the bundle holds, read the same way: unchanged unless a flag differs or a name was resolved
            // other than exactly.
            var kept = new List<string>();
            var have = new Dictionary<long, string>();
            bool inexact = false;
            foreach (var c in ParseTermbases(existing.Termbases))
            {
                var tb = Resolve(c, local);
                if (tb == null) { kept.Add(c.Line); continue; }   // not here (or unreadable): carried, never dropped
                if (!string.Equals(tb.Name, c.Name, StringComparison.Ordinal)) inexact = true;
                if (!c.Any) continue;
                have[tb.Id] = MergeFlags(have.TryGetValue(tb.Id, out var f) ? f : "", c.FlagText());
            }
            bool same = !inexact && have.Count == wanted.Count && wanted.All(kv => have.TryGetValue(kv.Key, out var f) && f == kv.Value);
            if (!same)
                v.Termbases = string.Join("\n", lines.Concat(kept).OrderBy(l => l, StringComparer.Ordinal));
            return v;
        }

        static string MergeFlags(string a, string b)
        {
            var c = new TermbaseChoice
            {
                Read = a.Contains('R') || b.Contains('R'), Write = a.Contains('W') || b.Contains('W'),
                Project = a.Contains('P') || b.Contains('P'), Ai = a.Contains('A') || b.Contains('A'),
            };
            return c.FlagText();
        }
    }
}
