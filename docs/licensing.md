{% hint style="info" %}
You are viewing help for **Supervertaler for Trados** — the Trados Studio plugin. Looking for help with the standalone app? Visit [Supervertaler Workbench help](https://help.supervertaler.com).
{% endhint %}

Supervertaler for Trados uses a subscription-based licensing model with a free trial to get started.

## Free Trial

When you first install Supervertaler for Trados, a **14-day free trial** starts automatically. During the trial, all features are unlocked — both TermLens terminology features and the AI-powered Supervertaler Assistant.

No sign-up or credit card is required to start the trial. The remaining days are shown in the **Licence** tab in Settings and in the About dialog.

## Plans

| Plan | Price | What's included |
|------|-------|-----------------|
| **TermLens** | €10/month | TermLens panel, termbases, MultiTerm support, Term Picker, quick-add shortcuts, non-translatable management, Termbase Editor, TSV import/export |
| **TermLens + Supervertaler Assistant** | €15/month | Everything in TermLens, plus AI Assistant chat panel, Batch Translate, Prompt Library, multimodal image support, TM match injection |

{% hint style="info" %}
Both plans include all future updates and new features within their tier.
{% endhint %}

## Purchasing a Licence

1. Visit [supervertaler.com/trados](https://supervertaler.com/trados/) and choose a plan
2. Complete the checkout — you will receive a **licence key** by email
3. Open Trados Studio → **Settings → Licence** tab
4. Paste your licence key and click **Activate**

Your licence allows activation on up to **2 machines** (e.g. a desktop and a laptop).

## Activating Your Licence

1. Open Trados Studio
2. Click the **gear icon** (⚙) on the TermLens or Supervertaler Assistant panel
3. Go to the **Licence** tab
4. Enter your licence key in the text field
5. Click **Activate**

A confirmation message appears when activation succeeds. The Licence tab shows your plan name, masked licence key, status, and last verification date.

{% hint style="success" %}
You can also reach the Licence tab by clicking the licence status text in the **About** dialog (accessible via the **?** button on any panel).
{% endhint %}

## Managing Your Subscription

From the **Licence** tab in Settings, you can:

- **Verify Now** — manually check your licence status with the server
- **Deactivate** — remove the licence from this machine (frees up an activation slot)
- **Manage subscription →** — opens the Lemon Squeezy billing portal where you can update payment details, change plans, or cancel

## Offline Use

After activation, the plugin caches your licence status locally. You can work offline for up to **30 days** before the plugin needs to verify your licence again. When you reconnect to the internet, verification happens automatically in the background.

## What Happens When the Trial Expires

After the 14-day trial ends, features are locked based on tier:

- **TermLens panel** — shows a "licence required" overlay. Terminology keyboard shortcuts show a message asking you to purchase a licence.
- **Supervertaler Assistant** — shows an "upgrade required" overlay if you have a TermLens-only licence, or a "licence required" overlay if you have no licence.

Your termbases, settings, and prompt library are preserved. Activating a licence immediately unlocks the features again.

## Changing Machines

If you replace a computer or need to move your licence:

1. On the old machine: open **Settings → Licence** and click **Deactivate**
2. On the new machine: enter your licence key and click **Activate**

If you can no longer access the old machine, the activation slot will be freed automatically when the licence is next validated.

## Privacy & Security

The plugin makes **no network calls** except to:

1. **Your chosen AI provider** (OpenAI, Anthropic, Google Gemini, or local Ollama) — only when you use AI features
2. **Lemon Squeezy licence API** (`api.lemonsqueezy.com`) — for licence activation and periodic validation
3. **Anonymous usage statistics** (strictly opt-in) — if you consent, a single ping on startup sends only: plugin version, OS version, Trados version, and system locale. See [Usage Statistics](settings/usage-statistics.md) for details.

The licence validation sends only your licence key and a hashed machine fingerprint (a one-way hash of your computer name and Windows user ID). No personal data, no translation content, no termbase information is ever collected.

Your API keys are stored locally in `%LocalAppData%\Supervertaler.Trados\settings.json` and are never transmitted anywhere except to your chosen AI provider.

{% hint style="info" %}
The full source code is available on [GitHub](https://github.com/Supervertaler/Supervertaler-for-Trados) for security audit. You can verify exactly what the plugin does and does not transmit.
{% endhint %}
