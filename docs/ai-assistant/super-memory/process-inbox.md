---
description: Organise raw Markdown notes into structured knowledge base articles
---

# Process Inbox

The **Process Inbox** button in the Supervertaler Assistant toolbar reads raw Markdown notes from the active memory bank's `00_INBOX/` folder and uses AI to organise them into structured knowledge base articles – client profiles, terminology entries, domain knowledge, and style guides.

## How to use

1. Drop **Markdown notes** into the active memory bank's `00_INBOX/` folder: client briefs, glossaries, feedback notes, style guides, reference articles, or anything else you have written down as plain `.md` text.
2. Open the **Supervertaler Assistant** panel and look for the SuperMemory toolbar below the context bar.
3. The toolbar shows how many files are waiting in the active bank (e.g. "3 files in inbox").
4. Click **Process Inbox**.
5. The AI reads each Markdown file, creates structured articles in the appropriate folders, and archives the originals to `00_INBOX/_archive/`.

A summary of all created files appears in the chat when processing is complete.

{% hint style="info" %}
Process Inbox always runs against the **active** memory bank – the one currently selected in the toolbar dropdown. If you want to process material into a different bank, switch the dropdown first.
{% endhint %}

{% hint style="info" %}
The inbox count updates automatically when files are added externally (e.g. via the [Obsidian Web Clipper](obsidian-setup.md#web-clipper)). You can also click the refresh button (↻) on the toolbar to update the count manually.
{% endhint %}

## Markdown only – use Distill for everything else

Process Inbox is a Markdown compiler. It reads `.md` files and writes structured `.md` articles. It does **not** read translation memories, termbases, Word documents, PDFs, or spreadsheets. For any file that is not plain Markdown, use [**Distill**](distill.md) instead – it knows how to extract knowledge from binary formats and writes its output as Markdown into the same `00_INBOX/` folder, ready for Process Inbox to compile.

If you drop a non-Markdown file (e.g. a `.tmx` or `.pdf`) into the inbox folder by mistake, the inbox count still includes it – so the Process Inbox button lights up – but clicking the button shows a message pointing you at Distill instead. Process Inbox will not silently ignore your file, and it will not crash trying to compile a binary blob as Markdown.

Obsidian plugin sidecar files (currently `.edtz`) are a special case: they are editor metadata, not knowledge content, so they are skipped silently by both Process Inbox and Distill. They do not count towards the inbox total and they do not trigger the "non-Markdown files" warning.

| You have… | Use this |
|---|---|
| A Markdown brief, glossary, feedback note, or anything plain-text you wrote yourself | **Process Inbox** |
| A `.tmx` translation memory | **Distill** |
| A `.docx` style guide or client briefing | **Distill** |
| A `.pdf` reference document | **Distill** |
| A `.xlsx` / `.csv` glossary | **Distill** |
| A `.sdltb` MultiTerm termbase | **Distill** (right-click in TermLens settings → *Distill into memory bank*) |
| A `.tbx` termbase | **Distill** |

The two features compose: run Distill on your binary files first, review the draft `.md` articles it produces in the inbox, then run Process Inbox to compile them into the structured knowledge base.

## What gets created

Depending on the content of your raw material, Process Inbox creates articles in one or more of these folders:

| Folder | Article type | Example |
|--------|-------------|---------|
| `01_CLIENTS` | Client profiles | Language preferences, terminology decisions, contact details |
| `02_TERMINOLOGY` | Term articles | Approved translations with rejected alternatives and reasoning |
| `03_DOMAINS` | Domain knowledge | Conventions, common pitfalls, reference material |
| `04_STYLE` | Style guides | Formatting rules, register, localisation conventions |

Each article includes rich YAML frontmatter with metadata and backlinks to related articles, building up an interconnected knowledge graph. The frontmatter fields are:

| Field | Purpose |
|-------|---------|
| `type` | Article type — `terminology`, `client`, `domain`, `style`, or `template` |
| `domain` | Subject area (e.g. Legal, Medical, Marketing) |
| `client` | Client name when applicable |
| `language_pair` | Source → target language codes |
| `confidence` | `high` / `medium` / `low` — set by the AI based on the authority of the source material. Low-confidence articles are flagged for human review by [Health Check](health-check.md). |
| `sources` | Original filenames the article was derived from, for traceability. Terminology articles always quote exact source and target terms verbatim. |
| `tldr` | One-sentence summary for fast scanning in Obsidian previews and the master indices below |
| `created` / `updated` | Timestamps maintained automatically |

## Automatic indexes

After every successful Process Inbox run, three master index files are refreshed in the bank's `05_INDICES/` folder:

* `master-terminology.md` — a flat table of all source → target term decisions across the bank, with domain, client, confidence, and status columns
* `client-summary.md` — one section per client with their `tldr` or first paragraph
* `domain-summary.md` — one section per domain with their `tldr` or first paragraph

These indexes are built by scanning frontmatter directly — no extra LLM call — and complete in under a second. Open them in Obsidian to browse the whole bank at a glance. [Health Check](health-check.md) refreshes them as well.

## Templates and the heal-on-activation prompt

Process Inbox is driven by an AI prompt that lives inside the active memory bank at `06_TEMPLATES/compile.md`. Health Check uses a sister file at `06_TEMPLATES/lint.md`. Both files are bundled with the plugin and copied automatically into every newly created bank, so a fresh bank works out of the box.

If you switch to an older bank that does **not** have these template files (for example, a bank you created before the template-bundling feature shipped, or a bank where you accidentally deleted one of them), the plugin notices the gap and offers to restore the missing files from its built-in defaults. You will see a small dialog titled *"Missing memory bank templates"* listing the missing files with **Yes / No** buttons. Click **Yes** to restore them and Process Inbox / Health Check immediately become usable on that bank. Click **No** if you have a reason to want the templates absent (e.g. you are intentionally disabling those features for that bank), and the plugin will leave the bank as-is.

The restore is non-destructive: existing template files in the bank are never overwritten, only missing ones are added. Your edits to template files are per-bank and safe.

## See Also

- [Distill](distill.md) – extract knowledge from translation files (TMX, DOCX, PDF, termbases)
- [Health Check](health-check.md) – scan and repair the active memory bank
- [SuperMemory](../super-memory.md) – overview of SuperMemory and memory banks
- [Obsidian Setup](obsidian-setup.md) – installing Obsidian and the Web Clipper
