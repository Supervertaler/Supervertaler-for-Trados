{% hint style="info" %}
You are viewing help for **Supervertaler for Trados** — the Trados Studio plugin. Looking for help with the standalone app? Visit [Supervertaler Workbench help](https://help.supervertaler.com).
{% endhint %}

Supervertaler for Trados includes an optional, anonymous usage statistics feature to help the developer understand how many people are using the plugin and what environments they are running it on.

<figure><img src="../.gitbook/assets/image (5).png" alt=""><figcaption></figcaption></figure>

### How it works

* **Strictly opt-in** — on first launch after install or update, a dialog asks if you would like to participate. You must explicitly click "Yes" to opt in. If you click "No", no data is ever sent.
* **Minimal data** — a single lightweight ping is sent once per session on plugin startup. The only data included is:
  * A random anonymous ID (a UUID generated locally on your machine — not tied to any account, machine, or identity)
  * Plugin version (e.g. 4.11.0)
  * OS version (e.g. Windows 11)
  * Trados Studio version
  * System locale (e.g. en-GB)
* **Country detection** — the hosting provider (Cloudflare) determines your country from the network connection. No IP addresses are stored.
* **Silent failure** — if the ping fails (no internet, firewall, etc.), nothing happens. No retries, no queuing, no error messages.
* **First-party only** — data is sent to a Supervertaler-operated Cloudflare Worker endpoint. No third-party trackers, no Google Analytics, no advertising platforms.

### What is NOT collected

* No translation content
* No termbase or glossary data
* No file names or project names
* No personal information (name, email, etc.)
* No information about which features you use or how often
* No API keys or credentials

### Changing your preference

You can change your choice at any time:

1. Open **Settings** (click the gear icon in the Supervertaler panel)
2. In the **TermLens** tab, scroll to the bottom
3. Check or uncheck **"Share anonymous usage statistics (no personal data)"**
4. Click **OK**

The change takes effect on the next Trados Studio session.

### Why this exists

As a solo developer, usage statistics provide invaluable insight into:

* How many people are actually using the plugin
* Which Trados Studio versions to prioritise for testing and compatibility
* Which OS versions and locales are most common
* Whether users run Trados on a Mac (via Parallels) or natively on Windows

This information directly informs development priorities and compatibility testing.

### Transparency

The full source code for both the plugin-side statistics and the server-side endpoint is publicly available:

* **Plugin code**: [`Core/UsageStatistics.cs`](../../src/Supervertaler.Trados/Core/UsageStatistics.cs) on GitHub
* **Server code**: The Cloudflare Worker that receives the pings is also open source

You can verify exactly what data is sent by inspecting the code yourself.
