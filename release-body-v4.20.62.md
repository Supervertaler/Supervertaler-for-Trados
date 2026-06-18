Supervertaler for Trados **v4.20.62** — unsigned builds for Trados Studio 2024 and 2026 (beta) are attached below. Covers 4.20.58 → 4.20.62.

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

## [4.20.62] – 2026-06-19

### Changed (Editor context menu — the proper fix)

- **The duplicate "AI translate current segment" entry is removed from the editor right-click menu, without crashing Studio.** The earlier crashes were both from removing *too much*: 4.20.57 deleted the action type (but the manifest still referenced it, so the command bar couldn't instantiate it); 4.20.60 deleted the action's entire `<auxiliaryExtensionAttributes>` element from `plugin.xml` (so the startup shortcut-cache loader hit a null and threw). The startup log pinpointed the second one (`ActionService.ReloadShortcutSettings → Extension.get_AuxiliaryExtensionAttributes → NullReferenceException`). The fix keeps the action **registered** and keeps the element present but **empty** (`<auxiliaryExtensionAttributes />`), dropping only the `ActionLayoutAttribute` — exactly the shape three other extensions in this plugin already ship and load fine. Net: no menu entry, no crash. Use **"Translate active segment" (Ctrl+T)**. (The action still appears in the keyboard-shortcuts editor, by design — registration must stay.)

## [4.20.61] – 2026-06-18

### Fixed (Startup crash — abandons the menu-hide attempt)

- **Studio starts reliably again; the duplicate "AI translate current segment" entry stays.** Two different ways of hiding that entry each crashed Studio on startup: deleting the action (4.20.57) failed because Studio instantiates every cached command-bar action on launch, and removing only its menu-layout from `plugin.xml` (4.20.60) made the action service throw a `NullReferenceException` because the cached editor command bar still referenced that item. Studio's persisted command-bar state makes both removals unsafe. This release restores the known-good configuration (the action and its menu placement both present), so Studio launches normally. The entry is a harmless exact duplicate of **"Translate active segment" (Ctrl+T)**; it is being left in place.

## [4.20.60] – 2026-06-18

### Changed (Editor context menu) — REVERTED in 4.20.61

- Removed the action's `ActionLayoutAttribute` from `plugin.xml` to drop the menu entry. **This crashed Studio on startup** (`IActionService` NullReferenceException — the cached command bar still referenced the item). Reverted.

## [4.20.59] – 2026-06-18

### Changed (Editor context menu)

- Attempted to hide the duplicate entry by dropping its C# `[ActionLayout]`. **Ineffective** — Studio reads the menu layout from `plugin.xml`, not the C# attribute. Superseded.

## [4.20.58] – 2026-06-18

### Fixed (Startup crash — reverts 4.20.57)

- **Trados Studio 2024 and 2026 no longer crash on startup.** Version 4.20.57 deleted the legacy "AI translate current segment" action to declutter the right-click menu. But Studio caches the editor command bar by action id, and on startup it tries to instantiate every cached action — so with the action type gone it threw *"Failed to add view command bar extensions for view 'EditorView'"* and exited before the editor loaded. This release restores that action registration, so the cached reference resolves again and Studio starts normally. The action is intentionally kept registered for exactly this backward-compatibility reason; the menu still shows the (harmless) duplicate entry. **If you installed 4.20.57, update to this build.**

## Links

- RWS App Store (signed): https://appstore.rws.com/plugin/432
- Full changelog: https://github.com/Supervertaler/Supervertaler-for-Trados/blob/main/CHANGELOG.md
- Questions & discussion: https://github.com/orgs/Supervertaler/discussions
