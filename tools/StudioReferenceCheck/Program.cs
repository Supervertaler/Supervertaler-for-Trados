// Checks a Trados Studio plugin package the way Studio checks a third-party plugin
// before loading it, so a package Studio would refuse never gets installed.
//
//   dotnet run --project tools/StudioReferenceCheck -- <Studio>\pluginconfig.xml <package.sdlplugin | folder>
//
// Studio's rule (Sdl.Core.PluginFramework.PackageSupport,
// ValidatingThirdPartyPluginLocator.ValidateDllReferences, identical in Studio 2022, 2024
// and 2026): for every *.dll in the unpacked plugin, every assembly it references that
// carries RWS's public key token c28cdb26c445c888 must match a <PublicAssembly> in the
// pluginconfig.xml next to Studio's own DLLs - the first entry whose name followed by a
// comma occurs in the reference's full name. No match: "Assembly reference is not public"
// (unless the referencing DLL is RWS's own). A match whose version is newer than the
// entry's (major, or same major and higher minor), or a major older: rejected too. One
// rejected reference and Studio starts without the plugin.
//
// In 20.205 a reference to Sdl.Desktop.Platform got in this way: it built, it passed every
// test, and Studio 2024 and 2026 refused to load the plugin at all.
//
// Reads metadata only (System.Reflection.Metadata); nothing is loaded or run. Exit code 0
// when everything is accepted, 1 when Studio would refuse the package, 2 on bad input.

using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;

const string RwsToken = "PublicKeyToken=c28cdb26c445c888";

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: StudioReferenceCheck <pluginconfig.xml> <package.sdlplugin | folder>");
    return 2;
}

List<(string Name, Version Version)> publicAssemblies;
try
{
    // Studio reads every element named PublicAssembly, whatever its namespace, in file order.
    publicAssemblies = XDocument.Load(args[0]).Descendants()
        .Where(e => e.Name.LocalName == "PublicAssembly")
        .Select(e => ((string)e.Attribute("name"), new Version((string)e.Attribute("version"))))
        .ToList();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"  ERROR: cannot read {args[0]}: {ex.Message}");
    return 2;
}
if (publicAssemblies.Count == 0)
{
    Console.Error.WriteLine($"  ERROR: no PublicAssembly entries in {args[0]}");
    return 2;
}

List<(string Name, byte[] Bytes)> dlls;
try
{
    dlls = ReadDlls(args[1]);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"  ERROR: cannot read {args[1]}: {ex.Message}");
    return 2;
}
if (dlls.Count == 0)
{
    Console.Error.WriteLine($"  ERROR: no DLLs in {args[1]}");
    return 2;
}

int managed = 0, native = 0, rwsReferences = 0, rejected = 0;
foreach (var (name, bytes) in dlls.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
{
    try
    {
        using var pe = new PEReader(new MemoryStream(bytes));
        if (!pe.HasMetadata) { native++; continue; }
        var md = pe.GetMetadataReader();
        if (!md.IsAssembly) { native++; continue; }
        managed++;

        bool ownedByRws = md.GetAssemblyDefinition().GetAssemblyName().FullName.Contains(RwsToken);
        foreach (var handle in md.AssemblyReferences)
        {
            var reference = md.GetAssemblyReference(handle).GetAssemblyName();
            string full = reference.FullName;
            if (!full.Contains(RwsToken)) continue;
            rwsReferences++;

            var entry = publicAssemblies.FirstOrDefault(p => full.Contains(p.Name + ","));
            var v = reference.Version;
            string problem = null;
            if (entry.Name == null)
            {
                if (!ownedByRws) problem = "not public (not in pluginconfig.xml)";
            }
            else if (v.Major > entry.Version.Major || (v.Major == entry.Version.Major && v.Minor > entry.Version.Minor))
                problem = $"newer than Studio's {entry.Version}";
            else if (v.Major < entry.Version.Major)
                problem = $"older major than Studio's {entry.Version}";

            if (problem == null)
                Console.WriteLine($"  ok        {name} -> {reference.Name} {v} (Studio: {entry.Version?.ToString() ?? "RWS-owned DLL"})");
            else
            {
                rejected++;
                Console.WriteLine($"  REJECTED  {name} -> {reference.Name} {v}: {problem}");
            }
        }
    }
    catch (Exception ex)
    {
        rejected++;
        Console.WriteLine($"  REJECTED  {name}: cannot read it ({ex.GetType().Name}: {ex.Message})");
    }
}

Console.WriteLine($"  {dlls.Count} DLLs ({managed} .NET, {native} native), {rwsReferences} references to RWS assemblies, {rejected} rejected");
if (rejected > 0)
{
    Console.WriteLine("");
    Console.WriteLine("  ERROR: Trados Studio would refuse to load this package (\"Assembly reference is not public\").");
    Console.WriteLine("  Reach the part of Studio you need by reflection instead (see Core/StudioEditor.cs).");
    return 1;
}
return 0;

// Every *.dll in a folder (recursively, as Studio scans the unpacked plugin) or in a
// .sdlplugin, which is a zip (OPC package) that Studio unpacks as it is.
static List<(string, byte[])> ReadDlls(string path)
{
    var list = new List<(string, byte[])>();
    if (Directory.Exists(path))
    {
        foreach (var f in Directory.GetFiles(path, "*.dll", SearchOption.AllDirectories))
            list.Add((Path.GetRelativePath(path, f), File.ReadAllBytes(f)));
        return list;
    }
    using var zip = ZipFile.OpenRead(path);
    foreach (var e in zip.Entries)
    {
        if (!e.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
        using var s = e.Open();
        using var m = new MemoryStream();
        s.CopyTo(m);
        list.Add((Uri.UnescapeDataString(e.FullName), m.ToArray()));
    }
    return list;
}
