# RWS App Store Manager - v18.20.93

Two builds ship from this one release (identical feature set, distinct
version numbers so the App Store never sees a collision):

| Build | Version number | Min studio | Max studio | Checksum (SHA-256) |
|-------|----------------|------------|------------|--------------------|
| Studio 2024 | `18.20.93.0` | `18.0` | `18.9` | `ca42910b04c6820a97411c3115deee2fe6455349a7dbf8d89605160ee84ac192` |
| Studio 2026 | `19.20.93.0` | `19.0` | `19.0.9` | `400eec692156c83ff5baf0f889ffa8d5eccf77f3338cbbafada4047c3874e66e` |

---

## Changelog

*(Covers everything since the previously published App Store version, 18.20.89 / 19.20.89.)*

### Added

- **SuperSearch now searches server-based (GroupShare) translation memories, not just local `.sdltm` files.** When your project uses a GroupShare TM, SuperSearch queries it alongside your project files and any local TMs and shows the hits inline. Server-TM results are badged **"GroupShare"** in the Status column so you can tell them apart from local files at a glance, and each appears under its own TM name (e.g. `en-US to nl-BE`) rather than a raw server address. This was the top request from institutional users running GroupShare.
- **New "GroupShare" tab in Supervertaler Settings, where you enter your server login once.** Trados Studio does not hand its stored server credentials to plugins, so you set the server URL, login provider, username and password here. The password is encrypted at rest with Windows DPAPI (current user) and is never written in clear text. Both **GroupShare and Windows (AD) authentication** are supported, via a Login provider dropdown that mirrors GroupShare's own two options. Works on both Trados Studio 2024 and 2026.
- **Once in a while, a small dialog may appear at startup with a single question about Supervertaler's development** – for example, whether you use a particular feature, or what you'd most like improved. You can answer with one click (plus an optional comment), or just close it – it's completely optional and designed to be easy to ignore, and each question is only ever asked once. **No personal data is sent** – only the same anonymous ID and licence/trial status as the existing anonymous usage statistics, nothing that identifies you. Most of the time there is no active question and nothing appears at all. This lets me make better decisions about which features to keep, improve, or retire, based on what people actually use.

### Fixed

- **SuperSearch now searches your translation memories by default, not just the project files.** The search-scope dropdown previously defaulted to "Project files" (SDLXLIFF files only), which silently skipped every TM – so on a fresh install, TM and GroupShare hits never appeared even though the TMs were ticked in the list, and there was no error to explain why. The default is now **"Files + TMs"**, listed first in the dropdown as the recommended scope. If you had previously left the scope on "Project files", just switch it to "Files + TMs" once (your choice is remembered). "Project files" and "TMs only" remain available for narrowing the search.
- **SuperSearch dialog text no longer clips on high-resolution screens.** In the "Select translation memories / files to include" pickers, the instruction line and buttons had fixed pixel sizes, so on a high-DPI display the heading was cut off and "Select None" was truncated to "Select". The label and buttons now auto-size to their (scaled) text and sit in a proper layout bar, so everything stays readable at any display scaling. Applies to both the Select-TMs and Select-Files dialogs.

For the full changelog, see: https://github.com/Supervertaler/Supervertaler-for-Trados/releases
