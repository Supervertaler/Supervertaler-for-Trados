# Create a Trados project from a folder

**Status:** designed, not started. Build after the 199 release, in a fresh session,
on a branch.
**Date:** 2026-10-10
**Prompted by:** wanting to hand Claude Desktop (or a small tool of our own) a folder
of source files for a new job and have it set up the Trados Studio project – name,
languages, files, template, TMs, termbases – instead of clicking through Studio's
New Project wizard.

## Decided so far

- **Studio open is fine** (Michael, 10 Oct). The project is created *inside* the
  running Studio by the plugin, through the same bridge every MCP tool already uses.
  No headless route, no separate process.
- **Branch first** (Michael, 10 Oct): built on a branch, used on his own jobs, merged
  for a public release once it works. See *Rollout*.
- **No catch-22 with "the plugin only appears in a project".** The panels do, but the
  bridge does not: `AppInitializer.EnsureBridgeViewPartLoads` starts it at Studio
  startup ("studio-startup" in the bridge log), so the machine-wide MCP tools already
  work with Studio on the Projects view and nothing in the editor, and `create_project`
  joins them. A later plugin dialog belongs on the **Projects view** (ribbon button or
  right-click), not in the editor panes.

## How Michael sets up a project today

Studio's **New Project** → **Use Settings from** → one of his templates, one per end
client or kind of job (e.g. *ACME_LEGAL*, *PROJ-001 Machines*, *PATENTS
(nl-NL to en-GB)*). The same dropdown also offers **Previous Projects**: a new
project can take its settings from an earlier project instead of a template, and
Studio records which (Project Details: *Project Template*, *Reference Project*).
`create_project` follows that: settings from a template **or** a previous project.

## Prior art: Paul Filkin's server, and why not reuse it

[paulfilkin/Trados-Powershell-MCP](https://github.com/paulfilkin/Trados-Powershell-MCP)
(Unlicense, last pushed April 2026) is an MCP server for project *infrastructure*:
`studio_new_project`, analyse, pre-translate, package export/import, TMX, TM creation,
plus GroupShare and Language Cloud groups. A thin Node server writes a `.ps1`, runs it
under 32-bit Windows PowerShell 5.1, and the script calls RWS's
[Studio PowerShell Toolkit](https://github.com/RWS/Sdl-studio-powershell-toolkit),
which uses the Project Automation API with Studio closed.

Its `studio_new_project` is a good model for the *conversation* – its tool description
makes the AI ask for name and location, look up TMs by name, and suggest a project
folder from existing projects – but the route does not suit us:

- **Studio Professional only.** Its own docs say Freelance and Starter do not include
  the Project Automation API. Most of our users are freelancers.
- **Studio 2022 and 2024 only.** The toolkit loads 32-bit Studio DLLs; Studio 2026 is
  64-bit.
- **More to install.** Node, the toolkit, the right PowerShell host – per user.
- **Outside Studio's view.** A project made headless still has to be found and opened.

## What we already have

- **Read side, all versions:** `list_projects`, `get_project`, `list_tms`,
  `list_project_templates` read the 2022/2024/2026 registries and folders
  (`Core/TradosTools.cs`).
- **Batch tasks on the open project:** `analyze_files`, `pretranslate`,
  `export_target`, `update_tm`, `run_verification` – all
  `FileBasedProject.RunAutomaticTask` (`AiAssistantViewPart.cs`, the `RunTaskJob`
  background-job machinery with `get_task_status` polling).
- **The references:** the csproj already references `Sdl.ProjectAutomation.Core`,
  `Sdl.ProjectAutomation.FileBased` and `Sdl.TranslationStudioAutomation.IntegrationApi`,
  so no new assembly has to clear Studio's public-API whitelist (see CLAUDE.md, #132).

So creating is the one missing verb.

## The shape

Two new MCP tools, and the same core usable later from a plugin dialog.

### `scan_project_folder` (read-only)

`path`, `recursive` (default true). Returns the files grouped by extension, with size,
and sorted into:

- **translatable** – types Studio's file-type definitions can open;
- **probably reference** – PDFs, images, spreadsheets that look like glossaries;
- **skip** – `~$*` Office lock files, `Thumbs.db`, `.DS_Store`, zero-byte files;
- **already bilingual** – `.sdlxliff`, `.xliff`, `.sdlppx`/`.sdlrpx` packages,
  which mean "this is a package or a returned job", not "new project".

How "translatable" is decided is an open question (below). The AI uses this to propose
the project; nothing is created.

### `create_project` (write)

| Parameter | Notes |
|-----------|-------|
| `name` | e.g. `Acme PROJ-001` – suggested by the AI from the folder name |
| `folder` | project location; refuse a non-empty folder (or create a subfolder named after the project, as Paul's does) – never write into an existing project |
| `sourceLanguage`, `targetLanguages` | required unless a template gives them |
| `files` | translatable files, from the scan; `referenceFiles` separately |
| `template` | `.sdltpl` path, from `list_project_templates` (registered list) |
| `referenceProject` | `.sdlproj` of an earlier project to take settings from, instead of a template |
| `tms` | `.sdltm` paths, from `list_tms` |
| `termbases` | `.sdltb` on 2022/2024, `.ttb` on 2026 |
| `dueDate`, `description` | optional |
| `prepare` | the name of one of Studio's task sequences (*Prepare*, *Prepare without project TM*, *Analyse only*…), read from `projects.xml`; or `none`. Default: the template's own, else *Prepare* |
| `open` | open the project in Studio afterwards (default true) |

### Templates: the main route for recurring jobs

Michael uses project templates (`.sdltpl`) for recurring jobs that need the same
settings, so a template is the shortest path, not an extra: it already carries the
language pair, TMs, termbases, file-type and batch-task settings. With `template`
given, everything it defines comes from it, and `sourceLanguage` / `targetLanguages` /
`tms` / `termbases` become optional overrides – an explicit value is added to what
the template sets, never silently dropped (decide in Phase 1 whether an explicit TM
*replaces* the template's TMs or is added to them, and say which in the tool
description).

The conversation should start from templates when there are any: `list_project_templates`
first, and if one fits the job ("Acme" in the folder name, an `Acme legal` template),
propose it before asking about languages and TMs. Templates differ per Studio
version; a 2024 template must not be offered for a project in 2026 without saying so.

**`list_project_templates` misses the templates people actually use – fix it in
Phase 0.** It only scans each version's `Documents\Studio …\Project Templates`
folder. Studio's **Use Settings from** list is the *registered* templates, kept in
that version's `Projects\projects.xml` under
`<ProjectTemplates><ProjectTemplateListItem ProjectTemplateFilePath="…">`, and they
can live anywhere: Michael's three client templates are in a backups folder on a
synced drive, none in the default folder (checked 10 Oct on Studio 2026). Read the
registered list for each version (the same `projects.xml` `list_projects` already
parses), say which Studio registered each, keep the folder scan as a fallback, and
dedupe by path. A registered path that no longer exists is reported as missing, not
dropped.

**Settings from a previous project** take an `.sdlproj` instead of an `.sdltpl`
(`referenceProject`, from `list_projects`); one or the other, not both.

**Task sequences come from Studio, not from us.** The same `projects.xml` lists the
`ComplexTaskTemplates` (*Prepare*, *Prepare without project TM*, *Analyse only* and
so on – the sequences Studio's wizard offers). `prepare` should name one of those,
not an invented set; Paul's server uses the same names.

Idea for later: SuperMemory could record which template a client's jobs use
(`01_CLIENTS/acme.md`: "new jobs: template *Acme legal*"), so the AI proposes it
without being told.

Runs as a background job like `pretranslate` and returns a `jobId`; `get_task_status`
reports progress and, at the end, the project file path and the analysis if one ran.

The tool description must make the AI **show the plan and get the user's yes** before
calling it – it writes a project to disk and into Studio's list.

### Sketch of the implementation

A new `Core/ProjectCreator.cs`, not more lines in `AiAssistantViewPart.cs` (already
~9,000):

1. `ProjectInfo` (name, `LocalProjectFolder`, source/target languages, due date,
   description) → `new FileBasedProject(info, templateReference)` (or the default
   template).
2. `AddFiles(...)` for translatable files; reference files added and given the
   reference role (API call to confirm).
3. TMs: a `TranslationProviderConfiguration` with one cascade entry per TM →
   `UpdateTranslationProviderConfiguration`. Termbases: `GetTermbaseConfiguration` /
   `UpdateTermbaseConfiguration` – the 2026 `.ttb` path differs; check it.
4. `RunAutomaticTask` for `Scan`, `ConvertToTranslatableFormat`,
   `CopyToTargetLanguages`, then `AnalyzeFiles` / `PreTranslateFiles` if asked – off
   the UI thread, as `RunTaskJob` does.
5. `Save()`, then on the UI thread `ProjectsController.Add(projectFilePath)` so it
   appears in the Projects view
   ([RWS: Projects controller](https://developers.rws.com/studio-api-docs/apiconcepts/integration/projects_controller.html)),
   and `Open`/`ActivateProject` if `open`.

Opening the new project also answers the "open project only" limit of the existing
batch tools: after `create_project`, `analyze_files` and friends work on it as they
are.

## Failure paths (the Marz list)

- **Half-made projects.** If any step fails after the folder is created, remove what
  *we* created – only if the folder was empty or new before we started – and say what
  failed. Never delete anything that was there before. A failed job must also leave no
  entry behind in the job registry (cleanup in `finally`, not on success only).
- **Must be tested, not assumed:** an unsupported file in `files`; a file locked by
  Word; a TM whose languages do not match; a template that does not exist; the folder
  already holds a project; Studio showing a modal dialog; two Studios open (the call
  must carry `instance`, and a 2022 project must not land in 2024); a licence that
  refuses the API (below).
- **Originals are never moved or changed.** Studio copies source files into the
  project; the user's folder is only read.

## Scale

Real jobs are a few files; the design target is **1,000 files / several hundred MB**,
the tested size at least a few hundred. One `AddFiles` call, not one per file; the
scan task's progress reported through the job; nothing on the UI thread that grows
with the file count. State it in the hand-off: tested at N, designed for 1,000.

## The licence question – answer it first

Paul's route needs Studio Professional. Whether `new FileBasedProject(...)` from
**inside a plugin** works on a Freelance licence is unknown – Freelance users create
projects in the UI every day, so it may well. Michael has exactly the machine to find
out: **Studio 2026 Professional, Studio 2024 and 2022 regular**. Phase 0 tests all
three. If the regular licence refuses it, the tool must say so in plain words
("creating projects needs Trados Studio Professional"), not surface an API exception –
and that finding decides how public the feature can be.

## Phases

0. **Spike** (one session): `list_project_templates` reading the registered list
   (above), then a minimal `create_project` – name, folder, languages, files,
   `prepare`, **and `template`** (one of Michael's real recurring-job templates, e.g.
   *PATENTS (nl-NL to en-GB)*) – on a branch. Try it in 2026 Pro, 2024 and 2022.
   Answers the licence question, whether `ProjectsController.Add` behaves in all
   three, and whether a template's TMs and settings come through intact.
1. **Usable:** `scan_project_folder`, `referenceProject`, explicit TMs, termbases,
   reference files, analyse/pre-translate, the template-first conversation, the
   failure-path tests above. Michael uses it on real jobs.
2. **Public:** help-site page (MCP Server), changelog, `mcp-tools.json` descriptions;
   later, perhaps, a **New project from folder…** dialog in the plugin on the same
   `ProjectCreator`.

## Rollout

**Decided (Michael, 10 Oct): branch first, then main – no feature flag, no private
build.** Phases 0–1 live on a branch; Michael runs that branch's build (`build.sh`
deploys it locally) on his own jobs. When it has handled a few real projects in both
editions, merge it for the next release. A hidden setting or a "Michael only" build
would be configuration nobody asked to vary, and a second thing to keep working.

The cost: every release built from `main` replaces the branch build on his machine, so
the branch should stay short-lived – merged, or rebased onto main before each release
build.

## Open questions

- Does the plugin route work on a Freelance licence? (Phase 0.)
- How does `scan_project_folder` decide "translatable"? Ask Studio's file-type
  definitions (which API, and on which thread?), or a fixed extension list kept in
  step with Studio's defaults?
- Termbases on 2026 (`.ttb`) – the configuration call and whether a 2022/2024 `.sdltb`
  can be attached at all there.
- Default project folder: Studio's own default location per version, or the last
  folder used?
- Should `create_project` also accept a package (`.sdlppx`) and simply open it? Out of
  scope for now; the scan only flags them.
