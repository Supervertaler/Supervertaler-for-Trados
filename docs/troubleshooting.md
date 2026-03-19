{% hint style="info" %}
You are viewing help for **Supervertaler for Trados** — the Trados Studio plugin. Looking for help with the standalone app? Visit [Supervertaler Workbench help](https://help.supervertaler.com).
{% endhint %}

Solutions to common issues with the Supervertaler for Trados plugin.

---

## Plugin not loading

**Symptoms:** The TermLens panel does not appear in Trados Studio, or the plugin ribbon tab is missing.

**Solutions:**

1. **Check Trados version** –the plugin requires **Trados Studio 2024** or later
2. **Verify .NET Framework** –ensure **.NET Framework 4.8** is installed on your system
3. **Reinstall the plugin** –remove the plugin via **Trados Plugin Management**, restart Trados, then install it again
4. **Check for errors** –open **Trados Plugin Management** and look for error messages next to the Supervertaler plugin entry

{% hint style="info" %}
After installing or updating the plugin, always restart Trados Studio completely (close all windows, not just the project).
{% endhint %}

---

## "Could not load SQLite" or DLL errors

**Symptoms:** Error messages about missing DLLs or SQLite when opening settings or loading a termbase.

**Solutions:**

- **Restart Trados Studio** after the first install. The plugin pre-loads its own SQLite DLL to avoid conflicts with other plugins, but this requires a clean startup
- If the error persists, reinstall the plugin to restore any missing DLL files

---

## Database locked / "cannot open database"

**Symptoms:** Error when trying to load or write to the termbase database.

**Solutions:**

- **Close Supervertaler Workbench** if it has the same `.db` file open. Two applications writing to the same SQLite file simultaneously can cause lock conflicts
- The plugin uses **read-only mode** where possible to minimise conflicts, but write operations (adding terms) require exclusive access
- Verify the `.db` file is not on a drive that has gone offline (e.g., a disconnected network share)

{% hint style="warning" %}
If you share the database via a cloud-sync folder, ensure the file is fully synced before opening it in the plugin. Partially synced files can appear locked or corrupt.
{% endhint %}

---

## Terms not appearing

**Symptoms:** TermLens shows no matches even though you know the segment contains terms that exist in your termbase.

**Solutions:**

1. **Check the Read toggle** –open [TermLens Settings](settings/termlens.md) and verify the termbase has **Read** enabled
2. **Verify the database path** –ensure the path points to the correct `.db` file
3. **Press F5** to force a full reload of your Supervertaler termbases from disk (note: F5 does not reload MultiTerm termbases)
4. **Reload the database** –click the **gear icon** in the TermLens panel to open settings, then close the dialog. This forces a reload of the termbase data
5. **Check language pair** –the termbase source/target languages must match the current Trados project languages

---

## MultiTerm terms not appearing

**Symptoms:** Green chips from your MultiTerm termbases (`.sdltb` files) are not showing in TermLens, even though the termbases are attached to your Trados project.

**Solutions:**

1. **Check your Trados project** –verify that MultiTerm termbases are attached via **Project Settings > Language Pairs > Termbases**
2. **Check the Read toggle** –open Supervertaler Settings (gear icon) and make sure the MultiTerm termbase's Read checkbox is enabled
3. **Check languages** –the termbase's source and target languages must match the current project's language pair
4. **Navigate to another segment and back** to trigger a MultiTerm auto-refresh (F5 does not reload MultiTerm termbases — only segment navigation does)

{% hint style="info" %}
When you add terms in MultiTerm, navigate to a different segment in Trados to trigger the auto-refresh. TermLens checks for file changes on each segment change.
{% endhint %}

See [MultiTerm Support](multiterm-support.md) for full details.

---

## AI features not working

**Symptoms:** Batch Translate produces no output, or single-segment translation returns an error.

**Solutions:**

1. **Verify the API key** –open [AI Settings](settings/ai-settings.md) and confirm the key is entered correctly with no extra spaces
2. **Check provider endpoint** –ensure the provider's API endpoint is reachable from your network (no firewall or proxy blocking it)
3. **Ollama users** –make sure the Ollama service is running locally:
   ```bash
   ollama serve
   ```
   Then verify the endpoint in AI Settings (default: `http://localhost:11434`)
4. **Custom provider** –double-check the endpoint URL and model name in the Custom OpenAI-compatible settings
5. **Check your API credits** –some providers return errors when your account balance is zero

---

## Database errors on Mac (Parallels)

**Symptoms:** Database locked errors, "cannot open database", or corrupt termbase data when running Trados Studio inside Parallels Desktop on a Mac.

**Cause:** Your Supervertaler data folder is on a Mac-side shared path (e.g., `\\Mac\Home\Supervertaler`). Parallels mounts Mac folders as virtual network shares, and SQLite databases do not work reliably on network filesystems — WAL mode (used by Supervertaler termbases) requires a local filesystem for correct locking.

**Solution:**

1. Move your data folder to the Windows side (e.g., `C:\Users\<username>\Supervertaler`)
2. Copy your `.db` termbase files from the Mac-side location into the new Windows-side folder
3. Update the data folder path in Supervertaler settings, or delete `%AppData%\Supervertaler\config.json` and restart Trados to trigger the first-run setup again

{% hint style="info" %}
See [Installation — Running on a Mac (Parallels)](installation.md#running-on-a-mac-parallels) for the recommended setup.
{% endhint %}

---

## Performance issues

**Symptoms:** The editor feels sluggish, or TermLens takes a long time to display matches.

**Solutions:**

- **Large termbases** (50,000+ terms) may take a moment to index when the database is first loaded on startup. This is a one-time cost per session
- **Close and reopen the editor** if the plugin feels unresponsive after a long session
- **Disable unused termbases** –uncheck **Read** for termbases you do not need for the current project to reduce the matching workload
- **Reduce batch size** in [AI Settings](settings/ai-settings.md) if Batch Translate is slow or timing out

---

## Still having issues?

1. Ask a question on the [Supervertaler Forum](https://github.com/orgs/Supervertaler/discussions) — the main community hub for both Supervertaler Workbench and Supervertaler for Trados
2. Check the [GitHub Issues](https://github.com/Supervertaler/Supervertaler-for-Trados/issues) for known bugs, feature requests, and workarounds
3. Open a new issue to report a bug or request a feature, including:
   - Your Trados Studio version
   - The Supervertaler plugin version
   - Steps to reproduce the problem
   - Any error messages or screenshots

See [Support & Community](support.md) for all the ways to get help.

---

## See Also

- [Support & Community](support.md)
- [MultiTerm Support](multiterm-support.md)
- [TermLens Settings](settings/termlens.md)
- [AI Settings](settings/ai-settings.md)
- [Termbase Management](termbase-management.md)
- [Common Issues (Workbench)](https://supervertaler.gitbook.io/supervertaler/troubleshooting/common-issues)
