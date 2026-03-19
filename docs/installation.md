{% hint style="info" %}
You are viewing help for **Supervertaler for Trados** — the Trados Studio plugin. Looking for help with the standalone app? Visit [Supervertaler Workbench help](https://help.supervertaler.com).
{% endhint %}

## Installation

### Download

1. Go to the [GitHub Releases](https://github.com/Supervertaler/Supervertaler-for-Trados/releases) page
2. Download the latest `Supervertaler for Trados.sdlplugin` file

### Install

1. **Close Trados Studio** if it is running
2. **Double-click** the downloaded `Supervertaler for Trados.sdlplugin` file
3. The Trados Plugin Installer opens — select your Trados Studio version and choose an installation location:

<figure><img src=".gitbook/assets/Trados-plugin-installation-dialogue.png" alt="Trados Plugin Installer showing version selection and installation location options"><figcaption><p>The Trados Plugin Installer lets you choose which Trados version to install for and where to place the plugin.</p></figcaption></figure>

4. Click **Next**, then **Finish** to complete the installation
5. **Start Trados Studio** — the plugin loads automatically

#### Installation locations

The installer offers three options for where to place the plugin. Each option stores the plugin in a different Windows folder, which determines who can use it and whether it follows you to other computers.

**"All your domain computers"** (default) : Installs to: `C:\Users\<user>\AppData\Roaming\Trados\Trados Studio\18\Plugins\` : The Windows **Roaming** profile folder. In corporate environments with Active Directory, this folder automatically syncs to a central server and follows your Windows account when you log into a different PC on the same network. If you log into PC-A at the office and then PC-B, the plugin is available on both without reinstalling. **If you are not on a corporate domain network, this behaves the same as "This computer for me only"** — the folder simply stays on your machine.

**"This computer for me only"** : Installs to: `C:\Users\<user>\AppData\Local\Trados\Trados Studio\18\Plugins\Packages\` : The Windows **Local** profile folder. The plugin stays on this specific machine and is only available to your Windows user account. If another person logs into the same PC with a different Windows account, they will not have the plugin.

**"This computer for all users"** : Installs to: `C:\ProgramData\Trados\Trados Studio\18\Plugins\Packages\` : The shared **ProgramData** folder. The plugin is available to every Windows user account on this machine. Use this on shared workstations where multiple people log in with their own Windows accounts and all need the plugin. Rarely needed for most translators.

{% hint style="info" %}
**Which should I choose?** For most users, **just accept the default** ("All your domain computers") and click Next. On a personal computer this behaves identically to "This computer for me only". The important thing is to use the **same option every time** you update — switching between options can leave old copies behind.
{% endhint %}

### Verify Installation

After restarting Trados Studio, open a project in the Editor view. You should see:

* **TermLens panel** — docked above the editor area (or in the bottom panel area)
* **Supervertaler Assistant panel** — docked on the right side

#### If the TermLens panel is not visible

Go to **View > TermLens** to show the panel.

#### If the Supervertaler Assistant panel is not visible

Go to **View > Supervertaler Assistant** to show the panel.

{% hint style="success" %}
Both panels are standard Trados dockable panels. You can drag them to any docking position (left, right, top, bottom, floating) or move them to a second monitor. Trados remembers their position between sessions.
{% endhint %}

### Running on a Mac (Parallels)

If you are running Trados Studio inside **Parallels Desktop** on a Mac, there is one important rule for the first-run setup:

**Keep your data folder on the Windows side** — use the default path (e.g., `C:\Users\<username>\Supervertaler`). Do **not** point it to a Mac-side path like `\\Mac\Home\Supervertaler`.

Supervertaler stores termbases as SQLite databases, and SQLite requires a local filesystem to work reliably. The `\\Mac\Home\...` paths in Parallels are mounted via a virtual network share, which can cause database locking errors or data loss.

{% hint style="warning" %}
**Mac users:** When the first-run setup dialog appears, accept the default `C:\Users\<username>\Supervertaler` path. If you previously used Supervertaler Workbench on the Mac side, copy your termbases into the Windows-side folder rather than pointing to the Mac path directly.
{% endhint %}

The plugin automatically detects Parallels and shows a warning if you select a Mac-side path during setup.

#### Sharing termbases between Workbench and the Trados plugin on a Mac

On Windows, both Supervertaler Workbench and the Trados plugin can point to the same shared data folder and work from the same `.db` termbase file simultaneously.

On a Mac with Parallels, this is **not possible** because the two products run on different filesystems:

- **Supervertaler Workbench** runs natively on macOS — its data folder is on the Mac filesystem (e.g., `/Users/<username>/Supervertaler/`)
- **Supervertaler for Trados** runs inside Parallels (Windows) — its data folder must be on the Windows filesystem (e.g., `C:\Users\<username>\Supervertaler\`)

The Trados plugin cannot reliably use a Mac-side path (`\\Mac\Home\...`) due to SQLite limitations on virtual network shares. To keep your termbases in sync between the two products on a Mac, copy the `.db` file from one side to the other after making changes. This is a limitation of the Parallels virtualisation layer, not of the termbase format.

***

### Updating

To update to a newer version:

1. Download the latest `Supervertaler for Trados.sdlplugin` file from [GitHub Releases](https://github.com/Supervertaler/Supervertaler-for-Trados/releases)
2. **Close Trados Studio completely** — the plugin files are locked while Trados is running
3. Double-click the new `.sdlplugin` file — the Trados Plugin Installer handles the rest
4. Start Trados Studio — it detects the updated package and loads the new version automatically

The new version cleanly replaces the previous installation. Your settings, termbases, and licence key are all preserved — no need to uninstall first.

{% hint style="warning" %}
Trados Studio **must be fully closed** before installing or updating. If Trados is still running, the installer may silently fail because the plugin files are locked.
{% endhint %}

### Troubleshooting: old version still showing after update

If Trados still loads an older version of the plugin after installing a new one, an old copy may be lingering in a different installation location. Check all three plugin folders and remove any old `Supervertaler for Trados.sdlplugin` (in `Packages`) and `Supervertaler.Trados` folder (in `Unpacked`):

| Folder    | Path                                                       |
| --------- | ---------------------------------------------------------- |
| Roaming   | `%AppData%\Trados\Trados Studio\18\Plugins\Packages\`      |
| Local     | `%LocalAppData%\Trados\Trados Studio\18\Plugins\Packages\` |
| All users | `%ProgramData%\Trados\Trados Studio\18\Plugins\Packages\`  |

{% hint style="info" %}
**Quick way to check:** paste each path into the Windows Run dialog (`Win+R`) or File Explorer address bar. If the folder exists and contains an old `Supervertaler for Trados.sdlplugin`, delete it. Also check for an `Unpacked\Supervertaler for Trados` folder at the same level and delete it if present.
{% endhint %}

After removing the old files, double-click the new `.sdlplugin` to install it fresh, then start Trados.

### Uninstalling

To remove the plugin:

1. Open Trados Studio
2. Go to **Help > Plugin Management**
3. Find "Supervertaler for Trados" in the list
4. Click **Uninstall**
5. Restart Trados Studio

***

### Next Steps

* [Getting Started](getting-started.md) — set up your first termbase and API key
