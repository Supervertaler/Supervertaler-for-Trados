---
description: Self-organizing, AI-maintained translation knowledge base
---

# SuperMemory

SuperMemory is a self-organizing translation knowledge base that replaces traditional translation memories and term bases with a living, AI-maintained wiki. Instead of rigid fuzzy matching, SuperMemory gives the AI full contextual understanding of your clients, terminology decisions, domain conventions, and style preferences.

<figure><img src=".gitbook/assets/Sv_SuperMemory-Graph.png" alt="SuperMemory knowledge graph in Obsidian"><figcaption><p>SuperMemory knowledge graph showing interconnected clients, terminology, and domain knowledge</p></figcaption></figure>

## How it works

SuperMemory is built on [Obsidian](https://obsidian.md/) and stores all knowledge as interlinked Markdown files — human-readable, portable, and future-proof.

The workflow has three phases:

### 1. Ingest

Drop raw material into the inbox: client briefs, style guides, glossaries, feedback notes, reference articles, or previous translations. SuperMemory accepts anything that helps you translate better.

### 2. Process

The AI reads your raw material and writes structured knowledge base articles:

* **Client profiles** — language preferences, terminology decisions, style rules, project history
* **Terminology articles** — approved translations with rejected alternatives and the reasoning behind each choice
* **Domain knowledge** — conventions, common pitfalls, and reference material for specific fields (legal, medical, technical, marketing)
* **Style guides** — formatting rules, register, localisation conventions

Every article is interlinked with backlinks, so you can navigate from a client to their preferred terms to the domain those terms belong to.

### 3. Maintain

SuperMemory periodically scans itself for inconsistencies: conflicting terminology, broken links, stale content, missing cross-references. It heals itself — like a librarian who keeps the shelves organised.

## Why SuperMemory?

| Traditional TM/TB                | SuperMemory                                         |
| -------------------------------- | --------------------------------------------------- |
| Fuzzy matching on surface text   | Contextual understanding of _why_ terms were chosen |
| Static — requires manual updates | Self-healing — AI maintains and interlinks          |
| Opaque — hard to audit decisions | Every decision traceable to a readable `.md` file   |
| Locked to one tool               | Portable Markdown — works with any editor           |
| Segments in isolation            | Connected knowledge graph                           |

## Folder structure

SuperMemory organises knowledge into six folders:

| Folder           | Contents                                                     |
| ---------------- | ------------------------------------------------------------ |
| `00_INBOX`       | Raw material — drop zone for unprocessed content             |
| `01_CLIENTS`     | Client profiles and preferences                              |
| `02_TERMINOLOGY` | Term articles with translations, alternatives, and reasoning |
| `03_DOMAINS`     | Domain-specific conventions and pitfalls                     |
| `04_STYLE`       | Style guides and formatting rules                            |
| `05_INDICES`     | Auto-generated indexes and maps of content                   |

## Getting started

SuperMemory ships as a vault skeleton in your [user data folder](data-folder.md):

```
C:\Users\{you}\Supervertaler\supermemory\
```

1. Open this folder as a vault in [Obsidian](https://obsidian.md/)
2. Drop raw material (client briefs, glossaries, feedback) into `00_INBOX`
3. Click **Process Inbox** in the Supervertaler Assistant toolbar to organise your raw material into structured articles
4. Watch your knowledge graph grow as connections form between clients, terms, and domains

## Quick Add (Ctrl+Alt+M)

While translating in Trados, you can instantly add a term or correction to your SuperMemory vault – and optionally inject it into your active translation prompt so the next Ctrl+T picks it up immediately.

<figure><img src=".gitbook/assets/image (11).png" alt=""><figcaption></figcaption></figure>

### How to use

1. In the Trados editor, select the source text you want to capture (optional — the full source segment is used if nothing is selected)
2. Press **Ctrl+Alt+M** or right-click and choose **Add to SuperMemory**
3. Fill in the dialog:
   * **Term / pattern (what's wrong)** — the incorrect or ambiguous term (pre-filled from your selection)
   * **Correction** — the correct translation (pre-filled from target selection, if any). The label adapts to your target language (e.g. "Correct Dutch form")
   * **Notes** — optional context or explanation
   * **Also append to active translation prompt** — when ticked, a row is added to the TERMINOLOGY table in your [active prompt](supermemory.md#active-prompt) so the correction takes effect immediately
4. Click **Add**

### What happens

* A Markdown article is created in your vault's `02_TERMINOLOGY` folder with YAML frontmatter (source term, target term, domain, status, date)
* If the "append to prompt" option is ticked, a new row is inserted into the active prompt's terminology table — the prompt is read fresh from disk on every Ctrl+T, so the change is instant

{% hint style="success" %}
**Tip:** Quick Add is the fastest way to build up your knowledge base while translating. Spotted a Dunglish pattern? Ctrl+Alt+M, type the correction, and carry on — your future translations automatically avoid that mistake.
{% endhint %}

## Active Prompt

Each Trados project can have an **active prompt** — the prompt that Quick Add appends terminology to. This is also the prompt that is auto-selected in the [Batch Translate](batch-translate.md) dropdown when you open the project.

### Setting the active prompt

1. Open **Settings → Prompts**
2. Right-click a translation prompt in the tree
3. Choose **Set as active prompt for this project**

The active prompt is shown with a pin icon and bold blue text in the Prompt Manager. In the Batch Translate dropdown, a checkmark appears next to the active prompt name.

To clear the active prompt, right-click it again and choose the same menu item (it toggles).

{% hint style="info" %}
The active prompt is saved [per project](settings/project-settings.md). Different Trados projects can have different active prompts.
{% endhint %}

## Process Inbox

The **Process Inbox** button in the Supervertaler Assistant (Chat tab toolbar) reads raw material from your `00_INBOX/` folder and uses AI to organise it into structured knowledge base articles — client profiles, terminology entries, domain knowledge, and style guides.

### How to use

1. Drop raw material into your `supermemory/00_INBOX/` folder: client briefs, glossaries, feedback notes, style guides, reference articles, or anything that helps you translate better
2. Open the **Supervertaler Assistant** panel and look for the toolbar below the context bar
3. The toolbar shows how many files are waiting (e.g. "3 files in inbox")
4. Click **Process Inbox**
5. The AI reads each file, creates structured articles in the appropriate folders, and archives the originals to `00_INBOX/_archive/`

A summary of all created files appears in the chat when processing is complete.

## Health Check

The **Health Check** button scans your entire knowledge base for problems and fixes what it can:

* **Conflicting terminology** — the same source term translated differently in different articles
* **Broken links** — `[[backlinks]]` that point to articles that don't exist
* **Orphaned articles** — articles that nothing links to (disconnected from the graph)
* **Stale content** — articles not updated in more than 6 months
* **Duplicate content** — overlapping articles that should be merged
* **Missing cross-references** — terms or domains that should be linked but aren't
* **Index accuracy** — statistics and listings that are out of date

The AI produces a detailed report in the chat and automatically applies safe fixes (creating stub articles, updating indexes, fixing broken references). Changes that need human judgement are flagged for review.

{% hint style="warning" %}
**Important:** SuperMemory is a living, AI-maintained knowledge base. The AI can and will create, update, and reorganise your vault files when you run Process Inbox or Health Check. To stay safe:

* **Keep originals elsewhere.** Don't put your only copy of a glossary or style guide in the vault — keep the original in its own folder.
* **Back up your vault regularly.** Copy the entire `supermemory` folder to a backup location before running Health Check for the first time, and periodically after that. If something goes wrong, you can simply replace the vault folder with your backup.
* **Review changes in Obsidian.** After running Process Inbox or Health Check, open Obsidian and browse the recently modified files to verify the AI made sensible changes. Obsidian's search and graph view make this easy.
{% endhint %}

## Integration with Supervertaler

SuperMemory is automatically integrated into all AI-powered features. When you translate (batch or chat), the AI consults your knowledge base before producing a translation.

### What the AI loads

Before every translation, Supervertaler reads your vault and loads the most relevant articles:

1. **Client profile** — The AI tries to match your Trados project name against client profiles in `01_CLIENTS/`. If your project is called "Acme Legal Contract 2026", it finds the Acme Corporation profile and loads their language preferences, terminology decisions, and style rules.
2. **Domain knowledge** — The AI analyses your document to detect the domain (legal, medical, technical, marketing, etc.) and loads the matching article from `03_DOMAINS/` with conventions and common pitfalls.
3. **Style guide** — The AI loads the most relevant style guide from `04_STYLE/`, preferring client-specific guides over general ones.
4. **Terminology articles** — The AI loads term articles from `02_TERMINOLOGY/` that match your client, domain, or language pair. These include not just the approved translations, but also rejected alternatives and the reasoning behind each decision.

### How it works with existing context

SuperMemory adds an extra intelligence layer on top of the context you already use:

| Context source | What it provides | How SuperMemory enhances it |
|---|---|---|
| **Termbases** (MultiTerm) | Flat term pairs: term A = term B | Adds the _why_: reasoning, rejected alternatives, client-specific overrides |
| **Translation memories** | Previous translations for style anchoring | Adds domain conventions and style rules |
| **Document content** | Document type detection | Adds specific domain pitfalls and formatting conventions |
| **AutoPrompt** | AI-generated translation instructions | Informed by KB context for more accurate prompt generation |

All of these work together. Termbases give the AI the terms; SuperMemory tells it _why_ those terms were chosen and what to watch out for.

### Memory-aware chat

The Supervertaler Assistant chat window is also memory-aware. When you ask the AI a question about a translation, it has access to your SuperMemory knowledge base alongside the document context, terminology, and TM matches. This means you can ask questions like "What register should I use for this client?" and the AI answers based on your actual KB articles, not generic assumptions.

### Token budget

To avoid overloading the AI's context window, SuperMemory is allocated a token budget (approximately 4000 tokens). If your vault contains more relevant content than fits in the budget, articles are prioritised: client profile first, then domain knowledge, then style guide, then terminology articles.

## Obsidian Web Clipper

The [Obsidian Web Clipper](https://obsidian.md/clipper) is a free browser extension that lets you clip web pages directly into your SuperMemory inbox. Install it for Chrome, Firefox, Safari, or Edge.

### Setting up the Web Clipper

1. Install the extension from [obsidian.md/clipper](https://obsidian.md/clipper)
2. Make sure Obsidian is running with your SuperMemory vault open
3. Click the Web Clipper icon in your browser toolbar, then the gear icon (settings)
4. Create a new template (e.g. "supermemory") and set:
   * **Note location:** `00_INBOX`
   * **Vault:** select your supermemory vault
5. Optionally add properties: `source_url` = `{{url}}`, `clipped` = `{{date}}`

Now when you find a useful reference — a client style guide, a terminology resource, a domain article — click the clipper, hit save, and it drops straight into your inbox. Next time you click **Process Inbox**, the AI organises it into structured articles.

## Installing Obsidian

SuperMemory stores all knowledge as Markdown files, which you can browse and edit with any text editor. For the best experience, we recommend [Obsidian](https://obsidian.md/) — a free knowledge-base app that visualises the links between your articles as an interactive graph.

1. Download Obsidian from [https://obsidian.md/download](https://obsidian.md/download) (available for Windows, Mac, and Linux)
2.  Install and open it — choose **Open folder as vault** and select your SuperMemory folder:

    ```
    C:\Users\{you}\Supervertaler\supermemory\
    ```
3. The free version of Obsidian includes everything you need — no subscription required. (The paid Sync and Publish add-ons are not needed for SuperMemory.)

## Learn more

SuperMemory is inspired by Andrej Karpathy's [LLM Knowledge Base](https://venturebeat.com/data/karpathy-shares-llm-knowledge-base-architecture-that-bypasses-rag-with-an) architecture. The source code and vault templates are available on [GitHub](https://github.com/Supervertaler/Supervertaler-SuperMemory).
