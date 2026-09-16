// The shared Supervertaler code moved out of Supervertaler.Trados.Core /
// .Models / .Settings and into Supervertaler.Core (the core/ submodule,
// compiled in via Supervertaler.Core.props).
//
// Forty-six files in this plugin use those types, most from inside the
// namespaces the types used to occupy, so they referenced them without a using
// at all. Rather than add an import to all forty-six, they are global here.
//
// Requires LangVersion latest in the .csproj: global usings are a C# 10
// compiler feature and this project targets net48, whose default is 7.3.
//
// THESE ALSO COVER THE SHARED SOURCES IN core/. They are compiled into this
// assembly rather than referenced as a DLL, so a global using declared here is
// in scope for core's own files too - and a core file that relies on it will
// compile in this plugin and NOWHERE ELSE. Supervertaler for memoQ compiles the
// same sources with no such declaration.
//
// That is not hypothetical: GlossaryRepair.cs used TermEntry without importing
// Supervertaler.Core.Models, built green here for weeks, and failed with CS0246
// the first time the memoQ side pulled it (2026-09-16).
//
// The check takes about three seconds and needs no host:
//
//     dotnet build core/build/Supervertaler.Core.Build.csproj
//
// It compiles core alone, with none of the above in scope, which is exactly the
// condition the other plugin builds in. Run it after touching anything in core/.

global using Supervertaler.Core;
global using Supervertaler.Core.Models;
