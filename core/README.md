# Supervertaler Plugin Core

> Not to be confused with the private `supervertaler-core` repo, which is the
> Qt-free Python core shared by Supervertaler Workbench and Otto. This one is the
> .NET code shared by the two CAT-tool plugins.

Code shared by [Supervertaler for Trados](https://github.com/Supervertaler/Supervertaler-for-Trados)
and [Supervertaler for memoQ](https://github.com/Supervertaler/Supervertaler-for-memoQ).

Both are CAT-tool plugins loaded into someone else's process, so this is
**compiled into each plugin as source**, not shipped as a DLL. Every extra
assembly in a plugin folder is another thing to package correctly and another
chance of an assembly-resolution conflict — a problem the Trados plugin has
already paid for once, with SQLite.

## What is here

| | |
|---|---|
| `LlmClient` | Anthropic, OpenAI, Google, and OpenAI-compatible endpoints. Translation, chat, tool use, usage accounting. |
| `LlmModels` | Model catalogue and provider constants. |
| `PricingTable` | Per-model prices, overridable by a `pricing.json` in the shared data folder. |
| `TokenEstimator` | Token counts and cost estimates before a call is made. |
| `ChatMessage`, `PromptLogEntry` | Conversation and audit types. |
| `AiApiKeys` | Per-provider keys, as stored in a plugin's settings. |
| `SupervertalerPaths` | The one folder every Supervertaler product shares. |

Deliberately UI-free and CAT-tool-free: nothing here references WinForms, `Sdl.*`
or `MemoQ.*`. That is the boundary — if a change to this repo needs one of those,
it belongs in a plugin instead.

## Using it

Added to each plugin repo as a submodule at `core/`, then imported:

```xml
<Import Project="..\..\core\Supervertaler.Core.props" />
```

The props file adds `core/src/**/*.cs` to the compilation and the framework
references the shared code needs.

## Building on its own

```bash
dotnet build build/Supervertaler.Core.Build.csproj
```

Not a shipping artefact — nothing consumes its output. It exists so a compile
error surfaces here in one line rather than halfway through a plugin build.

## Licence

Copyright © 2026 Michael Beijer. Source-available, not open source: see
[LICENSE](LICENSE).
