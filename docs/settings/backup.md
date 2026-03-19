{% hint style="info" %}
You are viewing help for **Supervertaler for Trados** — the Trados Studio plugin. Looking for help with the standalone app? Visit [Supervertaler Workbench help](https://help.supervertaler.com).
{% endhint %}

Use the **Export Settings** and **Import Settings** buttons in the **Backup** tab of the Settings dialog to back up and restore your Supervertaler configuration.

## Export

Click **Export Settings...** to save a copy of your current settings to a JSON file. Choose a location and filename — the default is `supervertaler-settings.json`. This file contains all your plugin settings: termbase paths, toggle states, font size, shortcut preferences, AI provider keys, model selections, and prompt configuration.

{% hint style="info" %}
**Tip:** Export your settings before upgrading the plugin or switching machines, so you can quickly restore your setup.
{% endhint %}

## Import

Click **Import Settings...** to restore settings from a previously exported JSON file. The import process:

1. Validates that the selected file is a valid Supervertaler settings file
2. Creates an automatic backup of your current settings (`settings.backup.json`)
3. Replaces your current settings with the imported ones
4. Closes the Settings dialog and applies the new settings immediately

{% hint style="warning" %}
Importing settings replaces **all** current settings. Your previous settings are automatically backed up in case you need to revert.
{% endhint %}

## Settings file location

Your settings are stored at:

```
%LocalAppData%\Supervertaler.Trados\settings.json
```

You can also manually back up or edit this file. After an import, the previous settings are saved as `settings.backup.json` in the same folder.
