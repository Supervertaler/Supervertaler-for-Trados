---
description: Scan and repair a memory bank
---

# Health Check

The **Health Check** button scans the active memory bank for problems and fixes what it can -- like a librarian who keeps the shelves organised.

## What it checks

* **Conflicting terminology** -- the same source term translated differently in different articles
* **Broken links** -- `[[backlinks]]` that point to articles that don't exist
* **Orphaned articles** -- articles that nothing links to (disconnected from the graph)
* **Stale content** -- articles not updated in more than four weeks that have newer siblings on related topics are flagged, and when a newer article contradicts an older one the older article is flagged as potentially superseded
* **Duplicate content** -- overlapping articles that should be merged
* **Missing cross-references** -- terms or domains that should be linked but aren't
* **Low confidence** -- articles carrying `confidence: low` in their frontmatter are reported as needing human verification (see [Process Inbox](process-inbox.md#enriched-frontmatter) for the confidence scoring)
* **Missing frontmatter fields** -- articles created before the enriched-frontmatter schema shipped have missing `tldr`, `confidence`, `sources`, etc. filled in automatically by inferring values from the article content and folder location
* **Index accuracy** -- the `05_INDICES/` master files (master-terminology, client-summary, domain-summary) are refreshed so they reflect the current contents of the bank

## How it works

The AI produces a detailed report in the chat and automatically applies safe fixes (creating stub articles, updating indexes, fixing broken references). Changes that need human judgement are flagged for review.

Health Check runs against the **active** memory bank only. If you keep several banks side by side, run it once per bank.

## Completion summary

When Health Check finishes, a summary bubble always appears at the bottom of the chat so you know the operation is done:

- **"Health Check: applied N changes"** – the AI auto-fixed N files. The summary lists each updated or newly created file. Scroll up to read the full report, and open Obsidian to review the changes.
- **"Health Check complete – no changes applied"** – the AI scanned the bank and wrote its report above but did not auto-fix any files. Any issues it flagged are for your review.

{% hint style="warning" %}
**Important:** The AI can and will create, update, and reorganise files in the active memory bank when you run Health Check. To stay safe:

* **Keep originals elsewhere.** Don't put your only copy of a glossary or style guide in a memory bank -- keep the original in its own folder.
* **Back up your memory banks regularly.** Copy the entire `memory-banks` folder to a backup location before running Health Check for the first time, and periodically after that. If something goes wrong, you can simply replace the bank folder with your backup.
* **Review changes in Obsidian.** After running Health Check, open Obsidian and browse the recently modified files to verify the AI made sensible changes. Obsidian's search and graph view make this easy.
{% endhint %}

## See Also

* [Process Inbox](process-inbox.md)
* [Distill](distill.md)
* [SuperMemory](../super-memory.md)
