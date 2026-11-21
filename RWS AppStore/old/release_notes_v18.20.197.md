# RWS App Store Manager - v18.20.197

Two builds ship from this one release (identical feature set, distinct
version numbers so the App Store never sees a collision):

| Build | Version number | Min studio | Max studio | Checksum (SHA-256) |
|-------|----------------|------------|------------|--------------------|
| Studio 2024 | `18.20.197.0` | `18.0` | `18.9` | `bba1378e12adc82ecc6d04d9aa61b6f17829682ae6f0879282a5f531f959eadf` |
| Studio 2026 | `19.20.197.0` | `19.0` | `19.9` | `c84daa57823a7867a8703736111c27698ea7e0bf93679c100e67e475e47eac35` |

---

## Highlights

- **Track Changes now shows only what the AI actually changed.**

Everything else is below.

---

## Changelog

### Added
- **Claude Opus 5.5 replaces Opus 5 in the model list.** It is the newest Opus and costs less ($4 input / $20 output per million tokens, against $5 / $25). Its costs now show in the prompt log and cost estimates, where a run on it used to say "unknown". If you have Opus 5 selected it keeps working: it appears in the custom model ID field instead of the list.
- **EditLens edit capture (off by default, no interface yet).** Records what was in the target box before you changed it, alongside what you confirmed, so a correction you keep making can become a prompt rule or a glossary entry instead of being made again. Nothing anywhere keeps that proposal today - the bilingual file has only the final target - so it is destroyed every working day. Capture is silent: no pane, no warnings, no model, no network. It refuses to run when the Supervertaler data folder is inside a synchronised folder, because the database holds client text. Switch it on with `"editCapture": true` in `settings.json` while Studio is closed.

### Changed
- **Your licence now belongs to your computer, not to one plugin.** Your licence and trial are now kept in the Supervertaler data folder, where every Supervertaler plugin on the computer reads them: an activation counts for the computer, and a computer has one trial. There is nothing to do: the first start after updating copies your existing activation across, nobody is asked to reactivate, and the old licence file is left where it was, so going back to an earlier version still finds it. Entering your key again on a computer that is already activated with it recognises that activation instead of using up another. Several plugins can be open at once; none can leave another with a half-written licence.
- **A licence file that cannot be read no longer counts as a lapsed licence.** Settings → Licence and the About box now say the licence could not be read, rather than that it has expired, and your features stay available. If the file was damaged you are asked to enter your key again.

### Fixed
- **Batch Translate no longer moves numbered lines from one segment to another.** A segment whose text had numbered lines of its own – a contents list with "4.1 …", "4.2 …", or the steps "1. …", "2. …" of a procedure – could come back with those lines cut off and added to a different segment of the batch, their numbers stripped, leaving the segment with only its first line. Such lines now stay in their own segment.
- **Cost estimates for Claude Sonnet 5 were too high.** The price list had it at $3 / $15 per million tokens; it is $2 / $10. Claude Opus 4.6 through OpenRouter is corrected the same way, to $5 / $25.
- **Deactivating your licence no longer shows an error box.** If the AI Assistant pane had never been opened in that session, Settings → Licence → Deactivate showed an error, although the deactivation had in fact worked. The pane now catches up with the licence the first time it is opened.
- **Track Changes now shows only what the AI actually changed.** With Track Changes on, a segment written through the MCP server's `update_segments` was recorded as the whole old sentence struck through and the whole new one inserted – even when a single word had changed – so a reviewer asked to check a post-edit saw what looked like a full retranslation. It is now recorded word by word, the way Studio records your own typing: inserting "The " at the start of a sentence shows as exactly that, a changed word as that word struck through and its replacement, and a change on either side of a tag as two small revisions with the tag itself untouched. Comments stay on the words they were attached to. Where a segment cannot be shown word by word – its tags differ from before, or it already carries tracked changes – it is recorded as a whole-segment change as before, and the tool says so. Every tracked write is checked before it is applied: accepting all its changes must give exactly the new text, and rejecting them exactly the old. (#138)
- **Rewriting a segment with identical text no longer marks it Draft.** A target written through `update_segments` without a status is set to Draft so you can review it, but that also happened when the text came back exactly as it was – leaving a status change as the only trace of an edit that changed nothing. The status is now left alone when the text is unchanged; a status the caller asks for explicitly is always applied. (#138)
- **EditLens records the AI's draft on every path that writes one.** Batch Translate, clipboard mode, MCP `update_segments` and bilingual import all write a target without the editor raising an event, so four of the nine write paths left no trace of what was proposed before you edited it - the one thing the feature exists to keep. Measured on a real file: of 549 segments left, 141 had no proposal recorded and 98 of those came from Batch Translate. Segments you type into from scratch still record nothing, correctly - there was no proposal to keep.
- **EditLens stopped photographing the same segment over and over.** Closing a document raised two separate events and each one re-recorded every segment in the file, so 936 rows landed in a single minute for a 468-segment document and 74% of the capture database was duplicate snapshots. A document is now recorded once on its way out, and only the segments that actually changed. On real data that is 63% fewer rows written, with no loss of any observed edit.
- **`update_segments` refuses an empty target instead of clearing the segment.** "I have nothing for this segment" and "blank this segment" arrive as identical JSON, and only one of them wants your work destroyed. An empty `target` is now refused with a reason; omit `target` to leave a segment alone, or pass `allowEmpty: true` to blank one deliberately. Batch Translate was never affected - it has always skipped empty translations.
- **TermLens could show a segment without its negation.** A multi-word term matched inside a hyphenated compound, so with a term `actieve anodes` in the termbase a source reading *"Deze **niet-**actieve anodes onderscheiden zich"* was displayed as *"Deze actieve anodes onderscheiden zich"* – the opposite of what the document said, which is worse than a missed highlight. The tokenizer treats a hyphen as part of a word, so `niet-actieve` is one word, but the multi-word boundary check used a letter-or-digit test that read the hyphen as a boundary; the two disagreed about what a word is. A hyphen, slash or apostrophe with a letter on its far side is no longer a boundary. Trailing punctuation still is, so a term before a comma or full stop matches as before.

For the full changelog, see: https://github.com/Supervertaler/Supervertaler-for-Trados/releases