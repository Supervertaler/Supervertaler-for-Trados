Supervertaler for Trados **v4.20.77** — unsigned builds for Trados Studio 2024 and 2026 (beta) are attached below. Covers 4.20.77.

## 📦 Installing from here (unsigned build – read first)

The plugins attached to this release are the **unsigned** builds. The version on the **RWS App Store is signed and notarised** – that's the recommended channel for most users. These downloads are for trying the latest fixes **before App Store approval** (which can take a few days, especially over a weekend).

**To install:**
1. Download the zip for your Trados version (table below).
2. **Extract it** – inside is a single `.sdlplugin` file.
3. Close Trados Studio, then double-click the `.sdlplugin` to run the Plugin Installer. **Do not rename the file** – Trados matches the filename against the plugin manifest.
4. Trados will warn that the plugin is **not signed**; that is expected for the direct build – click through to continue.

| Download | Trados version |
|---|---|
| `Supervertaler-for-Trados-Studio-2024.zip` | Trados Studio 2024 |
| `Supervertaler-for-Trados-Studio-2026-beta.zip` | Trados Studio 2026 (beta) |

## What's changed

## [4.20.77] – 2026-06-30

### Added (Batch Translate · memory guardrails for 32-bit Trados Studio 2024)

- **On 32-bit Trados (Studio 2024), Batch Translate now throttles itself to avoid running the host out of memory.** Diagnostic logs from a very large job (a 375 MB PPTX, ~990 segments) showed the crash was memory/address-space exhaustion in the 32-bit Trados process: it surfaced as out-of-memory and GDI/Direct2D failures inside Trados's *own* editor renderer, ending in a fatal .NET execution-engine error. A 32-bit process can only address ~2-4 GB no matter how much RAM the machine has. We can't raise that ceiling, so the plugin now stays under it on 32-bit hosts:
  - **Auto-throttle:** the batch size is capped and the (large) document-context embed is trimmed, transparently. No effect on 64-bit (Studio 2026).
  - **Memory watchdog:** between batches the plugin watches process memory; when it climbs it compacts the heap (incl. Large Object Heap), and if it nears the hard limit it **stops gracefully with a clear message** ("too large for 32-bit Trados - split the file / use smaller batches / use Workbench or Studio 2026") instead of letting Trados crash or hang.
  - For genuinely huge files, the realistic options remain: split the file, translate it in the 64-bit Supervertaler Workbench, or move to Trados Studio 2026 (64-bit).

### Fixed (Diagnostics · no longer turns a host crash into a hang)

- **Removed the `Application.ThreadException` handler added in 4.20.76.** It was capturing Trados's *own* UI-thread exceptions process-wide and swallowing them, which kept the message loop running through fatal paint failures and turned a clean crash into an unkillable hang. Crash capture is retained via `AppDomain.UnhandledException` and `TaskScheduler.UnobservedTaskException`, which log without altering the host's behaviour.

## Links

- RWS App Store (signed): https://appstore.rws.com/plugin/432
- Full changelog: https://github.com/Supervertaler/Supervertaler-for-Trados/blob/main/CHANGELOG.md
- Questions & discussion: https://github.com/orgs/Supervertaler/discussions
