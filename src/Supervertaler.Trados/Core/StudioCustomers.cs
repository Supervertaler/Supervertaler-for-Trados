using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Sdl.ProjectAutomation.FileBased;
using Sdl.TranslationStudioAutomation.IntegrationApi;

namespace Supervertaler.Trados.Core
{
    /// <summary>One entry of Studio's customer list.</summary>
    public sealed class StudioCustomer
    {
        public Guid Id;
        public string Name = "", Email = "";
    }

    /// <summary>
    /// Studio's customer list and a project's customer, for job profiles (step 2). Studio's public API has no customers,
    /// so this is the one place that reaches them, by reflection, through Studio's own objects - never by editing
    /// projects.xml behind Studio's back.
    ///
    /// <para><b>The path</b> (Studio 2022, 2024 and 2026 alike, from their decompiled code): the internal property
    /// <c>FileBasedProject.InternalProject</c> (an <c>Sdl.ProjectApi.IProject</c>), then public interface members:
    /// <c>IProject.Customer</c>, <c>IProject.ChangeCustomer(ICustomer)</c>, <c>IProjectConfiguration.ProjectsProvider</c>,
    /// <c>IProjectsProvider.CustomerProvider / MyProjects / Save()</c>, <c>ICustomerProvider.Customers / AddCustomer /
    /// RemoveCustomer</c>, and <c>ICustomer.Guid / Name / Email</c> (Name and Email can be set, so a rename keeps the
    /// customer's Guid). Keeping the list alphabetical needs two private members more (see <see cref="SortInStudio"/>).</para>
    ///
    /// <para><b>Fails closed.</b> Every step is checked; if Studio renamed anything, the caller gets a plain message,
    /// the diagnostic log gets the detail, and nothing is changed. The rest of Supervertaler is untouched by it.</para>
    ///
    /// <para><b>Threads.</b> Studio's project objects are used on the UI thread only; every entry point refuses
    /// otherwise. No state is kept between calls except whether the start-up sort has run.</para>
    ///
    /// <para><b>Scale.</b> A few hundred customers and projects: each call walks the list once (counting a customer's
    /// projects walks Studio's in-memory project list once, reading each project's customer, which loads nothing).</para>
    /// </summary>
    internal static class StudioCustomers
    {
        const string Log = "StudioCustomers";
        const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        const string Unreachable = "Studio's customer list could not be reached from Supervertaler (this Studio version may keep it differently). Nothing was changed.";

        /// <summary>Something on the reflection path was not where Studio used to keep it.</summary>
        sealed class BridgeException : Exception { public BridgeException(string m) : base(m) { } }

        // ─── reaching Studio ────────────────────────────────────────

        static bool OnUiThread => ProjectProfileStore.IsUiThread;

        /// <summary>A project to reach Studio's project list through: the one in the editor, the one selected in Projects, else any. UI thread.</summary>
        internal static FileBasedProject AnyProject(bool allowFullList = true)
        {
            try { if (StudioEditor.Controller?.ActiveDocument?.Project is FileBasedProject p) return p; } catch { }
            ProjectsController pc = null;
            try { pc = SdlTradosStudio.Application?.GetController<ProjectsController>(); } catch { }
            try { if (pc?.CurrentProject != null) return pc.CurrentProject; } catch { }
            if (!allowFullList) return null;
            try { return pc?.GetAllProjects()?.FirstOrDefault(); } catch { return null; }
        }

        static object Get(object o, string name)
        {
            if (o == null) throw new BridgeException("no object for '" + name + "'");
            var p = FindProperty(o.GetType(), name) ?? throw new BridgeException(o.GetType().FullName + " has no '" + name + "'");
            return p.GetValue(o, null);
        }

        static void Set(object o, string name, object value)
        {
            var p = FindProperty(o.GetType(), name);
            if (p == null || !p.CanWrite) throw new BridgeException(o.GetType().FullName + " has no settable '" + name + "'");
            Unwrap(() => p.SetValue(o, value, null));
        }

        /// <summary>A property of the type (also non-public), else of one of its interfaces (explicit implementations).</summary>
        static PropertyInfo FindProperty(Type t, string name)
        {
            for (var x = t; x != null; x = x.BaseType)
            {
                var p = x.GetProperties(Members | BindingFlags.DeclaredOnly).FirstOrDefault(pi => pi.Name == name && pi.GetIndexParameters().Length == 0);
                if (p != null) return p;
            }
            return t.GetInterfaces().SelectMany(i => i.GetProperties()).FirstOrDefault(pi => pi.Name == name && pi.GetIndexParameters().Length == 0);
        }

        static object Field(object o, string name)
        {
            if (o == null) throw new BridgeException("no object for '" + name + "'");
            for (var x = o.GetType(); x != null; x = x.BaseType)
            {
                var f = x.GetField(name, Members | BindingFlags.DeclaredOnly);
                if (f != null) return f.GetValue(o);
            }
            throw new BridgeException(o.GetType().FullName + " has no field '" + name + "'");
        }

        /// <summary>Calls a method by name and argument count, on the type's interfaces first (where Studio's public members are).</summary>
        static object Call(object o, string name, params object[] args)
        {
            if (o == null) throw new BridgeException("no object for '" + name + "()'");
            var t = o.GetType();
            bool Fits(MethodInfo m)
            {
                var ps = m.GetParameters();
                if (m.Name != name || ps.Length != args.Length || m.IsGenericMethodDefinition) return false;
                for (int i = 0; i < ps.Length; i++)
                    if (args[i] != null ? !ps[i].ParameterType.IsInstanceOfType(args[i]) : ps[i].ParameterType.IsValueType) return false;
                return true;
            }
            var method = t.GetInterfaces().SelectMany(i => i.GetMethods()).FirstOrDefault(Fits)
                         ?? t.GetMethods(Members).FirstOrDefault(Fits)
                         ?? throw new BridgeException(t.FullName + " has no '" + name + "' taking " + args.Length + " argument(s)");
            object result = null;
            Unwrap(() => result = method.Invoke(o, args));
            return result;
        }

        /// <summary>Runs a reflective call so that Studio's own exception reaches the caller, not TargetInvocationException.</summary>
        static void Unwrap(Action a)
        {
            try { a(); }
            catch (TargetInvocationException ex) when (ex.InnerException != null) { throw ex.InnerException; }
        }

        /// <summary>Studio's project list (IProjectsProvider) and customer list (ICustomerProvider) behind <paramref name="project"/>.</summary>
        static (object projects, object customers) Reach(FileBasedProject project)
        {
            if (project == null) throw new InvalidOperationException("No Studio project is open or selected. Open or select one (any project), then try again.");
            var internalProject = Get(project, "InternalProject");
            var projects = Get(internalProject, "ProjectsProvider");
            var customers = Get(projects, "CustomerProvider");
            return (projects, customers);
        }

        static StudioCustomer Read(object c) => new StudioCustomer
        {
            Id = (Guid)Get(c, "Guid"),
            Name = (Get(c, "Name") as string) ?? "",
            Email = (Get(c, "Email") as string) ?? "",
        };

        static List<object> CustomerObjects(object customerProvider) =>
            ((Get(customerProvider, "Customers") as IEnumerable) ?? throw new BridgeException("Customers is not a list")).Cast<object>().ToList();

        static object FindObject(object customerProvider, Guid id) =>
            CustomerObjects(customerProvider).FirstOrDefault(c => (Guid)Get(c, "Guid") == id);

        /// <summary>Runs <paramref name="body"/> against Studio's lists; every failure becomes <paramref name="error"/>, logged.</summary>
        static T Run<T>(string what, FileBasedProject project, Func<object, object, T> body, T failed, out string error)
        {
            error = null;
            if (!OnUiThread)
            {
                error = "Studio's customer list can only be changed from Studio's own window thread. Nothing was changed.";
                DiagnosticLog.WriteAlways(Log, what + ": not on the UI thread");
                return failed;
            }
            try
            {
                var (projects, customers) = Reach(project ?? AnyProject());
                return body(projects, customers);
            }
            catch (BridgeException ex)
            {
                error = Unreachable;
                DiagnosticLog.WriteAlways(Log, what + ": " + ex.Message);
            }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("No Studio project", StringComparison.Ordinal))
            {
                error = ex.Message;
            }
            catch (Exception ex)
            {
                error = "Studio did not accept the change: " + ex.Message;
                DiagnosticLog.WriteAlways(Log, what + " failed: " + ex.GetType().Name + ": " + ex.Message);
            }
            return failed;
        }

        // ─── the list ───────────────────────────────────────────────

        /// <summary>Studio's customers, alphabetical. Null and <paramref name="error"/> when the list cannot be read. UI thread.</summary>
        internal static List<StudioCustomer> List(out string error) => List(null, out error);

        internal static List<StudioCustomer> List(FileBasedProject project, out string error) =>
            Run("list", project, (projects, customers) =>
                JobProfile.SortByName(CustomerObjects(customers).Select(Read), c => c.Name), null, out error);

        /// <summary>Adds a customer (name checked against the list first), then keeps the list alphabetical and saves it. UI thread.</summary>
        internal static StudioCustomer Add(string name, string email, out string error) => Add(null, name, email, out error);

        internal static StudioCustomer Add(FileBasedProject project, string name, string email, out string error)
        {
            string refused = null;
            var added = Run("add", project, (projects, customers) =>
            {
                var existing = CustomerObjects(customers).Select(Read).ToList();
                refused = JobProfile.CheckCustomerName(name, existing.Select(c => new KeyValuePair<Guid, string>(c.Id, c.Name)));
                if (refused != null) return null;
                var created = Call(customers, "AddCustomer", name.Trim(), (email ?? "").Trim());
                var result = Read(created);
                DiagnosticLog.Log(Log, "added a customer (" + (CustomerObjects(customers).Count) + " now)");
                SortAndSave(projects, customers);
                return result;
            }, null, out error);
            if (refused != null) error = refused;
            return added;
        }

        /// <summary>Renames a customer in place: same Guid, so projects that use it still point at it. UI thread.</summary>
        internal static bool Rename(Guid id, string newName, out string error)
        {
            string refused = null;
            bool ok = Run("rename", null, (projects, customers) =>
            {
                var existing = CustomerObjects(customers).Select(Read).ToList();
                refused = JobProfile.CheckCustomerName(newName, existing.Select(c => new KeyValuePair<Guid, string>(c.Id, c.Name)), id);
                if (refused != null) return false;
                var c = FindObject(customers, id);
                if (c == null) { refused = "That customer is no longer in Studio's list."; return false; }
                Set(c, "Name", newName.Trim());
                SortAndSave(projects, customers);
                return true;
            }, false, out error);
            if (refused != null) error = refused;
            return ok;
        }

        /// <summary>Changes a customer's email address. UI thread.</summary>
        internal static bool SetEmail(Guid id, string email, out string error)
        {
            string refused = null;
            bool ok = Run("email", null, (projects, customers) =>
            {
                var c = FindObject(customers, id);
                if (c == null) { refused = "That customer is no longer in Studio's list."; return false; }
                Set(c, "Email", (email ?? "").Trim());
                Call(projects, "Save");
                return true;
            }, false, out error);
            if (refused != null) error = refused;
            return ok;
        }

        /// <summary>How many projects in Studio's project list have this customer; -1 when that cannot be read. One pass. UI thread.</summary>
        internal static int CountProjectsUsing(Guid id, out string error) =>
            Run("count", null, (projects, customers) =>
            {
                int n = 0;
                foreach (var p in ((Get(projects, "MyProjects") as IEnumerable) ?? Enumerable.Empty<object>()).Cast<object>())
                {
                    var c = Get(p, "Customer");
                    if (c != null && (Guid)Get(c, "Guid") == id) n++;
                }
                return n;
            }, -1, out error);

        /// <summary>Removes a customer from Studio's list (the caller has asked first). UI thread.</summary>
        internal static bool Remove(Guid id, out string error)
        {
            string refused = null;
            bool ok = Run("remove", null, (projects, customers) =>
            {
                var c = FindObject(customers, id);
                if (c == null) { refused = "That customer is no longer in Studio's list."; return false; }
                Call(customers, "RemoveCustomer", c);
                Call(projects, "Save");
                return true;
            }, false, out error);
            if (refused != null) error = refused;
            return ok;
        }

        /// <summary>Puts Studio's list in alphabetical order (if it is not already) and saves it. UI thread.</summary>
        internal static bool Sort(out string error) =>
            Run("sort", null, (projects, customers) => { SortAndSave(projects, customers, alwaysSave: false); return true; }, false, out error);

        /// <summary>Sorts, then saves Studio's list: always after a change of ours, otherwise only when the order changed.</summary>
        static void SortAndSave(object projects, object customers, bool alwaysSave = true)
        {
            bool changed = false;
            try { changed = SortInStudio(projects, customers); }
            catch (Exception ex)
            {
                // Sorting needs Studio's private members; without them the list still works, unsorted in Studio.
                DiagnosticLog.WriteAlways(Log, "Studio's customer list could not be put in order (shown in order in Supervertaler only): " + ex.Message);
            }
            if (changed || alwaysSave) Call(projects, "Save");
            if (changed) DiagnosticLog.Log(Log, "Studio's customer list put in alphabetical order");
        }

        /// <summary>
        /// Reorders Studio's customer list in place, by name: the same objects, the same Guids, only the order changes, so
        /// no project loses its customer. Two lists hold it (Studio 2022-2026, CustomerProvider): the provider's own,
        /// which the customer dropdown shows (private <c>CustomerList</c>), and the XML list that projects.xml is written
        /// from (<c>_customerProviderRepository._mainRepository.XmlProjectServer.Customers</c>). Both are found before
        /// either is touched, and both are sorted under Studio's own locks. True when the order changed.
        /// </summary>
        static bool SortInStudio(object projects, object customers)
        {
            var shown = FindProperty(customers.GetType(), "CustomerList")?.GetValue(customers, null) as IList
                        ?? throw new BridgeException("no CustomerList");
            var repository = Field(customers, "_customerProviderRepository");
            var main = Field(repository, "_mainRepository");
            var stored = Get(Get(main, "XmlProjectServer"), "Customers") as IList ?? throw new BridgeException("no XmlProjectServer.Customers");
            var outerLock = Get(projects, "SyncRoot") ?? throw new BridgeException("no SyncRoot");
            var innerLock = Field(customers, "_syncRoot") ?? throw new BridgeException("no _syncRoot");

            lock (outerLock)      // Studio's order too: ChangeCustomer holds SyncRoot, then the provider's lock
            lock (innerLock)
            {
                bool a = SortList(shown), b = SortList(stored);
                return a || b;
            }
        }

        static bool SortList(IList list)
        {
            var items = list.Cast<object>().ToList();
            var sorted = JobProfile.SortByName(items, o => Get(o, "Name") as string ?? "");
            if (items.SequenceEqual(sorted)) return false;
            for (int i = 0; i < sorted.Count; i++) list[i] = sorted[i];
            return true;
        }

        // ─── a project's customer ───────────────────────────────────

        /// <summary>The project's customer name, "" for none; null and <paramref name="error"/> when it cannot be read. UI thread.</summary>
        internal static string ProjectCustomer(FileBasedProject project, out string error) =>
            Run("project customer", project, (projects, customers) =>
            {
                var c = Get(Get(project, "InternalProject"), "Customer");
                return c == null ? "" : (Get(c, "Name") as string ?? "");
            }, null, out error);

        /// <summary>
        /// Sets the project's customer to the one called <paramref name="name"/> in Studio's list (matched ignoring case,
        /// as Studio matches a template's). With <paramref name="addIfMissing"/>, a name not in the list is added first
        /// (the user was asked). The caller saves the project. UI thread.
        /// </summary>
        internal static bool SetProjectCustomer(FileBasedProject project, string name, bool addIfMissing, out string error)
        {
            string refused = null;
            bool ok = Run("set project customer", project, (projects, customers) =>
            {
                var c = CustomerObjects(customers).FirstOrDefault(x => JobProfile.SameCustomerName(Get(x, "Name") as string, name));
                if (c == null)
                {
                    if (!addIfMissing) { refused = "\"" + name + "\" is not in Studio's customer list."; return false; }
                    c = Call(customers, "AddCustomer", name.Trim(), "");
                    try { SortInStudio(projects, customers); }
                    catch (Exception ex) { DiagnosticLog.WriteAlways(Log, "Studio's customer list could not be put in order: " + ex.Message); }
                }
                Call(Get(project, "InternalProject"), "ChangeCustomer", c);
                // One save of Studio's project list: the new customer, and the project's entry, which holds its customer too.
                Call(projects, "Save");
                return true;
            }, false, out error);
            if (refused != null) error = refused;
            return ok;
        }

        // ─── once when Supervertaler starts in Studio ───────────────

        static int _startSortDone;

        /// <summary>
        /// Puts Studio's customer list in order once per Studio session: now if a project is at hand, else when the
        /// first project is selected. Failures are logged; nothing else depends on it. UI thread.
        /// </summary>
        internal static void SortOnceThisSession()
        {
            try
            {
                if (TrySortAtStart()) return;
                var pc = SdlTradosStudio.Application?.GetController<ProjectsController>();
                if (pc == null) return;
                EventHandler handler = null;
                handler = (s, e) =>
                {
                    if (TrySortAtStart()) { try { pc.CurrentProjectChanged -= handler; } catch { } }
                };
                pc.CurrentProjectChanged += handler;
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteAlways(Log, "start-up sort not run: " + ex.Message);
            }
        }

        static bool TrySortAtStart()
        {
            if (Volatile.Read(ref _startSortDone) != 0) return true;
            var project = AnyProject(allowFullList: false);   // never load the whole project list during start-up
            if (project == null) return false;
            Interlocked.Exchange(ref _startSortDone, 1);
            if (!Run("start-up sort", project, (projects, customers) => { SortAndSave(projects, customers, alwaysSave: false); return true; }, false, out var error))
                DiagnosticLog.WriteAlways(Log, "start-up sort: " + error);
            return true;
        }
    }
}
