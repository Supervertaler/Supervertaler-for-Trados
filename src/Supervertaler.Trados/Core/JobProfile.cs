using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Supervertaler.Trados.Core
{
    // ─── what a Studio project template carries ─────────────────────

    /// <summary>One translation provider in a template's list: a TM (file or server) or another provider (MT).</summary>
    public sealed class TemplateProvider
    {
        public string Uri = "";
        public string State;          // as stored; null when the template has none
        public bool Enabled = true;

        public bool IsFileTm => JobProfile.SchemeOf(Uri) == "sdltm.file";
        public bool IsServerTm => !IsFileTm && JobProfile.SchemeOf(Uri).StartsWith("sdltm.", StringComparison.Ordinal);
        /// <summary>The .sdltm path of a file TM; null otherwise.</summary>
        public string FilePath => IsFileTm ? ProjectCreator.TmPathFromUri(Uri) : null;
        /// <summary>For people: a file TM's name, a server TM's last path part, otherwise the provider's kind.</summary>
        public string DisplayName => JobProfile.ProviderName(this);
    }

    /// <summary>One line of a template's TM list, with its lookup / update / concordance switches.</summary>
    public sealed class TemplateTmEntry
    {
        public TemplateProvider Main = new TemplateProvider();
        public List<TemplateProvider> ProjectTms = new List<TemplateProvider>();
        public bool Update, Lookup = true, Concordance = true;
        public int Penalty;
    }

    /// <summary>A template's TM list: the one for all language pairs, or one pair's own.</summary>
    public sealed class TemplateTmList
    {
        public string Source = "", Target = "";   // "" for the list for all language pairs
        public bool OverrideParent, StopWhenFound;
        public List<TemplateTmEntry> Entries = new List<TemplateTmEntry>();
    }

    public sealed class TemplateTermbase
    {
        public string Name = "", SettingsXml = "", FilterName, Path = "";
        public bool Enabled = true, Local = true;
    }

    public sealed class TemplateTermRecognition
    {
        public int MinimumMatchValue, SearchDepth;
        public bool ShowWithNoAvailableTranslation;
        public string SearchOrder = "";   // Hierarchical, Sequential or Parallel
    }

    /// <summary>
    /// Everything a job profile (a Studio project template, .sdltpl) carries that "Apply job profile" uses. Read from the
    /// file itself, fresh every time; no Studio types, so it is tested outside Studio.
    /// </summary>
    public sealed class TemplateProfile
    {
        public string Path = "";
        /// <summary>The template's file name without extension.</summary>
        public string Name = "";
        /// <summary>Supervertaler's group in the template's settings; null when the template has none.</summary>
        public ProjectProfileValues Supervertaler;
        /// <summary>Studio's customer, by name (GeneralProjectInfoSettings.CustomerId); "" when none.</summary>
        public string CustomerName = "";
        public TemplateTmList AllPairs = new TemplateTmList();
        public List<TemplateTmList> PerPair = new List<TemplateTmList>();
        public List<TemplateTermbase> Termbases = new List<TemplateTermbase>();
        /// <summary>Language code → termbase index name.</summary>
        public List<KeyValuePair<string, string>> TermbaseIndexes = new List<KeyValuePair<string, string>>();
        /// <summary>Null when the template has none: the project's own are kept.</summary>
        public TemplateTermRecognition Recognition;

        /// <summary>The job profile's name: what the template's Supervertaler group calls it, else the file name.</summary>
        public string ProfileName =>
            ProjectProfileValues.Has(Supervertaler?.ProfileName) ? Supervertaler.ProfileName.Trim() : Name;

        public bool HasAnyTm => AllPairs.Entries.Count > 0 || PerPair.Any(p => p.Entries.Count > 0);
    }

    // ─── what applying it to a project would do ─────────────────────

    /// <summary>What the dialog knows about the project before applying (read on the UI thread).</summary>
    public sealed class ProjectFacts
    {
        public string Name = "", Path = "", Source = "";
        public List<string> Targets = new List<string>();
        /// <summary>The project's termbase language indexes now: language code → index name.</summary>
        public List<KeyValuePair<string, string>> TermbaseIndexes = new List<KeyValuePair<string, string>>();
        /// <summary>The project's Studio customer now; "" for none.</summary>
        public string CustomerName = "";
        /// <summary>Studio's customer list; null when it could not be read.</summary>
        public List<string> CustomerNames;
        /// <summary>From a package or GroupShare: only Supervertaler's own record is changed.</summary>
        public bool FromElsewhere;
    }

    public sealed class TmPlan
    {
        /// <summary>The template has no TMs: the project's are left as they are.</summary>
        public bool LeaveAlone;
        public TemplateTmList AllPairs = new TemplateTmList();
        /// <summary>Per project target language: the list it gets (empty and not overriding = the list for all pairs).</summary>
        public List<KeyValuePair<string, TemplateTmList>> PerTarget = new List<KeyValuePair<string, TemplateTmList>>();
        public List<string> Notes = new List<string>();
        public bool HasServerTms => AllPairs.Entries.Concat(PerTarget.SelectMany(p => p.Value.Entries)).Any(e => e.Main.IsServerTm);
    }

    public sealed class TermbasePlan
    {
        /// <summary>The template has no usable termbases: the project's are left as they are.</summary>
        public bool LeaveAlone;
        public List<TemplateTermbase> Termbases = new List<TemplateTermbase>();
        public List<KeyValuePair<string, string>> Indexes = new List<KeyValuePair<string, string>>();
        /// <summary>Null: keep the project's own recognition options.</summary>
        public TemplateTermRecognition Recognition;
        public List<string> Notes = new List<string>();
    }

    public enum CustomerChoice { LeaveAlone, AlreadySet, Change, AskToAdd, CannotRead }

    public sealed class CustomerDecision
    {
        public CustomerChoice Choice;
        /// <summary>The name to set: as Studio's list spells it (Change), or as the template does (AskToAdd).</summary>
        public string Name = "";
    }

    /// <summary>The template read and the plan for one project: what the confirmation shows and what is applied.</summary>
    public sealed class PreparedProfile
    {
        public TemplateProfile Template;
        public TmPlan Tms;
        public TermbasePlan Termbases;
        public CustomerDecision Customer;
    }

    public enum StepOutcome { Done, Unchanged, Failed }

    /// <summary>One part of an apply, for the summary.</summary>
    public sealed class ApplyStep
    {
        public string What = "";
        public StepOutcome Outcome;
        public string Detail = "";
        public ApplyStep(string what, StepOutcome outcome, string detail) { What = what; Outcome = outcome; Detail = detail ?? ""; }
    }

    /// <summary>
    /// Job profiles, step 2: the rules for applying a Studio project template ("job profile") to a project that already
    /// exists, and for Studio's customer list. Pure: no Studio types, everything here is tested outside Studio
    /// (tools\job_profile_step2_test.ps1). The Studio side is <see cref="JobProfileStudio"/> and <see cref="StudioCustomers"/>.
    ///
    /// <para><b>Reading a template.</b> Studio's public API cannot open a template (only create a project from one, which
    /// also registers it), so the .sdltpl is read as the XML file it is: its TM lists, termbase configuration, and its
    /// project settings bundle through Studio's own public <c>SettingsUtil.DeserializeSettingsBundle</c>. What is read is
    /// then written through the public API (<c>UpdateTranslationProviderConfiguration</c>, <c>UpdateTermbaseConfiguration</c>).
    /// A template that cannot be read in full changes nothing.</para>
    ///
    /// <para><b>Rules.</b> A part the template does not have leaves the project's own as it is (a template with no TMs
    /// never empties a project's TM list). A file TM or termbase the template names but that is not there is left out
    /// and reported. Server termbases are reported, not set (Studio 2022/2024 and 2026 differ in how one is given).</para>
    /// </summary>
    public static class JobProfile
    {
        // ─── names ──────────────────────────────────────────────────

        /// <summary>Alphabetical as a person expects: by the user's language, ignoring case; ties in a fixed order.</summary>
        public static int CompareNames(string a, string b)
        {
            int c = CultureInfo.CurrentCulture.CompareInfo.Compare((a ?? "").Trim(), (b ?? "").Trim(), CompareOptions.IgnoreCase);
            return c != 0 ? c : string.CompareOrdinal(a ?? "", b ?? "");
        }

        public static List<T> SortByName<T>(IEnumerable<T> items, Func<T, string> name)
        {
            var list = (items ?? Enumerable.Empty<T>()).ToList();
            // A stable sort: List.Sort is not, and equal names must keep one order every time.
            return list.Select((x, i) => (x, i)).OrderBy(p => p, Comparer<(T x, int i)>.Create((p, q) =>
            {
                int c = CompareNames(name(p.x), name(q.x));
                return c != 0 ? c : p.i.CompareTo(q.i);
            })).Select(p => p.x).ToList();
        }

        /// <summary>
        /// Two customer names are the same customer when they match ignoring case and surrounding spaces: that is how
        /// Studio finds a template's customer in its list (InvariantCultureIgnoreCase), so two names that differ only in
        /// case could not be told apart there.
        /// </summary>
        public static bool SameCustomerName(string a, string b) =>
            string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.InvariantCultureIgnoreCase);

        /// <summary>
        /// Null when <paramref name="name"/> can be used for a customer; otherwise why not, for the user.
        /// <paramref name="existing"/> is Studio's list as (id, name); <paramref name="self"/> the customer being renamed.
        /// </summary>
        public static string CheckCustomerName(string name, IEnumerable<KeyValuePair<Guid, string>> existing, Guid? self = null)
        {
            var n = (name ?? "").Trim();
            if (n.Length == 0) return "A customer needs a name.";
            if (n.IndexOfAny(new[] { '\r', '\n', '\t' }) >= 0) return "A customer's name must be on one line.";
            var clash = (existing ?? Enumerable.Empty<KeyValuePair<Guid, string>>())
                .FirstOrDefault(c => (self == null || c.Key != self.Value) && SameCustomerName(c.Value, n));
            if (clash.Value != null)
                return "Studio already has a customer called \"" + clash.Value.Trim() + "\""
                    + (string.Equals(clash.Value.Trim(), n, StringComparison.Ordinal) ? "." : " (names that differ only in capitals count as the same).");
            return null;
        }

        /// <summary>The question before removing a customer that <paramref name="projectCount"/> projects use.</summary>
        public static string RemoveQuestion(string name, int projectCount)
        {
            var q = "Remove the customer \"" + name + "\" from Studio's customer list?";
            if (projectCount < 0) return q + "\n\nSupervertaler could not check which projects use it. Projects that do keep it as their customer.";
            if (projectCount == 0) return q + "\n\nNo project in Studio's project list uses it.";
            return q + "\n\n" + (projectCount == 1 ? "1 project in Studio's project list uses it." : projectCount + " projects in Studio's project list use it.")
                + " They keep it as their customer, but Studio will no longer offer it for other projects.";
        }

        // ─── reading a template ─────────────────────────────────────

        internal static string SchemeOf(string uri)
        {
            var u = uri ?? "";
            int i = u.IndexOf(':');
            return i <= 0 ? "" : u.Substring(0, i).ToLowerInvariant();
        }

        internal static string ProviderName(TemplateProvider p)
        {
            if (p == null) return "";
            if (p.IsFileTm)
            {
                var f = p.FilePath;
                return string.IsNullOrEmpty(f) ? p.Uri : System.IO.Path.GetFileNameWithoutExtension(f);
            }
            if (p.IsServerTm)
            {
                var u = p.Uri;
                int q = u.IndexOf('?'); if (q >= 0) u = u.Substring(0, q);
                var last = u.TrimEnd('/').Split('/').LastOrDefault() ?? u;
                try { last = System.Uri.UnescapeDataString(last); } catch { }
                return last + " (server TM)";
            }
            var scheme = SchemeOf(p.Uri);
            return (scheme.Length > 0 ? scheme : p.Uri) + " (provider)";
        }

        /// <summary>
        /// Reads a Studio project template. Null when it was read; otherwise why not, naming the file, for the user.
        /// <paramref name="readBundle"/> turns the template's project settings bundle (its inner &lt;SettingsBundle&gt;
        /// element) into Supervertaler's values and the customer name; it is Studio's own deserialiser in the plugin
        /// (<see cref="ReadBundleWithStudio"/>). Throws nothing.
        /// </summary>
        public static string ReadTemplate(string path, out TemplateProfile template,
            Func<XElement, (ProjectProfileValues supervertaler, string customer)> readBundle = null)
        {
            template = null;
            if (string.IsNullOrWhiteSpace(path)) return "No template was chosen.";
            XDocument doc;
            try
            {
                using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    doc = XDocument.Load(s);
            }
            catch (Exception ex) { return "The template \"" + path + "\" " + WhyUnreadable(ex); }

            var root = doc.Root;
            if (root == null || root.Name.LocalName != "ProjectTemplate")
                return "The template \"" + path + "\" is not a Studio project template (no <ProjectTemplate> in it). Nothing was changed.";

            var t = new TemplateProfile { Path = path, Name = System.IO.Path.GetFileNameWithoutExtension(path) };
            try
            {
                t.AllPairs = ReadTmList(Child(root, "CascadeItem"), "", "");
                foreach (var ld in Children(Child(root, "LanguageDirections"), "LanguageDirection"))
                {
                    var list = ReadTmList(Child(ld, "CascadeItem"), Attr(ld, "SourceLanguageCode"), Attr(ld, "TargetLanguageCode"));
                    if (list.Target.Length > 0) t.PerPair.Add(list);
                }

                var tbc = Child(root, "TermbaseConfiguration");
                foreach (var tb in Children(tbc, "Termbases")) t.Termbases.Add(ReadTermbase(tb));
                foreach (var m in Children(tbc, "LanguageIndexMappings"))
                {
                    var lang = Text(Child(m, "Language")).Trim();
                    var index = Text(Child(m, "Index")).Trim();
                    if (lang.Length > 0 && index.Length > 0) t.TermbaseIndexes.Add(new KeyValuePair<string, string>(lang, index));
                }
                var ro = Child(tbc, "RecognitionOptions");
                if (ro != null)
                    t.Recognition = new TemplateTermRecognition
                    {
                        MinimumMatchValue = Int(Text(Child(ro, "MinimumMatchValue")), 70),
                        SearchDepth = Int(Text(Child(ro, "SearchDepth")), 200),
                        ShowWithNoAvailableTranslation = Bool(Text(Child(ro, "ShowWithNoAvailableTranslation")), false),
                        SearchOrder = Text(Child(ro, "SearchOrder")).Trim(),
                    };
            }
            catch (Exception ex)
            {
                return "The template \"" + path + "\" could not be read as a Studio project template (" + ex.Message + "). Nothing was changed.";
            }

            var bundle = ProjectBundleElement(root);
            if (bundle != null && readBundle != null)
            {
                try
                {
                    var (sv, customer) = readBundle(bundle);
                    t.Supervertaler = sv;
                    t.CustomerName = (customer ?? "").Trim();
                }
                catch (Exception ex)
                {
                    return "The settings in the template \"" + path + "\" could not be read (" + ex.Message + "). Nothing was changed.";
                }
            }
            template = t;
            return null;
        }

        static string WhyUnreadable(Exception ex)
        {
            if (ex is FileNotFoundException || ex is DirectoryNotFoundException)
                return "is not there any more (moved, renamed or deleted?). Nothing was changed.";
            if (ex is UnauthorizedAccessException)
                return "could not be opened: no permission to read it. Nothing was changed.";
            if (ex is XmlException)
                return "is not a readable Studio project template (" + ex.Message + "). Nothing was changed.";
            if (ex is IOException)
                return "could not be read (" + ex.Message + "). If it is on a network drive or NAS, check that it is reachable. Nothing was changed.";
            return "could not be read (" + ex.GetType().Name + ": " + ex.Message + "). Nothing was changed.";
        }

        /// <summary>The project-level settings bundle: the inner &lt;SettingsBundle&gt; of the one the root's SettingsBundleGuid names.</summary>
        internal static XElement ProjectBundleElement(XElement root)
        {
            var guid = Attr(root, "SettingsBundleGuid");
            if (guid.Length == 0) return null;
            var outer = Children(Child(root, "SettingsBundles"), "SettingsBundle")
                .FirstOrDefault(b => string.Equals(Attr(b, "Guid"), guid, StringComparison.OrdinalIgnoreCase));
            return outer == null ? null : Child(outer, "SettingsBundle");
        }

        /// <summary>
        /// Supervertaler's values and the customer name from a template's project settings bundle, with Studio's own
        /// public deserialiser (Sdl.Core.Settings). Supervertaler's part is null when the template has no group of its own.
        /// </summary>
        public static (ProjectProfileValues supervertaler, string customer) ReadBundleWithStudio(XElement bundleElement)
        {
            Sdl.Core.Settings.ISettingsBundle bundle;
            using (var r = bundleElement.CreateReader())
            {
                r.MoveToContent();
                bundle = Sdl.Core.Settings.SettingsUtil.DeserializeSettingsBundle(r, null);
            }
            ProjectProfileValues sv = null;
            if (bundle.ContainsSettingsGroup(nameof(SupervertalerProjectSettingsGroup)))
                sv = ProjectBundleSettings.ReadValues(bundle);
            string customer = "";
            if (bundle.ContainsSettingsGroup("GeneralProjectInfoSettings"))
            {
                var g = bundle.GetSettingsGroup("GeneralProjectInfoSettings");
                if (g != null && g.ContainsSetting("CustomerId") && g.GetSetting<string>("CustomerId", out string v)) customer = v ?? "";
            }
            return (sv, customer);
        }

        static TemplateTmList ReadTmList(XElement cascade, string source, string target)
        {
            var list = new TemplateTmList { Source = source ?? "", Target = target ?? "" };
            if (cascade == null) return list;
            list.OverrideParent = Bool(Attr(cascade, "OverrideParent"), false);
            list.StopWhenFound = Bool(Attr(cascade, "StopSearchingWhenResultsFound"), false);
            foreach (var e in Children(cascade, "CascadeEntryItem"))
            {
                var entry = new TemplateTmEntry
                {
                    Main = ReadProvider(Child(e, "MainTranslationProviderItem")),
                    Update = Bool(Attr(e, "PerformUpdate"), false),
                    Lookup = Bool(Attr(e, "PerformNormalSearch"), true),
                    Concordance = Bool(Attr(e, "PerformConcordanceSearch"), true),
                    Penalty = Int(Attr(e, "Penalty"), 0),
                };
                foreach (var p in Children(e, "ProjectTranslationProviderItem")) entry.ProjectTms.Add(ReadProvider(p));
                list.Entries.Add(entry);
            }
            return list;
        }

        static TemplateProvider ReadProvider(XElement e) =>
            e == null ? new TemplateProvider() : new TemplateProvider
            {
                Uri = Attr(e, "Uri"),
                State = e.Attribute("State")?.Value,
                Enabled = Bool(Attr(e, "Enabled"), true),
            };

        static TemplateTermbase ReadTermbase(XElement e)
        {
            var tb = new TemplateTermbase
            {
                Name = Text(Child(e, "Name")).Trim(),
                SettingsXml = Text(Child(e, "SettingsXml")),
                Enabled = Bool(Text(Child(e, "Enabled")), true),
            };
            var filter = Text(Child(Child(e, "Filter"), "Name")).Trim();
            tb.FilterName = filter.Length > 0 ? filter : null;
            try
            {
                var settings = XDocument.Parse(tb.SettingsXml).Root;
                tb.Path = Text(Child(settings, "Path")).Trim();
                tb.Local = Bool(Text(Child(settings, "Local")), true);
            }
            catch { tb.Path = ""; }   // unreadable settings: left out with a note when planned
            return tb;
        }

        static XElement Child(XElement e, string name) => e?.Elements().FirstOrDefault(x => x.Name.LocalName == name);
        static IEnumerable<XElement> Children(XElement e, string name) =>
            e == null ? Enumerable.Empty<XElement>() : e.Elements().Where(x => x.Name.LocalName == name);
        static string Attr(XElement e, string name) => e?.Attribute(name)?.Value ?? "";
        static string Text(XElement e) => e?.Value ?? "";
        static bool Bool(string s, bool fallback) => bool.TryParse((s ?? "").Trim(), out var b) ? b : fallback;
        static int Int(string s, int fallback) =>
            int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : fallback;

        // ─── planning ───────────────────────────────────────────────

        /// <summary>The template read and planned for <paramref name="facts"/>. Null and the reason when the template cannot be read.</summary>
        public static string Prepare(string templatePath, ProjectFacts facts, Func<string, bool> fileExists,
            Func<XElement, (ProjectProfileValues, string)> readBundle, out PreparedProfile prepared)
        {
            prepared = null;
            var error = ReadTemplate(templatePath, out var t, readBundle);
            if (error != null) return error;
            prepared = new PreparedProfile
            {
                Template = t,
                Tms = PlanTms(t, facts, fileExists),
                Termbases = PlanTermbases(t, facts, fileExists),
                Customer = DecideCustomer(t.CustomerName, facts),
            };
            return null;
        }

        /// <summary>
        /// The TM lists the project gets. Each project target language gets the template's list for that pair when it has
        /// one (same source and target; else the only one with that target), otherwise none of its own, so the list for all
        /// language pairs applies. Left out, with a note: a file TM that is not there, a provider whose address cannot be
        /// read, and a second line for the same provider (Studio refuses the whole list over either).
        /// </summary>
        public static TmPlan PlanTms(TemplateProfile t, ProjectFacts facts, Func<string, bool> fileExists)
        {
            var plan = new TmPlan();
            if (t == null || !t.HasAnyTm)
            {
                plan.LeaveAlone = true;
                plan.Notes.Add("The template has no TMs: the project's TMs are left as they are.");
                return plan;
            }
            var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            plan.AllPairs = Usable(t.AllPairs, fileExists, plan.Notes, reported);
            foreach (var target in facts?.Targets ?? new List<string>())
            {
                var candidates = t.PerPair.Where(p => string.Equals(p.Target, target, StringComparison.OrdinalIgnoreCase)).ToList();
                var match = candidates.FirstOrDefault(p => string.Equals(p.Source, facts.Source, StringComparison.OrdinalIgnoreCase))
                            ?? (candidates.Count == 1 ? candidates[0] : null);
                if (match != null)
                {
                    plan.PerTarget.Add(new KeyValuePair<string, TemplateTmList>(target, Usable(match, fileExists, plan.Notes, reported)));
                    if (!string.Equals(match.Source, facts.Source, StringComparison.OrdinalIgnoreCase))
                        plan.Notes.Add("For " + target + " the template's pair " + match.Source + " → " + match.Target + " is used (the project's source language is " + facts.Source + ").");
                }
                else
                {
                    plan.PerTarget.Add(new KeyValuePair<string, TemplateTmList>(target, new TemplateTmList { Source = facts.Source, Target = target }));
                    if (t.PerPair.Count > 0)
                        plan.Notes.Add(facts.Source + " → " + target + " is not one of the template's language pairs: it uses the template's TMs for all language pairs.");
                }
            }
            return plan;
        }

        static TemplateTmList Usable(TemplateTmList list, Func<string, bool> fileExists, List<string> notes, HashSet<string> reported)
        {
            var copy = new TemplateTmList { Source = list.Source, Target = list.Target, OverrideParent = list.OverrideParent, StopWhenFound = list.StopWhenFound };
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in list.Entries)
            {
                var name = e.Main.DisplayName;
                if (!System.Uri.TryCreate(e.Main.Uri, UriKind.Absolute, out _))
                {
                    if (reported.Add("bad|" + e.Main.Uri)) notes.Add("Left out: a TM whose address cannot be read (\"" + e.Main.Uri + "\").");
                    continue;
                }
                if (e.Main.IsFileTm && (string.IsNullOrEmpty(e.Main.FilePath) || !(fileExists?.Invoke(e.Main.FilePath) ?? true)))
                {
                    if (reported.Add("missing|" + e.Main.Uri)) notes.Add("Left out: the TM \"" + (e.Main.FilePath ?? e.Main.Uri) + "\" is not there.");
                    continue;
                }
                if (!seen.Add(e.Main.Uri + "|" + (e.Main.State ?? "")))
                {
                    if (reported.Add("twice|" + e.Main.Uri)) notes.Add("Left out: " + name + " was in the template's list twice; once is kept.");
                    continue;
                }
                var entry = new TemplateTmEntry
                {
                    Main = e.Main,
                    Lookup = e.Lookup || e.Update,   // Studio refuses "update" without "lookup"
                    Update = e.Update,
                    Concordance = e.Concordance,
                    Penalty = Math.Max(0, e.Penalty),
                    ProjectTms = e.ProjectTms.Where(p => System.Uri.TryCreate(p.Uri, UriKind.Absolute, out _) && p.Uri != e.Main.Uri).ToList(),
                };
                if (e.Main.IsServerTm && reported.Add("server|" + e.Main.Uri))
                    notes.Add(name + " is on a server: Studio asks you to sign in to it if it needs to.");
                copy.Entries.Add(entry);
            }
            return copy;
        }

        /// <summary>
        /// The termbases the project gets: the template's local termbases that are there, in its order (the first is the
        /// default). Language indexes: the template's, plus the project's own for languages the template does not map.
        /// Recognition options: the template's when it has them. With no usable termbase the project's are left alone.
        /// </summary>
        public static TermbasePlan PlanTermbases(TemplateProfile t, ProjectFacts facts, Func<string, bool> fileExists)
        {
            var plan = new TermbasePlan();
            if (t == null || t.Termbases.Count == 0)
            {
                plan.LeaveAlone = true;
                plan.Notes.Add("The template has no termbases: the project's termbases are left as they are.");
                return plan;
            }
            foreach (var tb in t.Termbases)
            {
                if (!tb.Local)
                {
                    plan.Notes.Add("Not set: the server termbase \"" + tb.Name + "\". Add it in Studio's Project Settings > Termbases.");
                    continue;
                }
                if (tb.Path.Length == 0)
                {
                    plan.Notes.Add("Left out: the termbase \"" + tb.Name + "\" (its settings in the template cannot be read).");
                    continue;
                }
                if (!(fileExists?.Invoke(tb.Path) ?? true))
                {
                    plan.Notes.Add("Left out: the termbase \"" + tb.Path + "\" is not there.");
                    continue;
                }
                if (plan.Termbases.Any(x => string.Equals(x.Path, tb.Path, StringComparison.OrdinalIgnoreCase))) continue;
                plan.Termbases.Add(tb);
            }
            if (plan.Termbases.Count == 0)
            {
                plan.LeaveAlone = true;
                plan.Notes.Add("None of the template's termbases can be used here: the project's termbases are left as they are.");
                return plan;
            }
            plan.Indexes.AddRange(t.TermbaseIndexes.GroupBy(m => m.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.First()));
            foreach (var own in facts?.TermbaseIndexes ?? new List<KeyValuePair<string, string>>())
                if (!plan.Indexes.Any(m => string.Equals(m.Key, own.Key, StringComparison.OrdinalIgnoreCase)))
                    plan.Indexes.Add(own);
            plan.Recognition = t.Recognition;
            return plan;
        }

        /// <summary>
        /// The customer: the template's, found in Studio's list the way Studio finds it (ignoring case). A template with
        /// none leaves the project's as it is; a name not in the list is offered to be added, never added unasked.
        /// </summary>
        public static CustomerDecision DecideCustomer(string templateCustomer, ProjectFacts facts)
        {
            var wanted = (templateCustomer ?? "").Trim();
            if (wanted.Length == 0) return new CustomerDecision { Choice = CustomerChoice.LeaveAlone };
            if (facts?.CustomerNames == null) return new CustomerDecision { Choice = CustomerChoice.CannotRead, Name = wanted };
            var known = facts.CustomerNames.FirstOrDefault(n => SameCustomerName(n, wanted));
            if (known == null) return new CustomerDecision { Choice = CustomerChoice.AskToAdd, Name = wanted };
            if (SameCustomerName(facts.CustomerName, known) && string.Equals((facts.CustomerName ?? "").Trim(), known.Trim(), StringComparison.Ordinal))
                return new CustomerDecision { Choice = CustomerChoice.AlreadySet, Name = known };
            return new CustomerDecision { Choice = CustomerChoice.Change, Name = known };
        }

        /// <summary>
        /// The project's Supervertaler values after applying a template: the template's wherever it has one, the project's
        /// own for the rest (the step-1 rule: the template's group wins value by value). The profile name is the job profile's.
        /// </summary>
        public static ProjectProfileValues MergeForApply(ProjectProfileValues current, ProjectProfileValues template, string profileName)
        {
            current = current ?? new ProjectProfileValues();
            template = template ?? new ProjectProfileValues();
            string Pick(string t, string c) => ProjectProfileValues.Has(t) ? t : (c ?? "");
            return new ProjectProfileValues
            {
                MemoryBankName = Pick(template.MemoryBankName, current.MemoryBankName),
                TranslatePromptPath = Pick(template.TranslatePromptPath, current.TranslatePromptPath),
                ProofreadPromptPath = Pick(template.ProofreadPromptPath, current.ProofreadPromptPath),
                ClientName = Pick(template.ClientName, current.ClientName),
                ProfileName = (profileName ?? "").Trim(),
                Termbases = Pick(template.Termbases, current.Termbases),
                DisabledMultiTerm = Pick(template.DisabledMultiTerm, current.DisabledMultiTerm),
                AiEnabledMultiTerm = Pick(template.AiEnabledMultiTerm, current.AiEnabledMultiTerm),
            };
        }

        // ─── words for people ───────────────────────────────────────

        /// <summary>What applying would do, line by line: the preview and the confirmation question.</summary>
        public static List<string> Describe(PreparedProfile p, ProjectFacts facts)
        {
            var lines = new List<string>();
            if (p?.Template == null) return lines;
            var t = p.Template;
            var sv = t.Supervertaler;
            if (sv == null)
                lines.Add("Supervertaler: the template has no Supervertaler set-up; the project keeps its own.");
            else
            {
                var parts = new List<string>();
                if (ProjectProfileValues.Has(sv.MemoryBankName)) parts.Add("memory bank \"" + sv.MemoryBankName.Trim() + "\"");
                if (ProjectProfileValues.Has(sv.TranslatePromptPath)) parts.Add("translate prompt \"" + sv.TranslatePromptPath.Trim() + "\"");
                if (ProjectProfileValues.Has(sv.ProofreadPromptPath)) parts.Add("proofread prompt \"" + sv.ProofreadPromptPath.Trim() + "\"");
                if (ProjectProfileValues.Has(sv.ClientName)) parts.Add("client \"" + sv.ClientName.Trim() + "\"");
                int tbs = ProjectProfile.Lines(sv.Termbases).Count;
                if (tbs > 0) parts.Add(tbs == 1 ? "1 Supervertaler termbase choice" : tbs + " Supervertaler termbase choices");
                if (ProjectProfileValues.Has(sv.DisabledMultiTerm) || ProjectProfileValues.Has(sv.AiEnabledMultiTerm)) parts.Add("MultiTerm termbase choices");
                lines.Add(parts.Count == 0
                    ? "Supervertaler: the template's Supervertaler set-up is empty; the project keeps its own."
                    : "Supervertaler: " + string.Join(", ", parts) + ". Anything the template leaves empty stays as the project has it.");
                if (facts != null && facts.FromElsewhere)
                    lines.Add("   This project came from a package or GroupShare: this goes into Supervertaler's own record only, not into the project.");
            }

            if (facts != null && facts.FromElsewhere)
            {
                lines.Add("Studio's TMs, termbases and customer: left as they are (the project came from a package or GroupShare).");
                return lines;
            }

            if (p.Tms.LeaveAlone) lines.Add("TMs: the template has none; the project's are left as they are.");
            else
            {
                lines.Add("TMs for all language pairs: " + ListTms(p.Tms.AllPairs));
                foreach (var kv in p.Tms.PerTarget.Where(kv => kv.Value.OverrideParent || kv.Value.Entries.Count > 0))
                    lines.Add("TMs for " + (facts?.Source ?? "") + " → " + kv.Key + ": " + ListTms(kv.Value)
                        + (kv.Value.OverrideParent ? " (instead of the list for all pairs)" : ""));
                lines.Add("   The open document keeps its old TMs until you close it and open it again.");
            }

            if (p.Termbases.LeaveAlone) lines.Add("Termbases: the project's are left as they are.");
            else lines.Add("Termbases: " + string.Join(", ", p.Termbases.Termbases.Select(tb => tb.Name.Length > 0 ? tb.Name : System.IO.Path.GetFileNameWithoutExtension(tb.Path))) + ".");

            switch (p.Customer.Choice)
            {
                case CustomerChoice.LeaveAlone: lines.Add("Customer: the template names none; the project's is left as it is."); break;
                case CustomerChoice.AlreadySet: lines.Add("Customer: \"" + p.Customer.Name + "\" already."); break;
                case CustomerChoice.Change: lines.Add("Customer: \"" + p.Customer.Name + "\"" + (ProjectProfileValues.Has(facts?.CustomerName) ? " (now \"" + facts.CustomerName + "\")" : "") + "."); break;
                case CustomerChoice.AskToAdd: lines.Add("Customer: \"" + p.Customer.Name + "\" is not in Studio's customer list; you will be asked whether to add it."); break;
                case CustomerChoice.CannotRead: lines.Add("Customer: Studio's customer list cannot be read here, so the customer is left as it is."); break;
            }

            // The notes behind those lines; "the template has none" is said above already.
            if (!p.Tms.LeaveAlone) foreach (var n in p.Tms.Notes) lines.Add("   " + n);
            if (t.Termbases.Count > 0) foreach (var n in p.Termbases.Notes) lines.Add("   " + n);
            return lines;
        }

        static string ListTms(TemplateTmList list)
        {
            if (list.Entries.Count == 0) return "none.";
            return string.Join(", ", list.Entries.Select(e =>
            {
                var flags = new List<string>();
                if (e.Lookup) flags.Add("lookup");
                if (e.Update) flags.Add("update");
                return e.Main.DisplayName + (flags.Count > 0 ? " (" + string.Join(", ", flags) + ")" : " (off)");
            })) + ".";
        }

        /// <summary>
        /// The summary after applying. Says plainly what changed, what was left as it was and what failed, and - when the
        /// TMs changed - that the open document picks them up only after it is closed and reopened.
        /// </summary>
        public static string Summary(string profileName, string projectName, IList<ApplyStep> steps)
        {
            var sb = new StringBuilder();
            steps = steps ?? new List<ApplyStep>();
            bool failed = steps.Any(s => s.Outcome == StepOutcome.Failed);
            sb.Append(failed ? "Job profile \"" + profileName + "\" applied to \"" + projectName + "\" in part." : "Job profile \"" + profileName + "\" applied to \"" + projectName + "\".");
            sb.Append("\r\n");
            void Section(string title, StepOutcome o)
            {
                var these = steps.Where(s => s.Outcome == o).ToList();
                if (these.Count == 0) return;
                sb.Append("\r\n").Append(title).Append("\r\n");
                foreach (var s in these) sb.Append("- ").Append(s.What).Append(s.Detail.Length > 0 ? ": " + s.Detail : "").Append("\r\n");
            }
            Section("Changed:", StepOutcome.Done);
            Section("Left as it was:", StepOutcome.Unchanged);
            Section("Not done:", StepOutcome.Failed);
            if (steps.Any(s => s.Outcome == StepOutcome.Done && s.What == StepTms))
                sb.Append("\r\nThe open document keeps its old TMs until you close it and open it again.\r\n");
            return sb.ToString().TrimEnd();
        }

        /// <summary>The step names, shared with the summary's TM notice.</summary>
        public const string StepSupervertaler = "Supervertaler set-up", StepTms = "TMs", StepTermbases = "Termbases", StepCustomer = "Customer", StepSave = "Saving the project";
    }
}
