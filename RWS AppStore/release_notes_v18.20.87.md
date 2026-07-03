# RWS App Store Manager - v18.20.87

Two builds ship from this one release (identical feature set, distinct
version numbers so the App Store never sees a collision):

| Build | Version number | Min studio | Max studio | Checksum (SHA-256) |
|-------|----------------|------------|------------|--------------------|
| Studio 2024 | `18.20.87.0` | `18.0` | `18.9` | `3ff55a932691e4bb39efe1353374dc40ea6c12794a476bd9f0e45d3ef9296244` |
| Studio 2026 | `19.20.87.0` | `19.0` | `19.0.9` | `8f3d520e026cb8f471470e9db6d84bd5280b8e5543f08fde3a72c5d12f91355b` |

---

## Changelog

### Fixed
- **The update check no longer offers a Studio 2026 build to Studio 2024 users, or vice-versa.** Under the new versioning scheme the version major encodes the target Studio (18.x = Studio 2024, 19.x = Studio 2026), and the RWS App Store lists both generations' builds side by side. The updater was picking the numerically-highest published version regardless of generation, so a Studio 2024 user on `18.20.86` was shown the `19.20.86` build meant for Studio 2026. It now filters the App Store's version list to the **same major as the installed build** and offers the newest match within that generation only – 18.x installs only ever see 18.x updates, 19.x installs only 19.x. (Trados's own `RequiredProduct` gate would have refused to load the mismatched build, so nothing broke, but the prompt was wrong and confusing.)

For the full changelog, see: https://github.com/Supervertaler/Supervertaler-for-Trados/releases