# Changelog

> **Versioning change (2026-07-02):** from this release on, the plugin's major
> version tracks the Trados Studio major it targets – **Studio 2024 = 18**,
> **Studio 2026 = 19** – so the two builds always carry distinct, non-colliding
> version numbers that share one tail (e.g. `18.20.86` / `19.20.86`). Earlier
> releases (`4.20.85` and below) used a single independent sequence for both
> builds.

## [18.20.187 / 19.20.187] – unreleased

### Added

- **Right-click a termbase hit in SuperSearch to edit it.** Search for a term, find it in the results with its termbase named in green, and **Edit term…** on the right-click menu opens it in the same Edit term entry dialog TermLens uses – termbase already resolved, and TermLens updated the moment you save. Until now the only route was the Termbase Editor’s own search box, one termbase at a time. MultiTerm and .ttb entries stay read-only, as everywhere else, and the menu says so rather than going quiet.
- **“Log prompts and responses” now also keeps them on disk.** The setting fed only the Reports tab, so the prompt actually sent to the model was never available after the fact – working out which tag notation the model had really been given took an afternoon of cross-referencing. With the setting on, every AI call is now also appended as one JSON line to a daily file under `trados\logs\prompts`, with the full system and user prompt, chat messages and response, plus tokens and cost. For Batch Translate, each batch is also recorded as its own line – the segments sent and the reply received – with the system prompt written once per run rather than once per batch. Image bytes are never written (attachments are recorded by type and size), connection tests are skipped, and files older than seven days are removed, so the folder stays bounded even in a synced data folder.
- **You can now choose whether your Trados comments go into a bilingual export.** A new **Include Trados comments** tick box on the Import/Export tab, on by default. Leave it on when the file is going to a reviewer who needs to read your queries; turn it off when it is going to a client, where your working notes were never meant for them. With it off, the Comments column does not appear at all.

### Changed

- **Translator-comment markers are now written as `[[TC: …]]` instead of `⟦TC: …⟧`.** The white square brackets never occur in source text, which is why they were chosen – but they are missing from the fonts memoQ’s grid uses and rendered there as empty boxes. Both plugins share one prompt library, so prompts written here run in memoQ too; the shared code now uses plain double brackets for both. Only the two delimiter characters change – the rules (one per segment, at the very end, only for a real defect) do not. Prompts already on disk keep whatever they say; they are not rewritten.
- **Prompt files carry a product marker in their name, and your selected prompt survives it.** Built-in QuickLauncher prompts that only Trados can run are now named e.g. `Define [Trados].md`, so a library shared with memoQ shows which product each belongs to; the marker is never part of the displayed name. The plugin remembers your selected prompt – globally and per project – by its filename, so a rename would previously have made it silently stop resolving mid-job, with the job running on fallback instructions. Every place that looks a stored prompt up now ignores the marker, so paths saved before the rename keep working after it.
- **The memory-bank reader is now shared code.** memoQ needed the same reader to serve banks over its own bridge, and two copies of a format four programs share is how they drift. No change in behaviour.
- **QuickLauncher no longer offers to send prompts to Supervertaler Workbench.** Workbench is being retired, and a destination that will not be there is not a choice worth offering. Prompts go to the AI Assistant in Trados, which is where they already went whenever Workbench was not running.

### Fixed

- **The AI now sees your document in one tag notation, and AutoPrompt describes the right one.** Batch Translate sends segments with inline formatting as numbered placeholder tags, but every *other* rendering of the document – the document context in the same prompt, the excerpt AutoPrompt reads, the surrounding segments QuickLauncher and Chat include – used Trados’s own internal markup instead. The prompt log showed the result plainly: one request carrying the document in two notations at once, and AutoPrompt writing tag instructions for a notation the batch never sends. Everything the model reads now uses the same placeholders, and AutoPrompt is told that the excerpt it sees is the notation to describe – with one rule added that generic “preserve the tags” never covers: never turn a tagged subscript into a Unicode one, or the reverse, however much tidier it looks.
- **The SuperSearch box no longer shows your search term with its first letter missing.** Send a phrase to SuperSearch while its panel was narrow and the box scrolled to keep the end of the text in view; widen the panel and the box stayed scrolled, so `droge stof` sat there as `roge stof` until you pressed Home. The box now scrolls back to the start whenever the panel is laid out, leaving your cursor and selection where they were.
- **Opening a project no longer switches on termbases you never chose.** The Read and AI ticks are stored per project as a list of what is switched *off*, and a project remembered only the termbases that existed when it was last saved. Anything created since was missing from that list – and missing meant *on*. So opening almost any project quietly ticked Read and AI for every termbase created after its last save, other clients’ among them: on one real installation, 141 of 145 projects would have done so, a median of 25 termbases each, and the batch prompt they fed the model was 98% termbase by weight. A project now speaks only for the termbases it knew about; anything created after it was last saved is simply off in that project – it reads, and sends to the model, only what it was told about. No project needs re-saving. Two things follow: a termbase you switch on in one project no longer follows you into projects that predate it (tick it where you want it), and one you had switched on in a project by leaving it unlisted, with a newer id than anything that project mentions, is now off there – the safe direction.
- **The diagnostic log is no longer flooded by the MultiTerm poll.** Every two seconds, for as long as a document was open, the plugin wrote the same line – “project termbase configuration has no termbases” – into `diagnostic.log`: 43,560 copies in one file, drowning anything useful in it. The poll is fine and cheap; its outcome is now written only when it changes.
- **Renaming a termbase after sorting the list renames the right one.** Sort the Termbases tab by any column, select a termbase, right-click → Rename, and the dialog offered a different termbase’s name: the row’s position was being used to find it, and positions stop matching the list the moment the list is sorted. The same mistake sat behind the Write and Project tick checks, which after a sort ran their language-pair check against the wrong termbase. All three now read the termbase from the row itself, as Open in Editor always did.
- **Escape now closes the Edit Term dialog and the term-details window.** Trados consumes the Escape key before dialogs ever see it, so the Edit Term dialog stayed open despite its Cancel button being wired exactly as Windows requires, and the little metadata window (opened with **I** in TermPicker or the TermLens popup) could only be dismissed by pressing I again or clicking elsewhere. Both are now handled by the same low-level hook that already made Escape work for the TermLens popup and TermPicker. One press closes one layer, innermost first: details window, then the dialog or picker it belongs to.
- **The Termbase Editor’s search box now shows its blinking text cursor.** You could click in, type, and filter – but no cursor ever appeared, so the field looked dead while working perfectly. The “Search:” label sits to its left and sizes itself to your display scaling, while the box sat at a fixed position; once the label grew past that point it overlapped the box’s left edge and – being white on white – invisibly painted over the strip where the cursor lives. Windows reported a perfectly healthy cursor the whole time, which is what made this one hard to find. The box is now placed a measured distance after the label, at any display scaling.
- **Deleting a large selection from the Termbase Editor is now immediate, and no longer leaves TermLens wedged.** Removing a few hundred entries took over a minute with Studio showing “Not responding”, and afterwards the TermLens panel would draw only the first word or two of a segment – a state that survived a refresh, a segment change and closing the panel, and needed a restart. Each row was being deleted in its own database transaction, removed from the grid one at a time, and – the expensive part – followed by a full re-render of the TermLens panel and a database snapshot refresh. Three hundred rows meant three hundred of each, on the interface thread; re-entering the panel’s layout that many times is what left it stuck. The whole selection now goes in one transaction, with one pass over the grid and a single re-render at the end. Nothing was ever at risk in the termbase itself: the entries really were deleted, and correctly.
- **Right-click → Delete in the Termbase Editor now deletes everything you selected.** It removed only the entry under the pointer and left the rest of the selection sitting there, still highlighted – while the Delete key on the same selection removed all of it. Two ways of asking, two different results, and the quieter one looked like it had worked. The menu now also says how many it is about to remove.
- **A termbase export now says which language each column is in, so importing one the wrong way round no longer reverses your terms.** The exported file headed its two columns “Source” and “Target”, which says nothing about what is in them – so importing an English → Dutch export into a Dutch → English termbase filed every English term as Dutch and every Dutch term as English, with no error and nothing to notice until the termbase started matching the wrong way round. The columns are now headed by language, the way memoQ and MultiTerm write theirs, and a file pointing the other way has its columns swapped to match. The import dialog says so when that happens. When the file is for a different pair altogether – an English → Dutch export picked by mistake while a German → English termbase was selected – it now says which pair the file is, what would be stored where, and defaults to Cancel, rather than importing 281 Dutch terms as German. And where the direction genuinely cannot be known – an older export, a file from another tool, or a termbase with the same language on both sides (English → English) – it warns and does not guess. Regional variants make no difference: Dutch (Belgium) and Dutch (Netherlands) both count as Dutch, and the variant is written into the file for whoever opens it. Files already exported still import exactly as before.
- **Importing a termbase export now puts the terms in the termbase you chose.** Exporting one termbase to TSV and importing it into another added nothing to the destination and quietly rewrote the original instead, while reporting that it had imported them into the termbase you picked. Every exported row carries a Term UUID, and the importer looked that UUID up across the whole database rather than within the destination – so it found the term the row had been exported from, and updated it where it stood. Three things follow, all now fixed: a termbase could not be duplicated by exporting and re-importing it; editing the file in Excel first – the ordinary reason to round-trip one – wrote your edits back into the termbase you exported from rather than the one you were importing into; and the confirmation message said it had worked. Nothing was lost when it happened, because what got written back was what had just been exported.
- **The numbers in a bilingual export are now the segment numbers Trados shows you.** They were a count of the rows in the file, which is the same thing only until a document has a split or a merged segment. Split segment 209 and Studio gives you 209a and 209b and carries on at 210; merge 3 and 4 and it skips straight from 3 to 5. The export renumbered both cases 1, 2, 3, 4 – so from the first edit onwards every row was labelled with a different segment's number, and a proofreader's "see row 244" sent you to segment 243. Splitting a project into one file per language did the same thing again, restarting each file at 1. Nothing was ever imported to the wrong place – re-import matches on the identity recorded alongside the file, not on the number – but anyone reading a row number and looking it up in Studio was misled. Confirmed on a real document with a merged segment: the export now reads 1, 2, 3, 5, 6, exactly as the editor does.
- **A prompt can no longer be invisible because of a setting you never chose.** Prompts carried an "App" setting – Both, Trados only, Workbench only – and one of those three quietly removed the prompt from every list while leaving it sitting in your library. The other two did nothing at all. The setting is gone: a prompt in your library belongs to whoever opens it. Your files are not rewritten, and nothing you have is affected – but the trap is closed before it catches anyone.
- **A synonym written like `[!something]` survives a termbase export and re-import.** Square brackets with a leading exclamation mark are how the exported file marks a synonym you have banned, so a synonym whose own text happened to look like that came back as a banned one with its brackets stripped – a flag you never set, on wording you never wrote. It is the same round-trip fault as the unescaped pipe fixed in 20.177, in this format's other delimiter, and just as quiet: the counts looked right and the entry stayed plausible. Only terms that actually contain the marker were ever affected, and nothing else in the file changes.

## [18.20.186 / 19.20.186] – 2026-08-31

### Changed (the TermLens popup has a key of its own)

- ★ **The floating TermLens popup opens with Alt+L. The Ctrl tap is retired.** Pressing and releasing Ctrl on its own was a pleasant gesture and an unreliable trigger: once any other program consumes the middle key of a Ctrl-modified shortcut, what reaches Studio is a bare Ctrl press and release — indistinguishable from a deliberate tap. The popup then opened by itself, took the focus, and broke whatever the other program was in the middle of doing. This is not hypothetical and it is not rare: Supervertaler's own voice commands hit it in 20.132 and needed a synthetic-keystroke guard written specially to work around it. Every keyboard tool, text expander and macro utility a translator runs alongside Studio hits the same thing, and none of them can reach that guard. Diagnosed against one such tool, where a single hotkey press left the popup open and the tool's own copy landing in a window with nothing selected in it.
- **Escape still closes the popup**, by the low-level hook it has always used — the tap detector was never what did that.
- **Alt+L rather than a three-key chord**, because this is a key you press constantly, and beside **Alt+P** for TermPicker, its sibling. Note that Alt is the ribbon's KeyTip prefix, so an Alt+letter can collide with a ribbon command that appears nowhere in Studio's keyboard settings; the letters already used this way — P, Q, S, T, W, and the digits — are known good.
- **An existing installation keeps whatever it had bound**, Studio storing shortcuts per user, so this default reaches fresh installs only. To take it on an existing one: **File → Options → Keyboard Shortcuts → Supervertaler for Trados**, find *TermLens: Show TermLens popup*, and press Alt+L. The row turns red if something in the same scope already holds the key.

### Fixed

- **The Import/Export status checkboxes no longer clip each other.** The six confirmation-status boxes sit in fixed 180px columns while sizing themselves to their own text, so "Approved (translation)" overran its column and left "Approved (sign-off)" with a half-drawn checkbox beside it. The column is now measured from the widest label plus the glyph, so it holds at any DPI scaling and whatever the labels are changed to.

### Changed (shared code, no change in behaviour)

- **The LLM client, the prompt library, the prompt and term models, the prompt generator and the document analyser now live in Supervertaler.Core**, a submodule shared with the forthcoming memoQ plugin, rather than being reimplemented per plugin. Nothing about the plugin behaves differently and nothing new ships inside it: the code is compiled in as source rather than referenced as a library, so the package contains the same twelve DLLs it did before. It is listed here because it is a large structural change, and because the point of it is that a fix to the prompt library now reaches every Supervertaler product at once instead of one of them.

## [18.20.185 / 19.20.185] – 2026-08-27

### Added (Supervertaler can look at your drawings)

- ★ **Supervertaler now reads the reference signs printed on your figures, and tells you which ones appear nowhere in the text.** On a patent, a reference sign carried in the drawings with no basis in the description is an Art. 84 / Rule 42 objection — the kind of thing an attorney wants raised before filing. Until now the only way to find one was to open every drawing and check it by eye. On the job this was built against, it found **ST 05**, printed on two figures and absent from all 354 segments. No amount of searching the text could ever have found it: it exists only as pixels inside the image.
- **Analyse images** on the Batch Operations tab does the whole thing in one go: pulls the images out of your Word documents, shows each one to the AI together with what the document says about it, and writes the result to `figures.md` in your active memory bank — where it is read into every prompt from then on. One AI request per image.
- **The AI is asked what the drawing shows and what is legible on it, not what the invention does.** A plausible-sounding summary of a mechanism is exactly the kind of wrong that survives review, so the question is deliberately narrow.
- **`figures.md` says which parts of it came from the document and which came from the AI**, and asks you to correct anything wrong — because from the moment it exists, it is in every prompt. A mistaken caption would otherwise be repeated into every request silently.
- **Extract images to folder** writes your document's images out as `Figure 01.png`, `Figure 02.png` and so on — zero-padded so they sort properly, original format preserved, and re-running simply overwrites. Verified against a set a translator had extracted and named by hand: fourteen files, byte-identical, same names.
- **Document images** reports what your project's Word files actually contain — each image, its figure label, and what the document says it shows — without calling the AI at all.
- **Reference numerals now finds more than `(12)`.** Lettered points such as `(A)`…`(W)` and label-series signs such as `ST 01` are recognised alongside parenthesised numerals, and `N°7` is understood as another way of writing `(7)` rather than counted as a separate part.

### Added (MCP server – bulk terminology, glossary files, and the fuzzy bands)

- **Your AI assistant can add many terms in one call.** `add_term` now takes an `entries` array of up to 40 term pairs, so locking a 48-term glossary into a project termbase is two calls rather than 48. Each pair is decided on its own: a duplicate or a failure on one does not stop the rest, and the response says what happened to every entry in order, including which termbase each reached and in which direction. Which termbases to write to, and whether to stay inside the project termbase, remain settings for the whole call – so a batch cannot leak into a background termbase 40 rows at a time.
- **Client glossaries that only exist as spreadsheets can now be imported.** `import_project_termbase` reads a `.csv`, `.tsv` or `.txt` export from memoQ or Excel, not just a Trados termbase. The delimiter is detected, and a column headed with a language name – `Dutch`, `English` – is recognised as the source or target side, with `<language> synonyms` columns read as alternative spellings. You must say which language is which, because a text file carries none of that and guessing would write every pair backwards. Always dry-run it first: for a file format with no rules, that report is the only place a wrong column can be caught before it is in your termbase – and it also flags invisible characters such as a non-breaking space inside a term, which would otherwise stop that term ever matching.
- **Analysis figures now come back as separate fuzzy bands**, not one lump. `get_project_statistics` still reports the total, and adds each band with its own range. The 95-99% band is the one that matters: a match that high reads fluent and plausible while differing from the source in exactly the load-bearing words – on a patent, an ordinal, a reference letter or a claim back-reference. Knowing there are seven such segments carrying 207 words is something you can act on; knowing there are 27 fuzzy segments somewhere between 50% and 99% is not.

### Fixed (the memory bank follows the project now)

- **Your active memory bank no longer follows you from one project into the next.** It was remembered per installation rather than per project, so opening a different client's job left you pointed at the previous client's bank – and because the bank feeds every prompt, that quietly supplied the wrong terminology and style to every request with nothing on screen to say so. The bank is now remembered per Trados project: choose one and it sticks to that job. A project with no bank recorded now uses **none**, and says so, rather than inheriting whichever one you had open last – no bank is better than another client's bank.
- **And it now actually sticks.** Three things stood between the setting and the behaviour, each silent on its own. The bank was realigned from one panel while the project it aligned to was tracked by *another*, on the same event with no ordering between them — so the bank lagged exactly one project behind on every switch. Only the SuperMemory dropdown recorded your choice: pick a bank from the Library tab's **Set as active** and it was forgotten the moment the project changed. And renaming a bank left every project still naming the old one, which resolves to no bank at all. The project is now read from the document itself, all three ways of choosing a bank record it, and a rename carries every project with it. A fourth, found by watching the files rather than reading the code: the choice was being written correctly and then blanked seconds later, because saving a project's settings rebuilds the whole file from your global settings and drops anything that belongs to the job rather than to the installation — and that save runs on every project switch. So choosing a bank and then leaving the project erased the choice between those two actions.

### Fixed (termbase direction, and a check that could hide its own findings)

- **The prompt dropdown on Batch Operations was hiding the prompts it shipped with.** Picking Proofread offered nothing but "(None – default)", even though the Proofread folder in your library was sitting there with prompts in it. The dropdown matched a prompt's folder name exactly, and the prompts that come with the plugin live one level down – in Proofread/Default – so none of them matched. Prompts in any subfolder now count, so everything under Proofread appears when you choose Proofread, and everything under Translate when you choose Translate. This affected Translate too: on a fresh install, where the only prompts are the ones supplied, both dropdowns were empty. Reported by a user who could see three prompts in the folder and none in the list.
- **The Studio 2026 build no longer stops working when Studio updates.** It declared that it needed a Studio version between 19.0 and 19.0.9, so the first 19.1 update would have taken it out of Studio's plugin list without warning and made every reinstall appear to succeed and change nothing – a failure with no error message anywhere. The Studio 2024 build always allowed the whole 18.x range; the 2026 build now allows the whole of 19.x to match.
- **The term editor could label a term's languages wrongly.** Opening a term that exists in several termbases and switching between them reloaded the terms but left the language labels describing the termbase you had just left – so on a Dutch-to-English job an English-to-Dutch termbase showed its English term under "Dutch" and its Dutch term under "English". Nothing was ever wrong in the termbase itself; only the labels were. That matters more than it sounds, because the natural response to seeing it is to swap the two fields and save, which would break an entry that was correct all along.
- **The two language columns now stay the same width, with each label over its own field.** They were laid out correctly and then drifted apart as soon as the dialog was resized, because the left column stretched, the right one only slid sideways, and the labels did not move at all.
- **A terminology check narrowed to one termbase could hide that termbase's own findings.** When a longer term from an excluded termbase overlapped a shorter one from the termbase you asked about, the longer one claimed the words first and the shorter one was never looked at – and the check then reported a clean result. Restricting a check to a client termbase is precisely when you are relying on it to be complete.
- **Your AI assistant can now edit a term named either way round.** Every place a term is shown to you – the terminology check, TermLens, the assistant's own context – presents it in your project's direction. A termbase whose declared languages are the reverse of the project's stores it the other way, so asking the assistant to change a term it had just shown you was refused as "no entry found". It now matches on both terms whichever column each sits in, and a rename you give in that same order is written in the termbase's own order, so correcting a term cannot silently reverse it. The reply says when an entry was stored the opposite way from how you named it.
- **An edited term now records when it was edited.** Changing a term's note through the AI assistant left its "modified" date at the day it was created, so an entry rewritten today read as untouched for months.

### Fixed (figure labels, and how the reports read)

- **Figures could be labelled wrongly, and the report said everything was fine.** Where several images shared a paragraph, all of them took that paragraph's label — so four figures came out as "FIG. 3", three figure numbers were never assigned to anything, and the summary called it "the easiest case there is". Images and labels are now paired by position and the pairing is *checked*: if the numbers do not line up, no labels are applied and the report says what it counted. A wrong figure label is invisible downstream and corrupts everything built on top of it, so refusing is better than guessing.
- **Word writes a floating text box twice**, once for modern Word and once for old versions, which made figure labels count double — 21 labels for 14 images on a real document, enough to make the check above refuse the whole file.
- **A figure's description is matched by its number, not by what sits next to it.** On a patent the plates are at the back and their descriptions are hundreds of paragraphs away in the body, so "the text around the image" is the figure label and some blank lines. All fourteen figures on the test document now carry the description that actually describes them.
- **The same description no longer appears twice** when the figure list and the detailed description differ only by a full stop — while genuinely different wordings are both kept, because the longer one usually names the parts.
- **Reference numerals: the citation shown for each numeral now contains that numeral.** The preview was cut from the start of the segment, so on a real patent 14 of 20 rows showed a snippet in which the row's own numeral had been cut off.
- **Reports read properly in the panel.** They were being laid out as tables and then flattened, repeating the column headings on every row; they are now written for the width they are shown at. `figures.md` keeps its table, because it is read full width.
- **The Reference images folder can be set from the tab that uses it.** The setting existed only in Settings → Library on a memory bank, four levels into a dialog, where two people looking for it failed to find it.
- **Saving a chat to a memory bank no longer strips its formatting.** A table arrived as loose pipe-separated lines, and headings and bold were flattened — in a file that is read as Markdown by Obsidian and by Supervertaler itself.
- **Saving a chat no longer tells you to run a command that does not exist.** The confirmation asked you to "run Process Inbox", which was removed long ago, to compile the note into a knowledge base that never reads that folder. It now names the file it wrote and says plainly that the `reference` folder is the audit trail and is not read into prompts.
- **Long AI replies keep their "Show full response" link** when they arrive while you are on another tab, and are cut at a line break rather than mid-word.
- **The bilingual review export links to supervertaler.com** rather than the Trados sub-page.

## [18.20.184 / 19.20.184] – 2026-08-25

### Changed (QuickLauncher is on Alt+Q, because Ctrl+Q never worked)

- ★ **QuickLauncher has moved from `Ctrl+Q` to `Alt+Q`.** `Ctrl+Q` is a Trados factory default — **View Internally Source** — and Trados wins, so pressing it opened Trados's own command and QuickLauncher did nothing at all. No error, no hint that a plugin feature was meant to fire. On a fresh install the entire QuickLauncher menu, and the ten prompt slots behind it, were unreachable until you found the conflict yourself and cleared the binding in Studio's settings. `Alt+Q` matches the other shortcuts — `Alt+T` translate, `Alt+S` SuperSearch, `Alt+W` web search, `Alt+P` TermPicker. Trados does put **Tell me what you want to do** on `Alt+Q` (seen in Studio 2024), so it joins the short list of keys to free up in **File → Options → Keyboard Shortcuts** — a one-off, and Tell Me is a ribbon search box that works fine without a shortcut. The difference from `Ctrl+Q` is that the conflict is now documented in the help, the About box links to that page, and the key is one you would guess.
- **If you had already cleared the Trados binding to make `Ctrl+Q` work**, that key now does nothing for QuickLauncher. Use `Alt+Q`, or set your own under **File → Options → Keyboard Shortcuts**. You may also want to give **View Internally Source** its `Ctrl+Q` back.
- **The About box now lists QuickLauncher, its ten slots, and a link to the full shortcut reference.** That list is where you would go to look up a shortcut, and it did not mention `Ctrl+Q` — which is exactly how a flagship feature sat behind a dead key without it being obvious. The link goes to the docs page, which also carries the table of Trados defaults that override other Supervertaler keys and how to clear them.
- **The insert-term range was listed as `Alt+1…9`**; it is `Alt+0…9`.

### Fixed (the MCP server for ChatGPT was never updated after the first install)

- **Connect AI assistant… only ever downloaded the MCP server if it was missing**, so anyone who set ChatGPT up once kept that same server for ever. Pressing the button again rewrote the configuration and nothing else. The plugin could tell the server was too old for it — and said so — but there was no way to act on that short of deleting the file by hand. It now checks the installed server's version and replaces it when it is behind.
- **Updating no longer requires quitting ChatGPT.** Windows will not let a running program be deleted, so overwriting the server while ChatGPT had it open failed with a raw *"the process cannot access the file"*. The old server is now moved aside instead, which Windows does allow: ChatGPT keeps using it until you restart, and the new one is in place for next time. If even that is blocked, the message tells you what to quit rather than showing the underlying error, and your working server is left untouched.
- **A failed download can no longer leave you with no server at all.** The download used to unpack straight onto the existing file, which empties it before writing — so a download that failed half way took the working server with it. It now unpacks alongside and only swaps once the file is complete.
- **Installing the Claude Desktop extension over an older one** fails with an `EPERM … unlink` error, for the same reason: Claude Desktop leaves the server running while it replaces the extension's files. The Connect dialog now spells out the extra step — quit Claude Desktop from the notification area first — and explains the error if you hit it anyway. (Only when an extension is already installed; a first install is unaffected.)

### Added (translate two projects at once, in two Studios, with two AI assistants)

- ★ **Translate two different projects at the same time, in two versions of Trados Studio, each driven by its own AI assistant.** Open Studio 2024 and Studio 2026 side by side, tell ChatGPT *"use the 2024 one"* and Claude Desktop *"use the 2026 one"*, and set both going. Each assistant reads and writes only its own project and cannot touch the other's document. Two jobs progress at once, in two Studio windows, on one machine — and because you can talk to these apps by voice, both can be running while you are doing something else entirely.
- **Say which Studio you mean in plain language** — *"work with the 2026 one"*, *"use the Acme project"* — and that chat is bound to it for the rest of the session. Ask *"which Trados instances are running, and which are you using?"* to see the list first. Two new tools do the work: **list_trados_instances** and **select_trados_instance**.
- **The choice follows the project, not the process**, so it survives that Studio being closed and reopened. You do not have to say it again after a restart. Closing the other Studio works just as well — the one left is unambiguous immediately, with nothing to restart.
- **Or pair an app with a Studio permanently.** For people who always keep the same pairing, add `--instance 2024` to the server's arguments in the app's MCP configuration, or set `SUPERVERTALER_TRADOS_INSTANCE`. An app pinned this way never asks — and if the Studio it wants is not running, it says so instead of quietly using the other one.
- **Until you choose, reading works and editing waits.** Questions are always answered and the reply names the Studio and project it came from; anything that would change a document stops and lists what is open. Guessing is the one thing it will not do.
- **The Connect dialog warns when a second Studio is running**, and names its project — there is no way to tell from inside the first one, and it decides whether the AI will accept an edit at all.
- Requires the updated MCP server: reinstall the extension in Claude Desktop, or press **Connect AI assistant…** for ChatGPT, which now updates the server for you.

### Fixed (two Trados versions open at once no longer send the AI to the wrong one)

- **With Trados Studio 2024 and 2026 both open, an AI assistant could edit the wrong project without anything going wrong on screen.** Each Studio runs its own Supervertaler bridge, but they announced themselves in a single shared file, and whichever started last overwrote the other. So a chat app you had pointed at your 2024 project would quietly send its edits to the 2026 one instead — no error, no warning, the segments simply landed in the wrong document. Each Studio now publishes its own entry, carrying its Studio version and the project it has open.
- **When two are open, the AI is told so, and edits are refused until you say which one you mean.** Anything that reads — segments, TM matches, terminology, QA checks — still works and now names the Studio and project it answered from, so the AI can tell you which project it is describing. Anything that writes stops and asks. Refusing to guess is the point: a lookup from the wrong project is confusing, an edit into the wrong project is damage.
- **Closing one Studio no longer disconnects the other.** Shutting down deleted the shared entry no matter who owned it, so closing one Studio left the other running, connected to nothing, reporting only that no bridge could be found. Each Studio now cleans up after itself and hands the connection over to whichever is still running. A Studio that crashes or is killed is cleaned up by the next one to start.
- **A Studio still running now cleans up after one that has closed.** Trados ends its process without giving plugins a chance to tidy up, so a closed Studio always leaves its entry behind – harmless, because a stale entry is ignored, but it meant an older AI app saw a dead connection instead of being pointed at the Studio still running. The Studio that is still open now clears those entries and takes over the connection, rather than depending on the one that closed.
- **A closed Studio cannot come back as a phantom.** Windows reuses process numbers, so a fresh Studio could inherit a closed one's number and make it look as though two were open – which would refuse your edits for no reason. Entries now record when they were written and are checked against it.
- **Under the bonnet:** entries are matched on process identity rather than process id alone, so a recycled id cannot resurrect a Studio that has closed and block your edits with a phantom second instance. This needs the updated MCP server, which is on the release page — an older one keeps working exactly as before, on a single Studio.

### Added (the Library tab – see and edit your memory banks without leaving Trados)

- ★ **The Prompts tab is now the Library, and it shows SuperMemory.** Every memory bank and the files inside it appear beneath your prompt folders, so the one thing you could never see from inside the plugin – what the AI actually knows about a client – is now in front of you. Until now the only ways to look at a bank were Explorer or Obsidian.
- **The tree says two things nothing else did.** `_shared` is marked *“loaded with every bank”* rather than sitting in the list looking like an alternative to the active bank, and `reference/` is greyed and marked *“not read into prompts”* – it is the audit trail, and without that label people keep filing things there and wondering why the AI ignores them.
- **Bank files render as Markdown** rather than raw text, using the same converter as the chat panel, so a terminology table reads as a table. Select a file and **Edit** opens it for editing.
- **Rename and delete memory banks from inside the plugin.** Right-click a bank. Previously this meant closing Trados and renaming folders by hand. Deleting moves the bank to a `.trash` folder inside `memory-banks` rather than destroying it, so you can put it back by renaming that folder – and the confirmation tells you where it went.
- **`_shared` is protected**, and deleting the bank you are currently using is refused rather than quietly switching you to another one: which bank is active decides what every prompt is built from.
- **A Reference images row** on a bank and on its `figures.md`, naming the folder of drawings the current project points at. Groundwork for the figure-analysis feature; the analysis pass itself is not built yet, so no button pretends otherwise.
- **Editing a bank file leaves the rest of the file alone.** These files are shared with Obsidian and the Supervertaler assistant, which do not agree on line endings, so a naive save would rewrite every line and bury your one-word change in a whole-file diff. Supervertaler now writes each file back in its own style. If something else changes the file while you have it open, you are told before anything is overwritten.

### Changed (Alt+W no longer opens the SuperSearch panel as well)

- **A web search opens the browser window and nothing else.** `Alt+W` used to pop the SuperSearch panel open at the same time, which cost editor height for no benefit — the browser window already names the term, the resource count and a tab per site, and it raises itself, often on another monitor where the panel is not even in view. `Alt+S` still opens the panel, because that is where its results appear.

### Fixed (the term editor's hinted fields looked dead when you clicked them)

- **Clicking Abbreviation, or either synonym box, showed no text cursor.** The field had the focus and typing worked, but the grey hint sits on top of the box and the cursor was blinking behind it, so the field looked like it was not accepting input. The hint now steps aside the moment you click in, rather than waiting for the first keystroke. Affects the four hinted fields in **Edit term entry**; the other boxes were never affected.

### Fixed (Markdown rendering, everywhere it is used)

- **Headings below `###` printed their own hashes.** A file using `#####` for its sections rendered as a wall of text with `##### 4. Title blocks and document metadata` sitting in it literally. Affected the chat panel too, so any AI reply using `####` had the same problem.
- **Wrapped text lost its formatting and its shape.** Paragraphs broke at whatever column the file happened to wrap at; two-line bullets had their second half thrown to the left margin, outside the bullet; and a **bold** span split across a line break showed its asterisks instead of going bold.
- **Blocks ran together with no space between them**, so a heading was indistinguishable from the paragraph above it.

### Fixed (the settings fix, finished off everywhere else)

- **The same fix now covers the whole plugin, not just the panels.** Every remaining place that read or wrote settings on its own — the termbase editor, the term picker, the voice strip, the QuickLauncher, the update prompt, SuperSearch's mode and web-resource settings — goes through the one shared copy. Ten of those could previously lose a change outright: they read the file, altered one field and wrote the whole thing back, so anything saved by another part of the plugin in between was reverted.
- **Anonymous usage statistics could count one installation as two.** The anonymous id was read, checked and written in three separate steps, so two things starting at once could each find it missing and generate a different one. Only affects people who opted in, and only the accuracy of the totals — no additional information was ever collected.

## [18.20.183 / 19.20.183] – 2026-08-16

### Fixed (settings quietly reverting, depending on which panel you opened them from)

- **A setting could be undone by a panel that was not even involved.** Change your memory bank in the Supervertaler Assistant, then open Settings from the TermLens panel and click OK: the bank goes back to what it was. Nothing warns you, and the panel afterwards agrees with the reverted value, so the change looks like it never happened rather than like it was lost.
- **The cause was five copies of one settings file.** Each panel and dialog held its own, and saving wrote the whole thing back, so whichever saved last silently reverted every field another had changed since it loaded. There is now one shared instance, so "a stale copy" is no longer something that can exist. This was not a memory-bank fault: **any** setting could be lost this way — API keys, which termbases are ticked, batch size, provider choice. The memory bank is simply where the damage was visible, because it ends up as the wrong terminology in a finished translation.
- **A new prompt not appearing in the dropdown was the same fault**, seen from the other end, and is fixed by the same change.
- **Two of the five ways to open Settings could not see your changes at all.** The licence link in the About box and the QuickLauncher menu header each opened their own copy of the settings file, so anything you had changed in either panel since Studio started was reverted the moment you clicked OK — and nothing refreshed afterwards, so the panels went on showing values the file no longer held until you restarted. Every gear icon, menu entry and link now opens the same Settings, and both panels update whichever one you came from.
- **Changing a setting no longer freezes Studio for up to two minutes**, depending on which gear icon you used. Settings reloads the termbases when it closes, and one of the two paths did it on the interface thread. Both now do it in the background, as the faster one already did.
- **A memory bank created inside the Settings dialog now appears in the dropdown straight away.** It used to appear only if you had opened Settings from the TermLens panel rather than the Assistant.
- **Deleting a termbase and then pressing Cancel used to leave the settings pointing at it.** The termbase itself was already gone — deletion happens immediately — but the references to it were only cleaned up if you pressed OK. They are now cleaned up either way.

### Fixed (the AI being handed less than it asked for, without being told)

- **Part of a memory bank could be left out of an answer with nothing to say so.** A bank that does not fit the size limit is trimmed, which is necessary — but it was silent, and two of your three articles look exactly like all three. A rule you had written down could be simply absent from what the AI saw, and neither of you would know. The AI is now told which files were left out and that it should ask rather than guess when a question turns on them.
- **`get_supermemory_context` ignored the bank you asked for.** Ask for one client's bank while another is active and you were quietly given the active one, with the response naming your requested bank back at you — so the reply read as confirmation. The argument now works, and an unknown bank name is refused, listing the banks that exist, rather than falling back to the active one and injecting another client's locked terminology.
- **Files you added to a memory bank yourself did nothing.** Only three fixed filenames were ever read into a prompt, while the bank listing counted every `.md` — so a hand-written `figures.md` was reported as present and contributed nothing. All Markdown files at the top of a bank are now read, under their own filename.

### Fixed (Batch Operations showing a different prompt from the one it would use)

- **The dropdown could show one prompt while the tick inside it marked another.** The closed box was filled in from a guess based on the project name; the tick came from the prompt you had actually set as active. So a run used the ticked prompt while the box named a different one. The active prompt now decides both. The project-name guess still applies when no prompt is active — a guess should not outrank a choice.

### Changed (Add to SuperMemory: say where it goes, and let you choose)

- **The Quick Add dialog now asks which memory bank to write to**, with the active one preselected, every other bank listed, and **_shared** at the bottom, labelled *(applies to all banks)* because it is the one entry that is not a single client. Until now it wrote to the active bank silently and named it only in the confirmation afterwards — so a term could land in the wrong client's terminology and the first sign of it would be a delivery. **_shared** could not be reached from the dialog at all; it had to be edited by hand.
- **It also names the exact file before you commit**, under the picker: *terminology.md in Acme (PROJ-001)*, or *reference/* if you tick the second mode. Choosing _shared is marked in amber, because a rule put there applies to every job you do.
- **The two mode labels were describing the old memory-bank layout.** They said your entry became "a structured article in **02_TERMINOLOGY**", or a "raw note ... (**00_INBOX**)". Neither folder has been written to since the bank redesign: entries go into **terminology.md**, and notes into **reference/**. The labels now say what actually happens, and what the difference is for — whether the AI ever reads it.
- **The second mode no longer claims your note will be processed by AI.** It offered to save "for AI processing", to be "compiled by Process Inbox" — a command that does not exist in this plugin. The reference folder is deliberately never read into a prompt: it is the record of where knowledge came from, so a claim the AI makes can be checked against its source. The checkbox is now **"Save as background reference instead"** and says plainly that nothing reads it automatically. It is still worth using for something you want to keep but cannot yet write as a term pair — the confirmation tells you how to promote it when you can.

### Changed

- **The bilingual Word export no longer has a Notes column.** It was empty on the way out and discarded on the way back, so it named nothing you could rely on, and sitting next to Comments it suggested a distinction that did not exist. Its width has gone to Source and Target. Files exported before this change still import correctly.

## [18.20.182 / 19.20.182] – 2026-08-15

### Added (connect ChatGPT desktop to your Trados session, in one click)

- ★ **The MCP server works with ChatGPT desktop, and the plugin now sets it up for you.** Ask ChatGPT about the project open in Studio, search your TMs and termbases, run QA checks — the same live connection Claude Desktop has had. **Settings → AI Settings → Connect AI assistant…** now has a **Set up ChatGPT desktop** button that downloads the server, keeps it in your Supervertaler data folder and registers it, so there is no zip to unpack and no configuration file to edit. Quit ChatGPT from the notification area afterwards — closing the window is not enough — and start it again.
- **Your existing configuration is backed up first, and nothing else in it is touched.** Only Supervertaler's own entry is written; other MCP servers you have set up are left exactly as they are, and running the button again refreshes the server rather than adding a second copy.
- **Earlier versions of the documentation said ChatGPT could not be used at all.** That was true when written — it ran MCP servers in the cloud, with no route to a bridge that is local to your machine by design — and has since changed. What still cannot work is a client that runs the server in the cloud, which includes the claude.ai and chatgpt.com **websites**, as opposed to the desktop apps.

### Added (ProZ.com's new term search, as an option)

- **ProZ has rebuilt its term search, and both versions are now available.** The classic one is now labelled **ProZ.com (old)** and the rebuilt one **ProZ.com (new)**, sitting next to each other in the list. The classic search stays switched on as before; the new one ships **switched off**, so nothing about your setup changes unless you want it to. Tick it in the **Web** picker to try it, and keep whichever you prefer — or both.

### Fixed (legacy termbase entries whose stored languages contradict their termbase)

- **A term saved the wrong way round for its termbase is checked against nothing.** Every read path orients an entry by the *termbase's* declared direction, so a row whose source column holds the termbase's target language gets indexed under the wrong language: no source segment can ever match it. The entry still sits in the termbase, still answers `lookup_term`, still reads as locked — and `check_terminology` passes over it in silence, however badly the document violates it. The failure is an *absence* of checking, which is the one kind nothing on screen can show you.
- **What is now detectable is the legacy population**, where old write bugs corrupted an entry's stored language labels and its text together: the contradicting labels are the signal, and every such entry is now reported. **An entry typed into the wrong boxes today is not caught**, because its labels are correct — only the text is reversed, and telling that apart needs the text's actual language, which the plugin will not guess. A wrong silent answer there would be worse than none.
- **`lookup_term` hits now carry `directionMismatch`** when the entry's own stored languages contradict its termbase's declared pair. This matters more than it sounds: a reversed entry's output looks entirely sensible on inspection — Dutch text in the field reported as Dutch, English in the field reported as English — so a reviewer verifying orientation through `lookup_term` would confidently confirm that all was well, using an instrument that could not see the fault. Reported from a live job, where fifteen entries were "verified" exactly that way.
- **`check_terminology` now reports the same contradiction**, in a `directionMismatches` section listing the affected entries per termbase with sample pairs, and a note saying plainly that where such an entry is genuinely reversed, silence about it means "not looked at", not "not violated". A term is usually locked *because* it was a known defect source, so one sitting in that list is the worst case, not an edge case.
- **The flag says "inspect this", never "this is dead", because two different faults produce it.** Either the text is reversed — the entry then matches nothing — or only the language labels are wrong while the terms are correctly oriented, in which case the entry works perfectly, since the read path ignores those labels anyway. Telling them apart needs the text's actual language, which the plugin deliberately never guesses (the same refusal as in the write path: term pairs are routinely identical across languages, so a detector would guess, and a wrong silent answer is worse than an honest "check this"). On the termbase this was built against, 2 of the 40 flagged rows were the harmless kind, so wording that called them all broken would have been wrong twice.
- **Reported, not silently repaired.** The per-entry language tags are exactly the field the read path stopped trusting in v4.19.21, after legacy write bugs left them wrong on rows whose text was perfectly fine; auto-flipping on that signal would turn a cosmetic tagging slip into a genuinely broken entry. Repair means re-adding the pair the right way round, or `tools/repair_termbase_directions.py` for a whole termbase, which weighs the text as well as the tags.
- **An entry whose two terms are the same string is not reported**, even with contradicting tags. Reversing it changes nothing — the index key is identical either way, so it matches exactly as it should. That is brand names, units and chemical formulae, and on the database this was built against it was 68 of 108 candidate rows: reporting them would have buried the 40 that are genuinely broken.
- **New entries have not been able to acquire this shape since the strict write path landed.** `add_term` orients per termbase, or refuses rather than guessing, so this is legacy damage: on the reporting user's own database, 40 broken rows across two termbases, none newer than June.

## [18.20.181 / 19.20.181] – 2026-08-14

### Changed (the web-search shortcut is Alt+W, not Ctrl+Alt+L)

- **Ctrl+Alt+L was the wrong choice and never reached the App Store.** That combination belongs to **Supervertaler Workbench**, which registers it as a *global* hotkey for its own SuperLookup — global meaning it fires wherever you happen to be typing, including inside Trados. Anyone running Workbench alongside Studio, which is the normal way to use them, would have triggered both at once.
- **Alt+W is the shortcut instead**, pairing with SuperSearch's existing **Alt+S**: S searches your own material, W searches the web. Alt+S is unchanged, and both can be rebound in Studio's keyboard shortcut settings.

## [18.20.180 / 19.20.180] – 2026-08-14

### Added (SuperSearch now searches the web too) — [#64](https://github.com/Supervertaler/Supervertaler-for-Trados/issues/64)

- ★ **Select a term, press Alt+W, and 41 reference sites are one keystroke away** – IATE, Linguee, Reverso, ProZ, Juremy, Glosbe, EUR-Lex, Wikipedia and the rest, with the query and your project's own language pair already filled in. There is nothing to type and no language dropdown to set. Also on the editor right-click menu as **Search the web**, and on a new **🌐** button in the SuperSearch bar.
- **Alt+W is a new, second shortcut – Alt+S is unchanged.** Alt+S still searches your project files, TMs and termbases into the results grid, exactly as before. Alt+W is the web half. Neither affects the other, and both can be rebound in Studio's keyboard shortcut settings.
- **Web resources are a fourth SuperSearch scope**, sitting beside Files, TMs and TBs. Click **Web (n)** to choose which sites are active; five are on out of the box – Beijerterm, IATE, Linguee, ProZ and Reverso – and the other 36 are one tick away. Your own sites can be added with a URL template.
- **Results open either in a Supervertaler window or in your own browser**, your choice, from a checkbox in that same dialog. Neither is a fallback for the other and both are worth having: your browser brings your ad blocker and your signed-in sessions, while the Supervertaler window keeps one window and refreshes its tabs in place instead of leaving a trail of browser windows behind.
- **In the Supervertaler window, tabs load only when you click them.** Eight enabled resources would otherwise mean eight embedded browsers at once inside Studio 2024, which is a 32-bit application with a memory ceiling that Studio itself already presses against.
- **A term picked from the target side is searched in the target language.** Looking up a Dutch word in an EN→NL project searches nl→en, not en→nl – the latter is how you get a screen of nothing and conclude the site is broken.
- **Sites that demand a human-verification check are flagged, not fought.** ProZ in particular blocks embedded browsers; when that happens the tab offers to hand the page to your own browser, where you are already signed in and pass instantly. It is an offer, not a jump: nothing drags you out of the editor mid-segment.
- **Four resources were repaired or removed after checking all 41 against the live sites.** 2lingual, Oxford Collocations and the Financiële Begrippenlijst had all changed their URL schemes and had been quietly returning nothing – the Dutch one addressed an A–Z index page that has never been a term page at all. ChemIndustry is gone: the domain changed hands and now serves an unrelated site.
- **The resource list is interchangeable with the standalone SuperLookup app**, which uses the same file format, so a list exported from one imports into the other unchanged.

### Fixed (AutoPrompt could attribute wording to a source that never supplied it) — [#58](https://github.com/Supervertaler/Supervertaler-for-Trados/issues/58)

- **A generated prompt could describe wording as "anchored by validated TM segment" when the TM contained nothing of the sort.** Seen on a real run: several glossary notes citing validated segments against a TM consisting of one title and five section headings, and one citing a segment for a word that does not appear in the source at all. Worth fixing carefully, because a provenance note is easy to read back as your own earlier decision rather than as something to check. A provenance claim may now only name a source that actually supplied the term — TM wording only for terms literally present in the supplied pairs, house wording only for rules in the knowledge base, termbase wording only for supplied terms — and where an input is absent, the prompt says so explicitly rather than leaving the claim available.
- **Given 7 validated TM pairs, the model was emitting 11**, filing its own renderings under "additional validated project segments" — one even carrying a tracked-change marker, which no human TM produces. That section outranks the glossary, so an invented entry was the most authoritative and least grounded thing in the prompt. Both branches now say "and no others", and say where a self-derived rendering belongs instead.
- **A glossary row could carry three candidate translations under a heading marked MANDATORY, LOCKED.** On a 535-segment patent run the Notes column was used to smuggle in an alternative: the locked cell offered *housing (enclosure)* while the note governed the term's only actual occurrence with a third rendering. Batch translation has no memory between batches, so an open choice is resolved differently each time. One locked target per row now, with the model told to split a term into one row per collocation where it genuinely differs. The same rule now covers mappings written in prose, which previously escaped it.
- **A memory bank you created but never filled in was announced to the model as "hard-won translation decisions and client-specific rules", followed by nothing** — an invitation for the model to supply conventions of its own and present them as established. A bank still matching the skeleton it was created from is now treated as absent rather than empty-but-asserted. Anything unrecognised counts as content, so a bank you have edited is never silently dropped.
- **When no termbase is enabled for the AI, the prompt now says so honestly.** The old warning claimed no glossary would be sent; in fact the model built one from the source text anyway, and the result was indistinguishable from a termbase-backed glossary in the finished prompt. The derived glossary is now explicitly derived, and its provenance is recorded in the saved prompt's metadata — shown in the library panel and QuickLauncher tooltip, where a person reads it, and never sent to the translating AI, where a "verify before use" line would have licensed the very substitution the lock exists to prevent.

### Fixed (a finished Batch Translate run could still be unsaved)

- **Batch translations went into the in-memory document and stayed there** until you saved by hand — so a 27-batch job could finish and exist only in memory for as long as the document stayed open. The project is now saved once when a run completes, including when you cancel it, which is when keeping the partial output matters most. If the save fails you are told to press Ctrl+S; the translations are in the document either way.
- **Studio's AutoSave does not close this gap, despite the name.** Measured on a live project: with AutoSave set to 5 minutes, an edited document went three and a half hours without its `.sdlxliff` being touched. AutoSave keeps a *crash-recovery copy* under `AppData\Roaming\Trados\Trados Studio\Studio19\AutoSave\`, from which Studio offers to restore after a crash — it never writes the project file. So until something performs a real save, the file on disk stays stale, and anything reading the file rather than Studio's memory — batch tasks, exports, external tools — sees the old content.
- **Saving after every batch was considered and rejected**: Studio's save is synchronous on the UI thread, so it would freeze Trados at every batch boundary to close a gap that the every-10-segments backup TMX already covers between runs.

### Fixed (the cost warning recommended models your provider may not have)

- **The cost tip named GPT-5.4 Mini and GPT-5.5 whatever provider you were using**, so a user on Claude, Gemini or Ollama was advised to switch to a model that does not exist for them. It no longer names a model.

## [18.20.179 / 19.20.179] – 2026-08-11

### Fixed (TermLens missed terms wrapped in Markdown, e.g. `**doelstelling**`) — [#63](https://github.com/Supervertaler/Supervertaler-for-Trados/issues/63)

- **A one-word term did not highlight when the segment had Markdown emphasis around it.** In a document where the client writes `**doelstelling**` inside the text – literal asterisks, not Studio tags – the term was in the termbase and TermLens showed nothing. Reported from a live job.
- **Multi-word terms in the same document did match**, which made it look random: `**duidelijk en concreet**` highlighted while `**doelstelling**` did not, two segments apart. They are found by different means, and only the single-word path was tripped by the asterisks.
- **Your prompts were never missing those terms.** The AI side matches on word boundaries and reads straight through the asterisks, so Batch Translate, AutoPrompt and the chat had the terminology all along. What was affected is what you can see and click: TermLens chips, TermPicker, and Alt+number insertion – which is worse than it sounds, because a term with no highlight looks like a term you never saved.
- Markdown emphasis is now trimmed from the ends of a word before it is looked up, covering `**bold**`, `*italic*` and `_italic_`. Terms with an underscore or asterisk inside them, like `snake_case`, are untouched.

## [18.20.178 / 19.20.178] – 2026-08-11

### Fixed (a new termbase switched itself on for the AI) — [#62](https://github.com/Supervertaler/Supervertaler-for-Trados/issues/62)

- **Every termbase created since the AI opt-in was introduced has been sent to the AI by default**, which is the opposite of the intent and of what the settings describe. The "AI" column is stored as a list of termbases to *exclude*, and a brand-new termbase was in no list at all – so it counted as included, and the grid then showed its AI box ticked, making it look like a deliberate choice.
- **Creating a termbase is not consent to send its contents to a model.** A new termbase may be large, unreviewed, or full of material that has no business in a prompt. From this build a new one starts **Read-enabled but not AI-enabled**, whichever way it was created: the **+ Add** button, importing a Trados `.sdltb`/`.ttb` into a new termbase, or an AI assistant creating one through the MCP server. Tick its **AI** column when you want it used.
- **Existing termbases are left exactly as they are.** The stored list cannot tell "the user switched this on" apart from "this was never recorded", so correcting it in bulk would silently switch off termbases people had chosen on purpose. **If you have created a termbase recently, check its AI column** – it may be on without you having asked for it.

## [18.20.177 / 19.20.177] – 2026-08-11

### Fixed (a term containing a pipe was mangled by a TSV export/import round trip) — [#61](https://github.com/Supervertaler/Supervertaler-for-Trados/issues/61)

- **A term whose own text contains a `|` came back split in two.** The TSV export uses `|` to separate a term from its synonyms, and never escaped the character when it appeared in the term itself – so exporting and re-importing turned `DC| mode` into the term `DC` with a synonym ` mode`. Silently, on both the source and target side, with correct-looking counts. Found in a real project termbase, on two entries.
- **Pipes and backslashes in a term are now escaped**, so the delimiter and the character can be told apart. Verified across the awkward combinations, including a term containing both.
- **This does not repair an existing export.** In a file written before this build, a delimiter and a literal pipe are indistinguishable, and no amount of later cleverness can separate them. Re-export anything you intend to keep.
- **Files written from now on are also slightly different for older builds**: a pre-20.177 Supervertaler reading one will show a stray backslash in an affected term rather than splitting it. Only terms containing a pipe or a backslash are affected at all.
- **The MultiTerm XML and TBX exports were never affected** – they use XML escaping, and a round trip through them preserves such terms exactly.

## [18.20.176 / 19.20.176] – 2026-08-11

### Fixed (MultiTerm XML export named its languages wrongly)

- **The exported `<language>` element repeated the language code where MultiTerm expects the language name** – `type="EN"` instead of `type="English"`. MultiTerm matches its language indexes by that name, so an import would at best have created indexes called "EN" and "NL". Caught by comparing the export against a MultiTerm XML file known to import cleanly; the two are now identical in that respect. Everything else about the structure already matched exactly.

## [18.20.175 / 19.20.175] – 2026-08-11

### Added (get terminology *out* of Supervertaler and into Trados) — [#60](https://github.com/Supervertaler/Supervertaler-for-Trados/issues/60)

- **Export now offers MultiTerm XML and TBX, alongside the existing TSV.** Until now terminology only travelled one way: a Trados termbase could be imported into Supervertaler, and nothing could go back. Pick the format in the save dialog on **Settings → Termbases → Export**.
- **MultiTerm XML** is what Glossary Converter and MultiTerm import, so it is the route to a `.sdltb` — and, via Studio 2026's Termbases view, to a `.ttb`. **TBX** is the ISO standard and is read by most other CAT tools too, so it is the better choice if you are not only a Trados shop.
- **Both carry more than the TSV export does.** Definition, context, part of speech and URL have always been dropped by the TSV export; the two new formats have proper homes for them, so they are kept.
- **Supervertaler still cannot write a `.sdltb` or `.ttb` directly, and the dialog says so.** Those are a Microsoft Access database and an undocumented SQLite schema respectively; writing either means guessing at a format that is not published, and a termbase Studio half-accepts would be worse than one it refuses. One conversion step outside the plugin is the honest trade.
- **What a round trip does not preserve:** MultiTerm entries are concept-oriented and can hold many languages, while a Supervertaler termbase is bilingual rows. Going out and back gives you your terms, not your original structure.

### Added (the AI can copy a project termbase into Supervertaler in one step) — [#59](https://github.com/Supervertaler/Supervertaler-for-Trados/issues/59)

- **New `import_project_termbase` MCP tool.** Ask your AI assistant to copy the Trados termbase attached to the open project into a Supervertaler termbase and it now happens in a single step — the same operation as the *Import .sdltb/.ttb…* button, which an assistant previously could not reach at all. Its only option was adding terms one at a time, which for a few hundred terms is not a realistic offer.
- **It asks first, and it is safe to repeat.** A dry run reports what would be imported — how many entries, which language pair, how each Trados field will be mapped — before anything is written. Running it twice adds nothing the second time: every entry is checked against what is already there.
- **It respects the Write column.** An existing termbase must be Write-enabled, exactly as when the assistant adds a single term. A name that does not exist yet is created for you, and is deliberately left *not* Write-enabled, so the assistant cannot then add to it without your say-so.
- **Your Trados termbase is never touched** — it is read through a temporary snapshot, which also means a `.ttb` currently open in Studio is read correctly rather than half-read.

### Changed (Termbases tab wording)

- **The *Import .sdltb/.ttb…* button now has a tooltip**, because nothing said what it imported *into*. It also states that the Trados file is only ever read.
- The Export and Import tooltips said "CSV file" while both dialogs have always used tab-separated `.tsv`.

## [18.20.174 / 19.20.174] – 2026-08-11

### Fixed (nothing told you when your termbases were switched off for the AI) — [#58](https://github.com/Supervertaler/Supervertaler-for-Trados/issues/58)

- **A termbase has two separate ticks — Read and AI — and only the Read one was ever checked.** With Read on and AI off, TermLens shows term matches on screen exactly as usual while every prompt goes out with an empty glossary. Nothing anywhere said so, and a prompt carrying no glossary looks identical to a model that ignored one, so this could run for months unnoticed. Found on the developer's own machine, on a live job with a 221-term glossary attached specifically for it.
- **This is the out-of-the-box state, not a setting anyone chose.** Termbases are not sent to the AI by default, and the change that introduced that default added every termbase that already existed to the "off" list. So unless you have been into the AI column since, the answer is probably that none of yours is enabled.
- **Three places now tell you.** A **Batch Translate or Batch Proofread** run says so in its log before it sends anything. **AutoPrompt** stops and asks, the same way it already warns when a termbase is too large for it. And an AI assistant connected over the MCP server is told when it asks about the project, so it can warn you rather than quietly producing untermed work.
- The wording distinguishes the two failures. "No termbase is read-enabled" and "read-enabled but none reaches the AI" need different fixes in different places, and being sent to check a tick that is already on helps nobody.
- **Where the tick is:** the **AI** column in the termbase grid on **Settings → Termbases**, which is also where the AI Settings tab has always pointed.
- **Also corrected: two older messages named a tab that does not exist.** The read-enabled warning and the `list_resources` note both said "Supervertaler settings > TermLens"; the tab is called **Termbases**. Nobody had reported it, but an instruction naming the wrong tab is worse than none.

## [18.20.173 / 19.20.173] – 2026-08-11

### Fixed (terminology was silently missing from AI prompts when the TermLens panel had not been opened)

- **If you never opened the TermLens panel in a session, every AI prompt went out with no terminology at all** — Batch Translate, Batch Proofread, AutoPrompt, QuickLauncher and the Assistant chat alike — and nothing said so. Studio only starts a panel when it is first shown, and the terminology the AI is given comes from that panel: no panel, no terms. Reported by a user who noticed it while chasing something else. The plugin now starts TermLens itself at Studio start, so it follows the document and loads your termbases whether or not the panel is ever on screen. (This is the same fix the TermPicker pane got in 20.139, applied to the AI side.)
- **If it ever happens again, the plugin log says so.** A prompt that quietly carries no terminology looks exactly like a prompt the AI ignored, which is what made this so hard to spot.

### Fixed (startup notices opened behind the Trados window, and arrived in a heap)

- **The survey, the update notice and one-off announcements could open *behind* Studio**, where — since none of them appear in the taskbar — there was no way to reach them until they resurfaced. They are now attached to the Studio window, which cannot cover a window it owns, and are brought to the front when they appear.
- **They no longer stack.** Each notice used to open independently, so two could land on top of each other — which is why a survey sometimes appeared alongside an update notice when there was no new version. They are now shown one at a time, in order.
- **A notice is no longer lost when the TermLens panel is closed.** Each one waited for that panel to exist and gave up after fifteen seconds if it did not, which would have suppressed every notice for exactly the users helped by the terminology fix above.

### Fixed (the survey's "Don't ask again" did not stick, and only silenced one question)

- **Ticking "Don't ask again" now means it.** The record was kept in the main settings file, which is written back whole from around 29 places, so anything holding a copy from startup would overwrite it on the next unrelated save. Two narrower fixes were tried first — reloading before writing in 20.147, merging on save in 20.169 — and users kept reporting the question coming back. It now lives in its own small file where nothing else can touch it, the same treatment announcements got in 20.169. Answers recorded under the old scheme are still honoured, so nothing you have already answered will be asked again.
- **"Don't ask again" now retires surveys altogether**, rather than just the question in front of you. The checkbox carries no qualifier, and a user who ticks it has not asked to be surveyed about something else next month.

### Fixed (SuperSearch · Alt+S put a target term in the source box, and kept the last search's terms)

- **Pressing Alt+S with a selection in the target now searches the target box.** It used to put the selected target text into the *source* box, where — a target term not being in the source — it reliably found nothing.
- **The other box is now cleared.** Source and target are combined, so a term left over from the previous search silently cut the new one to zero results. Alt+S starts a search rather than refining the last one. (Both reported by a user.)

## [18.20.172 / 19.20.172] – 2026-08-10

### Fixed (SuperMemory · the `_shared` bank was invisible to everything except the AI)

- **`_shared` reported "0 articles" while holding three files, could not be searched, and switching to it emptied your active bank.** All three came from one line: bank names are cleaned before being turned into a folder path, and that cleaning strips a leading underscore — which is exactly how `_shared` is kept un-createable from the New-bank dialog. Applied to a name read back off disk it turned `_shared` into `shared`, and the plugin then went looking for a bank that does not exist.
- **Nothing was ever lost, and nothing was withheld from the AI.** Prompt injection reaches the shared bank by a different route, so your house defaults have been in every prompt the whole time. What was broken was everything that *reported* on the bank — which is worse than it sounds: a bank that says it is empty is a bank you stop trusting, and `search_supermemory` answered "no matches" for rules you had written down and were being applied.
- **Search now covers the shared bank as well as the active one.** Every result says which bank it came from, results from the active bank win ties (it overrides the shared defaults, so it should be read first), and an empty answer now names the banks it actually searched instead of implying you never wrote it down.
- **`list_supermemory_banks` no longer presents `_shared` as an ordinary bank.** Each entry carries its role — a project bank, or the shared layer that is loaded on top of whichever bank is active — so an assistant can no longer read `active: false` as "this knowledge is not in play".
- **The memory-bank dropdown explains what `_shared` is.** It stays selectable, because selecting it is how you edit your house defaults, and the toolbar's Open folder button works on whichever bank is active.

### Fixed (`check_tags` reported a phantom tag on every segment you had commented)

- **A Trados comment was being counted as an inline tag, so every commented segment failed the tag check** as "source has 0 inline tag(s), target has 1" — pointing at a tag that is not in the source, not in the target, and not visible anywhere in the editor. Found on a 213-segment patent with 15 comments, which produced exactly 15 findings; the only clue that they were phantoms was that the two counts matched.
- **Why it happened.** A comment is markup wrapping the commented text, and Studio renders it as a coloured highlight rather than as a tag. The plugin's serialiser had no case for it and fell through to its catch-all, which turns any unrecognised wrapper into a paired tag.
- **The same phantom was being shown to the AI.** `get_segments` returned the comment as a `<t1>…</t1>` around target text the translator sees unmarked, which an assistant would then dutifully carry into its own translation. Comments are unaffected by the fix: they live only in the target and were already carried across a write separately.

## [18.20.169 / 19.20.169] – 2026-08-09

### Changed (SuperMemory · a memory bank is now three files you can actually read)

- **A memory bank is now `brief.md`, `terminology.md`, `style.md` and a `reference/` folder — and nothing else.** The seven numbered folders are gone, along with one-Markdown-article-per-fact and the YAML metadata on each. You are meant to open these files and edit them; the new **📂 Open folder** button in the toolbar exists for exactly that.
- **Why it changed.** A real bank reached 136 terminology files — for what is a 136-row table — behind a 97-file inbox backlog nobody had processed. Around 15% of articles had malformed metadata that silently excluded them from the very filtering the folders existed to enable: they were in the bank and they were not reaching the AI, and nothing said so. By that size no human could read the bank and tell. Knowledge you cannot audit is not knowledge you can rely on, and three files can be read start to finish in a few minutes.
- **Terminology is a table.** One row per decision, with a Scope column saying how far it travels (`project`, `client`, `domain`). A table is the format in which a *wrong* entry is findable — you can scan a hundred rows in half a minute and spot the one that says the wrong thing; you cannot do that with a hundred files.
- **New `_shared` bank, always loaded alongside the active one.** It holds the defaults that are true of your work rather than of any one client — house style, domain conventions, jurisdictional rules. **The active bank overrides it where they disagree**, and the AI is told which layer is which so it can apply the override rather than average the two. A rule earns its place in `_shared` once it has held for more than one client.
- **`reference/` is the audit trail.** Source material — client style guides, PDFs, glossaries, tracked-changes harvests — kept unmodified and never sent to the AI. Everything in the three files is derived from something, and keeping the original is what lets you check a rule that looks wrong.
- **Old banks are detected, not silently ignored.** A bank on the previous layout has none of the three files, so it would contribute nothing to a prompt without saying a word. When one is active an amber **⚠ Convert this bank** button appears; converting folds the old articles into the three files and moves the originals to `reference/_legacy`. Nothing is deleted. The conversion copies text across as-is rather than distilling it — deciding which of a hundred old decisions still hold is judgement, and a machine that guesses confidently there is how the old system filled up with material nobody could check.
- **Quick Add (Ctrl+Alt+M) appends a table row** instead of writing an article per term. Successive additions accumulate in one table rather than scattering. Raw notes go to `reference/`.
- **Process Inbox, Distill and Health Check are gone**, along with the inbox counter. They existed to manage complexity the new structure does not have, and on a converted bank they had nothing left to operate on.
- **Overview and Summary are replaced by ☰ Report**, which is computed from the files rather than from metadata that no longer exists: which of the three files are present and how big, how many rows the terminology table holds, **how many tokens the bank actually adds to a prompt**, whether `_shared` is being applied, and warnings for what now goes wrong — a missing brief, a terminology file still in prose, files in the bank root that are searchable but never sent to the AI. No AI call, so it is instant and free.
- Documentation for the whole section has been rewritten to match.

### Fixed (announcements and settings could lose what they had recorded)

- **A one-off notice could reappear after you had dismissed it** — sometimes, and not always, which made it look random. The dismissal was stored in the main settings file, and that file is written back whole from around 29 places; anything holding a copy loaded at startup (the AI Assistant keeps one for the session) would overwrite the flag on the next unrelated save, minutes later. Dismissals now live in their own small file where nothing else can clobber them.
- **The same flaw could have lost survey answers and the usage-statistics choice.** Saving now merges those append-only records with whatever is already on disk, so a stale copy can no longer erase them. They record things that *happened*, so a union is always the correct result.

## [18.20.163 / 19.20.163] – 2026-08-08

### Fixed (Supervertaler MCP Server · one slow request no longer blocks every other one)

- **The bridge served requests strictly one at a time, so a single slow call stalled everything queued behind it.** Measured on a large project: a request that answers in 0.4 seconds against an idle bridge took **84 seconds** when it happened to be issued behind two long-running ones. Because a client-side timeout does not cancel work already running inside Studio, an abandoned request kept the queue blocked — and retrying, the natural reaction, made it worse rather than better.
- The listener thread now only accepts connections and hands each request to a worker, so it is never blocked. Operations that touch the Trados editor still take their turn on the UI thread — that is required, the editor is not safe to drive from several threads — but everything that does not need the editor (termbase lookups, help, the tool list, `session_report`) now answers immediately instead of waiting behind them.
- **If you saw "the request was canceled due to the configured HttpClient.Timeout of 30 seconds elapsing", update the MCP server itself as well.** That message comes from the separately-installed MCP server, not the plugin, and its timeout was raised to 5 minutes in 20.148 — so seeing it means that component is older than the plugin. It does not update with Studio: reinstall the `.mcpb` in Claude Desktop (or re-unzip the exe for other clients) after a plugin update, then restart the client.
- **Anyone still on a pre-20.110 MCP server is now told so, once, in chat.** Those builds predate the timeout fix, so they report long saves and updates as failures that in fact succeeded — and an assistant that retries on a "failure" then writes twice. The plugin now recognises them and asks the assistant to relay a short explanation, including not to retry a write after a timeout without checking whether it landed. Nothing else is gated: the tool list is read from the plugin at runtime, so an older server still has the current tools and keeps working normally.

## [18.20.162 / 19.20.162] – 2026-08-08

### Fixed (Supervertaler MCP Server · find_and_replace refused every segment containing a tag)

- **`find_and_replace` skipped any segment carrying an inline tag, reporting it as a match that "straddles inline formatting/tags" even when it plainly did not.** On a tag-heavy document that is most of the file, which made the tool useless exactly where a consistency sweep is worth most. Confirmed live: the phrase *Overzicht aansluitingen*, sitting wholly inside a single `<t1>…</t1>` wrapper, was refused, while the same phrase in two untagged segments was replaced.
- **The safety check was comparing two different kinds of string.** It built the expected result by replacing across `Target.ToString()` — which renders the *markup* as well, `<cf size=8>` and the like — then compared that against a simulation built by replacing inside each text node, which has no markup in it. For any segment with a tag the two could never match, so the guard fired on every one of them.
- Both sides now start from the same basis: the segment's concatenated text nodes. The guard therefore tests what it was always meant to test — whether replacing across the whole text gives the same answer as replacing inside each node — so a match genuinely straddling a tag boundary is still refused, and one that merely sits near a tag is no longer punished for it. The `before`/`after` preview now shows the segment's text rather than raw internal markup.

## [18.20.161 / 19.20.161] – 2026-08-08

### Fixed (Supervertaler MCP Server · get_active_segment misrepresented every tag)

- **`get_active_segment` showed the current segment's target with all its inline tags stripped, and its source in raw internal markup.** A footer segment whose source and target both carry a page-number field was reported as source `Side <field name="Page" value="10"/>` and target `Pagina ` – so the target looked like it had lost the field. It had not: `get_segments` correctly reports the same segment as `Side <t1/>` → `Pagina <t1/>`. The same applied to every entry in `surroundingSegments`.
- **This invited exactly the wrong repair.** An AI reading the active segment or its neighbours would conclude that formatting had been dropped and "fix" segments that were never broken — writing real tag damage into a clean document. The raw source form (`<group name="Group 258">`, `<cf size=8>`) is also not a marker `update_segments` accepts, so copying it would fail.
- Both fields now go through the same serializer `get_segments` uses, so every tool describes a segment the same way and a marker copied from one tool is valid in another. The AI chat and prompt-context path deliberately keeps its previous plain-text rendering — an LLM writing prose is not helped by tag noise — so only the MCP surface changed.

## [18.20.160 / 19.20.160] – 2026-08-08

### Fixed (Supervertaler MCP Server · update_segments destroyed a segment's comments)

- **Writing a segment through `update_segments` deleted every Trados comment on it, silently, and reported success.** The write path clears the target and rebuilds it from the *source*'s tags — and a comment lives only in the target's markup, so it went with the clear and nothing put it back. Verified on a real job: the comment was gone from the saved SDLXLIFF, with `ok: true` and no warning.
- **This turned the normal delivery workflow into a shredder.** Read the comments, act on what they say, fix the segments they refer to — and each fix deletes the comment that prompted it, one segment at a time. Following the documented rule guaranteed the loss: tag markers are to be copied from the segment's *source* field, and the comment anchor exists only on the target side, so a correctly-behaving AI dropped it every time. Nothing surfaced this until someone re-parsed the whole document from disk.
- Comment markers are now captured before the rewrite and restored around the new text, on both the tagged and the plain-text write paths. Re-anchoring is deliberately coarse — a comment that covered part of a segment now covers all of it — because the span it pointed into no longer exists after a rewrite, and a comment attached to slightly too much text is far better than one silently deleted.
- If a comment cannot be preserved after all, the segment's result now carries a `warning` saying so, on the same channel as the tag-id audit. A destructive silent success is the worst of the available behaviours; an announced failure is recoverable.

### Fixed (Supervertaler MCP Server · two limits that hid real findings)

- **`find_inconsistencies` could not reach past its first 200 groups.** The cap was applied with no way to page, so on a document with 375 inconsistent groups the remaining 175 were unreachable at any `limit` — and on the job where this surfaced, those later groups were the cross-file terminology drift, i.e. the part worth finding. There is now an `offset` parameter, the cap is 500, and when the result is truncated the note gives the exact offset to pass for the next page.
- **`add_term` no longer strips trailing punctuation from terms.** `Rev.` → `Rev.` was being stored as `Rev` → `Rev`, an entry that no longer records the decision it was created for; `NOTE:`, `Doc.nr.`, `PO-nr.` and `SAFETY INFORMATION!` lost their final character the same way. Abbreviation-with-period is a legitimate term form. Stripping still happens for the in-Studio quick-add, where the term is captured from a selection in running text and a final `.` really is sentence punctuation — but a term named deliberately through the MCP server is now stored exactly as sent. Lookup is unaffected: the search trims the stored term as well as the query, so `Rev.` still matches a search for `Rev`.

## [18.20.159 / 19.20.159] – 2026-08-07

### Fixed (TermLens · selection tracking survives brackets)

- **Selecting source text that includes brackets no longer breaks TermLens's selection tracking.** The panel renders "(101, 201)" as "101, 201" – its tokenizer deliberately drops brackets (so "verkoper(s)" can match the term "verkoper") – but the yellow follow-the-selection highlight matched the editor's selection *verbatim* against that rendering, so one selected "(" made the whole match fail and the highlight silently vanish. Typical patent selections ("waarbij het omsluitend deel (101, 201)") hit this constantly. The selection is now reduced to the same character set the panel renders before matching, so brackets, quotes and other dropped punctuation in the selection can no longer kill the anchor.

### Added (Supervertaler MCP Server · termbase editing, stale-termbase warning, project vs background scoping)

- **`update_term` can now edit `newNotes`, `newDefinition`, `newDomain`** alongside `newSource`/`newTarget` – only the fields you pass change, everything else (including flags) is preserved, and the response lists exactly what changed. Notes carry most of a termbase's actual terminological knowledge (usage warnings, spelling variants, context); previously changing one meant delete+re-add, which lost the entry's id and needed two orientation-sensitive calls.
- **`update_comment` can now change `severity` independently of `text`** – provide either, both, or neither is now a clear error instead of a forced text rewrite.
- **`add_term` warns when a Write-enabled *project* termbase's name doesn't match the open project** – a one-line `note` in the response, so a termbase left Write-ticked from a previous job doesn't silently receive new terms without the user noticing.
- **New `scope: "project" | "background" | "both"` parameter on `add_term`**, resolved from each Write-enabled termbase's Trados "Project" flag (visible via `list_resources`). Use `scope: "project"` to keep a job-specific decision out of a shared background termbase (e.g. a large personal glossary) without having to name it explicitly; `termbases` still wins if both are given. Every per-termbase result now echoes its resolved `role` ("project"/"background").
- **`add_term`'s `duplicate` results now echo the matched existing entry** (id, source, target) instead of a bare "already exists" – and `lookup_term` hits now report `isProjectTermbase` so project-termbase matches can be weighted above background ones, mirroring TermLens's pink-vs-blue chip distinction.

### Fixed (Supervertaler MCP Server · lookup_term and add_term could disagree about whether an entry exists)

- **A term could be refused as a "duplicate" on write while `lookup_term` insisted the termbase held nothing for it.** Both statements looked authoritative and only one could be true, which left the only recourse a manual look in the termbase view. The cause was a normalisation gap: `lookup_term`'s exact-match stage compared the *query* trimmed against the *stored* term untrimmed, while `add_term`'s duplicate check trimmed both sides – so an entry carrying incidental leading or trailing whitespace was findable by one path and invisible to the other. Both now trim consistently. Paired with the duplicate echo above, a `duplicate` result can be verified instead of taken on trust.

## [18.20.158 / 19.20.158] – 2026-08-06

### Added (Supervertaler MCP Server · get_tracked_changes – your corrections become reusable knowledge)

- **New tool `get_tracked_changes` extracts the document's tracked changes as (before, after) pairs per segment** – the target as it was offered (an AI draft, a fuzzy match) versus the reviewed final. When you translate with the batch translator or the MCP server and then review with Track Changes on, every tracked change is a record of how you correct machine output for this client. That record used to evaporate when the project shipped; now it can be harvested.
- **Pass `save=true` to write the full harvest into the active SuperMemory bank's `00_INBOX`** as a Markdown note (project, language pair, and per segment: source, before, after, who edited and when). One harvest file per project, stored in the client's bank – so the next project for that client can draw on it, and Process Inbox can distill it into style rules (`04_STYLE`) and terminology (`02_TERMINOLOGY`). Check the right bank is active before saving; the response names the bank it wrote to.
- Target-side revisions only, and only segments whose text actually changed – formatting-only edits, reverted edits, and wholly inserted/deleted targets are skipped and counted in the response note. This is the first slice of a planned feedback loop: harvest now; distillation into style rules, prompt improvement, and "previously corrected" QA checks build on these files later.

## [18.20.157 / 19.20.157] – 2026-08-03

### Added (Supervertaler MCP Server · coverage tracking – "was it looked at?" becomes checkable)

- **Two new tools, `get_coverage` and `mark_reviewed`, track which segments have actually been read this session.** The worst QA miss on record – 84 defects delivered behind two clean QA passes – was not caused by any broken check. It was caused by nothing tracking which segments had been *looked at*: the AI fixed the defect categories it had found, re-ran the checks, saw green, and stopped, without ever reading about forty of the fuzzy-match segments. Every check can pass on a document nobody read.
- `mark_reviewed` records segments the AI compared source-against-target and deliberately left unchanged; segments it *writes* are recorded automatically. `get_coverage` then reports, per TM match band, how many segments are neither – with the riskiest band first, since a 95–99% fuzzy match reads fluent and plausible while differing from the source in exactly the words that matter. An honest delivery note can now say "23 segments in the fuzzy band were never reviewed" instead of implying completeness.
- Session-scoped by design: the record lives in the plugin's memory, is never written into your document, clears when Studio restarts, and cannot see edits you make by hand – it tracks the assistant's work only, and says so in every response.

### Changed (Supervertaler MCP Server · check_terminology is now worth running)

- **The terminology check was drowning in its own noise – 635 findings on a real job, of which roughly one was real – so in practice nobody ran it.** Worse, the one real finding was invisible: the termbase held both `valve → klep` and `safety valve → veiligheidsventiel`, and the check reported the generic entry on every occurrence while never surfacing the specific one – fifteen segments of a regulated pressure-vessel component name. Four changes:
- **Longest match wins.** When entries match overlapping words, only the longest is checked – `safety valve` now owns its words and bare `valve` no longer fires inside it.
- **Junk entries no longer fire.** Termbase entries shorter than three characters (`m`, `to`, `No`, `16`) matched ordinary prose far more often than terminology – 198 segments of noise from `to` alone – and are now skipped, as are grams with no letters.
- **Findings are ranked by signal, not by count.** Multi-word terms and project-termbase entries come first, because somebody curated those. A term affecting a very large share of segments now ranks *lower* – that pattern almost always means the project consistently uses a different translation than the termbase, which is a decision for you, not a defect for the AI.
- **A new `termbases` filter** restricts the check to named termbases, so a small curated client termbase can be checked without an 8,000-term general-domain one burying it. Inflected target forms (shared word stems) now count as found instead of being reported missing.

### Changed (Supervertaler MCP Server · checks that state their own limits)

- **`check_tags` now compares the underlying tag ids, not just the counts.** Two tags sharing one id – the corruption a stale fuzzy match leaves behind – has the right count, fails Studio's Tag Verifier, and was invisible to every count-based check. The finding names the ids and says how to repair the segment.
- **A clean `compare_document_to_tm` result now says what it does and does not prove.** It reported "all segments match the TM" in a tone that read as a clean bill of health – on a job where the TM itself was the source of the defects, so the agreement was the *symptom*. The note now states plainly that agreement with a contaminated memory is circular and must not be cited as evidence of quality.
- **`update_segments` now documents line breaks.** A literal `\n` in the target is a safe, ordinary JSON escape and writes correctly; a `<tN/>` placeholder should be sent as the tag. An AI had assumed the nearby non-breaking-space warning covered newlines too, and flagged two perfectly fixable segments as an unfixable server limitation.

### Fixed (Supervertaler MCP Server · update_segments could corrupt a segment's inline tags, permanently)

- **Writing a segment through `update_segments` could give its inline tags the wrong underlying ids, and rewriting the segment could not repair it.** Studio's Tag Verifier reported one tag pair removed and another added, plus a duplicated tag id alongside a missing one; in the editor two bold runs showed the *same* id where they should have shown two consecutive ones. Found on a real job, on segments of the shape "Set the **I/O** switch (11) to the **O** position" – two separate bold runs with ordinary text between them.
- **The cause was which side of the segment was treated as authoritative.** The write path resolved tag markers against a map combining source and target, in which the target won any numbering collision. That rule is correct where it came from – the bilingual re-import path, where the markers really do come from the target – but wrong here, because the markers come from the *source* field of `get_segments`, and, decisively, because Studio verifies the target's tag ids **against the source**. A tag cloned from the target could only pass verification by luck.
- **It was self-perpetuating, which is why a rewrite never healed it.** Once a segment had two tags carrying the same id, the next write re-read that same corrupt target and let it win again. The corrupt state was its own input. Deleting a tag by hand in Studio was the only way out, because that pulls a fresh tag from the source. The write path is now source-authoritative, so **re-sending an affected segment repairs it** – including segments damaged by the old behaviour.
- Segments with a single tag pair mostly escaped, because with one pair the target's tag was often the right one anyway. Two pairs made a mismatch nearly certain, which is why this surfaced on a manual full of two-bold-run sentences.

### Fixed (Supervertaler MCP Server · a repeated tag marker produced two tags with one id)

- **Sending the same `<tN>` marker twice cloned that tag twice**, producing two tags sharing one underlying id – a second, independent route to "Duplicated tag with id 'N'", and one that no tag *count* check could ever catch, because the count was right. A tag number can now be used only once per segment; a repeat drops the wrapper and keeps its text, which loses one formatting run instead of writing a tag Studio rejects.

### Added (Supervertaler MCP Server · update_segments now audits its own writes)

- **After each write, the tag ids actually in the target are compared against the source's, and any difference is reported in a new `warning` field on that segment's result.** Previously a corrupt write reported plain success and the damage surfaced only later in `run_verification` – which reads the last *saved* state, so it might not surface at all in that session. Silent success on a corrupt write is what let this ship in the first place.
- The comparison counts **how many times** each id appears rather than merely which ids are present, because the failure mode is precisely one id appearing twice while another goes missing – which a presence-only check would call clean.
- The audit never fails a write: the text is written either way, and the warning tells you the segment needs attention.

### Changed (Supervertaler MCP Server · clearer instructions to the AI)

- `update_segments` now tells the caller to copy tag markers from the segment's **source** field rather than its existing target, to use each marker at most once, and that a `warning` on a result means re-sending that segment with the source's markers will repair it.

### Added (Terminology · the AI fills in a term and its abbreviation for you)

- **A new "Add term with abbreviation" (Ctrl+Alt+A) reads the segment you are on, finds the term that carries an abbreviation, and opens the term entry dialog with all four fields already filled in.** The case it exists for is text like *"Deze verklaring wordt opgesteld conform de Sustainable Finance Disclosure Regulation (SFDR, Verordening (EU) 2019/2088)"*, where the term and its abbreviation are both sitting there on screen and were previously selected, then typed into the two Abbreviation fields by hand, every single time. Now the dialog opens with the term pair and both abbreviations in place, and you check them and press Add.
- **It only ever handles terms that actually carry an abbreviation**, deliberately. An abbreviation is a reliable signal about which words matter – somebody thought the term important enough to abbreviate. Without one there is no such signal, and asking an AI which term in a segment is "interesting" produces a confident guess that is often not the term you had in mind. Alt+↓ and Ctrl+Alt+T already add exactly what you selected, so a guess could only be worse. When there is no abbreviation in the segment, this simply falls back to the ordinary dialog with your selection in it.
- **Your selection decides which term**, when you have one. Select any part of a term – or a whole phrase containing it – and that is the term you get, completed to its full extent. The AI settles where the term starts and ends and hunts down the abbreviation; it does not get to substitute a different term it finds more interesting.
- **Nothing is written to a termbase without you.** The dialog opens, you confirm, you press Add – exactly as with Ctrl+Alt+T. Saving straight to every Write termbase would have been faster and is precisely the shape of the mishap behind 20.153.
- **The AI cannot invent an abbreviation.** It is only permitted to copy text that already appears in the segment, and every string it returns is then checked against the segment before it reaches the dialog – anything that is not genuinely there is discarded. This matters more than it sounds: source and target abbreviations are frequently identical (SFDR, radar, PAI), so a plausible invention is not something you could reliably catch by eye while confirming a pre-filled dialog.
- Uses the provider and model from your AI settings, and works with any of them.

### Changed (Voice · the toggle moves to Ctrl+Alt+D)

- **"Toggle voice commands" is now Ctrl+Alt+D instead of Ctrl+Alt+V.** Supervertaler Workbench uses Ctrl+Alt+V for its own voice-command push-to-talk, and registers it as an OS-level **global** hotkey – meaning it fires whichever application is in front, Trados included. If you run both, and many people do, one press started two listeners. Worse than that sounds: Workbench's is a *hold* and this one is a *toggle*, so letting the key go stopped only Workbench's, leaving this one latched on with nothing visible having switched it on. The Trados side moved because it is the newer binding, with far fewer fingers trained on it.

### Fixed (Terminology · abbreviation variants leaked their pipe into AI prompts)

- **An abbreviation field holding several spellings – `PCPs|PCP's` – passed the pipe straight to the AI.** The glossary in every translation and proofreading prompt was telling the model that the target abbreviation literally *was* `PCP's|PCPs`, pipe included, which a model following its glossary closely could reproduce in your translation. Prompts now list each source spelling separately, so all of them are recognised, and name a single target form, so one canonical abbreviation comes back. The same raw text also reached the TermLens hover popup, which now shows the primary form.
- **Variants are trimmed**, so `PCPs | PCP's` written with spaces means the two spellings you intended rather than one that could never match anything.
- **The Abbreviation fields now tell you the convention exists.** They show a greyed `PCPs|PCP's` hint, and hovering explains that every *source* spelling is matched wherever it appears, while the *first* target spelling is the one inserted into your translation. The feature has been there for a long time and was documented, but nothing in the dialog itself ever said so.

### Fixed (About dialog · the Privacy Policy link had fallen off the bottom)

- **The About dialog's height was a fixed number that nobody recalculated as its contents grew.** Every keyboard shortcut and link added to it over the years pushed the last entry nearer the bottom edge, and adding Ctrl+Alt+A this release finally pushed **Privacy Policy** off the dialog entirely. The height is now worked out from where the content actually ends, so adding another line cannot do this again.

## [18.20.156 / 19.20.156] – 2026-08-02

### Changed (Updates · "Not Now" now quietens updates for a week)

- **The update dialog's "Skip This Version" button is now "Not Now", and it silences update prompts for seven days rather than for one version.** Skipping a version only ever silenced the exact build named in the dialog, so the next release asked again — fine at one release a week, but Supervertaler is moving to submitting each meaningful fix to the App Store as it lands, which would have turned a per-version skip into a near-daily dialog you could never quiet. A time window decouples the prompt from the release rate: the more often updates ship, the *less* often you are interrupted. Settings that already recorded a skipped version keep working.

### Fixed (Updates · builds from before the July renumbering were never offered an update)

- **If you are still on a version starting with "4", the plugin has been telling you that you are up to date when you are not.** On 2 July the numbering changed so that the major version identifies the Trados generation (18.x = Studio 2024, 19.x = Studio 2026); before that a single 4.x sequence covered both. The update check only considers versions whose major matches the running build, so once the App Store stopped listing 4.x there was nothing for those installs to match — and they were quietly told there was no update, every time, indefinitely. Legacy builds are now matched against the generation of the Studio actually running, so they are offered the current version. (Found while helping a user who had installed in June, never seen an update notice, and was consequently missing months of fixes.)

### Changed (Distribution · the App Store is now the only channel)

- **The plugin is published through the [RWS App Store](https://appstore.rws.com/plugin/432) only.** GitHub releases keep the changelog, the tags and the MCP server files, but no longer carry the plugin. Three reasons, all of them things users actually hit: App Store builds are RWS-signed, so Studio stops asking you to confirm an "unsigned plug-in" at **every** start (a warning that is not an error, but reads like one); the plugin's update check reads the App Store catalogue, so anyone who installed from GitHub was running a build newer than the catalogue and was therefore never told about updates again; and an archive of old builds is a liability, since builds predating the trial anchor could be used to restart the trial indefinitely. All historical plugin downloads have been removed from GitHub.
- If you need a fix before it clears App Store review, email support@supervertaler.com and the build can be sent directly.

## [18.20.155 / 19.20.155] – 2026-08-01

### Added (SuperSearch · your termbases are now searchable too)

- **SuperSearch now searches terminology alongside files and TMs.** "Where does this phrase appear?" and "what have I called this term?" are the same question at different granularities, and answering them in two different panels meant searching twice. All three kinds of termbase are covered: your **Supervertaler** termbases, the project's **MultiTerm** (`.sdltb`) termbases, and Trados 2026's **`.ttb`** termbases – through the same reader the rest of the plugin uses, so nothing new has to be configured.
- **The scope dropdown is now one entry per source**: **Everything** · **Project files** · **TMs** · **Termbases**. "Everything" replaces the old "Files + TMs" and now includes terminology; "TMs" is the old "TMs only". A scope you had already chosen is carried over, not reset.
- Search options behave identically in every scope – case sensitivity, regular expressions and whole-word matching all run through the same matcher the file and TM searches use, as does the source + target box combination.
- **Terminology comes from the index TermLens already holds in memory**, rather than a fresh read of the database — so a termbase search is effectively instant instead of taking tens of seconds on a large termbase collection, with no second copy of your terminology in memory. The database is read only as a fallback, when TermLens has not finished its initial load yet.
- **Only the termbases you have switched on are searched** – Supervertaler termbases with their **Read** tick set, and MultiTerm/`.ttb` termbases enabled in Trados Project Settings. The Read column is your statement of which terminology is in play for a job; searching the rest would contradict it and make every search pay for termbases you deliberately turned off.
- Termbases are discovered when the project opens, so the **TBs** button — beside **Files** and **TMs** — is populated before your first search. It works like the other two: click it to include or exclude individual termbases.
- Termbase hits show the **termbase name in green** (echoing TermLens's MultiTerm chips) and the termbase **kind** in the Status column – `Supervertaler`, `MultiTerm` or `TTB`. Navigate and Replace don't apply to a term (it isn't a document location) and say so.
- The results grid's first column is now headed **Found in** rather than *File/TM*, since it can hold a file, a TM or a termbase name.
- Search terms are matched **in the project's direction**: a termbase declared the other way round (an EN→NL termbase in an NL→EN project) is oriented before matching, so the **Src** box always means "the language you translate from" rather than "whichever column that termbase happens to call source" — the same treatment TermLens gives terminology.

### Fixed (SuperSearch · button labels clipped at some display scalings)

- Buttons in the SuperSearch bar now grow to fit their labels instead of using fixed widths, which had truncated **Stop** to "Sto" at some font/DPI combinations.

## [18.20.154 / 19.20.154] – 2026-08-01

### Fixed (Batch translate & proofread · locked segments are now left alone)

- **Batch translation sent locked segments to the AI.** Locked segments typically have empty targets, so the "empty segments" scope picked them up first: a batch run would jump the editor to locked content at the top of the file and pay to translate exactly the text someone locked so it would be left alone – instead of starting at the first genuinely open segment. Verified by a user against the batch backup TMX. Locked segments are now excluded from every batch-translate scope, from batch proofreading (worse there: a locked segment has a target, so the proofreader could *rewrite* protected content), and from the segment counters above the Translate button, so the numbers match what a run will actually process. (Reported by a user.)

### Added (Batch translate & chat · custom providers in the model menus)

- **Custom OpenAI-compatible profiles now appear in the provider menus.** The model selector at the bottom of Batch Translate (and the chat status bar) listed every built-in provider's models but silently omitted the user-defined custom endpoints, so anyone comparing institutional gateways had to open Settings for every switch. Both menus now end with a "Custom (OpenAI-compatible)" submenu listing each profile with its model, active profile ticked – switching is one click, exactly like the built-ins. (Requested by a user.)

## [18.20.153 / 19.20.153] – 2026-08-01

All of the below came out of one production incident: an AI adding a term pair through the MCP server wrote it reversed into two termbases of opposite directions, and the tools reported success throughout.

### Fixed (Supervertaler MCP Server · add_term wrote reversed entries and reported success)

- **The root cause was a contract gap, not broken direction logic.** The per-termbase orientation code was doing its job – but it rests on the assumption that `source` is the term in the *project's* source language, and nothing said so or checked it. The AI passed the pair the other way round; the orientation logic then faithfully produced a wrong entry in *each* termbase, one "aligned", one "swapped", both reversed. No language detection can catch this in translation work – term pairs are routinely identical across languages (radar, transponder) – so the fix is to make orientation explicit rather than guessed.
- **`add_term` now takes `sourceLang`/`targetLang`.** When supplied, each termbase stores the pair according to its own declared direction – one call is correct for an en→nl and an nl→en termbase simultaneously. Without them, the project-direction assumption still applies but is now stated loudly in the tool contract, and a termbase whose languages cannot be related to the project's **refuses instead of writing silently**: no document open, or an unrelated language pair, is an error asking for explicit languages. A wrong silent write is far worse than a refusal.
- **The response now proves what happened.** Instead of a bare list of termbase names, every targeted termbase reports back: `added` (with *exactly* what was stored – both terms in stored order, the termbase's languages, and whether the pair was reoriented), `duplicate`, or an error with the reason. Success can be verified, not trusted.

### Added (Supervertaler MCP Server · add_term targeting and full fields)

- **`termbases` parameter** – restrict the write to named termbases (or numeric ids) instead of fanning out to every Write-enabled one. Fan-out was the direct reason one wrong call corrupted two termbases. The default remains all Write-enabled termbases, now itemised in the response; unknown or read-only names are reported per entry without blocking the rest. Duplicate detection was already per termbase and stays that way.
- **`definition`, `domain` and `notes`** can now be supplied – the storage always existed and `lookup_term` already returned the fields; they were simply unreachable from the MCP side, so all context had to be typed in by hand afterwards.

### Fixed (Supervertaler MCP Server · lookup_term was blind to half the database)

- **Exact lookup matched the source column only**, despite claiming "source or target". A query in the project's target language found nothing unless an entry stored that text in its source column – which is precisely what reversed entries do, so during the incident the tool surfaced *only* the corrupted entries and made them look normal, while hiding the correct ones. Worse, any exact hit suppressed the substring fallback (which does search both columns), so each query returned exactly one misleading termbase. Exact matching now covers source and target terms alike.
- **Hits now report their evidence.** Each hit carries `matchedField` ("source"/"target"/"both") plus the entry's stored language pair, and the contract states plainly that results are returned exactly as stored, never reoriented – making `lookup_term` usable for verifying what `add_term` wrote. During the incident that verification was structurally impossible, which is how a reversed write passed its own check.

## [18.20.152 / 19.20.152] – 2026-08-01

### Added (TermLens · target selections highlight their term chips)

- **Selecting text in the target segment now lights up the term chips whose translation the selection covers.** The counterpart of 20.151's source-selection tracking, with one deliberate difference: a source selection highlights a continuous run of words, while a target selection highlights only term chips. That is not a shortcut – there is no word-alignment data between source and target in the editor, so mapping arbitrary target text back onto source words would be guesswork, and on heavily reordered language pairs it would guess wrong often. What TermLens does know is every chip's translation, abbreviation and target synonyms, so those are matched against your selection instead: select *"transverse ship axis (roll) and/or longitudinal ship axis"* and the chips for *dwarsscheepsas* and *langsscheepsas* light up. Partial words work too (*"radiating elem"* finds *radiating elements*). Matching is textual: if your target wording departs from every rendering the termbase knows, that chip stays unlit. Whichever side you selected last drives the highlight, so the panel always reflects exactly one selection.

## [18.20.151 / 19.20.151] – 2026-08-01

### Added (TermLens · your editor selection is now mirrored in the panel)

- **Selecting text in the source segment now highlights the corresponding words in TermLens.** On a long segment – a patent claim running to a dozen lines – the panel shows the whole segment's terms, and finding the part you are actually reading meant scanning the entire flow. Now the words covered by your editor selection carry a soft yellow band, matched and unmatched alike, so your eye lands straight on the right region and the term chips around it. The highlight follows the selection live, clears when the selection does, and when the same phrase occurs more than once in a segment the occurrence nearest your cursor is the one that lights up. Selections spanning an inline tag simply show no highlight rather than a wrong one.

### Fixed (Add term entry dialog · generic title-bar icon)

- **The Add term entry dialog showed the generic WinForms icon instead of the Supervertaler logo.** The dialog is one class with three entry paths – add, edit and multi-termbase edit – and only the edit path set the icon. All three now share it.

## [18.20.150 / 19.20.150] – 2026-08-01

### Fixed (TermLens & TermPicker · Escape now dismisses both pop-ups)

- **Escape now closes the floating TermLens popup and the Alt+P TermPicker.** The popup opens on a Ctrl tap and closed on a second tap, but Escape – the key everyone tries first – did nothing; the TermPicker window equally ignored it. Two causes, one deep: the TermLens popup deliberately never takes keyboard focus (so your typing stays in the editor), and – measured, not assumed – Studio's input pipeline consumes dialog-navigation keys before they reach the places WinForms normally hands them to a plugin. Escape is therefore intercepted with a low-level keyboard hook inside Studio's own process, which dismisses whichever Supervertaler surface is open (TermLens popup, TermPicker window, or the docked TermPicker pane – which hands focus back to the editor) and swallows the keypress so Studio does not also act on it. When nothing of ours is open – or another application is in the foreground – Escape passes through completely untouched.
- Worth knowing if Escape still seems dead after updating: on the machine where this was diagnosed, a background application's global keyboard hook was swallowing Escape system-wide – it did nothing in Notepad or the Start menu either, and no application could see it. Screen-capture tools, clipboard managers, dictation software and macro tools all install such hooks. If Escape does nothing anywhere, the problem is outside Supervertaler; closing those tools one at a time will find the culprit.

## [18.20.149 / 19.20.149] – 2026-07-31

### Fixed (Translate active segment · single segments now get the same context as a batch)

- **Translating a single segment (Alt+T, or right-click → Translate active segment) produced noticeably weaker translations than Batch Translate – and the difference was real, not an impression.** Both use the same provider, model, prompt and termbase configuration, but the batch pipeline also hands the AI the document context (the surrounding source text, up to your configured limit) and your SuperMemory bank context. The single-segment path passed neither, so the model translated one isolated sentence with no register, no disambiguation and nothing to stay consistent with. Single-segment translation now sends the same context blocks a batch run does, honouring the same *Include document context* setting and the same 32-bit memory limits. If you translate segment by segment as a way into Supervertaler, the quality should now match what Batch Translate gives you. (Reported by a user.)

## [18.20.148 / 19.20.148] – 2026-07-30

All of the below came out of one real job: a 2,889-segment manual translated end to end through the MCP server. None of it was found in testing.

### Added (Supervertaler MCP Server · compare the whole document against your TM)

- **`compare_document_to_tm` reports every segment translated differently from what the TM already holds for the same source.** Concordance search answers "was this phrase translated before?" one query at a time, for phrases you already suspect; it cannot answer "across this file, where have I departed from the client's reference TM?", because that is a join over every segment rather than a lookup. On the job that prompted this, a term coined in good faith already had an established rendering in the client's own TM, and no amount of searching would have found it — you only look up what you already doubt. Runs against file-based `.sdltm` and GroupShare TMs alike, through the Trados API rather than by reading the file format directly.
- The comparison happens **inside the plugin**, so only the deviations travel to the assistant, never the TM. Sending a whole TM across for the model to diff would be enormously expensive and would fall apart on a large one — the reference TM in that report held 1,490 units and a master TM is far bigger. Ordinary spacing differences are ignored; non-breaking spaces are not, so a target that quietly lost one still shows up.
- Only finished segments are checked by default, and only sources that match the TM verbatim — so a clean result means nothing contradicts the TM, not that the whole document agrees with it. The response says so explicitly, and says that a difference is not automatically an error: a deliberate improvement is indistinguishable from a mistake here, so the assistant is told to present the list for review rather than align anything itself.

### Fixed (TermLens · terms with "(s)" were invisible, and Alt+Down mangled them)

- **A term written with the optional-plural convention – `verkoper(s)`, `party(ies)` – never matched anything in TermLens.** The tokeniser's character class read `%-/`, which looks like three literal characters but is a *range* covering U+0025 to U+002F, so it also matched `(`, `)`, `*`, `+` and `&`. `kandidaat-koper(s)` therefore tokenised as one single word, which no termbase entry could ever equal, and the panel simply reported no matches. Terms are now split at brackets as they always should have been, so an entry for `kandidaat-koper` highlights inside `kandidaat-koper(s)`. Percentages, `km/h`, `well-known`, `R&D`, `don't`, `C++`, `1.234,56`, `H₂O` and `m²` all tokenise exactly as before. (Reported by a user.)
- **Alt+Down on a selection ending in a bracket lost the closing bracket**, offering `kandidaat-verkoper(s` for the new entry — visibly wrong, and wrong in a way that was easy to save by accident. Balanced bracket groups now survive intact, leading or trailing, so `kandidaat-verkoper(s)` and `(her)certificering` are saved exactly as selected — the entry records what you chose, and TermLens indexes a bracket-stripped alias alongside it so the stored form still matches the `kandidaat-verkoper` token the tokeniser produces. Both spellings resolve to the same entry, and an alias merges with an existing base-form entry rather than being shadowed by it. Stray unbalanced edge punctuation (`koper)`, `verkoper,`) is still trimmed. (Reported by a user, twice — the first shipped fix reduced selections to the base term, which the same user demonstrated was the wrong call.)
- **Existing entries are not repaired automatically.** An entry saved under the old behaviour may still carry an unbalanced bracket (`… at Work (Cpbw`, `re)certification`) — mechanical repair is possible but some cases need a human decision about what was intended, so nothing in your termbase is rewritten behind your back. Ask the AI to list entries with unbalanced brackets if you want to review yours.

### Fixed (Supervertaler MCP Server · find & replace quietly unconfirmed finished work)

- **A single find & replace demoted every segment it touched to Draft**, with no way to opt out. Editing a segment's content makes Studio reset its confirmation status – correct while you are still translating, wrong when you are running a consistency sweep over a file that is already finished. On a fully translated document the replacement worked and the file silently became unfinished; you only noticed if you thought to re-check the statuses afterwards. Each changed segment now keeps the status it had. The AI can still ask for a specific status where that is what you want, and the response reports which of the two happened. (Reported by a user.)

### Added (Supervertaler MCP Server · non-breaking spaces you can actually see)

- **A new `check_nbsp` QA check** lists translated segments that came out with fewer non-breaking spaces than their source. Non-breaking spaces are invisible in Studio, in the AI's view of your segments and in every report, so a lost one normally surfaces only when the client rejects the file – which matters if your style guide wants one between a value and its unit (230 V, 3,5 mm, 50 %) or before a figure reference.
- **The AI can now write one, as `&nbsp;`.** A non-breaking space placed directly into a tool call reaches Trados only *sometimes*: depending on the AI client and the individual call it either arrives intact or turns into an ordinary space along the way, and the write reports success either way – so nothing distinguishes the two. Escape codes are no safer, because the client decodes them into the character first, and the character is what gets flattened. Intermittent is worse than broken here: it survives testing and then fails on the job. `update_segments` and `find_and_replace` therefore take a `decodeEntities` option, which lets the AI write the HTML entity `&nbsp;` (or any `&#NNN;` code point) and have Supervertaler convert it at the Trados end; plain ASCII travels intact, so nothing en route can mangle it. It covers both sides of find & replace, so *"put a non-breaking space between every value and its unit"* fixes a whole document in one pass – and because find & replace now preserves confirmation status, a finished file stays finished. Opt-in by design, so a document that genuinely contains the text `&nbsp;` is never silently rewritten. Supervertaler itself was never the culprit: it stores and returns the character faithfully, which is exactly why the loss was so hard to spot.

### Fixed (Supervertaler MCP Server · verification results that looked current but weren't)

- **`run_verification` reads the last *saved* state of your files, and its findings gave no sign of it.** That is documented behaviour, but the response came back as a full, confident findings list, so edits the AI had just applied were invisible to it – in one case reporting 17 segments as still untranslated when they had all been translated moments earlier. The response now carries an explicit `stale` flag whenever there are unsaved AI edits, and tells the AI to save and re-run rather than report anything. Nothing is saved automatically: that stays your decision.

### Fixed (Supervertaler MCP Server · large write batches could lose their confirmation)

- **Batches above roughly 45 segment updates outran the connection timeout.** The write itself went through, but the confirmation never came back, leaving the AI unable to tell success from failure – and a retry would apply the same edit twice. The per-call limit drops from 200 to 40, and the timeout on the MCP server side is raised well beyond any legitimate call, so both ends of the problem are closed.

### Added (Supervertaler MCP Server · a warning when no termbase is switched on)

- **`get_active_project` now warns when the open project has no read-enabled termbase.** Termbases are activated per project, so a project with all of them switched off is indistinguishable over MCP from one with no terminology attached: lookups simply return nothing, and nothing says why. A whole job was translated that way before anyone noticed. `list_resources` carries the same warning alongside its `readEnabled` flags.

## [18.20.147 / 19.20.147] – 2026-07-29

### Fixed (Clipboard Mode · multi-paragraph translations were cut short)

- **Pasting back a segment whose translation runs to more than one paragraph kept only the first one**, silently discarding the rest. If your source segment contains blank lines – a fault description followed by the steps to fix it, say – everything after the first blank line was lost on re-import. The response parser treated a blank line as the end of the translation; it now keeps reading to the end of the segment block, which was already marked by the next `Segment N` header. (Reported by a user.)
- **The same parser also cut a translation short at any paragraph beginning with a word and a colon** – `Note: …` in English, or `注意：…` in Chinese and Japanese. This was never reported separately, but anyone translating into CJK was especially likely to hit it. Both stopping rules are gone; the only thing that now ends a translation is the source-language label, for the models that put the pair the other way round.

### Added (Supervertaler MCP Server · your memory bank, from any AI client)

- **Three read-only tools put SuperMemory on the MCP server**, so Claude Desktop – or any MCP client – can consult your memory bank while you translate, whatever CAT tool you have open. `get_supermemory_context` loads the bank for the current project and cites the articles it drew from; `search_supermemory` searches the active bank by keyword; `list_supermemory_banks` shows which banks exist and which is active. Nothing is written back.
- **The tools reach existing installs without reinstalling the MCP extension** – the exe reads its tool list from the plugin, so a plugin update is enough.

### Fixed (SuperMemory · unverified notes no longer overrule the AI)

- **A note you had flagged as low-confidence, or that was never finished, carried the same authority as a verified one.** The prompt told the AI that knowledge-base decisions take priority, full stop – so a half-written Quick Add note could override a model that had it right. Low-confidence, draft and stub articles are now marked *unverified* and explicitly presented as a hint rather than an instruction, with the AI told to prefer its own judgement where the two disagree. Notes with no confidence set are unchanged.

### Fixed (Dialogs · long text no longer clipped)

- **The in-app survey cut off longer questions mid-sentence**, and could overlap its own controls at some display-scaling settings. Both dialogs now size themselves to their text instead of using fixed positions, so they render correctly whatever your resolution, DPI scaling or system font size.
- **A one-off notice could reappear on every launch.** Several startup tasks each saved the whole settings file, so whichever finished last silently discarded what the others had written. They now re-read immediately before saving.

## [18.20.146 / 19.20.146] – 2026-07-28

### Fixed (AI Assistant · GPT-5.6 failed instantly in chat)

- **Any GPT-5.6 model (Sol, Terra, Luna) returned an immediate error in the Supervertaler Assistant chat**: *"Function tools with reasoning_effort are not supported for gpt-5.6-sol in /v1/chat/completions"*. The chat gives the model tools so it can look things up in your project (projects, statistics, TMs, termbases) – and OpenAI does not allow that combination with reasoning on this endpoint, applying a reasoning setting of its own that the request never asked for. The chat request now opts out of reasoning explicitly, so GPT-5.6 works there again.
- **This covered everything that runs through the Assistant chat** – your own messages, **AutoPrompt**, and QuickLauncher prompts sent to the Assistant – since they all submit through the same chat. **Batch Translate and Batch Proofread were never affected**: they send no tools, so GPT-5.6 keeps its full reasoning exactly where it matters most for translation quality. GPT-5.5 and earlier are unchanged throughout. (Reported by a user.)

## [18.20.145 / 19.20.145] – 2026-07-27

### Added (Supervertaler MCP Server · the AI can remove Trados comments too)

- **New `delete_comment` tool** rounds out comment handling (read, add, edit – and now remove). It's addressed exactly like `update_comment`: call `get_comments`, then pass the segment id and the comment's index, or `all=true` to clear every comment on a segment. It removes the **whole** comment, version history included – Studio's per-version *Delete version* surgery stays in the editor, where it belongs. Like the other destructive tools, the AI is told to act only on your clear request or confirmation and to say which comment it removed; a comment marker left empty is unwrapped so no dangling annotation remains on the segment, and the change is part of the document's unsaved edits until you (or `save_document`) save.

## [18.20.144 / 19.20.144] – 2026-07-27

### Added (AI models · the GPT-5.6 family)

- **GPT-5.6 Sol, Terra and Luna are now selectable** (Settings → AI Settings). OpenAI released this three-tier family on 9 July 2026, all with a ~1M-token context window:
  - **Sol** – the flagship, for complex translation and AutoPrompt. **$5/$30 per million tokens: the same price as GPT-5.5, which it supersedes**, so there is no reason to stay on 5.5 for quality work.
  - **Terra** – GPT-5.5-class quality at **$2.50/$15**, half the price. A strong everyday default.
  - **Luna** – **$1/$6**, for high-volume batch work.
- Pricing for all three is in the shared pricing list, so cost estimates and usage reports handle them out of the box. GPT-5.5 remains available and its prices stay listed, so existing projects and past usage logs still resolve.

## [18.20.143 / 19.20.143] – 2026-07-27

### Fixed (AI · the timeout fix now covers every GPT-5.x route)

- **GPT-5.5 via OpenRouter** had the same problem as the direct OpenAI route and is now recognised as a reasoning model too.
- **Any GPT-5.x model ID** – including one you type in yourself as a custom model, and the GPT-5.6 family – now gets the long timeout automatically, instead of only the older o-series being recognised.

## [18.20.142 / 19.20.142] – 2026-07-27

### Fixed (AI · AutoPrompt timed out on GPT-5.5 and other slow OpenAI models)

- **AutoPrompt failed with "The request timed out." on GPT-5.5** (reported by a user; GPT-5.4 Mini worked fine on the same job). AutoPrompt asks the model for a large amount of output, and the OpenAI request paths allowed a flat two minutes for it regardless of how much was requested – where the Claude paths have always allowed ten minutes for large generations. All OpenAI paths now scale the same way, based on the size of the request rather than on a list of known-slow models, so this keeps working for models released after this build.
- **GPT-5.5 is now recognised as a reasoning model**, so every request to it gets the longer timeout, not just large ones.
- **AI request timeouts are now recorded in the diagnostic log**, and the error message suggests trying a faster model or sending less context. Previously a timeout left no trace in the log at all, which made it impossible to diagnose from a bug report.

## [18.20.141 / 19.20.141] – 2026-07-27

### Fixed (Batch Operations · Proofread prompt put verdicts on the wrong segments) — [#50](https://github.com/Supervertaler/Supervertaler-for-Trados/issues/50)

- **The clipboard Proofread prompt numbered its review list from 1 instead of using the real segment numbers**, so as soon as any segment was skipped – a tag-only segment, for instance – every number after it was wrong, and the AI's verdicts were reported against the wrong segments. Nothing looked amiss: the output was well-formed and only a manual comparison revealed the drift. Found on a real 949-segment job where three tag-only segments pushed 826 of 946 verdicts three segments out of place. The batch now uses the same `[SEGMENT NNNN]` document numbers as the document-context block (and as the API path, which was never affected), and states that the numbers are deliberately non-contiguous.
- **The prompt also specified its output format twice, in two different ways** (`[SEGMENT 0002] ISSUE` with `Issue:`/`Evidence:`/`Suggestion:` versus `Segment 2: ISSUE` with `Problem:`/`Suggestion:`). A model following the second one dropped the evidence citations the first one asks for. The format is now defined once.

## [18.20.140 / 19.20.140] – 2026-07-27

### Fixed (TermPicker)

- **Escape now closes the term-details window** – in the docked pane and in the Alt+P popup alike. (Windows treats Escape as a dialog key, so it never reached the list; it is handled a level up now.) In the popup, a second Escape closes the picker itself. The details window also closes when you move to another row, so it can no longer describe the previous term.
- **The top row no longer flashes when you press Alt+P.** The list was hiding its selection while the editor had focus and redrawing it on arrival; the selected row now stays visibly selected (grey when unfocused, blue when focused). The list is also double-buffered, so rebuilding it on each segment change doesn't flicker.

## [18.20.139 / 19.20.139] – 2026-07-27

### Fixed (TermPicker pane)

- **The pane no longer starts empty.** If you kept TermPicker visible with the TermLens panel collapsed to a tab, the pane stayed blank until you clicked that tab: Studio only starts a panel when it is first shown, so TermLens wasn't yet following the document that the picker takes its matches from. The pane now starts TermLens itself, so it is populated the moment you open it.
- **You can now see which terms have details.** Rows whose term carries a definition, domain, notes or a URL are marked with an amber dot – the same signal the TermLens chips give – so it's clear when pressing `I` will show you something.
- **Escape closes the details popup** (previously it stayed on screen). In the Alt+P popup, a second Escape then closes the picker itself.
- **The right-click menu is back**: Edit Term, Mark as Non-Translatable and Delete Term, matching the TermLens chips. It acts on the row you right-click, and is disabled for MultiTerm entries, which are read-only.

## [18.20.138 / 19.20.138] – 2026-07-27

### Added (TermPicker · press I for term details)

- **Pressing `I` on a row in TermPicker shows the term's metadata** – the same popup, with the same content, as hovering a TermLens chip: forbidden / MultiTerm / non-translatable tags, and for every entry its synonyms, definition, domain, notes and URL. Press `I` again to dismiss it. Works in both the Alt+P popup and the dockable pane, and matches the `I` key that the TermLens popup has always had. TermPicker's keyboard set is now: arrows to navigate, ←/→ to collapse/expand synonyms, a term number to jump, **I** for details, **E** to edit, Enter to insert (and Esc to close the popup).

## [18.20.137 / 19.20.137] – 2026-07-27

### Changed (TermPicker pane · polish from first use)

- **The pane now opens pinned**, i.e. permanently visible. Previously it arrived auto-hidden, sliding in and straight back out again, which looked like a glitch. Studio still remembers wherever you drag it afterwards.
- **Alt+P now moves focus into the pane when it is open**, instead of covering it with the popup: from there arrows navigate, ←/→ collapse/expand synonyms, a term number jumps to it, Enter inserts. With no pane in your layout, Alt+P opens the popup exactly as before.
- **Escape closes the TermPicker popup** (the list was swallowing the key).
- **Pressing E on a row opens the term editor**, matching the TermLens popup's key – in both the pane and the popup. MultiTerm entries are skipped, as those termbases are read-only.

## [18.20.136 / 19.20.136] – 2026-07-27

### Added (TermPicker · now available as a dockable pane)

- **TermPicker can now be docked like TermLens**, for anyone who prefers a flat, sortable list as their permanent terminology display rather than TermLens's in-context chips. Open it from Studio's **View** tab (it is not pinned by default, so your existing layout doesn't change when you update). The pane updates on every segment change, in step with the TermLens panel, and inserting from it behaves exactly like the popup and the chips – same capitalisation adaptation, same keyboard grammar (arrows to navigate, Right/Left to expand/collapse synonyms, a term number to jump, Enter to insert).
- Both terminology views are now available in both placements: TermLens as a docked panel or at the cursor (tap **Ctrl**), TermPicker as a docked pane or at the cursor (**Alt+P**) – so you can choose the representation and the placement independently. **Alt+P still opens the popup** even when the pane is visible, mirroring how Ctrl-tap works alongside the docked TermLens panel.

## [18.20.135 / 19.20.135] – 2026-07-27

### Changed (TermPicker · new shortcut, synonyms shown up front)

- **TermPicker now opens with Alt+P** (was Ctrl+Shift+P). Ctrl+Shift+P is also Trados Studio's own *View Target*, so it appeared twice in Studio's keyboard-shortcut list and looked like a conflict. Alt+P is free. Note that Studio keeps your existing binding across plugin updates – if you had it on Ctrl+Shift+P, clear that and set Alt+P under **File > Options > Keyboard Shortcuts > Supervertaler for Trados**.
- **TermPicker opens with every synonym group already expanded**, so a single Alt+P shows all alternative translations at a glance instead of hiding them behind collapsed markers. Left/Right still collapse and re-expand individual groups.
- The **About** dialog's shortcut list now includes Alt+P (TermPicker) and Ctrl+Alt+V (voice commands), and its entry for *Translate active segment* is corrected to **Alt+T** – it still showed the old Ctrl+T, which was replaced in 20.119 because it collides with Trados's *Apply Translation Result*.

## [18.20.134 / 19.20.134] – 2026-07-26

### Fixed (MCP Server · tools failing with a UI-thread error) — [#44](https://github.com/Supervertaler/Supervertaler-for-Trados/issues/44)

- **MCP tools could fail with "This method/property must be called on the UI thread"** – reported for `add_comment`, but it could affect any tool that reads or changes the Studio document. It happened whenever the Supervertaler Assistant panel had not been opened in that Studio session: the plugin decided whether it needed to hand work to Studio's UI thread by asking that panel, and a panel that has never been shown answers misleadingly, so the call went ahead on the wrong thread and Studio rejected it. Working only in TermLens – as the new voice-command workflow encourages – made it reliably reproducible. The plugin now captures Studio's UI thread at startup and always routes bridge work through it, independently of which panels are open.

## [18.20.133 / 19.20.133] – 2026-07-26

### Added (MCP Server · filter segments by TM match rate) — [#44](https://github.com/Supervertaler/Supervertaler-for-Trados/issues/44)

- **`get_segments` can now filter by TM match percentage** – requested by a user after a real 10K-segment job. Pass `matchMin`/`matchMax` (0–100): *"list the fuzzy matches between 75% and 94%"* or *"which segments have no match at all?"* (`matchMax=0`) now just work. Every returned segment also carries its `match` percentage and `origin` type (TM, MT, auto-propagated…). Tool definitions are served live from the plugin, so the new filter appears in Claude for Desktop automatically – no extension update needed.

### Fixed (Voice commands)

- Alias lists in Voice command settings now accept semicolons as well as commas as separators – `a;b` used to be silently stored as one unmatchable phrase.

## [18.20.132 / 19.20.132] – 2026-07-26

### Changed (Voice commands · naming) — [#48](https://github.com/Supervertaler/Supervertaler-for-Trados/issues/48)

- The command editor dialog is now called **Voice command settings** (was "Voice commands – advanced") – in its title bar, the TermLens mic right-click menu, and the tooltips.

## [18.20.131 / 19.20.131] – 2026-07-26

### Changed (Voice commands · contextual help) — [#48](https://github.com/Supervertaler/Supervertaler-for-Trados/issues/48)

- The Advanced voice-commands dialog now has a **?** help button in the title bar (and **F1**) opening the [Voice Commands help page](https://docs.supervertaler.com/trados/voice-commands/), and its title uses an en dash like the rest of the UI.

## [18.20.130 / 19.20.130] – 2026-07-26

### Fixed (Voice commands · two field-testing bugs) — [#48](https://github.com/Supervertaler/Supervertaler-for-Trados/issues/48)

- **Saying "zoom in"/"zoom out" no longer opens the TermLens popup as a side effect.** Voice keystroke commands with a Ctrl modifier synthesise a Ctrl press/release pair; when Studio consumed the key in the middle (a bound accelerator), the pair looked exactly like a Ctrl-tap – the popup gesture. The Ctrl-tap detector now ignores taps that coincide with a synthetic voice keystroke. Physical Ctrl-taps are unaffected.
- **The 🎤 mic button in the TermLens header now responds to the first click** when the panel was inactive (the Studio 2026 first-click-eaten quirk – same fix the other header buttons already had).

## [18.20.129 / 19.20.129] – 2026-07-26

### Fixed (Voice commands · new defaults now reach customised command sets) — [#48](https://github.com/Supervertaler/Supervertaler-for-Trados/issues/48)

- **Saving custom voice commands no longer hides default commands added in later updates.** A `voice_commands.json` saved in the Advanced dialog used to replace the built-in list entirely, so newly shipped defaults (e.g. "zoom in"/"zoom out") never appeared for anyone with a customised set – and *Restore defaults* was the only remedy, at the cost of your customisations. Saved command sets now carry a generation marker: on load, only the **new** default commands are appended, and your existing rows, custom phrases/aliases and deletions of old defaults stay exactly as you left them. To hide a default command you don't want, untick it rather than delete it.

## [18.20.128 / 19.20.128] – 2026-07-26

### Changed (Voice commands · polish) — [#48](https://github.com/Supervertaler/Supervertaler-for-Trados/issues/48)

- The Advanced voice-commands dialog now carries the Supervertaler icon instead of the generic form icon.
- **New default commands "zoom in" / "zoom out"** (aliases "bigger font" / "smaller font") mapped to Ctrl+Alt+PgUp / Ctrl+Alt+PgDn. Trados Studio's *Adapt font sizes* actions ship with no default shortcut, so bind those two chords once under **File > Options > Keyboard Shortcuts > Editor** – scroll to the actions named simply *Increase* and *Decrease* (that page has no search box) – and the voice commands control the editor font size hands-free.

## [18.20.127 / 19.20.127] – 2026-07-26

### Changed (Voice commands · integrated indicator + more default commands) — [#48](https://github.com/Supervertaler/Supervertaler-for-Trados/issues/48)

- **The voice indicator now lives in the TermLens header** as a permanent 🎤 button next to ↻ – grey when off (click to start), orange while starting, green while listening; heard commands flash briefly in the panel's status label, and right-clicking the mic opens the Advanced command editor. The floating strip no longer covers any part of the UI when TermLens is open; it remains only as a fallback for sessions without the TermLens panel, and is now draggable with its position remembered.
- **New default commands**: "match one"–"match nine" (apply Translation Results match N, Ctrl+1–9), "escape" (close the focused popup/dialog – term popup, TermPicker…), "go to the top" / "go to the bottom" (Ctrl+Home / Ctrl+End), and "add term" (Alt+Down, write termbases) is now distinct from "add project term" (Alt+Up, project termbase).
- If you saved custom commands in the Advanced dialog on an earlier build, click **Restore defaults** there to pick up the new command set.

## [18.20.126 / 19.20.126] – 2026-07-26

### Added (Voice commands · hands-free Studio control) — [#48](https://github.com/Supervertaler/Supervertaler-for-Trados/issues/48)

- **One-button voice commands.** Press **Ctrl+Alt+V** (or use the editor right-click menu) and control Studio hands-free with a ready-made command set – no configuration needed: "confirm", "next/previous segment", "copy source", "clear target", "term one" … "term nine" (inserts the numbered TermLens match, with capitalisation adaptation), "term picker", "term popup", "add term", "translate", "concordance" and "stop listening". A small status strip shows the listening state and each command as it is heard, with stop and Advanced buttons.
- **Offline and private.** Recognition runs locally via the Vosk engine in grammar mode – it listens *only* for the command phrases, which makes commands fast and reliable, and no audio ever leaves your machine. The first activation downloads the engine and a small English model (~50 MB, one-time, with progress shown); the plugin package itself stays the same size. Commands only execute while Trados Studio is the foreground window, so speech in other apps can't trigger anything.
- **Advanced dialog** (the gear on the status strip) for those who want to go deeper: edit phrases and aliases, enable/disable commands, and map new spoken phrases to any Studio keyboard shortcut or plugin action. The command file is compatible with Supervertaler Workbench's voice-command JSON, so command sets can be exchanged between the two products. Designed to pair with dictation tools (e.g. Wispr Flow) – they dictate, Supervertaler handles the hands-free commands.

## [18.20.125 / 19.20.125] – 2026-07-26

### Fixed (TermLens · adding terms via the dialog and merge-as-synonym is now instant)

- **The add-term dialog (Ctrl+Alt+T) and the "add as synonym?" prompt no longer trigger a full reload on save.** Both paths used to re-read the settings, reload the entire termbase database, re-read every attached MultiTerm termbase and rebuild the display after each save – which made them feel noticeably slower than the Alt+↑/Alt+↓ quick-adds. They now update the in-memory index incrementally, the same way the quick-adds always have, so saving a term or merging a synonym is effectively instant. A newly added source synonym also becomes a live match immediately (previously it wouldn't match until the next full reload). The full reload still runs where it is genuinely needed – editing an existing entry and "Add & Edit".

## [18.20.124 / 19.20.124] – 2026-07-26

### Added (TermLens · term capitalisation now follows the segment)

- **Displayed and inserted terms now adapt their capitalisation to the source occurrence in the segment.** A term stored as "More preferably" ↦ "Meer bij voorkeur" used to show and insert with the stored capital even when the segment contained it lower-case mid-sentence; the chip and every insertion path (chip click, Alt+digit shortcuts, the TermLens popup and the TermPicker dialog) now follow the segment: lower-case occurrences show a lower-case term, sentence-initial occurrences are capitalised, and ALL-CAPS occurrences (headings) upper-case the whole term. The rules are deliberately conservative – acronyms and mixed-case terms (MRI, pH) are never altered, and abbreviation or suffix-tolerant (Korean/Japanese) matches are left untouched. Can be switched off with the new **Adapt term capitalisation to the segment** option in TermLens settings (on by default).

## [18.20.123 / 19.20.123] – 2026-07-25

### Fixed (Terminology · MultiTerm termbases now reliably reach the AI and the Termbases tab) — [#38](https://github.com/Supervertaler/Supervertaler-for-Trados/issues/38)

- **A MultiTerm/Trados termbase’s terms could silently miss the AI prompt in batch jobs.** When a `.sdltb`/`.ttb` can’t be read directly (no ACE/JET driver, 64-bit host, Studio 2026), Supervertaler falls back to Trados’s own terminology provider – which only answers **one segment at a time**. TermLens queried it for the segment you were looking at, so its terms appeared on screen *and* reached the prompt for that segment – but a Batch Translate, Proofread or clipboard run covers segments you never visited, and those were never queried, so their terms were silently absent from the prompt. Because TermLens kept showing hits, it looked as though terminology was being sent. Batch Translate, Batch Proofread, the clipboard and preview paths, and both single-segment (Alt+T) paths now query the fallback provider for exactly the segments being processed before assembling the term list. Results are cached per document, so a repeat run costs nothing, and the bridge log records how many lookups were pre-warmed. Termbases read directly (the normal case) were never affected.
- **An attached termbase could be missing entirely from Settings → Termbases.** The grid was built from a snapshot of the editor’s loaded termbase list, with a fallback that only kicked in when that snapshot was **completely** empty. If it held some termbases but was missing one (for example taken before that one finished loading), the missing termbase had no row at all – not even “Failed to load” – so its **AI** tick box was unreachable even while TermLens was showing its terms. The grid now reconciles the snapshot against the termbases actually attached to the project and adds a row for anything missing, so every attached termbase is always listed and tickable.

## [18.20.122 / 19.20.122] – 2026-07-24

### Changed (AI models · Claude Opus 5 added, superseded models retired)

- **Claude Opus 5 added** (released 24 July 2026). Anthropic’s new flagship Opus: near-Fable-5 intelligence at **$5/$25 per million tokens** – the same price as Opus 4.8 and half of Fable 5 – with no always-on-thinking surcharge. It is now the premium choice for hard legal/technical translation and long-context jobs. **Claude Sonnet 5 remains the recommended default** for routine work.
- **Claude Opus 4.8 and Claude Sonnet 4.6 removed from the model picker** – both are superseded (Opus 5 costs the same as Opus 4.8 and is better; Sonnet 5 supersedes Sonnet 4.6), so keeping them only made the list harder to choose from. The OpenRouter routes were updated to match (Sonnet 5 / Opus 5). Their prices stay in the shared pricing list, so cost figures for existing projects and past usage logs still resolve. If you had one of the retired models selected, pick a current one in **Settings → AI Settings**.

## [18.20.121 / 19.20.121] – 2026-07-24

### Fixed (MCP Server · non-ASCII search terms now match)

- **`get_segments` (and every other query-based tool) returned zero results for any non-ASCII search text.** A `contains=` / `q=` filter for a word like *oriëntatie*, or a symbol like *α*, matched nothing even when the document plainly contained it, while ASCII words (*hoek*, *strok*) worked. Cause: the bridge read query parameters via .NET Framework’s `HttpListenerRequest.QueryString`, which does not reliably UTF-8-decode percent-escaped non-ASCII (the MCP client correctly sends `ori%C3%ABntatie`, but the plugin decoded it to mojibake that never matched). The bridge now parses the raw query with explicit UTF-8 decoding, so accented, Greek, CJK and other non-ASCII searches work across all tools (`get_segments`, TM/termbase search, lookups, etc.). ASCII queries are unaffected. Found via the Supervertaler MCP Server while chatting to Claude about a live project.

## [18.20.120 / 19.20.120] – 2026-07-24

### Fixed (TermLens · terms with an apostrophe, e.g. "SDG’s", now match) — [#19](https://github.com/Supervertaler/Supervertaler-for-Trados/issues/19)

- **Single-word terms containing an apostrophe were never matched.** The word tokeniser splits on apostrophes, so a segment like "SDG’s" was cut into "SDG" + "s" and a termbase entry "SDG’s" could never be looked up. Such terms now go through the same substring matcher that multi-word terms use, so they are found whole.
- **Curly vs straight apostrophes now fold together.** Word, InDesign and most DTP tools auto-convert a typed apostrophe to the "smart" curly form (U+2019), so a term stored with one apostrophe form silently failed to match a segment carrying the other. Matching now folds curly/modifier/fullwidth apostrophes to a plain ' on both sides (the same length-preserving normalisation that already folds Unicode spaces and sub/superscripts). A term stored as "SDG’s" matches "SDG's" in the text and vice versa.
- Terms *without* an apostrophe are unaffected: "SDG" still matches "SDG’s" as before.

## [18.20.119 / 19.20.119] – 2026-07-24

### Fixed (Keyboard shortcuts · "Translate active segment" no longer collides with Ctrl+T)

- **The default shortcut for "Translate active segment" moved from `Ctrl+T` to `Alt+T`.** `Ctrl+T` is a Trados **factory default** ("Apply Translation Result"), so a fresh install had *both* commands on the same key. Pressing it fired both – the native match-apply and Supervertaler’s translate – which raced on the same segment and could **freeze Studio** (seen once the AI write and the native apply landed on the same keypress). `Alt+T` is collision-free. This affects **new installs and default bindings only** – Studio stores each user’s keyboard shortcuts, so if you already rebound it (or cleared the Trados `Ctrl+T`), your setup is untouched; to switch, reassign "Translate active segment" to `Alt+T` in **File → Options → Keyboard Shortcuts**. The `Ctrl+T` row is gone from the "free up Trados shortcuts" list in the help docs.
- **The dead duplicate action is relabelled.** The long-deprecated "Translate active segment (deprecated – use Ctrl+T)" entry (kept registered only so Studio doesn’t crash on a missing type) no longer references `Ctrl+T`; it now reads "(deprecated – do not use)".

## [18.20.118 / 19.20.118] – 2026-07-24

### Added (Licensing · server-side trial registration, observe-only)

- **Trial installs now register with the licence server on startup** ([#47](https://github.com/Supervertaler/Supervertaler-for-Trados/issues/47)). The server records the trial’s authoritative start date on first contact and returns the same original date ever after, giving the trial one reliable record that survives reinstalls, data-folder moves, and clock changes. **In this release the server’s answer is only recorded, not enforced** – local trial behaviour is completely unchanged, and the call fails silently when offline (a legitimate user is never blocked or nagged). A later release will make the server date authoritative with a generous offline-grace window for air-gapped work. Privacy: only the anonymous machine hash already used for licence activation, plugin/Studio version, locale, and the trial’s local start date are sent – details in the privacy policy at supervertaler.com/privacy.
- **"Cost shouldn’t be a barrier."** The trial-expired message and the Licence settings panel now say it explicitly: if the price is a problem for you, get in touch and we’ll work something out.

## [18.20.117 / 19.20.117] – 2026-07-23

### Added (AI Assistant · Claude Fable 5)

- **Claude Fable 5 is now selectable as a Claude model** (Supervertaler Settings → AI Settings). Fable 5 is Anthropic’s most capable model (released June 2026), sitting above Opus 4.8: it runs deeper, always-on reasoning on every request and costs double Opus – $10/$50 per million tokens vs Opus 4.8’s $5/$25 – and the always-on reasoning itself bills as output tokens, so the real per-job cost is higher than the sticker ratio suggests. Worth reaching for on the hardest jobs – dense legal/technical material, AI Proofreader passes over a whole document – while **Claude Sonnet 5 stays the recommended default** for routine translation and batch work. The shared pricing list (`pricing.json`) now covers Fable 5, so the cost estimator handles it out of the box.
- **Response parsing handles always-on reasoning.** Fable 5 puts a "thinking" block before the text in every response; the response parser previously read only the first content block, so every Fable 5 call – including Test Connection – failed with "Could not parse Claude response". The parser now extracts the text-typed block(s) regardless of position (the chat/tool path already did). A safety refusal (Fable 5’s content classifiers) now also produces a clear "Claude declined this request" message instead of a generic parse error.

## [18.20.116 / 19.20.116] – 2026-07-23

### Fixed (TermLens · invisible characters can no longer hide your term matches)

- **Multi-word terms now match segments containing Unicode space variants.** InDesign/IDML-derived documents routinely carry a no-break space inside a phrase ("display panel" with U+00A0 instead of a plain space). TermLens's multi-word matching is an exact substring search, so a termbase entry stored with a normal space silently never matched such a segment – and because single words still matched, the miss looked like a termbase problem rather than a document quirk. Matching now folds all Unicode space variants (no-break, narrow no-break, en/em/thin/hair spaces, ideographic space) to a plain space on both the segment side and the termbase-index side – covering Supervertaler termbases, MultiTerm `.sdltb`, Studio 2026 `.ttb` and API-fallback termbases.
- **Terms can no longer be *saved* with invisible characters.** Selecting text in the editor to add a term pair copied any no-break/zero-width characters straight into the termbase – producing an entry that looked identical to a clean one but could never match anything (a no-break space even stopped the entry being classified as a multi-word term at all). Every term/synonym write path – add, quick-add, batch add, edit, TSV import, non-translatables – now folds space variants to a plain space, strips zero-width characters (ZWSP, word joiner, BOM) and collapses runs of spaces before storage.

## [18.20.115 / 19.20.115] – 2026-07-23

### Added (Supervertaler MCP Server · no more "now press Ctrl+S in Studio")

- **New `save_document` tool** – the AI can save the document open in the editor itself (the same as Ctrl+S, covering all files of a merged document) instead of handing you back to Studio to do it. It's instructed to save only when you ask or approve – AI-written translations still land as Draft for your review first – but *"save and then run the analysis"* is now one instruction. The batch-task tools now point the AI at `save_document` for their save-first-then-run flows.

## [18.20.114 / 19.20.114] – 2026-07-22

### Added (Supervertaler MCP Server · "look at segment 331" now fetches exactly segment 331)

- **`get_segments` can now fetch by grid number** – new `fromNumber`/`toNumber` parameters retrieve exactly the segment(s) you refer to by the number you see in Studio's grid, instead of the AI paging through the document and occasionally landing on the wrong window (which could produce confidently wrong conclusions about "what's in segment N"). Works in merged multi-file documents too: numbers restart per file, so the AI combines the range with the file name – and when it doesn't, the response says the match spans files. The tool now explicitly instructs the AI to use exact-number fetch, never offset-guessing, when you mention a segment number.

## [18.20.113 / 19.20.113] – 2026-07-22

### Added (Supervertaler MCP Server · the AI can now curate your termbase, not just add to it)

- **Term lookups now tell the AI which termbases are actually in use.** `lookup_term` searches every Supervertaler termbase in your database – including ones whose **Read** tick is off – which is useful for "do I have this anywhere?" questions, but was invisible. Hits from inactive termbases are now flagged, so the AI can say "found, but only in an inactive termbase", and a new `activeOnly` option restricts the search to your Read-enabled termbases – handy once you've accumulated many termbases and want lookups limited to the active set (*"only consult my active termbases"*).
- **New `update_term` and `delete_term` tools** complete the terminology loop: when the AI spots an outdated or wrong pair in your Supervertaler termbase, it can now fix or remove it instead of telling you to do it by hand. Rails: only termbases with the **Write** column ticked (the same gate as `add_term`); the entry must be identified by its **exact** current source and target; every other field of the entry (definition, notes, domain, flags) is preserved on update; and the response spells out exactly what changed, so the chat transcript doubles as your audit trail. Deleting is flagged to the AI as destructive – it's told to act only on your clear request or confirmation. Trados project termbases (`.ttb`/`.sdltb`) remain **read-only by design** – editing a live Studio termbase file from outside risks corrupting it, so those edits belong in Studio.

## [18.20.112 / 19.20.112] – 2026-07-21

### Changed (Supervertaler MCP Server · the connection now starts with Studio, not with the first document)

- **The AI bridge starts as soon as Trados Studio is up** – previously it waited for a document to be opened in the editor, so with Studio sitting on the Projects view your AI app saw a dead connection (and a stale tool list). The machine-wide tools (`list_projects`, `list_tms`, `list_project_templates`, the prompt library, `help`) don't need a document at all, and now work the moment Studio is running. Tools that do need one keep answering gracefully ("no document is open in the editor") until you open it.

### Fixed (Supervertaler MCP Server · Studio 2026's project registry is actually found now)

- **`list_projects` (and the by-name project lookups) missed every Studio 2026 project**, because Studio 2026 keeps its Documents folder under a *different name* than expected – `Studio 2026 Release`, not `Studio 2026`. The Studio folders are now discovered by enumerating `Documents\Studio *` instead of hardcoding names, so all versions' registries, Translation Memories folders and Project Templates folders are found regardless of how the edition names its folder – current and future.

## [18.20.111 / 19.20.111] – 2026-07-21

### Added (Supervertaler MCP Server · your AI assistant can now see all your projects, TMs and templates)

- **Four new machine-wide tools**: **`list_projects`** (every project registered in Trados Studio, with status, dates, paths and which Studio version registered it), **`get_project`** (details of any registered project by name – languages, files, status – without opening it), **`list_tms`** (the file TMs in your Studio folders plus those referenced by your projects), and **`list_project_templates`**. Ask *"what projects do I have?"*, *"when did I create the ACME job?"*, *"which TMs are on this machine?"*
- **All of these read every Studio version's registry** – Studio 2026, 2024 and 2022 each keep a separate project list, and previously only one was consulted (which is why a Studio 2026 project could come back as "not found"). Projects registered under more than one version are deduplicated. The same multi-registry search now also backs `get_project_statistics`'s by-name lookup, and the TM/template folders of all three versions are scanned.

## [18.20.110 / 19.20.110] – 2026-07-21

### Added (Supervertaler MCP Server · the plugin can now tell you when your extension needs updating)

- **Version handshake between the plugin and the MCP extension.** The extension exe now reports its protocol level to the plugin on every request, so the plugin knows whether the installed extension supports everything it needs. If a future plugin version ever requires a newer extension, you'll hear about it in three places without any new machinery: your AI assistant tells you directly in chat (via the `help` tool and project-status responses), and the **Connect AI assistant** dialog shows the status. Nothing nags today – every current extension remains fully supported; this just puts the plumbing in place so "your extension is outdated" can never again go unnoticed. Older extensions that predate the handshake are detected automatically (they simply don't report a version).

## [18.20.109 / 19.20.109] – 2026-07-20

### Added (Supervertaler MCP Server · your AI assistant as prompt engineer)

- **New `get_prompt_context` tool** hands your AI assistant everything it needs to write a translation prompt tailored to the project open in Trados: source/target languages, the detected domain, the source text, the relevant termbase terms, a few confirmed TM example pairs, and your current Default Translation Prompt as a starting point. Ask *"look at my project and write me a tailored prompt,"* refine it together, then *"save it"* (via `save_prompt`). The plugin makes **no** prompt-engineering API calls of its own – the AI you're already chatting with does the work, which is what it's best at.
- **New AI Setting – "Prompt context – source segments"** (Settings → AI Settings, under External AI assistants): controls how much of the source document `get_prompt_context` sends. **0 = the whole document** (the default – ideal for large-context models like Claude and for high-value projects where you want the AI to see everything); a positive number caps it. The AI can also override it per request with `maxSegments`.

### Fixed (AutoPrompt · generated prompts no longer claim segments arrive "one at a time, in isolation")

- **AutoPrompt's meta-prompt described segment delivery wrongly**, so every generated prompt told the translator AI it receives *"one segment at a time, in isolation"* – but Batch Translate/Proofread actually send **numbered batches** of segments (your *Batch size* setting, e.g. 75 per request). The generated prompts therefore forbade using context the AI could legitimately see, and left terminology "choices" open that can't stay consistent across batches. The template now describes batched delivery correctly: translate every delivered segment and keep count/order aligned; in-batch context (e.g. a nearby antecedent) **may** be used; batch boundaries are arbitrary, so document-wide checks belong to a QA pass; there is no memory between requests, so the prompt must **lock** every recurring term (no open "X or Y" choices); and ⟦TC: …⟧ correction markers stay attached to their own segment, never pooled at the end of a batch. Existing AutoPrompt-generated prompts in your library keep the old wording – regenerate (or hand-edit) the ones you rely on.

## [18.20.108 / 19.20.108] – 2026-07-19

### Added (TermLens · import MultiTerm termbases into Supervertaler)

- **Import a Trados MultiTerm termbase into your Supervertaler termbase.** A new **Import .sdltb/.ttb…** button in Supervertaler Settings → Termbases reads a Trados termbase – `.ttb` (Studio 2026) or `.sdltb` (MultiTerm) – and imports its terms into a Supervertaler termbase, so they show up in TermLens (and in the Supervertaler Workbench, which shares the same database). A mapping dialogue detects the languages from the file and shows an example entry so you can confirm which side is which; you choose which descriptive fields (definition, note, subject/domain, status, part of speech …) map onto which Supervertaler fields, with sensible defaults filled in. Extra terms for a language are imported as synonyms, and a term's "forbidden/deprecated" status maps to the forbidden flag. Which language is stored as source or target is just an organisational choice – TermLens matches terminology in either direction automatically. `.ttb` import works in both the Studio 2024 and 2026 builds; `.sdltb` import needs the 32-bit Access engine and so runs in the Studio 2024 build (in Studio 2026, convert the termbase to `.ttb` first).

## [18.20.107 / 19.20.107] – 2026-07-18

### Fixed (TermLens · Trados Studio 2026 .ttb termbases)

- **Project termbases are now labelled by their real format** in Supervertaler Settings → Termbases – `[.ttb]` (Studio 2026's SQLite format) and `[.sdltb]` (MultiTerm) – instead of everything showing as `[MultiTerm]`.
- **A `.ttb` termbase you attach mid-session now appears on its own.** A just-attached `.ttb` can fail its first read while Studio is still wiring it up, and unlike `.sdltb` it has no fallback, so it produced no TermLens hits until you toggled it off and on. TermLens now retries a failed `.ttb` load automatically for a few seconds, so matches show up without the manual toggle.

## [18.20.106 / 19.20.106] – 2026-07-18

### Added (Supervertaler MCP Server · ask "what can I do?")

- **New `help` tool.** Ask your AI app *"what can I do?"* / *"what can you do?"* / *"help"* and it shows a curated, grouped menu of the things you can ask this Trados assistant – project status, finding segments, TM and terminology, quality checks, editing, batch tasks, and the prompt library – with example phrasings. It's an authoritative, consistent list (not the AI improvising from memory), and the card text is a plugin resource, so it stays in sync as features are added.

### Changed

- **The Analyse Files tool is now named `analyze_files`** (was `analyze`). "Analyse my project" is a natural request for a *review*, which made the AI reach for the read tools instead of the batch task; the clearer name maps "run analyse files" straight to Studio's Analyse Files task. No change to how you phrase it in chat.

## [18.20.105 / 19.20.105] – 2026-07-18

### Fixed (Supervertaler MCP Server · analysis leverage bands now show up)

- **`get_project_statistics` now reads the leverage breakdown from the analysis report** (`Reports\Analyze Files*.xml`), not the copy cached inside the `.sdlproj`. After running Analyse Files from your AI app, the perfect/in-context-exact/exact/fuzzy/new/repetition figures were coming back as zeros because the SDK writes them to the report file while leaving the project's inline copy empty. It now reads the most recent report, so the real match-leverage numbers (including your TM hits) come through. Confirmation statistics (draft/translated) are unchanged.

## [18.20.104 / 19.20.104] – 2026-07-18

### Changed (Supervertaler MCP Server · batch tasks no longer time out, and new tools appear without an app restart)

- **Batch tasks now run in the background instead of blocking.** Analyse Files, Pre-translate, Update Main TMs and Generate Target Translations can take minutes on a real project – longer than an AI app will wait for a single tool call, which is why *"analyse the project"* previously timed out. Now the tool returns immediately with a job id, and the AI checks progress with the new **`get_task_status`** tool (status, elapsed time, and the task's own messages such as pre-translate match counts). Only one batch task runs at a time. For Analyse Files, once it reports done, `get_project_statistics` shows the leverage bands.
- **The MCP server now tells your AI app when the tool list changes** (`tools/list_changed`). Previously, if Trados wasn't fully up when the AI app connected, the app could show a stale tool list until you restarted it. The server now watches the connection and refreshes the list on its own – so a newly-added tool (or Trados starting after the app) shows up without a restart.

## [18.20.103 / 19.20.103] – 2026-07-18

### Added (Supervertaler MCP Server · the AI can now run Analyse Files)

- **New `analyze` tool** runs Trados Studio's **Analyse Files** batch task on the open project. It computes the leverage breakdown (perfect / in-context-exact / exact / fuzzy bands / new / repetitions) and writes it into the project – which is exactly what `get_project_statistics` reads back. So if the analysis bands came back empty (because Analyse Files had never been run), you can now just ask the AI to *"analyse the project"* and then *"show me the statistics"* – no need to leave the conversation. Like the other batch tasks it runs against the last-saved state.

## [18.20.102 / 19.20.102] – 2026-07-18

### Fixed (Supervertaler MCP Server · project statistics now work for the project you have open)

- **`get_project_statistics` now reads from the project open in the editor** instead of looking it up by name in Trados' `projects.xml` on disk. The old lookup silently failed for recently-created projects and for projects registered under a different Studio version (Studio 2024 and 2026 keep *separate* `projects.xml` files, and the lookup only checked 2024/2022) – so asking for statistics on a fresh project returned "no project found". It now resolves the analysis report from the open project's own `.sdlproj`, so it works regardless of when or where the project was created. Looking up a *different* project by name still works and now also finds Studio 2026 projects. The response carries a `source` field (`open-project` or `projects.xml`) so it's clear which was used.

## [18.20.101 / 19.20.101] – 2026-07-18

### Added (Supervertaler MCP Server · your AI assistant can now work with your prompt library)

- **Three new MCP tools give your AI app access to your Supervertaler prompt library** – the same Markdown prompts you use in the QuickLauncher and Batch Translate, shared with the Supervertaler Workbench:
  - **`list_prompts`** – browse your prompts (name, description, folder, and flags), optionally filtered by folder or a search term.
  - **`get_prompt`** – read the full text of any prompt.
  - **`save_prompt`** – create a new prompt, or update one of your own, straight from the conversation. Built-in default prompts are protected (save your version under a new name instead).
  - This turns your AI app into a prompt engineer: *"look at my Default Translation Prompt and suggest improvements,"* then *"save that as a new prompt."* Because the tool list is now discovered from the plugin (see 20.100), these appear after a normal restart – no extension reinstall.

## [18.20.100 / 19.20.100] – 2026-07-18

### Changed (Supervertaler MCP Server · future tool updates no longer need an extension reinstall)

- **The MCP server now discovers its tools from the plugin at connect time**, instead of carrying a hard-coded list baked into the extension exe. The plugin publishes the tool registry over the bridge (new `GET /v1/tools`), and the server advertises whatever it finds there. The practical effect: when a plugin update adds new AI tools, they show up in your AI app on its next restart – you no longer have to download and reinstall the Claude Desktop extension to get them. The server keeps a local copy of the last known tool list, so your tools are still listed when Trados is closed, and ships with a built-in copy for the very first run.

## [18.20.99 / 19.20.99] – 2026-07-18

### Added (Supervertaler MCP Server · your AI assistant can now run your whole Trados workflow)

- **The connection now starts automatically** – no more clicking the Supervertaler Assistant panel to wake it up. As soon as you have a document open in the Trados editor, your AI app can reach the project. (Previously the connection only started once you activated the Assistant panel; it now starts on its own regardless of which panel is in front.)
- **Find and replace across your translations** – *"replace every 'shall' with 'must' in my targets."* The AI can preview exactly which segments would change before applying anything, respects your inline tags (matches that would break formatting are skipped and reported), and can restrict to one file or one confirmation status.
- **Run Studio's own QA and act on it** – *"run verification and show me the findings."* The AI runs Trados' built-in Verify Files (QA Checker 3.0, tag and terminology checks: punctuation, brackets, repeated words, spelling, length, etc.) and gets the findings back per segment, with the QA rule and severity. It catches things the AI's own checks don't, and each finding links straight to the segment so it can jump there or comment on it.
- **Trados batch tasks by conversation** – *"pre-translate everything with my TM matches," "save my confirmed translations to the TM," "export the translated Word document."* Pre-translate, Update Main Translation Memories, and Generate Target Translations can all be triggered from your AI app.
- **Jump to any segment** – *"take me to segment 47"* – the AI moves the Studio editor to the segment it's discussing, by the number you see in the grid or its id.
- **Read, add, and update Trados comments** – flag a source issue for the client, leave a review note, or rewrite an existing comment after fixing the segment it describes.
- **Your Trados project termbases are now included** – terminology lookups, the terminology QA check, and the resource listing now search the termbases attached to your Trados project (the new **.ttb** format in Studio 2026 and **MultiTerm .sdltb** in Studio 2024), not just your Supervertaler termbases. Definitions come through too.
- Segment listings now include the **segment number you see in Studio's grid**, so the AI cites the right number when it talks to you (and never invents one).

## [18.20.98 / 19.20.98] – 2026-07-17

### Added (Import/Export · Trados segment comments now appear in every export format)

- **Bilingual exports now include your Trados segment comments** – previously they were silently dropped in every format. Comments (from both the source and target side, including comments on a selection) are exported as `Author (yyyy-MM-dd): text`, with multiple comments on one segment stacked per line. Where they appear: a **Comments column** in the DOCX table and the HTML report (only added when at least one exported segment actually has a comment, so comment-free exports keep their familiar layout), and a **`Comment:` line** in the Bilingual Text format – the same line label the Supervertaler Workbench uses, so files remain readable by both tools. Comments are reference material for the proofreader: they are **not** written back into Trados on re-import, and the Notes column stays a free writing space.

### Fixed (Import/Export · comment lines from Workbench-made text files can no longer corrupt a re-imported target)

- **The Bilingual Text re-import parser now understands `Comment:` lines, including multi-line comments.** Before, a text file containing comment lines (e.g. one exported by the Supervertaler Workbench, whose format always includes them) could leak comment text into the re-imported target when a segment had no `Status:` line, and a comment continuation line that happened to look like a language line (`NB: check this`) could even be mistaken for the translation itself. Comment lines and their continuations are now cleanly skipped, matching the Workbench parser's rules.

## [18.20.97 / 19.20.97] – 2026-07-17

### Changed (Import/Export · DOCX exports no longer contain Word bookmarks)

- **Bilingual DOCX exports no longer wrap each source cell in a hidden Word bookmark** (`SV_seg_1`, `SV_seg_2`, …). For anyone with Word's "Show bookmarks" display option enabled – common on translators' machines, where CAT-related add-ins often switch it on – every source segment appeared surrounded by light grey square brackets, which looked like stray characters in the file. The bookmarks were a leftover from a retired export layout: re-import identifies each row by the number in the `#` column and the sidecar manifest, and never read the bookmarks, so nothing about the round-trip changes. Existing exports with bookmarks re-import exactly as before.

## [18.20.96 / 19.20.96] – 2026-07-17

### Changed (Import/Export · single-file exports are now named after the file, not the project)

- **A bilingual export that contains one source file is now named after that file** – e.g. `Application as filed.docx_bilingual_text.txt` instead of `<project name>_bilingual_text.txt`. This applies everywhere a single file is exported: a document opened on its own, a merged multi-file document with just one file ticked, and each file emitted by the "Separate file per file" output mode. Previously, opening a project's files in separate editor tabs and exporting each one suggested the **same project-based name for every file** – so the second export would silently overwrite the first (including its re-import sidecar manifest). Only a genuine combined export (several files ticked, "Combine into one file") still uses the project name.
- **"Separate file per file" outputs drop the project-name prefix** – files are now `<source file>_bilingual.docx` rather than `<project> — <source file>_bilingual.docx`. The project name is still recorded inside the file's header block and in the sidecar manifest.

### Fixed (Import/Export · manifest recorded the wrong file when exporting a non-active file)

- **Exporting a single non-active file from a merged document now records that file's name** in the export header and sidecar manifest. Previously the manifest always claimed the segments came from the file whose tab was active, even when you had ticked only a different file in the file list.

## [18.20.95 / 19.20.95] – 2026-07-15

### Added (Supervertaler MCP Server · more tools for your AI assistant)

- **Your AI assistant can now answer richer questions about your project.** Four new abilities for the [Supervertaler MCP Server](https://docs.supervertaler.com/trados/mcp-server/): it can **search your project's own Trados TMs** – the .sdltm files and GroupShare server TMs attached to the project, the same ones SuperSearch queries – so "how did I translate this before?" finally reaches the memories you actually translate against; it can list the **files** in a merged multi-file document (and you can ask it to work on just one of them – "only look at the contract file"); it can report **project statistics** (analysis bands and per-file confirmation counts – "how many words are left?", "how far along is each file?"); and it can **find inconsistencies** – repeated source sentences you translated differently – which pairs naturally with its ability to then align them ("find all repeated sentences I translated differently, and fix them to match"). See the [prompt cookbook](https://docs.supervertaler.com/trados/mcp-server/) for the full range of what you can ask.

### Added (Supervertaler MCP Server · QA checks and resource listing)

- **Your AI assistant is now a QA partner.** Three quality checks that work on the whole open document: **number checking** ("find segments where the numbers don't match" – decimal/thousand separator differences are handled), **tag checking** (missing or extra inline tags between source and target), and **terminology checking** (source contains a termbase term but the target doesn't use its expected translation or any of its synonyms). Each finding comes back with the segment, the reason, and enough context for the AI to explain it – and, after your approval, fix it. A new **resource listing** tool also lets the AI see which TMs (Trados project TMs, GroupShare, and Supervertaler TMs) and termbases are attached, including read/write flags.

### Changed (Supervertaler MCP Server · supported AI apps)

- **Documentation now clearly states which AI apps work.** Claude Desktop is fully supported (and recommended), and other clients that run local MCP servers on your own machine (such as Claude Code) also work. ChatGPT's desktop app is **not** supported, because it runs MCP servers in a cloud environment that can't reach the Supervertaler bridge – which stays on your computer by design, so your project never leaves your machine.

## [18.20.94 / 19.20.94] – 2026-07-15

### Added (Supervertaler MCP Server · connect Claude/ChatGPT directly to your live Trados Studio project)

- **Your AI assistant can now talk directly to your open Trados Studio project.** The new Supervertaler MCP Server connects AI apps that run local MCP servers – Claude Desktop (recommended), Claude Code and others – to your live Trados session. Ask "What's the status of my project?", "How did I translate this term elsewhere?", or "Find all segments containing X" in the AI app's own chat window, and it answers from your real project data: project statistics, segments (with filters and paging), your Supervertaler translation memories, and your termbases. It can also insert a translation into the active segment, exactly like the Assistant's Apply-to-target button. Everything stays on your machine – the connection is local-only and protected by a per-session token, and nothing is exposed to the network. Setup: **Settings → AI Assistant → Connect AI assistant…** – Claude Desktop users install a `.mcpb` extension (Settings → Extensions → Advanced settings → Install extension…); other apps get a copy-paste config snippet. This is the first MCP server that talks to a live Trados Studio editor session. Follow development in [issue #44](https://github.com/Supervertaler/Supervertaler-for-Trados/issues/44).
  - **Known limitation (fix coming soon):** the connection has to be woken up once per Trados session – after opening a document, **click the Supervertaler Assistant panel once** (its tab, or View → Supervertaler Assistant), or open Supervertaler Settings. If your AI app reports it can't reach Trados, this is almost always the reason. A future update will start the connection automatically so this step isn't needed.

- **The AI can also make changes – always under your supervision.** `update_segments` writes translations and/or confirmation statuses into the open document ("draft translations for all untranslated segments so I can review them"), and `add_term` adds entries to your Write termbases ("we agreed 'draagarm' = 'support arm' – add it"). Safety rails are built in: AI-written translations get **Draft** status unless another status is explicitly requested, **locked segments are never touched**, updates are capped at 200 segments per call, every change is reported per segment, and nothing is saved to disk until you save in Studio.

## [18.20.93 / 19.20.93] – 2026-07-14

### Added (an occasional one-question survey, so I can ask what you think)

- **Once in a while, a small dialog may appear at startup with a single question about Supervertaler's development** – for example, whether you use a particular feature, or what you'd most like improved. You can answer with one click (plus an optional comment), or just close it – it's completely optional and designed to be easy to ignore, and each question is only ever asked once. **No personal data is sent** – only the same anonymous ID and licence/trial status as the existing anonymous usage statistics, nothing that identifies you. Most of the time there is no active question and nothing appears at all. This lets me make better decisions about which features to keep, improve, or retire, based on what people actually use.

## [18.20.92 / 19.20.92] – 2026-07-13

### Fixed (SuperSearch · dialog text no longer clips on high-resolution screens)

- **The "Select translation memories / files to include" pickers now scale their text properly on high-DPI displays.** The instruction line and the buttons had fixed pixel sizes, so on a high-resolution screen the heading was cut off and "Select None" was truncated to "Select". The label and buttons now auto-size to their (scaled) text, and the buttons sit in a proper layout bar, so everything stays readable at any display scaling. Applies to both the Select-TMs and Select-Files dialogs.

## [18.20.91 / 19.20.91] – 2026-07-13

### Fixed (SuperSearch · now searches your TMs out of the box)

- **SuperSearch now searches your translation memories by default, not just the project files.** The search-scope dropdown shipped defaulting to **"Project files"** (SDLXLIFF files only), which silently skipped every TM – so on a fresh install, TM and GroupShare hits never appeared even though the TMs were ticked in the list, and there was no error to explain why. The default is now **"Files + TMs"**, and that option is listed first in the dropdown as the recommended scope. If you had previously left the scope on "Project files", just switch it to "Files + TMs" once (your choice is remembered). "Project files" and "TMs only" remain available for when you want to narrow the search.

## [18.20.90 / 19.20.90] – 2026-07-10

### Added (GroupShare · SuperSearch can now search your server-based GroupShare TMs)

- **SuperSearch now searches server-based (GroupShare) translation memories, not just local `.sdltm` files.** When your project uses a GroupShare TM, SuperSearch queries it alongside your project files and any local TMs and shows the hits inline. Server-TM results are badged **"GroupShare"** in the Status column so you can tell them apart from local files at a glance, and each appears under its own TM name (e.g. `en-US to nl-BE`) rather than a raw server address. This was the top request from institutional users running GroupShare.
- **New "GroupShare" tab in Supervertaler Settings, where you enter your server login once.** Trados Studio does not hand its stored server credentials to plugins, so you set the server URL, login provider, username and password here. The password is encrypted at rest with Windows DPAPI (current user) and is never written in clear text. It lives in Settings rather than inside SuperSearch because these credentials are meant to power more GroupShare-aware features over time.
- **Both GroupShare and Windows (AD) authentication are supported**, via a Login provider dropdown that mirrors GroupShare's own two options – for organisations that authenticate GroupShare against Active Directory.
- Works on both **Trados Studio 2024 and 2026**. (Under the bonnet, server concordance requests are capped to the GroupShare TM Server's limit so they are not rejected; local TMs are unaffected.)

## [18.20.89 / 19.20.89] – 2026-07-06

### Changed (AutoPrompt · AI-based context detection with a confirm step)

- **AutoPrompt now detects the document's context with the AI instead of a keyword heuristic, and lets you confirm or steer it before generating.** Clicking AutoPrompt sends a sample of the source to the model, which classifies the domain and describes the text type; a short "Reading the document…" window shows while it works. A confirm-context dialog then shows the detected domain with a dropdown to correct it and an optional briefing box (e.g. "creative marketing copy, playful tone"), which is fed to the generator as authoritative context. The default is one click straight through (**Generate**). This mirrors the Supervertaler Workbench AutoPrompt and fixes the cases where the old keyword detector misread a document (e.g. a creative text read as a patent). The keyword detector is kept for word/segment statistics and as an offline fallback if the AI call fails; its keyword "tone" read is dropped in favour of the AI's description. Each AutoPrompt run makes one small extra classification call (a few hundred tokens).

## [18.20.88 / 19.20.88] – 2026-07-06

### Fixed (Clipboard Mode · paste-back no longer crashes 32-bit Trados on large batches)

- **Pasting a large Clipboard-Mode batch back into Trados Studio 2024 no longer spikes memory and crashes.** After running a Batch Translate in Clipboard Mode and clicking "Paste from Clipboard" to write the LLM's response back, a large batch made the editor grid appear to loop endlessly and Trados closed with RAM up around 1.8 GB – far past what a 32-bit process can safely hold. The paste-back applied every parsed segment on the UI thread with no memory guard, no progress window, and no message pumping, so memory climbed unchecked and the grid never got a chance to repaint. It now uses the same safe writeback system as the bilingual re-import (added in 18.20.86):
  - **32-bit memory watchdog.** Every 20 segments the writeback compacts the heap when memory climbs (soft limit) and **stops gracefully with a clear message** before it can crash the host (hard limit), telling you to finish the remaining segments as a smaller batch or in Trados Studio 2026 (64-bit). A no-op on 64-bit.
  - **Responsive progress + Cancel.** A small progress window shows "Writing translations… N of M" with a **Cancel** button; the loop pumps the UI every 20 segments so the editor stays responsive instead of appearing frozen (the "looping" grid).
  - **Re-entrancy guard.** The paste button is disabled while a paste runs, and a second paste is refused until the first finishes.

## [18.20.87 / 19.20.87] – 2026-07-04

### Fixed (Auto-updater · no longer offers the wrong Studio generation)

- **The update check no longer offers a Studio 2026 build to Studio 2024 users, or vice-versa.** Under the new versioning scheme the version major encodes the target Studio (18.x = Studio 2024, 19.x = Studio 2026), and the RWS App Store lists both generations' builds side by side. The updater was picking the numerically-highest published version regardless of generation, so a Studio 2024 user on `18.20.86` was shown the `19.20.86` build meant for Studio 2026. It now filters the App Store's version list to the **same major as the installed build** and offers the newest match within that generation only – 18.x installs only ever see 18.x updates, 19.x installs only 19.x. (Trados's own `RequiredProduct` gate would have refused to load the mismatched build, so nothing broke, but the prompt was wrong and confusing.)

## [18.20.86 / 19.20.86] – 2026-07-02

### Fixed (Bilingual re-import · no longer freezes and crashes on large multi-file projects)

- **Re-importing a bilingual file into a big merged multi-file project no longer freezes the editor and crashes Trados.** A user re-importing a 1,178-segment, 9-file Bilingual Text (`.txt`) file saw the same segments appear to loop endlessly, then Trados closed with no warning. The cause was the writeback path, not the file (which was structurally perfect): for **every** changed segment it re-scanned the *entire* document's segment list to find the match (an expensive `GetParentParagraphUnit` per segment pair) — roughly **1.4 million SDK model calls** (O(n²)) on a document that size — all on the UI thread, with no memory guardrail. That froze the UI (the "looping" the user saw was a hung, mid-scroll editor) and, on 32-bit Trados Studio 2024, exhausted the ~2 GB address space into a silent crash. Three changes fix it:
  - **O(1) segment lookup.** The document's segments are now indexed once up front into a `(paragraph-unit-id / segment-id) → segment` map, so each change is resolved by a dictionary lookup instead of a full re-scan. This removes the ~1.4M-call blow-up; the writeback is now linear in the number of changes.
  - **32-bit memory watchdog.** The writeback now uses the same guard as Batch Translate (added in 4.20.77): it compacts the heap when memory climbs and **stops gracefully with a clear message** before it can crash the 32-bit host, telling you to finish in Trados Studio 2026 (64-bit) or to re-import with fewer files open. A no-op on 64-bit. Re-importing is safe to repeat — already-applied segments come back as "unchanged".
  - **Responsive progress + Cancel.** A small progress window now shows "Applying changes… N of M" with a **Cancel** button; the loop pumps the UI so it stays responsive instead of appearing frozen.
- **Collision note for merged files.** Paragraph-unit ids are only unique *within* a single `.sdlxliff`, so a merged multi-file document can in principle have colliding ids across files. Re-import now detects this and logs a note (it writes to the first match; full file-aware routing via the manifest's `SourceFileId` is a planned follow-up). The reported project did not collide — all 1,178 ids were unique — so this is hardening, not the fix for that case.

## [4.20.85] – 2026-07-01

### Changed (Bilingual export/import simplified to two round-trippable formats + one report)

- **The bilingual export now offers just three formats, matching the Supervertaler Workbench:**
  - **Word document (`.docx`)** – the 5-column Bilingual Table, re-importable.
  - **Bilingual Text (AI-friendly) (`.txt`)** – the `[SEGMENT NNNN]` plain-text format, re-importable. **New:** promoted from the old "Bracketed" *layout* to a standalone *format* with a `.txt` extension.
  - **HTML report (`.html`)** – read-only, as before.
- **Retired the standalone Markdown (`.md`) format and both Stacked layouts** ("source on top" / "target on top"). They were confusing and duplicated the Table/Text formats. The **Layout** dropdown is gone entirely – each format now has one natural layout (DOCX/HTML → table, Text → bracketed). Existing `.md`/stacked files can **still be re-imported** (the importer stays backward-compatible); they just can't be produced any more.
- **In-field line breaks in the Text format are now encoded as a `[newline]` token** (decoded back to a real break on re-import), so every segment field stays on one physical line. This matches the Workbench's "Bilingual Text" export, so a file produced by either tool round-trips through the other. Older multi-line files without the token still re-import unchanged.
- The multi-file output-mode radios are now format-neutral ("Combine into one file" / "Separate file per file") since they apply to both DOCX and Text exports, not just DOCX.

## [4.20.84] – 2026-07-01

### Added (Claude Sonnet 5)

- **Claude Sonnet 5 (`claude-sonnet-5`) is now the default Claude model.** Anthropic's newest Sonnet (released June 30, 2026) gives near-Opus quality – with substantial gains in reasoning, tool use, and knowledge work over Sonnet 4.6 – at the same Sonnet price tier. It's added to the Claude model list, the cost ledger (`pricing.json` + `PricingTable`), and is selected by default for new setups.
- **Sonnet 4.6 is kept as a selectable fallback**, so existing per-project model choices keep working.
- **Pricing note:** the ledger uses Sonnet 5's standard rate of **$3 / M input, $15 / M output** (same as 4.6). Anthropic's introductory pricing ($2 / $10) runs through Aug 31, 2026, so during that window the cost estimate is slightly *higher* than actual; from Sep 1 it matches.

## [4.20.83] – 2026-07-01

### Fixed

- **Markdown re-import dropped multi-line segments.** In the *Bracketed [SEGMENT NNNN]* (AI-friendly Markdown) layout, a segment whose text contains a hard line break – e.g. a source `VEILIGHEIDS-` / `HELM` exported across two lines – only had its **first** line re-imported; the continuation line (and the rest of the translation) was silently lost. The importer now reassembles multi-line `NL:` / `EN:` bodies, stopping correctly at the next language line or the `Status:` line. Single-line segments, empty targets, and proofreader-inserted extra lines round-trip exactly as before.

### Improved (Translate via Workbench · resilience)

- **Clear message when Trados can't close the document.** If the offload's `Close(document)` call fails because the 32-bit Trados process is in a degraded / low-memory state (an `AccessViolationException` – "Attempted to read or write protected memory… memory is corrupt"), the plugin now cancels cleanly (your file is untouched) and shows a plain-language prompt to **save, restart Trados Studio, and try again** instead of surfacing the cryptic SDK error.

## [4.20.82] – 2026-06-30

### Improved (Batch Operations · retry + layout)

- **"Retry segments left empty" now applies to a normal Batch Translate too**, not just *Translate via Workbench*. One shared checkbox in Batch Operations controls both: when ticked, any segment the model leaves empty (or fails to write) is re-translated in extra passes (up to 5) until none remain. The token usage from the retries is rolled into the same Trados ledger entry, and the translated/failed counts are corrected as segments fill in.
- **The *Translate via Workbench* button moved to the right of the ▶ Translate button** instead of sitting up among the scope/option controls. It shows only in normal (non-clipboard) Translate mode, alongside the Translate button it complements.

## [4.20.81] – 2026-06-30

### Improved (Translate via Workbench · parity with a normal Batch Translate)

- **The offload now matches a normal Batch Translate much more closely:**
  - **Document context is included.** It's collected from the open document (capped like the normal batch) and sent along – cheap for the media-heavy/text-light files this targets, so there's no reason to drop it.
  - **Token usage is recorded in Trados's Token Usage & Costs.** The AI calls run in Workbench, but the engine now reports tokens back and the plugin logs the cost into the Trados ledger (as a *BatchTranslate · via Workbench* entry, attributed to the project). Cost is computed at the no-cache rate (a slight overestimate).
  - **"Retry segments left empty" checkbox** next to the button: re-translate any segments the model leaves empty, in extra passes.
  - **Scope respects status.** *Empty segments only* and *All segments* as before, plus *All unfinished segments* now translates Not Translated + Draft and leaves Confirmed/Signed-off untouched (it no longer flattens to "All"). Locked segments are always skipped. *Filtered* scopes map to All, since the editor's display filter can't apply to a closed-document run.


## [4.20.80] – 2026-06-30

### Added (Translate via Workbench · finding Workbench without a terminal)

- **The plugin now finds Supervertaler Workbench on its own, and lets you point at it if needed – no terminal required.** Three parts:
  - **Auto-detect:** in addition to the `supervertaler` launcher on PATH, the plugin now probes common install locations for the bundled desktop app (`%LocalAppData%\Programs\Supervertaler`, `Program Files\Supervertaler`, etc.) and the pip `--user` scripts folder.
  - **A "Workbench (.exe)" setting** on *Settings → AI Settings*, with a **Browse** button, so you can point the offload at any Supervertaler executable. Blank = auto-detect.
  - **A friendly prompt when it still can't be found:** instead of a log line, a dialog offers to **locate** Workbench (the choice is remembered) or **open the download page**.


## [4.20.79] – 2026-06-30

### Added (Translate via Workbench · progress window)

- **The "Translate via Workbench" offload now shows a floating progress window** while it runs. Because the document is closed during the offload, the in-editor Batch log was invisible – this top-level window gives live feedback instead: a status line, a progress bar that becomes determinate once batches start (`batch n of N`), and a **Cancel** button (cancels the engine and reopens the document). It closes automatically when the translated document reopens. First live test of the feature (v4.20.78) worked end to end; this adds the missing feedback.


## [4.20.78] – 2026-06-30

### Added (Batch Operations · "Translate via Workbench" – offload large files to 64-bit Workbench)

- **New "Translate via Workbench (large files)" button on the Batch Operations tab** hands the whole document to the 64-bit Supervertaler Workbench, which translates it and hands it back – so jobs too large for 32-bit Trados Studio 2024 finish without crashing. Trados does **no** heavy lifting. Flow: the plugin builds a job (your provider/model/key/prompt/termbase, and the scope from the dropdown), **closes the active document**, runs `supervertaler --translate-sdlxliff` on its `.sdlxliff` (round-trip, inline tags preserved), then **swaps in the translated file and reopens** the document. A `.sv-backup` copy of the original is kept next to it. Requires Supervertaler Workbench v1.10.322+ installed and discoverable (the plugin runs `supervertaler` / `supervertaler-debug` from PATH). The API key is passed from the plugin, so Workbench needs no separate setup. **First release of this feature – please test on a small project first** ([#42](https://github.com/Supervertaler/Supervertaler-for-Trados/issues/42)).


## [4.20.77] – 2026-06-30

### Added (Batch Translate · memory guardrails for 32-bit Trados Studio 2024)

- **On 32-bit Trados (Studio 2024), Batch Translate now throttles itself to avoid running the host out of memory.** Diagnostic logs from a very large job (a 375 MB PPTX, ~990 segments) showed the crash was memory/address-space exhaustion in the 32-bit Trados process: it surfaced as out-of-memory and GDI/Direct2D failures inside Trados's *own* editor renderer, ending in a fatal .NET execution-engine error. A 32-bit process can only address ~2-4 GB no matter how much RAM the machine has. We can't raise that ceiling, so the plugin now stays under it on 32-bit hosts:
  - **Auto-throttle:** the batch size is capped and the (large) document-context embed is trimmed, transparently. No effect on 64-bit (Studio 2026).
  - **Memory watchdog:** between batches the plugin watches process memory; when it climbs it compacts the heap (incl. Large Object Heap), and if it nears the hard limit it **stops gracefully with a clear message** ("too large for 32-bit Trados - split the file / use smaller batches / use Workbench or Studio 2026") instead of letting Trados crash or hang.
  - For genuinely huge files, the realistic options remain: split the file, translate it in the 64-bit Supervertaler Workbench, or move to Trados Studio 2026 (64-bit).

### Fixed (Diagnostics · no longer turns a host crash into a hang)

- **Removed the `Application.ThreadException` handler added in 4.20.76.** It was capturing Trados's *own* UI-thread exceptions process-wide and swallowing them, which kept the message loop running through fatal paint failures and turned a clean crash into an unkillable hang. Crash capture is retained via `AppDomain.UnhandledException` and `TaskScheduler.UnobservedTaskException`, which log without altering the host's behaviour.


## [4.20.76] – 2026-06-29

### Added (Diagnostics · crashes are now captured to the log)

- **Global crash handlers now write any unhandled/terminating exception to the diagnostic log, even when "Enable diagnostic logging" is off.** Previously a silent, no-dialog close left the Supervertaler log empty, with nothing to go on. The plugin now subscribes at startup to `AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException` and `Application.ThreadException`, and writes a crash banner (plugin version, source, full stack trace) to `…\Supervertaler\trados\logs\diagnostic.log`. A one-line startup marker is also written each launch so a crash can be tied to a version/time. (Managed exceptions are captured; a true native AccessViolation/StackOverflow can still bypass these — in which case Windows Event Viewer's faulting-module entry is the source of truth, and "log still empty after a crash" is itself a strong signal that the fault is native.)

### Fixed (Batch Translate · no longer reads the Trados document model off-thread)

- **Token-usage attribution no longer touches the Trados document model from a background thread.** Batch translation fires its completion callback off the UI thread, and the usage logger was reading thread-affine Trados objects (active file, project/document names, language pair) from there — a potential source of hard, no-dialog crashes during long batch runs. The usage context is now built only on the UI thread, cached, and the cached snapshot is returned to off-thread callers (the cache is warmed on the UI thread when a batch starts). Attribution is unchanged; the off-thread model access is gone.


## [4.20.75] – 2026-06-29

### Changed (Shared TM Bridge · clearer "Workbench" naming throughout)

- **The bridged-TM provider and its dialogs now consistently say "Supervertaler Workbench TM".** Following the 4.20.74 picker-title change, the rest of the bridge UI is aligned so it's obvious these TMs come from the Supervertaler Workbench app:
  - The entry in Trados' *Use…* (add translation provider) menu is now **"Supervertaler Workbench TM (bridged)"** (was "Supervertaler TM").
  - The picker dialog title is now **"Add bridged Supervertaler Workbench TMs"** (pluralised).
  - Each attached TM shows in the Translation Memory list as **"Supervertaler Workbench TM: \<name\>"** (was "Supervertaler TM: \<name\>"), and the related status/error messages match.


## [4.20.74] – 2026-06-29

### Changed (Shared TM Bridge · clearer picker dialog title)

- **The "Add Supervertaler TM" picker is now titled "Add bridged Supervertaler Workbench TM".** New users were mistaking this dialog (reached via *Use… → Supervertaler TM* in a project's TM settings) for a place to create translation memories. It only lists TMs created in the separate **Supervertaler Workbench** app and ticked as **Bridge**, so the title now spells that out. The body text already notes that all listed TMs are flagged "Bridge" in Workbench. Prompted by a support question.


## [4.20.73] – 2026-06-29

### Added (Help · AutoTagger documentation + contextual "?" link)

- **AutoTagger is now documented at [docs.supervertaler.com](https://docs.supervertaler.com/trados/autotagger/), with a contextual "?" link in the app.** The **AutoTagger Instruction** panel in *Settings → Prompts* now has a **"?"** button in its header that opens the AutoTagger help page (what it does, the Ctrl+Alt+G / right-click triggers, validation behaviour, and the instruction placeholders).


## [4.20.72] – 2026-06-28

### Fixed (Translate active segment · works without the Assistant pane; clearer shortcut list)

- **Ctrl+T "Translate active segment" (and the right-click command) now works even if the Supervertaler Assistant pane was never opened this session.** Like AutoTagger, the handler relied on the lazy/unpinned pane being initialized, so after a Trados restart Ctrl+T could silently do nothing until you opened the pane. It now falls back to a pane-independent path (active document from the editor, settings from disk) that runs the same translation pipeline without opening the pane. The normal path is unchanged when the pane is open. (#41)
- **The duplicate "AI translate current segment" entry in Keyboard Shortcuts is now labelled "Translate active segment (deprecated – use Ctrl+T)".** That legacy action must stay registered (removing it crashes Studio on startup), so it can't be deleted from the shortcut list — but it's now clearly marked as the deprecated duplicate so it's obvious which command is the live one. The active command remains "Translate active segment" (Ctrl+T), the only one in the editor context menu.

### Fixed (Token Usage & Costs · records every AI call, even with the Assistant pane closed)

- **All AI usage is now logged to Token Usage & Costs regardless of whether the Supervertaler Assistant pane was opened.** Usage recording was wired up only when the pane first initialized, so in a session where you never opened the pane, nothing was logged — AutoTagger, Ctrl+T, and any other AI calls were all missed. The ledger subscription now runs at plugin startup (independent of the pane), so every call is recorded. (The pane's handler still drives the Reports tab; usage is not double-counted.)


## [4.20.71] – 2026-06-28

### Fixed (AutoTagger · usage now recorded in Token Usage & Costs)

- **AutoTagger's AI calls now show up in Token Usage & Costs.** The call passed no prompt "feature", and the usage ledger only records calls that carry one, so AutoTagger runs were silently omitted from the table. They are now logged under a new **"AutoTagger"** task, with token counts and cost like every other AI call.

### Fixed (AutoTagger · works without the Assistant pane, and never pops it open)

- **AutoTagger (Ctrl+Alt+G / right-click) now works reliably even if the Supervertaler Assistant pane was never opened this session, and it no longer opens that pane.** The pane is lazy (unpinned) and the handler previously did nothing until it had been created — so after a Trados restart the command appeared dead until you opened the pane or Settings. AutoTagger is now fully independent of the pane: it reads the active segment from the editor and its settings from disk, so it just works without disturbing your layout.


## [4.20.70] – 2026-06-28

### Added (AutoTagger · AI places inline tags into the target)

- **New AutoTagger feature: the AI looks at where the inline tags sit in the source segment and inserts that same set of tags into your existing translation at the right places, without changing any of the translated words.** Useful when a target has the right translation but is missing its tags or has them in the wrong spots (after MT, pasting, or typing the target by hand), which otherwise trips Trados' tag QA. It validates the result before writing (the tag set must match the source, the words must be unchanged, and tags must be well-formed); it re-inserts the tags into your exact target so punctuation like curly quotes is preserved; and if the AI's output doesn't validate it retries once and otherwise leaves the segment untouched, so it never writes broken tags. Reuses the same tag engine as batch translate.
  - **Where:** editor right-click → "Auto-tag active segment", and the **Ctrl+Alt+G** shortcut. Trados Undo (Ctrl+Z) reverts it.
  - **Shortcut change:** Ctrl+Alt+G now triggers AutoTagger. The floating TermLens popup keeps its **Ctrl-tap** trigger (the redundant Ctrl+Alt+G binding was removed); you can reassign a key to it via Trados' keyboard settings if you like.
  - **Configurable:** the instruction is an editable field under Settings → Prompts → "AutoTagger Instruction" (placeholders `{{SOURCE_TEXT}}`, `{{TARGET_TEXT}}`, `{{TAG_LIST}}`).
  - v1 is single-segment; a batch mode may follow. Mirrors the Supervertaler Workbench AutoTagger. (#39)


## [4.20.69] – 2026-06-28

### Fixed (Import/Export · re-import status line no longer truncated)

- **The re-import status line under the Format/Layout dropdowns now shows its full text.** Introduced in 4.20.68, the line's box was too short, so the longer "export only" messages (e.g. the Word + stacked-layout case) were clipped mid-sentence. The box is now wide and tall enough for the message to wrap fully. Reported by Michael.


## [4.20.68] – 2026-06-28

### Fixed (Import/Export · readable dropdowns + clear re-import status)

- **The Format and Layout dropdowns on the Import/Export tab no longer truncate their longer entries.** WinForms was clipping the drop-down list to the control width, so options like "Supervertaler Bilingual Table (5 columns)" and "Bracketed [SEGMENT NNNN] (AI-friendly, Markdown only)" were cut off mid-text (worse on high-DPI laptops). The popups now size to their widest item, matching the behaviour already used elsewhere in the plugin. Reported by Michael.
- **It's now clear which export can be re-imported.** Re-import support actually depends on the **Format and Layout together**, not the layout alone: Markdown round-trips every layout; Word (.docx) round-trips only the 5-column Bilingual Table (stacked/bracketed are export-only in Word); HTML is always read-only. The old per-item "round-trippable" tag was misleading because it ignored the format. A **live status line under the dropdowns** now states, for the current selection, whether the file can be edited and re-imported (green) or is export-only (amber) — and notes that the Bracketed layout only applies to Markdown. Reported by Michael.


## [4.20.67] – 2026-06-26

### Added (Batch Operations · "All unfinished segments" scope)

- **New Scope option in Batch Operations (Translate mode): "All unfinished segments".** It targets every segment whose status is *not* finalized — that is, everything **except Translated, Approved (translation), and Approved (sign-off)**. In practice it processes **Not Translated, Draft, and Rejected** segments, so you can batch-translate all the work that still needs doing in one go while leaving your confirmed and signed-off segments untouched. Drafts and rejected segments that already have target text are re-translated; empty ones are filled. Unlike "Filtered segments", it runs over the whole document, not the current display filter. The status is matched by name (`Translated` / `ApprovedTranslation` / `ApprovedSignOff`), so it's correct regardless of the ConfirmationLevel enum's ordering (where "Rejected" sorts above "Translated"). The Scope dropdown now also has a tooltip explaining every option, and the segment counter shows "*N* unfinished / *M* total" when this scope is selected. Works in Clipboard Mode too. Requested by Michael.


## [4.20.66] – 2026-06-19

### Added (MultiTerm · AI opt-in inherits from project templates)

- **The MultiTerm "AI" opt-in now travels with Trados project templates.** Tick a MultiTerm termbase for AI in a project, then save that project as a project template (Create Project Template based on this project) — and every new project created from that template inherits the choice automatically, with no per-project re-ticking. This is aimed at automated / CLI-driven project creation, where many projects are spun up from one template each day (issue #36). It works by mirroring the opt-in into the **Trados project settings bundle** (which templates capture and pass on), in addition to Supervertaler's own per-project store; the existing explicit opt-in is preserved — the conscious decision just happens once, on the template. The choice is stored as the termbase's path, so it applies to any project that attaches the same termbase.


## [4.20.63] – 2026-06-19

### Fixed (MultiTerm · termbase file locking)

- **Using Supervertaler with a MultiTerm (.sdltb) termbase no longer makes Trados's own terminology throw a `TermBaseDBAccess` / `SEHException (0x80004005)` error.** A `.sdltb` is a Microsoft Access (JET) database, and Supervertaler reads it directly via OleDb to load terms for TermLens and AI prompts. Those readers are opened and disposed correctly, but .NET pools the underlying OleDb connection by default, so the ACE/JET engine kept the file **locked** (via its `.ldb`/`.laccdb` lock file) long after Supervertaler was done with it. When Trados's *own* MultiTerm engine then browsed the same termbase — e.g. right after a Batch Processing task — it collided with that lingering lock and threw *"An external component has thrown an exception."* Supervertaler now disables OleDb connection pooling for `.sdltb` access (`OLE DB Services=-4`) and releases the connection pool on dispose, so the file lock is gone the moment it finishes reading and Trados can access the termbase normally. Reported in issue #36.


## [4.20.62] – 2026-06-19

### Changed (Editor context menu — the proper fix)

- **The duplicate "AI translate current segment" entry is removed from the editor right-click menu, without crashing Studio.** The earlier crashes were both from removing *too much*: 4.20.57 deleted the action type (but the manifest still referenced it, so the command bar couldn't instantiate it); 4.20.60 deleted the action's entire `<auxiliaryExtensionAttributes>` element from `plugin.xml` (so the startup shortcut-cache loader hit a null and threw). The startup log pinpointed the second one (`ActionService.ReloadShortcutSettings → Extension.get_AuxiliaryExtensionAttributes → NullReferenceException`). The fix keeps the action **registered** and keeps the element present but **empty** (`<auxiliaryExtensionAttributes />`), dropping only the `ActionLayoutAttribute` — exactly the shape three other extensions in this plugin already ship and load fine. Net: no menu entry, no crash. Use **"Translate active segment" (Ctrl+T)**. (The action still appears in the keyboard-shortcuts editor, by design — registration must stay.)


## [4.20.61] – 2026-06-18

### Fixed (Startup crash — abandons the menu-hide attempt)

- **Studio starts reliably again; the duplicate "AI translate current segment" entry stays.** Two different ways of hiding that entry each crashed Studio on startup: deleting the action (4.20.57) failed because Studio instantiates every cached command-bar action on launch, and removing only its menu-layout from `plugin.xml` (4.20.60) made the action service throw a `NullReferenceException` because the cached editor command bar still referenced that item. Studio's persisted command-bar state makes both removals unsafe. This release restores the known-good configuration (the action and its menu placement both present), so Studio launches normally. The entry is a harmless exact duplicate of **"Translate active segment" (Ctrl+T)**; it is being left in place.


## [4.20.60] – 2026-06-18

### Changed (Editor context menu) — REVERTED in 4.20.61

- Removed the action's `ActionLayoutAttribute` from `plugin.xml` to drop the menu entry. **This crashed Studio on startup** (`IActionService` NullReferenceException — the cached command bar still referenced the item). Reverted.


## [4.20.59] – 2026-06-18

### Changed (Editor context menu)

- Attempted to hide the duplicate entry by dropping its C# `[ActionLayout]`. **Ineffective** — Studio reads the menu layout from `plugin.xml`, not the C# attribute. Superseded.


## [4.20.58] – 2026-06-18

### Fixed (Startup crash — reverts 4.20.57)

- **Trados Studio 2024 and 2026 no longer crash on startup.** Version 4.20.57 deleted the legacy "AI translate current segment" action to declutter the right-click menu. But Studio caches the editor command bar by action id, and on startup it tries to instantiate every cached action — so with the action type gone it threw *"Failed to add view command bar extensions for view 'EditorView'"* and exited before the editor loaded. This release restores that action registration, so the cached reference resolves again and Studio starts normally. The action is intentionally kept registered for exactly this backward-compatibility reason; the menu still shows the (harmless) duplicate entry. **If you installed 4.20.57, update to this build.**


## [4.20.57] – 2026-06-18

### Changed (Editor context menu)

- **Removed the duplicate "AI translate current segment" entry from the editor right-click menu.** It was a legacy alias that did exactly the same thing as **"Translate active segment" (Ctrl+T)** — translate the active segment using your Batch Translate settings — and only added clutter (and a confusing second Ctrl+T label). The single **"Translate active segment"** command remains, with Ctrl+T. The legacy action is also no longer listed under the plugin's keyboard-shortcuts settings. **(Superseded: this change crashed Studio on startup — see 4.20.58.)**


## [4.20.56] – 2026-06-18

### Added (Token usage & costs)

- **Persistent token-usage log.** Every AI call now records its token usage and cost to a monthly log file under `…\Supervertaler\trados\usage\usage-YYYY-MM.jsonl` — covering Translate, Batch Translate, Quick Launcher, AutoPrompt, Proofread and Chat, for every provider including custom / self-hosted endpoints. It stores metadata only (model, token counts, cost, project, file, language pair) and **never the prompt or response text**, so the file stays small and is safe to open in Excel or hand to an institution's monitoring team. On by default; switch it off in Settings → AI Settings.
- **Usage & Costs report.** A new "Usage & Costs report…" button (Settings → AI Settings) opens a window that totals your usage over a date range, grouped by project, client, model, provider, task type, day or month, with a "% from provider" column showing how much is measured vs. estimated. Export the detailed ledger to **CSV or Excel (.xlsx)** for billing or analysis.
- **Monthly budget (advisory).** Set a soft monthly spend limit (Settings → AI Settings). Once this month's logged cost reaches it, starting a batch translation shows a warn-and-continue prompt — it never blocks, and a budget of 0 disables it.
- **Real token counts for Gemini and Ollama.** These two providers previously fell back to a character-based estimate; their actual reported token counts are now captured, so their cost figures are accurate.

### Changed (Pricing)

- **One canonical price list.** Model prices now live in a single `pricing.json` (shared with Supervertaler Workbench). To re-price both products at once — for example to add your own self-hosted model's rate — copy it to `…\Supervertaler\pricing.json` and edit it there; each app prefers that shared copy over its bundled default. A custom model gains a cost figure simply by adding its id and rate to that file.


## [4.20.55] – 2026-06-17

### Fixed (Add Term · Chinese and other no-space scripts)

- **Adding a term with a Chinese (or Korean/Japanese) target no longer saves the whole segment.** When you selected part of the target — e.g. 挂车控制模块 ("trailer control module") — the term that got saved was the entire target segment, 挂车控制模块的更换. The Add Term / Quick-Add actions expand a partial selection out to word boundaries, but that expansion stopped only at **whitespace**, and Chinese has no spaces between words, so it ran to the segment edges and swallowed everything. Two causes: (1) the language auto-detection for "no auto-expand" only recognised Korean and Japanese, never Chinese; and (2) the **target**-side expansion ignored the language entirely (only the source side honoured it), so even Korean/Japanese targets were affected. Both are fixed: Chinese is now detected as a no-space script, and the target expansion now honours the project's target language. For these scripts the Add Term actions keep your exact selection. (Chinese has no spaces — and therefore no word boundaries to expand to — so selecting the exact characters you want is the intended workflow, not a temporary limitation; a general word segmenter would tend to over-split multi-character technical terms and pick the wrong span.) As a side benefit, detecting Chinese also enables CJK suffix-tolerant term matching, so Chinese terms highlight more reliably in TermLens. (Reported by a user adding Chinese terminology.)


## [4.20.54] – 2026-06-16

### Fixed (MultiTerm · AI)

- **The MultiTerm "AI" tick now survives a Trados restart.** A MultiTerm termbase ticked for AI in Settings → Termbases would silently revert to unticked after reopening Trados, so the AI stopped seeing the terminology until you re-ticked it. The tick is stored per-project, but the per-project save only ran when the Editor view part had already tracked the active project — which isn't the case when Settings is opened from the AI Assistant panel (the same path 4.20.53 made the row visible from). The tick then lived only in the global settings and was overwritten by the empty per-project overlay on the next restart. The current-project lookup now falls back to the Editor's active document, so the per-project save always runs and the choice persists. (Fixes #36.)

### Changed (MultiTerm · internal)

- **MultiTerm termbase synthetic IDs are now derived from a stable hash of the file path** (FNV-1a) instead of `String.GetHashCode()`. `GetHashCode` is documented as unsafe to persist — it varies across .NET runtimes and between 32-bit and 64-bit processes — so the previous IDs, used as the persistence key for each termbase's AI / enabled state, would not survive a move to a different runtime or bitness. Hardening only; current behaviour is unchanged on the existing .NET Framework build.


## [4.20.53] – 2026-06-15

### Fixed (MultiTerm)

- **An attached MultiTerm (.sdltb) termbase now shows in the Termbases tab even when Settings is opened without the editor in focus.** The grid's MultiTerm list previously came only from the live TermLens editor view part, so if the Settings dialog was opened from the AI Assistant panel, or with no document active in the Editor, an attached .sdltb (and its "AI" checkbox) would silently not appear. The Settings dialog now falls back to detecting the active project's MultiTerm termbases directly, so the row appears whenever a .sdltb is attached, however Settings was reached. (Investigating #36.)


## [4.20.52] – 2026-06-15

### Added (diagnostics)

- **Opt-in diagnostic logging.** Settings → General → Diagnostics → "Enable diagnostic logging" writes a detailed debug trace to `…/trados/logs/diagnostic.log`, with the path shown in the dialog and "Open log folder" / "Open log file" / "Clear log" buttons. Off by default. Turn it on, reproduce a problem, and send the log — it records, among other things, exactly why a native MultiTerm (.sdltb) termbase is or isn't picked up (project termbase configuration, language-index mapping, how many terms the Supervertaler .db and the MultiTerm index load, and any fallback). Aimed at issue #36.


## [4.20.51] – 2026-06-14

### Changed (AI · termbases)

- **Native MultiTerm (.sdltb) termbases are now opt-in for the AI, the same as Supervertaler's own .db termbases.** Previously a MultiTerm termbase attached to your Trados project could have its terms sent to the AI by default. Now its **AI** column (Settings → Termbases) starts unticked, and its terms are included in AI prompts (Chat, AutoPrompt, Batch Translate and Proofread) only when you explicitly tick it. Your Supervertaler .db termbases are unaffected. If you were relying on a MultiTerm termbase feeding the AI, just tick its AI box once after updating.


## [4.20.50] – 2026-06-14

### Added

- **Usage stats now also include the Windows accessibility text size and the in-app UI-scale setting**, alongside the display scale added in 4.20.49. Together the three show the full picture of how large the UI renders for each user (Windows DPI × text size × the in-app slider). Still opt-out, still nothing identifying.


## [4.20.49] – 2026-06-14

### Fixed (high-DPI / Windows display scaling)

- **The Settings dialog now lays out cleanly at any Windows display scale.** A user on a 4K display at 175% display scaling plus 175% text scaling reported clipped buttons, overlapping rows and cut-off text, mainly in the Settings tabs (issue #37). Every tab — General, Termbases, AI Settings, Prompts, Licence and Backup — was rebuilt with `TableLayoutPanel`-based layout, AutoSize buttons and the plugin's `UiScale` system instead of absolute pixel positioning, so labels, fields and buttons size to their (scaled) content and reflow automatically. Nothing clips or overlaps at 100/125/150/175% or any custom scale, and the in-app **UI scale** setting now affects the Settings dialog too. The Batch Operations pane's mode toggle and Scope dropdown, and the editor pencil/glyph buttons, were fixed the same way.

### Added

- **Anonymous usage stats now include the Windows display scale** (e.g. "175"), so the share of users on high-DPI scaling can be seen at a glance. Still opt-out, still nothing identifying — just the DPI percentage alongside the existing OS / Studio version / locale.


## [4.20.48] – 2026-06-12

### Fixed (AI · inline tags)

- **Hardened inline-tag handling against models that mangle tags.** A user reported that Mistral Large recently began emitting empty inline-tag pairs — an opening tag immediately followed by its own closing tag, with the translated text left outside — instead of wrapping the translated words. Two defences: (1) the AI translation prompt now explicitly forbids empty tag pairs and reminds the model of the exact tag format (with a wrong/right example); (2) the tag-placeholder parser is now whitespace- and case-tolerant, so a tag that drifts slightly from the canonical form (a stray space inside it, or the wrong letter case) is still recognised and reconstructed — or cleanly stripped — instead of leaking into the target as literal text. Helps every provider, not just Mistral.


## [4.20.47] – 2026-06-12

### Added (TermLens: Korean / Japanese particle handling)

- **TermLens now recognises terms in Korean and Japanese even when a grammatical particle is attached to the noun.** Previously matching was whole-token, so a clean term like 값 ("value") or 제2 전압 값 ("second voltage value") would not highlight in 값으로 / 제2 전압 값을 / …, because the trailing particle made the segment token differ — and adding a term auto-expanded the selection to the whole token, capturing the particle (saving 장치의 instead of the intended 장치). Both sides are now particle-aware:
  - **Matching** is suffix-tolerant: a single CJK token matches the longest term that is a prefix of it (값 ↦ 값으로), and a multi-word term matches when its final CJK token is a prefix of the segment token (제2 전압 값 ↦ 제2 전압 값으로), with the highlight spanning the attached particle so no text is dropped.
  - **Adding a term** keeps your exact selection instead of expanding to the whitespace word, so the bare noun is saved. (F2 still expands explicitly when you want it.)
  - Controlled by a new **Particle matching** setting (Settings → Termbases): **Auto** (default — on for Korean/Japanese source), **Always on** (e.g. Chinese or another language), or **Always off**. Only CJK-script tokens are prefix-matched, so European languages are unaffected. Addresses issue #34.


## [4.20.46] – 2026-06-12

### Fixed (GPT-5.5 temperature)

- **GPT-5.5 now works – Test Connection and translation no longer fail with an "unsupported temperature" error.** OpenAI's GPT-5.5 only accepts the default temperature value and returns *"Unsupported value: 'temperature' does not support 0.3 with this model. Only the default (1) value is supported."* if any explicit temperature is sent. The plugin sent a fixed `temperature: 0.3` on every OpenAI request for non-reasoning models, so GPT-5.5 failed both the Test Connection check and any actual translation, while GPT-5.4 Mini (which does accept a custom temperature) worked. Models now carry a `SupportsTemperature` flag; GPT-5.5 is marked as temperature-locked, so the parameter is omitted for it across all request paths (Test Connection, single-segment, batch translate, and tool-use), and a heuristic covers any future full GPT-5.x model entered in the custom Model ID field. Reported in issue #36.


## [4.20.45] – 2026-06-12

### Changed (AI pane renamed)

- **The AI pane is called "Supervertaler Assistant" again.** The dockable pane that hosts Chat, Batch Operations, Import/Export and Reports had been shortened to bare "Supervertaler", which collided with the product name (Supervertaler for Trados) and read awkwardly in the documentation ("The Supervertaler supports …"). It is restored to "Supervertaler Assistant" – both the pane/dock-tab caption and the View menu entry – so the pane is clearly distinct from the product and the help docs match. No functional change.


## [4.20.44] – 2026-06-11

### Added (AI termbase selection + AutoPrompt safeguards)

- **Choose which termbases the AI sees with a new "AI" column on the Termbases tab.** Opting a termbase into AI prompts (Chat, AutoPrompt, batch operations) used to be a separate checklist on the AI Settings tab, which was easy to miss: enabling a termbase for term recognition did not include it in the AI context, and the two controls lived on different tabs. The AI-inclusion toggle is now an "AI" column in the termbase grid, right alongside the existing Read / Write / Project toggles, so it sits where you manage termbases. Click the column header to select or deselect all, and MultiTerm termbases can be toggled here too. The old AI Settings checklist has been retired in favour of a pointer to the new column.
- **AutoPrompt warns before generating a prompt from a large termbase.** A small, project-focused termbase produces a far better AutoPrompt glossary than a big general one; a large termbase injects many incidental whole-word matches – common words that merely happen to appear in the document. When the termbase(s) enabled for AI hold more than 50 terms, AutoPrompt now shows a dismissible warning recommending a compact, project-specific termbase before it spends a generation.
- **AutoPrompt records which termbase terms it injected.** Each run now logs the exact list of terms TermScan passed into the prompt – and how many, from which termbases – to the diagnostic log, so the terminology that reaches the AI can be audited after the fact.

### Fixed (AutoPrompt: TermScan false positives)

- **AutoPrompt no longer pulls in irrelevant terms through single-character abbreviations.** TermScan filters a termbase down to the terms that appear in the document, but it also matched a term's abbreviations as whole words with no minimum length, so an entry carrying a one-letter abbreviation (for example "S" for a chemistry term) matched any stray "S" in the text and injected the whole entry into the glossary. The relevance filter now ignores single-character candidates, so this kind of noise no longer reaches the prompt; legitimate two-character abbreviations (UI, API, ID …) are unaffected.


## [4.20.43] – 2026-06-07

### Fixed (Shared TM bridge — language names)

- **Bridged TMs no longer reverse matches when a row's languages are stored as names rather than codes.** v4.20.42 orients each returned row by its own `source_lang`/`target_lang`, but that comparison only understood ISO codes (`en`, `nl-BE`). A Supervertaler TM can hold rows tagged with human-readable language *names* ("English"/"Dutch") – e.g. from segments saved by a project whose language pair was held as a display name. Those rows matched neither side of the per-row check, so orientation fell back to the TM-level direction and swapped a row that was already correctly oriented, inserting the source language into the target. `CulturesCompatible` now normalises through `LanguageUtils` (the same name/code comparator used for termbase direction), so "English" ≡ "en" ≡ "en-US" and "Dutch" ≡ "nl" ≡ "nl-BE". Studio again sees correct project-source → project-target hits. (Workbench v1.10.253 additionally normalises languages to codes on write, so new rows avoid the mixed tagging in the first place.)


## [4.20.42] – 2026-06-02

### Fixed (Shared TM bridge — per-row direction)

- **Bridged TMs now return matches even when the TM mixes both directions.** v4.20.41 made a bridged TM attach to a project regardless of its declared direction, but it then assumed the *whole* TM was stored in one direction and searched only one column — so segments saved into the TM in the *opposite* direction to its declared one were silently missed. (Real case: an nl→en TM into which en→nl segments had been saved from an en→nl project — the lookup searched the English-target column and never found the rows whose English sat in the *source* column.) A Supervertaler TM's direction is a **per-row** property, so the bridge now (1) searches **both** the source and target columns for exact and concordance lookups, and (2) orients **each returned row individually** by its own `source_lang`/`target_lang` (on the base language, en≡en-US, nl≡nl-BE), falling back to the TM-level direction only when a row's languages are missing. Studio still always sees correct project-source → project-target hits.


## [4.20.41] – 2026-06-02

### Fixed (Shared TM bridge)

- **Bridged Supervertaler TMs now attach to a project regardless of their stored direction.** A TM bridged from Supervertaler Workbench was only offered to a Studio project when it was stored in the *exact same* direction as the project — so a Dutch→English TM was invisible to an English→Dutch project, with the misleading "None of the bridged Supervertaler TMs match this project's language pair" message. Matching is now **direction-agnostic and on the base language** (so `en` matches `en-US`, `nl` matches `nl-BE`, in either orientation): a TM is offered whenever it covers the project's two languages, whichever way round it was created. Crucially, the lookup follows through — when a TM is attached in the reverse direction, exact and concordance searches query the TM's *other* column and swap source/target on the way out, so Studio still sees correct project-source → project-target hits rather than empty or backwards results. (Regional-variant matching was already correct; the gap was the direction check.)


## [4.20.40] – 2026-05-29

### Changed (Claude Opus 4.8)

- **Upgraded the curated Claude flagship from Opus 4.7 to Opus 4.8** (`claude-opus-4-8`), Anthropic's new most-capable model (released 28 May 2026, 1M context, same $5 / $25 per 1M token pricing as 4.7). Updated in both the Claude and OpenRouter (`anthropic/claude-opus-4.8`) lists, and in the cost estimator. The Claude list stays a clean three: **Sonnet 4.6** (recommended), **Haiku 4.5** (fast/cheap), **Opus 4.8** (highest quality). Anyone still pointing at Opus 4.7 keeps working via the Model ID field.


## [4.20.39] – 2026-05-29

### Added (SuperSearch: live filter)

- **New "Live" toggle in SuperSearch.** When ticked, typing in the Src/Tgt boxes narrows the *current* results in place (instantly, in memory) instead of re-running a full file/TM search on every keystroke — useful for drilling into a large result set. Press Enter / Search to run a fresh full search and refresh the set. The status bar shows "N of M result(s) (live filter)". Honours the Case/Regex/Word toggles; an incomplete regex while typing simply matches nothing until it's valid.


## [4.20.38] – 2026-05-29

### Changed (SuperSearch: separate Source and Target boxes)

- **SuperSearch now has separate Source and Target search boxes** instead of one box plus a Source/Target/Both scope dropdown. Fill the **Src** box to search source text, the **Tgt** box to search target text, or **both** to find segments whose source matches one term *and* whose target matches another (memoQ-style concordance). Each box's term is highlighted in its own column and preview pane. Find & Replace continues to operate on the target text, using the Target box. (Progressive/live filtering is a follow-up.)


## [4.20.37] – 2026-05-29

### Changed (Clipboard Mode)

- **Stronger output-format instructions for Clipboard Mode translation.** Some web LLMs (notably DeepSeek's web chat) reformatted the reply into a bare list and dropped the "Segment N" headers, so the result couldn't be re-imported into Trados. The clipboard prompt now insists, explicitly, that every "Segment N" header be kept in order with both language lines, and that the output not be turned into a list, table, or prose – because the headers are what the importer uses to map translations back to segments.


## [4.20.36] – 2026-05-28

### Changed (Licence)

- Licence-file save failures are now logged instead of being silently ignored, so a trial that can't persist its start date no longer goes unnoticed.


## [4.20.35] – 2026-05-28

### Changed (Licence)

- Trial-period tracking now also resists system-clock changes.


## [4.20.34] – 2026-05-28

### Changed (Licence)

- More reliable trial-period tracking across reinstalls and data-folder moves.


## [4.20.33] – 2026-05-27

### Changed (Shared TM Bridge: per-hit TM attribution)

- **Each match now identifies which bridged TM produced it.** Previously, every result returned by the bridge stamped `OriginSystem = "Supervertaler"`, so when a user had several bridges attached (e.g. Acme + PATENTS), the grey origin strip under each hit in the Translation Results pane just said "Supervertaler" with no way to tell which TM the hit came from. Now the OriginSystem reads e.g. "Supervertaler: Acme (PROJ-001)" or "Supervertaler: PATENTS", matching the names shown in the project's TM list.


## [4.20.32] – 2026-05-27

### Fixed (Shared TM Bridge: critical – wrong "100%" matches no longer inserted)

- **`SearchTranslationUnit` now does true exact-source lookup instead of concordance.** This is a serious correctness fix. Trados Studio's per-segment matching in the editor and in batch tasks routes through `SearchTranslationUnitsMasked → SearchTranslationUnit`, NOT `SearchSegment` (which the SDK docs imply but Studio doesn't actually call in document context). Prior builds (v4.20.26 – v4.20.31) wrongly implemented `SearchTranslationUnit` as an FTS5 concordance search, then scored every substring hit as 100%. The practical effect: a segment with source "GEDETAILLEERDE BESCHRIJVING" would FTS5-match any TM row whose source or target contained "gedetailleerde" OR "beschrijving" – including a totally different row like "De gedetailleerde beschrijving beschrijft huidige uitvinding in zijn voorkeurdragende uitvoeringsvormen" / "The detailed description describes the preferred embodiments of the present invention" – and Studio would insert and auto-confirm that wrong translation at 100%.
- **Now**: `SearchTranslationUnit` extracts the TU's source segment, runs the same byte-exact `WHERE source_text = $src` lookup as `SearchSegment`, and returns only true 100% matches. Concordance lives where it always should: `SearchText`.
- **Query/result diagnostic logging.** Both `SearchSegment` and `SearchTranslationUnit` now log the query text plus the exact-match count to `%TEMP%\supervertaler-tm-bridge.log`. This made the previous build's bad behaviour easy to diagnose ("`SearchTranslationUnitsMasked` is hot, `SearchSegment` is cold") and gives quick feedback that the fix is producing the right exact-match counts going forward.

### Action for users on prior builds

- **Inspect any segments confirmed against bridged TMs in v4.20.26 – v4.20.31** – their targets may be totally unrelated text drawn from a same-token concordance hit. Filter by "Translated" + "Confirmed by Supervertaler TM" and re-review. The 100% match icon was not trustworthy in those builds.


## [4.20.31] – 2026-05-27

### Fixed (Shared TM Bridge: ACTUAL root cause of the "Object reference" NRE)

- **`SearchResults.SourceSegment` is now guaranteed non-null on every code path.** That was the bug. Studio's internal `SearchResultsMerged.CopyFromSearchResults(SearchResults other)` does `base.SourceSegment = other.SourceSegment.Duplicate();` with **no null guard** – decompiled from `Sdl.LanguagePlatform.TranslationMemory.dll`. Any provider that returns a default-constructed `new SearchResults()` (which has `SourceSegment = null`) makes Studio's `Cascade.MergeSearchResults` throw NullReferenceException deep inside `SegmentAndSubsegmentSearchResultsMerged..ctor`. v4.20.26 through v4.20.30's `SearchText`, `SearchTranslationUnit`, and (for masked-out slots) `SearchSegmentsMasked` / `SearchTranslationUnitsMasked` all returned such bare SearchResults objects – every concordance call or batch operation that hit those paths bombed Studio with the generic "Object reference not set to an instance of an object." dialog.
- **New `NewSearchResults` / `NewSearchResultsFromText` helpers** always pre-populate `SourceSegment` with either a duplicate of the input Segment or a fresh empty Segment stamped with the LD's source culture. Every search method on the LanguageDirection now routes through them. Cascade can now merge our results without choking.
- **`SafeLog` helper for entry logging.** The original `SearchSegment` entry log evaluated `segment.ToPlain()` inside string concatenation – if `ToPlain()` ever threw (it can, on certain tag-only segments), the whole log line threw before writing, the method exited abnormally, and no entry log appeared in `%TEMP%\supervertaler-tm-bridge.log`. `SafeLog` wraps the call in try/catch so a misbehaving stringification can't take down the method that's trying to record it.
- **Entry logging on the remaining concordance methods** (`SearchText`, `SearchTranslationUnit`, `SearchTranslationUnits`, `SearchTranslationUnitsMasked`) – the v4.20.30 build missed these, which is why the "no entries" log was so confusing. With Studio routing concordance and batch searches through them, the entry logs should now appear when the user looks something up in the Concordance window.

### Investigation notes

- The smoking gun was Studio's own log at `%AppData%\Trados\Trados Studio\Studio18\logs\Trados Studio_*.log`, which contained the full stack trace: `Cascade.ExecuteSearchCommand` → `Cascade.MergeSearchResults` → `SegmentAndSubsegmentSearchResultsMerged..ctor(SegmentAndSubsegmentSearchResults results)` → `SearchResultsMerged.CopyFromSearchResults(SearchResults other)` → NRE at `other.SourceSegment.Duplicate()`. Decompiling `Sdl.LanguagePlatform.TranslationMemory.dll` with ilspycmd confirmed `CopyFromSearchResults` does no null check on `SourceSegment`.
- Three prior builds attacked the wrong layer: v4.20.27 added defensive null-handling in our own search methods; v4.20.29 wrongly inferred `SupportsTranslation=true` was the problem (it's the gate Trados uses to even call search); v4.20.30 added entry logging that revealed the call patterns but not the offending field.


## [4.20.30] – 2026-05-27

### Changed (Shared TM Bridge: diagnostic build – reverts v4.20.29's wrong fix)

- **`SupportsTranslation` reverted to `true`.** v4.20.29's hypothesis that this flag meant "MT-style provider" was wrong. The %TEMP%\supervertaler-tm-bridge.log from v4.20.29 made the actual semantics clear: per-segment, Trados queries `LanguageDirection.TranslationProvider` → `Provider.TranslationMethod` → `Provider.SupportsSearchForTranslationUnits` → `Provider.SupportsTranslation` and only proceeds to call `SearchSegment` when that last flag is `true`. Setting it to `false` produced "The translation provider X does not support translation." warnings and zero lookups.
- **Entry logging on every method of the LanguageDirection** – all write methods (`AddTranslationUnit`, `AddTranslationUnits`, `AddOrUpdateTranslationUnits`, `AddTranslationUnitsMasked`, `AddOrUpdateTranslationUnitsMasked`, `UpdateTranslationUnit`, `UpdateTranslationUnits`) and the remaining search variants (`SearchSegments`, `SearchSegmentsMasked`) now log on entry. With `SupportsTranslation = true` back in place, the next failing build will show in the log exactly which method Studio calls between the capability poll and the (still missing) `SearchSegment ENTRY`. That's almost certainly where the residual "Object reference" NRE originates – Studio is probably calling a method we haven't routed cleanly.


## [4.20.29] – 2026-05-27

### Fixed (Shared TM Bridge: root cause of the "Object reference" NRE)

- **`SupportsTranslation` now reports `false`.** The previous v4.20.26/v4.20.27 builds advertised the bridge as `SupportsTranslation = true`, which in the Trados SDK signals an **automated-translation (MT-style) provider** rather than a TM lookup provider. With that flag on, Trados Studio invoked an MT-engine code path that doesn't exist for this provider, and the resulting NRE bubbled up to the UI as "An error has occurred while using the translation provider Supervertaler TM: <name>: Object reference not set to an instance of an object." on every segment, with no entry in our own log because the failure happened *inside* Trados before `SearchSegment` was reached. Stock SDLTM providers report `false` for the same reason; the bridge now matches that convention.
- **Comprehensive property-getter instrumentation** on both `SupervertalerTmProvider` and `SupervertalerTmLanguageDirection`. Every capability flag, every identity property, and every culture accessor logs on read to `%TEMP%\supervertaler-tm-bridge.log`. If a future build sees the bridge polled in a tight loop without ever reaching search, the log now pinpoints exactly which read Studio is making before giving up.

### What translators see

- Bridged Supervertaler Workbench TMs now actually return hits in the Trados TM-results pane and Concordance window. The same loose-locale matching from v4.20.26 (so `nl` matches `nl-NL`) applies.


## [4.20.27] – 2026-05-27

### Fixed (Shared TM Bridge: defensive null-handling + diagnostics for "Object reference" errors)

- **First testing of the v4.20.26 SupervertalerTmProvider against a real Trados Studio session surfaced "An error has occurred while using the translation provider Supervertaler TM: <name>: Object reference not set to an instance of an object." errors on every lookup,** with no usable stack trace in Studio's Messages panel. This release adds the infrastructure to diagnose and prevent that class of failure.
- **New `TmBridgeLog`** (file logger at `%TEMP%\supervertaler-tm-bridge.log`). All provider-side error paths now log with full stack traces before swallowing the exception. Append-only with size-capped rotation.
- **Defensive null-handling in `SearchSegment`**: previously dereferenced `segment.Duplicate()` before null-checking, which would throw NRE on Studio's pre-flight calls. Now null-safe at every level; falls through to an empty `SearchResults` on any failure instead of throwing.
- **`SearchText` and `SearchTranslationUnit`** get the same treatment – defensive null handling, instrumented with `TmBridgeLog` so failures are diagnosable.
- **New `TryBuildSearchResult` helper** wraps the per-row `TranslationUnit` construction in try/catch. One bad row in the result set no longer poisons the rest of the batch; it's logged and skipped.
- **Write methods (`AddTranslationUnit`, `UpdateTranslationUnit`, etc.) no longer throw `NotSupportedException`.** Although `IsReadOnly = true` and `SupportsUpdate = false` should keep Trados from ever calling these, batch-tasks pipelines (notably "Update Main Translation Memories") have been observed to call them speculatively, and the thrown exception bubbles up as a generic provider error. They now return a safe empty `ImportResult` (no-op).

### Changed

- **Sv icon on the "Add Supervertaler TM" picker dialogue** + on the project's translation-provider list (via `TranslationProviderDisplayInfo.TranslationProviderIcon`). Picks up the same shared icon every other plugin dialogue already uses.
- **Culture-pair empty-state logging.** If `BuildTranslationUnit` is ever called with an empty source/target culture, a warning lands in the log – this lets us catch language-pair-filtering bugs without silent fallout.


## [4.20.26] – 2026-05-27

### Added (Shared TM Phase 2: read-only access to Supervertaler Workbench TMs)

- **New translation-provider plugin: "Supervertaler TM Bridge".** Trados can now attach individual TMs that live in Supervertaler Workbench's shared SQLite database (`supervertaler.db`) directly, without any TMX export / import dance. Phase 2 of the Shared TM work tracked in #31, building on the Workbench-side opt-in flag shipped in Workbench v1.10.212.
- **Discovery via Trados' standard "Add → Translation provider" menu.** A new "Supervertaler TM" entry shows up alongside the standard SDLTM, GroupShare, etc. options. Clicking it opens a picker dialogue listing every TM the user has flagged "Bridge" in Workbench's TMs tab, filtered to those matching the current project's language pair (loose match: bare `nl` matches `nl-NL`, etc.). Tick one or more to attach.
- **Live data.** Both products read and write the same `supervertaler.db`. Adding a new entry to a bridged TM via Workbench's editor surfaces in Trados on the next lookup, no sync step. Conversely, Trados's read flows through to anything Workbench shows.
- **Exact match (100%) and concordance search supported.** Studio's TM-results pane shows exact hits with the standard 100% chip; the Concordance window searches source-side AND target-side via the FTS5 index Workbench already maintains.

### Phased scope (this is the read-only half)

- **Read-only in v1.** `SupportsUpdate = false`, `IsReadOnly = true`. Add/Update/Delete methods throw `NotSupportedException` defensively – any consumer ignoring the read-only flag fails loudly rather than silently dropping data. Write-back lands in Phase 3.
- **No fuzzy matching yet.** `SupportsFuzzySearch = false`. The provider returns *only* 100% matches; sub-100 fuzzy comes in Phase 3 once a Levenshtein-on-candidates scorer is in place. For now, fuzzy lookups fall through to any other providers attached to the project (e.g. the user's main SDLTM).

### Implementation notes

- New `Core/TmReader.cs` mirrors the existing `TermbaseReader.cs` shape (read-only SQLite via `Microsoft.Data.Sqlite`, no native-DLL interop). It only reads TMs where `bridged_to_trados = 1`, so freelancers with multiple-client TM libraries don't see cross-client leakage in a Trados session opened on a specific project.
- New `TranslationProviders/SupervertalerTmProvider.cs` + `SupervertalerTmLanguageDirection.cs` + `SupervertalerTmProviderFactory.cs` + `SupervertalerTmProviderWinFormsUI.cs` implement the Trados-side surface area. URI scheme is `supervertaler-tm:///<tm_id>` (using the stable string id Workbench stores in `translation_units.tm_id`).
- plugin.xml gains two `<extension>` entries – one for the factory, one for the WinForms UI – using the `TranslationProviderFactoryAttribute` / `TranslationProviderWinFormsUiAttribute` types from `Sdl.LanguagePlatform.TranslationMemoryApi`.

### What translators see

- **Workbench**: tick the orange "Bridge" checkbox next to a TM in the TMs tab. That's it.
- **Trados**: Project settings → Translation Memory and Automated Translation → Use → Add → Supervertaler TM. Pick the TM(s) from the dropdown.
- **The TM behaves like any other Trados TM** after that – exact matches and concordance hits show up in the standard panes, attribution shows "Supervertaler" as the origin system.


## [4.20.25] – 2026-05-26

### Changed

- **"Term Picker" renamed to "TermPicker"** (one word, Pascal-cased, no hyphen, no space). Pure naming cleanup aligned with Supervertaler Workbench v1.10.205 — both products now use the same name for the same surface.
- **Architecture clarification.** TermLens and TermPicker are now treated as **sibling surfaces** sitting on top of termbases, rather than the old "TermLens — Term Picker" framing where the picker felt like a sub-mode of TermLens. Underneath both sits the termbase layer; TermLens shows matches *in context* (inline chips anchored to source terms — the invention), TermPicker shows them as a flat sortable list with keyboard-driven Enter-to-insert. Same data, different ergonomics.
- **Window title** of the modal dialog: `TermLens — Term Picker` → `TermPicker`.
- **About dialog** keyboard-shortcuts table: "Term Picker (memoQ-style)" / "Term Picker (alternative)" → "TermPicker (memoQ-style)" / "TermPicker (alternative)".
- **Help URL slug** `trados/termlens/term-picker/` → `trados/termlens/termpicker/`. Old URL 301-redirects to the new one via `public/_redirects` on the help site, so existing bookmarks (and F1 from older installed plugin versions where the old slug is still baked into HelpSystem.cs) keep working. The C# field name `HelpSystem.Topics.TermPickerDialog` is unchanged — only its URL value moved.
- **README, HELP-LINKS, CLAUDE.md, and internal code comments** updated to use "TermPicker" everywhere.

### Kept (internal, deliberately not renamed to avoid breaking user data/customisations)

- File `Controls/TermPickerDialog.cs` and class `TermPickerDialog` — already correct camelcase
- File `TermPickerAction.cs` and class `TermPickerAction`
- Model class `TermPickerMatch`
- Plugin.xml action ID `TermLens_TermPicker` (the Ctrl+Shift+P binding) — would break custom shortcut remappings if changed
- Settings property names `TermPickerWidth`, `TermPickerHeight`, `TermPickerColumnWidths` — would wipe persisted layout
- Historical CHANGELOG entries and RWS AppStore release notes — left as-is, they are historical record

### Notes

The rename is mostly text. No behaviour changes. F1 from the dialog still opens the help page (now under the new slug); existing custom shortcut bindings for the Ctrl+Shift+P action still trigger the same code path; persisted dialog size/column widths still load. If you've been using "Term Picker" verbally, "TermPicker" is what we'll call it from here on.


## [4.20.24] – 2026-05-26

### Fixed

- **Bracketed `[SEGMENT NNNN]` Markdown re-import: segments with empty target on export now round-trip correctly.** A user with a multi-file project where many segments were untranslated noticed that some edits made to those rows didn't land in Trados after re-import. Root cause: the lang-line regex used `\s*` after the colon for trailing whitespace, but .NET treats `\s` as matching newlines too — so on a line like `NL: \n` the engine would greedily consume the newline AND the next line, capturing `Status: Unspecified` (etc.) as the body instead of the intended empty string. Subsequent re-import then either wrote the wrong content back or compared against the wrong baseline. Tightened the regex to `[ \t]*` so matching stays on the current line. Also made target selection more tolerant: the parser now picks the **last** non-`Status` lang-line in each block as the target (rather than the second), so a proofreader who edits by inserting an extra `NL: …` line after the empty placeholder still gets their actual translation captured rather than the empty line.

### Added

- **New confirmation-status filter on the Import/Export tab.** Six checkboxes (one per Trados `ConfirmationLevel` value: *Unspecified*, *Draft*, *Translated*, *Approved (translation)*, *Approved (sign-off)*, *Rejected*) let the user restrict the export to only segments in selected statuses. All checked (the default) = no filter, every segment included — matches pre-v4.20.24 behaviour. Unticking any subset narrows the export accordingly. Composes orthogonally with the existing **Include locked segments** option and the multi-file file-selection list.


## [4.20.23] – 2026-05-25

### Changed

- **Cleaned up stale license-tier documentation** to match Lemon Squeezy's current single-product layout. The store now sells exactly one product, **Supervertaler for Trados**, and has done since the multi-tier model was dropped in v4.18.48. Three things touched, no behavioural change (licence validation has been variant-name-agnostic since v4.18.48):
  - Removed three dead constants from `LicenseManager.cs` (`VariantTier1 = "TermLens"`, `VariantTier2 = "TermLens + Supervertaler Assistant"`, `VariantAssistant = "Supervertaler Assistant"`). They were never referenced — `MapVariantToTier()` ignores the variant name entirely and always returns `LicenseTier.Licensed` for any valid key.
  - Updated `MapVariantToTier()`'s XML doc comment to note the single-product Lemon Squeezy layout.
  - Updated `LicenseInfo.VariantName`'s XML doc comment, which previously documented the legacy "TermLens → Tier1" mapping table.
- Old cached `license.json` files from previous installations (with `VariantName` set to any of the legacy strings) still deserialise cleanly and unlock everything.


## [4.20.22] – 2026-05-25

### Fixed

- **Panel rename to "Supervertaler" finally takes effect in Trados.** v4.20.21 changed the `[ViewPart]` C# attribute's `Name` field, but Trados ignores that — it reads the panel title from `<property name="Name">…</property>` inside `Supervertaler.Trados.plugin.xml`. That file is generated from another source (Trados' build tooling) and the auto-rename didn't propagate. Patched the value in-place. The Trados panel header + dockable-tab strip now show **Supervertaler**.


## [4.20.21] – 2026-05-25

### Changed

- **Renamed the dockable panel from "Supervertaler Assistant" to "Supervertaler"** in every user-visible string: the Trados panel title (set via the `ViewPart` attribute), the chat-bubble name (`Supervertaler` instead of `Supervertaler Assistant`), the help-menu top item (`Supervertaler Help`), the legacy-memory-bank first-run dialog header, the "Saved on ... from Supervertaler" memory-bank line, and the QuickLauncher context-menu action (`Send to Supervertaler`). Internal class names and the Lemon Squeezy licence-variant constants are unchanged (variants are contractually bound to existing subscriptions; touching them would invalidate already-issued licences).
- **Renamed the in-panel tab "Import / Export" to "Import/Export"** (no spaces around the slash). Updated the tab title, the tab's heading label, and the matching help-menu item ("Import/Export Help").


## [4.20.20] – 2026-05-25

### Added

- **New "Bracketed `[SEGMENT NNNN]`" layout** for Markdown exports. Matches the Supervertaler Workbench's "AI-readable" segment-export style:
  ```
  [SEGMENT 0001]
  EN: <b>MASHUP APPLICATION PROCESSING SYSTEM</b>
  NL: <b>MASHUP-APPLICATIEVERWERKINGSSYSTEEM</b>

  [SEGMENT 0002]
  ...
  ```
  One block per segment, blank-line separated, with 2-letter ISO language codes labelling each language line. Re-importable just like the other layouts — the bracketed segment-number is the anchor. Some LLMs reportedly handle this format more reliably than markdown tables.
- **Available as the 4th option in the Layout dropdown** on the Import / Export tab: *Bracketed [SEGMENT NNNN] (AI-friendly, Markdown only)*. The Markdown renderer + importer both understand it; DOCX and HTML fall back to Stacked source-on-top (the bracketed format only makes sense as plain text).
- **Multi-file affordance preserved**: when the bilingual file spans multiple source files, a `## 📄 File: <name>` heading appears before the first segment of each new file, same as the other stacked layouts.


## [4.20.19] – 2026-05-25

### Added

- **Markdown bilingual export now shows per-file attribution in multi-file projects.** A user noted the Markdown version lacked any indication of which file each segment came from, while the DOCX version had a dedicated File column + yellow section-break rows between files. Brought to parity:
  - **Table layout**: when the export spans more than one source file, the table grows a 6th **File** column and a section-break row (`| | **📄 File: <name>** | | | | |`) precedes the first segment of each new file. Single-file exports are unchanged.
  - **Stacked layouts** (source-on-top / target-on-top): a `## 📄 File: <name>` heading appears before the first segment of each new file.
- **Re-import handles the new 6-column Markdown table** transparently. The importer auto-detects 5- vs 6-column rows by cell count and shifts its parser to read Status / Notes from the right offset. Older 5-column files (and 6-column files with extra layout) keep parsing cleanly.


## [4.20.18] – 2026-05-25

### Added

- **Locked segments are now visible and filterable in the Import / Export tab.** Two related behaviours:
  - **New checkbox: "Include locked segments (🔒 marked in Status column)"** — default ON (matches pre-v4.20.18 behaviour). When ON, locked segments are exported alongside everything else and get a **🔒** prefix in the Status column (e.g. `🔒 ApprovedTranslation`) so the proofreader sees at a glance which rows won't round-trip back to Trados. When OFF, locked segments are skipped entirely — useful on large projects where most segments are locked-approved and the proofreader should only see what's still editable. A user with a 14-file, 7308-segment project noted that locked segments came through without any indicator, leading them to edit segments that couldn't be saved back to Trados.
  - **Re-import now actually refuses to overwrite locked segments.** `SnapshotLockedSegments()` was a stub returning an empty set since v4.20.7 — meaning re-import would happily write back to locked segments. Now it walks the active document and reads `pair.Properties.IsLocked` for real, populates the lookup, and `BilingualImporter`'s existing `isWriteable` predicate uses it to skip locked segments (they show up under "other issues" in the re-import summary).
- **Locked flag is persisted in the sidecar manifest** as `is_locked: true/false` per segment, so older manifests (without the field) parse cleanly as `false`.


## [4.20.17] – 2026-05-25

### Fixed

- **Help button on the Import / Export tab now opens the right page.** v4.20.16 put a brand-new "?" inside the Import / Export tab content (in addition to the panel-level "?" that's existed in the top-right of the Supervertaler Assistant pane for ages), AND the panel-level button still routed to the Reports help page because its tab-index switch hadn't been updated when Import / Export was inserted between Batch Operations and Reports (case 2 was still mapped to Reports, case 3 to SuperSearch, etc.). Two fixes in one commit:
  - Removed the in-tab "?" button — the panel-level one is the right home for tab help, matching every other tab in the pane.
  - Added `HelpSystem.Topics.ImportExport` and slotted it into both `OnHelpDropdown` and the F1 keyboard handler at the correct tab index (2), shifting Reports → 3 and SuperSearch → 4.

## [4.20.16] – 2026-05-25

### Added

- **Contextual "?" help button on the Import / Export tab.** Top-right corner, anchored to the right edge so it follows panel resizes. Click to open the dedicated [Import / Export help page](https://help.supervertaler.com/trados/import-export/) on help.supervertaler.com — covers formats, layouts, semantic tag markers, multi-file mode, round-trip rules, and the re-import workflow in one place.

## [4.20.15] – 2026-05-25

### Fixed

- **Import / Export tab now scrolls vertically on smaller screens.** A user reported that on laptop monitors the log textbox sat below the bottom of the panel and was unreachable. Set `AutoScroll = true` on the control + computed `AutoScrollMinSize` from the final content height so the scrollbar appears whenever the tab is shorter than its content. The log textbox now has a fixed 160 px minimum height (instead of stretching to fill the panel) so the layout is well-defined regardless of viewport size.
- **Inline-formatting caveat label fits on big-monitor too.** Even at 72 px the four-line paragraph clipped its final phrase on wider monitors (where the text re-flowed differently). Dropped the "rendered in matching bold/italic/underline for preview" sentence (it's already obvious from looking at the table) and the label is now 56 px / three lines.

## [4.20.14] – 2026-05-25

### Fixed

- **Inline-formatting caveat on the Import / Export tab no longer truncates.** The descriptive blurb above the format/layout pickers was clipped at "Text inside semantic markers is rendered" because the label's height (46 px) couldn't hold the full four-line paragraph. Bumped to 72 px and tightened the wording.

## [4.20.13] – 2026-05-25

### Fixed

- **Re-import diff no longer over-reports changes for tag-bearing segments.** A user noted that after exporting 1267 segments and editing literally one word in the bilingual DOCX, the re-import dialog claimed 124 changes to apply. Root cause: the export side serialised each target through `SegmentTagHandler.Serialize()` + `BilingualTagNamer.ApplySemanticNames()` (so cells contain `<b>SEVT</b>` etc.), but the diff-comparison side fetched the current target via plain `pair.Target.ToString()` (which yields `SEVT`). Every segment whose target contained any inline cf-bold / cf-italic / cf-underline formatting therefore read as "changed" even when the proofreader hadn't touched it. `SnapshotCurrentTargets()` now uses the same serialisation pipeline as the exporter, so the diff is apples-to-apples and only actual edits get counted. The writeback path was already converting semantic markers back through `ResolveSemanticNames` + `ReconstructTarget`, so no change needed there.

## [4.20.12] – 2026-05-25

### Fixed

- **Bilingual DOCX table now fills the page width.** v4.20.11 used fixed-DXA column widths (9000 twips ≈ 6.25") which kept the table inside the page edge but left the right half of wider pages (A4 landscape, US Letter landscape, narrow-margin layouts) empty. Switched to percentage-based widths: `TableWidth = Pct 5000` (= 100% of section width) plus per-column percentages summing to 5000, with `TableLayout = Fixed` still on top to keep long content from blowing columns out. Table now expands to fill whatever the page gives it.

## [4.20.11] – 2026-05-25

### Fixed

- **Bilingual DOCX table no longer overruns the page.** v4.20.10 used percentage-width columns with the default `auto` table layout, which let Word stretch the table past the page edge when content (long file names, long source paragraphs) demanded it – the Status column got clipped to "Transla" in multi-file exports. Switched to fixed-DXA column widths (9000 twips total ≈ 6.25", fits A4 portrait + US Letter with default margins) plus a proper `<w:tblGrid>` and `TableLayout = Fixed` so Word respects the declared widths regardless of content length.
- **"None" button now actually clears the segment count.** Empty file selection in multi-file mode was being treated as "no filter, count everything" (a leftover from the single-file branch of the same code path), so clicking **None** left the count at the document total. Now multi-file mode correctly returns 0 segments when no files are checked. Single-file mode (where the file list is hidden) is unchanged.
- **No more blue "selected" highlight on the file rows.** Even though v4.20.10 already replaced `CheckedListBox` with plain `CheckBox` rows, the default WinForms focus rectangle made the focused row look quasi-selected. Added `TabStop = false` + `FlatStyle = System` to each row so it renders as the OS-native checkbox with no focus indicator. One click = one toggle. No selection state.

## [4.20.10] – 2026-05-25

### Fixed

- **Per-file segment attribution now works on real multi-file projects.** v4.20.9 walked paragraph-unit contexts looking for file metadata, but the v4.20.9 diagnostic dump (kindly provided by a user testing a 3-file Dutch project) revealed Trados puts **zero** file-identifying info in PU contexts – only paragraph-styling and header/footer flags. New approach: for each `ProjectFile.LocalFilePath` (the on-disk SDLXLIFF), read the file once and extract every GUID from it via regex. Trados paragraph-unit ids are GUIDs and are globally unique, so the set of GUIDs in file A's SDLXLIFF is exactly the set of PU ids belonging to file A. Then each segment pair's parent PU id is looked up against that table. Runs once per active-document change.
- **As a consequence, all the visible v4.20.9 bugs go away in one shot:**
  - "(0 segments)" beside each filename in the files-to-export list now shows the real count.
  - Checking / unchecking a file in the list now actually changes the "Segments: N" label.
  - Multi-file combined-DOCX export now produces the **6-column table with the File column** plus the **yellow "📄 File: \<name\>" section-break rows** between each file's segments (the renderer auto-switches when segments carry non-empty `SourceFileId`s).
  - "Separate DOCX per file" mode now produces one bilingual file per source file as advertised.

## [4.20.9] – 2026-05-25

### Fixed

- **Multi-file projects: per-file segment counts and the file-selection filter now actually work.** The v4.20.8 attribution map walked `IFile.ParagraphUnits` to figure out which segment belonged to which file, but `ProjectFile` in Studio 18 + 19 doesn't expose that collection at all – the map ended up empty, so checking any file in the list gave "Segments: 0" and Export emitted nothing. Replaced with the right SDK path: walk every segment pair, call `Document.GetParentParagraphUnit(pair)`, then match the paragraph unit's context-stack strings (DisplayName, Description, `FilePath` / `OriginalFilePath` metadata, etc.) against each file's Name / OriginalName / LocalFilePath / basename. Works in single-file and multi-file mode.
- **Graceful degradation when attribution can't be built.** If the SDK still doesn't surface enough info to attribute segments (e.g. an unusual file-type configuration), the file-selection filter is silently ignored and the export proceeds with every segment in the active view. A log line in the Import / Export tab explains what happened. Better than emitting an empty file.

### Changed

- **Files-to-export list redesigned.** Replaced the `CheckedListBox` (which had a confusing distinction between "highlighted" and "checked" – clicking on a row's text would change the highlight without flipping the check, or vice versa) with a scrollable panel of plain checkbox rows. One click = one checkbox toggle. No more selection vs. check confusion.

## [4.20.8] – 2026-05-25

### Added

- **Multi-file bilingual export.** Trados projects with multiple files merged into one editor tab now get a dedicated file list in the Import / Export tab, plus an output-mode chooser:
  - **File list** — a CheckedListBox showing every file in the active (merged) document with its segment count. Quick-select buttons ([Active only] / [All] / [None]) for common selections. The "Segments: N" label tracks the current selection live.
  - **Output mode** — *"Combine into one DOCX"* (default) produces a single bilingual file containing all selected files joined together, OR *"Separate DOCX per file"* asks for a folder and writes one bilingual file per selected source file.
  - **Single-file documents see no change** — the file list, output radio, and per-file UI are all hidden so the tab looks exactly as before.

- **File column + yellow section breaks in the combined DOCX.** When the export contains segments from more than one source file, the bilingual table grows from 5 to 6 columns — `#, Source, Target, File, Status, Notes`. Between each file's segments, a full-width yellow-highlighted section-break row appears reading "📄 File: `<filename>`", so the proofreader can spot file boundaries at a glance.

- **Per-segment file id + name in the manifest.** Each manifest entry now records the segment's `source_file_id` (Trados GUID) and `source_file_name` (e.g. "Chapter2.docx"). Re-import uses the file id to route each diff to the correct file in the merged document. Manifests stay backwards-compatible — older single-file manifests parse cleanly into the new fields as empty strings.

### Fixed

- **"Segments: N" label on the Import / Export tab now actually updates.** Was an unwired UI control sitting at 0 since v4.20.7. Now reflects the active document's total segment count (or the current selection's count, in multi-file mode), updating on every active-document change and file-list selection change.

## [4.20.7] – 2026-05-24

### Added

- **New "Import / Export" tab in the Supervertaler Assistant panel.** A dedicated home for bilingual review workflows that sits between Batch Operations and Reports. Three export formats and three layouts:
  - **Formats:** Word document (`.docx`), Markdown (`.md`), HTML report (`.html`, client-facing, read-only).
  - **Layouts:** *Supervertaler Bilingual Table* (5-column table — `#, Source, Target, Status, Notes` — the canonical round-trippable shape, identical to Supervertaler Workbench's "Bilingual Table" so files can flow between both products), *Stacked source-on-top* (source paragraph above target paragraph, sentence by sentence), and *Stacked target-on-top* (target paragraph above source).
  - Every export writes both the file itself and a small **sidecar manifest JSON** (`<file>.svexport.json`) that records the project name, source filename, language pair, export timestamp, tool version, and the per-segment `(number → Trados ParagraphUnitId / SegmentId)` mapping with a source-text SHA-256 prefix. The manifest lets re-import locate the exact Trados segments even if the proofreader accidentally reorders rows.
  - A **Recent exports** list at the bottom of the tab tracks every export this session with **Open file** / **Open folder** / **Re-import this** buttons next to it.

- **Round-trip re-import (DOCX and Markdown).** Send a bilingual file to a proofreader, get it back, click **📥 Re-import…**, pick the file. Supervertaler:
  - Reads the file's segment rows (DOCX table or Markdown `## Segment N` blocks).
  - Loads the sidecar manifest if present (fully detected by file path: `<file>.svexport.json`); falls back to a current-document mapping with a warning if the sidecar is missing.
  - Diffs each row against the live Trados segment state: classifies every row as *unchanged*, *changed*, *segment-missing*, *source-mismatched*, or *locked / rejected*.
  - Shows a confirmation prompt with counts before any write happens.
  - Applies changes via the same `ProcessSegmentPair` writeback path the AI batch translator uses, so soft-return handling for Excel / Visio segments behaves the same way and locked/rejected segments are skipped automatically.
  - HTML is **not** re-importable by design — the HTML renderer is for client-facing review reports, not editing.

- **Embedded segment markers in every exported file** (`SV_seg_N` bookmarks in DOCX, `<!-- sv-seg:N -->` HTML comments in Markdown / HTML). These are invisible in normal rendering but let future versions match segments by ID even when human numbering drifts.

- **Full inline-tag round-trip with Workbench-style semantic naming.** Source and target text in the bilingual file now go through `SegmentTagHandler.Serialize()` (the same serialisation the batch AI translator already uses for inline-tag-aware prompts) and then through `BilingualTagNamer.ApplySemanticNames()`. The cells contain:
  - `<b>...</b>`, `<i>...</i>`, `<u>...</u>`, `<bi>...</bi>` for recognised cf character-formatting pairs (matches the Supervertaler Workbench's "With Tags" Bilingual Table style)
  - Numbered `<t1>...</t1>` (paired) / `<t2/>` (standalone) for everything else — field codes, page numbers, custom format pairs, line breaks, etc. — so no Trados tag type is ever silently dropped
  - All markers coloured dark red (#7F0001) in the DOCX so the proofreader can see the anchors at a glance
  - On re-import, `BilingualTagNamer.ResolveSemanticNames()` walks the proofreader's edit and converts each `<b>` (etc.) back to the matching `<tN>` via positional matching against a freshly-regenerated TagMap (deterministic numbering as long as the source hasn't drifted — `SourceHash` guards against that). `SegmentTagHandler.ReconstructTarget()` then rebuilds the target segment with the cloned tags wrapped around the proofreader's translated text
  - The proofreader can freely reorder tags to fit target-language word order, remove a marker pair to drop that formatting, or leave a segment unchanged
  - If the marker structure is broken (mismatched, unknown name, unresolvable), the writeback falls back to plain text with a per-segment log entry — same defensive pattern the batch AI translator uses

- **Structural-tag integrity check (toggleable, ON by default).** A new checkbox in the Import / Export tab — *"Refuse to apply edits that drop source-required tags (recommended)"* — protects against the most common production hazard of the round-trip. The check enforces **strict equality on structural tags** between source and the proofreader's edit. "Structural" means `<tN>` markers that didn't get a friendly semantic name from `BilingualTagNamer` — i.e. field codes, page numbers, custom format pairs, line breaks. These drive Trados file structure and must round-trip 1:1; adding one creates a tag the target file can't render, removing one drops a tag Trados expects. **Semantic formatting tags** (`<b>`, `<i>`, `<u>`, `<bi>`) are explicitly *not* counted — the proofreader can freely add or remove character formatting in the target without breaking Trados QA, since those changes only affect cosmetic rendering. When the check is ON, segments where `count(<tN> in source) ≠ count(<tN> in edit)` are classified as a new `TagMismatch` issue and not written; the confirmation dialog breaks the count out explicitly. When OFF (advanced use), tag-mismatched edits are applied verbatim with a per-segment warning in the log — you take responsibility for verifying Trados QA afterwards.

- **Segment-number column centred in the bilingual DOCX table.** Reads more naturally as a vertical column when centred, especially with mixed digit counts (1 / 21 / 121).

- **Inline-formatted text is now rendered with the matching style in the bilingual DOCX.** Previously the `<b>...</b>` / `<i>...</i>` / `<u>...</u>` / `<bi>...</bi>` markers appeared in red as tag anchors, but the text *between* them stayed unstyled. The proofreader can now visually preview how each formatted span will look: text inside `<b>...</b>` renders bold, text inside `<i>...</i>` renders italic, and so on. Markers themselves stay red (so the proofreader can see exactly which text is anchored to a tag and reorder them); nested same-name markers (`<b>x <b>y</b> z</b>`) and combinations (`<bi>`) are handled via a counter-based active-state walk. Numbered structural markers (`<t1>`, `<t2/>`, etc.) don't carry semantic styling so they leave the body-text style alone.

- **Paragraph-level styling (bold / italic / underline) now visible in the bilingual DOCX.** Segments living in a "Heading 1" / "Title" paragraph, or any source paragraph styled bold / italic / underline as a whole, previously appeared as plain text in the bilingual file even though Trados renders them styled in its editor. The export now detects paragraph-level formatting by walking the segment's `IText.Properties.Formatting` (where Trados often pushes paragraph-wide styling down to runs) plus the parent `IParagraphUnit.Properties.Contexts` (for file types that keep it at paragraph level only) and applies matching bold / italic / underline to the source AND target cells in the DOCX. Both probes are wrapped in try/catch and degrade silently if the SDK exposes the shape differently for any one file type. Re-import ignores the styling completely — Trados regenerates paragraph styling from its own metadata on export, so the styling in the bilingual file is purely cosmetic for the proofreader's visual reference. Inline formatting (cf bold/italic tags inside the segment) is unchanged — it's still serialised as `<b>` / `<i>` / `<u>` markers and reconstructed on re-import.

- **DocumentFormat.OpenXml 2.20.0 NuGet dependency** added for DOCX read/write. Pinned at the 2.x line because the 3.x multi-assembly layout doesn't fit the `.sdlplugin` packaging model. Single-DLL deployment alongside the existing iTextSharp / Microsoft.Data.Sqlite stack.

- **DOCX bilingual export header now visually matches the Supervertaler Workbench's "Bilingual Table" format.** Decorative `━ × 50` horizontal line, centered "🌐 Supervertaler Bilingual Table" title in 18pt bold blue, clickable `Supervertaler.com/trados` URL subtitle (10pt blue underlined), second decorative line, then the Project / Source file / Languages / Segments / Exported key-value block, then the amber "⚠️ Important: …" notice with italic instructions. The only visible difference between a file exported by the Trados plugin and one exported by the Workbench is the subtitle URL (`Supervertaler.com/trados` vs `Supervertaler.com/workbench`) — the title text and re-import warning are identical. Files round-trip between both products either way. Paired with Supervertaler Workbench v1.10.167 which switched its subtitle URL to `Supervertaler.com/workbench`.

### Fixed

- **AiAssistantControl: Reports tab badge + tab navigation no longer break when a new tab is inserted.** Previously `UpdateReportsBadge` and `SwitchToReportsTab` were hard-coded to tab index 2; adding the Import / Export tab shifted Reports to index 3 and would have silently broken the badge. Both methods now look the Reports tab up by label, so future tab insertions can't re-break navigation — same fix pattern applied to the Workbench in v1.10.161.
- **Batch Operations → Clipboard Mode: "Copy to Clipboard" button no longer underlaps "Paste from Clipboard".** Companion fix to the v4.20.6 Preview-prompt overlap. The Paste button's Location was pinned at construction time using the Copy button's `.Right`, but `AutoSize` widens Copy *after* layout — so Paste landed too far left, partially hidden under Copy. Added a `SizeChanged` handler on Copy that repositions Paste with the same 8 px gap any time Copy resizes (DPI scale changes, font size changes, etc.). Same `SizeChanged` pattern already used for `_btnTranslate`. Spotted by a user.

## [4.20.6] – 2026-05-24

### Fixed

- **AI Assistant → Batch Operations: "Preview prompt" link no longer overlaps the "Copy to Clipboard" button when Clipboard Mode is on.** The reposition routine was anchored on the (now-hidden) Translate button, so the wider Copy to Clipboard button slid in underneath the link. The routine now picks the rightmost *visible* control on the action row — Translate, Copy to Clipboard, Paste from Clipboard, or the Proofread "Also add issues as Trados comments" checkbox — and places the link clear of all of them. Spotted by a user.

## [4.20.5] – 2026-05-21

### Fixed

- **Overview button icon now renders.** The previous icon used an emoji (📊) outside the button font's range, so it showed as an empty box. Replaced with a glyph (☰) from the same character block as the other toolbar icons.
- **Overview and Summary buttons respond to the first click.** Like the other dock-pane buttons, they now use the Studio first-click-eaten workaround, so a single click works even when the AI Assistant pane was not the active pane (previously the first click was swallowed and you had to click twice).

## [4.20.4] – 2026-05-21

### Added

- **Memory Bank Overview (new toolbar button).** A "📊 Overview" button in the SuperMemory toolbar generates a single, self-contained HTML page and opens it in your browser, so you can see at a glance what is in the active memory bank. It includes: dashboard counts (terminology, domains, clients, style guides); a searchable, sortable terminology table (source → target, domain, client, confidence, status, updated); a list of conflicting term pairs (same source term mapped to different targets); stub/incomplete notes; terminology notes missing a domain; stale notes (not updated in over a year); domain and client coverage; and the most recently updated notes. It reads only note frontmatter, so it is instant even on large banks.
- **AI memory-bank summary (new toolbar button).** A "✨ Summary" button asks the AI for a short, plain-English profile of the active bank — overall size and focus, strongest and thinnest domains, and what needs attention (conflicts, stubs, stale notes) with specific examples. The summary is built from a compact metadata digest rather than full article bodies, so it stays cheap, and the result is posted into the chat.

### Improved

- **AI chat now also matches your question against terminology note *bodies*, not just their term names.** Building on the v4.20.3 retrieval fixes, a note whose body discusses the topic you asked about is now surfaced even when its title/term field does not match your wording. Note bodies are read on demand and cached by file modification time, so external Obsidian edits are picked up automatically. (A persisted SQLite/FTS5 index – useful for very large banks with thousands of notes – remains a documented future step.)

## [4.20.3] – 2026-05-21

### Fixed

- **AI chat now reliably surfaces a memory-bank term you ask about.** When you ask the AI Assistant about a specific term (e.g. when to use "essential"), the relevant SuperMemory terminology note is now force-included in the chat context. Previously the knowledge-base retrieval selected terminology notes only by the open document's domain, client and language – never by your actual question – so a note could be silently skipped even though it existed. Three underlying issues were fixed:
  - The user's chat message is now used to match terminology notes (by source term, target term or filename); a directly-asked-about note is given top priority so it is never dropped by the context size limit.
  - The language-pair relevance signal was reading the wrong frontmatter key (`languages`/`source_language`) and so never matched real notes, which use `language_pair`. It now reads `language_pair` as well.
  - The "include all terminology notes" fallback was gated to banks with 20 or fewer notes, so larger banks could end up contributing nothing. The size cap is removed; the token budget now does the limiting.

## [4.20.2] – 2026-05-21

### Fixed

- **Trailing sentence punctuation is stripped when adding terms to a termbase.** A term pair such as "circumference." is now stored as "circumference". Wrapping quotes and parentheses are preserved, and non-translatables keep a meaningful trailing full stop (e.g. "Inc."). Applies to source terms, target terms and synonyms across the add, quick-add and edit routes. This matches the trailing-punctuation set already used when matching terms during translation, so stored and matched forms stay consistent.

## [4.20.1] – 2026-05-21

### Added

- **Gemini 3.5 Flash is now selectable as a Gemini model.** Google's newest Flash model is offered as a premium, higher-quality option in AI Settings alongside the existing Gemini models. Gemini 3.1 Flash-Lite stays the recommended default; 3.5 Flash costs roughly six times as much per segment, so it is best reserved for difficult content.
  - Gemini 3.5 Flash always performs a "thinking" step, which at its default level would bill several hundred hidden reasoning tokens (charged at the output rate) on even a single short segment. The plugin sends `thinkingConfig.thinkingLevel: minimal` for 3.5 models (and omits the now-unsupported `temperature` parameter for them), keeping the cost of short translation jobs in line with its headline price without affecting quality on normal text.

## [4.20.0] – 2026-05-19

### Added (Trados Studio 2026 support — a second, x64 build of the plugin that runs natively in Studio 2026 and reads its new SQLite-based .ttb termbases)

Trados Studio 2026 (internally Studio 19) is a 64-bit application and ships a redesigned terminology system: the legacy MultiTerm components are gone from the install, and the built-in termbase format is now a SQLite database with the `.ttb` extension (still concept-oriented, still using the MultiTerm-style termbase-definition XML internally, but on a completely new storage engine with an FTS5 full-text index). The existing plugin targeted Studio 2024 (Studio 18), is x86/AnyCPU, and reads `.sdltb` termbases via JET/ACE OleDb — none of which works under Studio 2026. This release adds a parallel build so the same source tree produces two `.sdlplugin` artefacts: the unchanged Studio 2024 build and a new Studio 2026 build.

**One source tree, two builds.** A `TradosStudioVersion` MSBuild property (default `18`) switches the build between Studio 18 and Studio 19: HintPaths (`Studio18` vs `Studio19Beta`), platform target (x86/AnyCPU vs **x64**), output folder (`bin\Studio18` vs `bin\Studio19`), and which `pluginpackage.manifest` is shipped. The Studio 19 manifest declares `RequiredProduct minversion="19.0" maxversion="19.0.9"`. `build.sh` builds both, packages both, and deploys each to its respective Trados plugins folder. `bump_version.py` keeps both manifests in version-lock.

**TtbReader.** A new `Core/TtbReader.cs` reads `.ttb` files directly with `Microsoft.Data.Sqlite` (the same SQLite stack already bundled for the Supervertaler database). It maps the `mtConcepts` / `mtTerms` / `mtIndexes` tables to the existing `TermEntry` model, matching the public surface of `MultiTermReader` so the rest of TermLens is unchanged. Both readers now implement a shared `ITermbaseReader` interface; `TermbaseReaderFactory.Create()` dispatches on file extension (`.ttb` → SQLite, otherwise `.sdltb` → OleDb). Locale handling is case-insensitive to bridge Trados's BCP-47 codes (`en-GB`) with the uppercase codes the `.ttb` store uses (`EN-GB`). The project termbase detector now recognises `.ttb` alongside `.sdltb`.

The legacy terminology-provider plugin API (`Sdl.Terminology.TerminologyProvider.Core`) is preserved in Studio 2026 behind compatibility adapters, so no migration to the new `Sdl.TranslationResourcesApi.TB` namespace was required — the plugin registers and reads terminology exactly as before. The Studio 2026 build ships with `.ttb` support only; `.sdltb` reading is not included in the 2026 build because JET OleDb is 32-bit-only and Studio 2026 is 64-bit (legacy `.sdltb` handling in 2026 is deferred pending RWS guidance on their migration story).

### Fixed (a stack of x64-specific defects that only surfaced once the plugin ran as a 64-bit process under Studio 2026)

**SQLite provider not registered.** Opening any database failed with "You need to call SQLitePCL.raw.SetProvider()". Two compounding causes: the startup init called `Batteries_V2.Init()` with the wrong type name (the classes live in the `SQLitePCL` namespace, not `SQLitePCLRaw`, despite the package name), so it silently no-op'd; and even once corrected, `Assembly.Load("SQLitePCLRaw.batteries_v2")` returned Studio's own bundled copy, registering the provider on a different `raw` instance than the one Microsoft.Data.Sqlite ends up bound to. The fix loads the plugin-folder copies of `SQLitePCLRaw.batteries_v2` and `SQLitePCLRaw.core` by absolute path so initialisation runs against the same instance the data layer uses. Studio 2024 (x86) never hit this because a different DLL-load order happened to register a provider already.

**Arithmetic overflow crash opening TermLens.** `CtrlTapFilter.PreFilterMessage` cast `m.WParam` (an `IntPtr`, 64-bit under x64) straight to `int`, throwing `OverflowException` whenever the WPARAM had high bits set. Now reads via `ToInt64()` and masks to the low byte before narrowing.

**Removed-API build break.** The two-argument `ITerminologyProviderFactory.CreateTerminologyProvider(Uri, ITerminologyProviderCredentialStore)` overload (obsolete in 2024, removed in 2026) is replaced with the single-argument form that exists in both.

**Two-minute editor freeze on first interaction.** Termbase loading (the Supervertaler database plus project termbases) ran synchronously during view-part initialisation, blocking editor activation until it finished — on x64 with a cold disk that was up to two minutes. The load now runs on a background thread; the editor is responsive immediately and term chips populate a few seconds later.

**First click on every plugin-pane button was ignored.** Studio 2026's WPF-based docking host consumes the first `WM_LBUTTONDOWN` while activating a previously-inactive dock pane, so neither `MouseDown` nor `Click` fired — every header/action button across TermLens, SuperSearch, and the AI Assistant required two clicks. A new `Core/ClickThrough` helper pre-emptively activates the pane on `MouseEnter` via a native `SetFocus` call (deliberately not WinForms `Control.Focus`, which would trigger `ScrollControlIntoView` and disturb the panel layout), with a `GotFocus`-based fallback that synthesises the action when the user is still holding the mouse button down on the control. Applied to all pane buttons.

**TermLens corner indicators clipped.** The amber metadata dot and indigo synonym icon at a chip's top-right corner were clipped on the right edge (reading visually as "cut off at the top") because the chip reserved too little right-side padding for the icon and its border. The chip now reserves the indicator's overflow width when an indicator is present. This was a pre-existing defect, fixed in both the Studio 2024 and Studio 2026 builds.

### Changed (AI prompt termbase inclusion is now opt-in by default)

The "Termbases included in AI prompts" list now defaults to **nothing included** — users explicitly enable the termbases they want fed to the AI, rather than everything being included unless explicitly excluded. A one-shot migration (mirroring the existing per-project behaviour) applies the opt-in default on first load: when the global AI termbase list has never been initialised and no explicit choices exist, all termbases are set to excluded. Users with existing explicit selections are left untouched. This is a privacy-first default — sensitive termbase contents are no longer sent to the AI provider unless the user opts in.

### Packaging

`package_plugin.py` now ships the `pluginpackage.manifest.xml` produced by the build (which carries the correct `RequiredProduct` range per Studio version) instead of regenerating it from a hardcoded template that always declared the Studio 2024 range — the cause of the Studio 2026 build initially being silently skipped by Studio's plugin loader.


## [4.19.114] – 2026-05-17

### Fixed (v4.19.113 ↻ refresh button was clipped against the floating gear icon overlay; bumped TermLens header right-padding to give it breathing room)

A user reported that the new ↻ refresh button added in v4.19.113 was being visually clipped against the ⚙ settings gear icon to its right. Investigation: there are two layered controls sharing the same horizontal strip at the top of the TermLens panel:

 - **`MainPanelControl`** (outer wrapper) floats the ⚙ gear (26 px wide) and ? help (26 px wide) buttons **absolutely** at the top-right via `PositionTopButtons`, sitting at `X = Width - 54` and `X = Width - 28` respectively. These are drawn on top of the inner panel.
 - **`TermLensControl._headerPanel`** (inner) hosts the `A` / `A` font buttons, the status label, and (since v4.19.113) the new ↻ refresh button — all `Dock = DockStyle.Right`. The inner panel reserved `Padding.Right = 56 px` to leave space for the gear+help overlay.

The old 56-px reservation just covered the 54-px combined gear+help footprint, with 2 px to spare. v4.19.113's new 24-px refresh button docked at the right edge of the inner content area, putting its right edge at `Width − 56` — only 2 px clear of the gear icon's left edge at `Width − 54`. At 100% DPI this read as "the buttons are touching"; at 150% Windows scaling the gap closed to 4 raw pixels, indistinguishable from clipping.

**Fix.** Bumped `_headerPanel.Padding.Right` from `UiScale.Pixels(56)` to `UiScale.Pixels(84)` — `26(help) + 26(gear) + 24(refresh) + 8(slack)` = 84 px reserved. The refresh button's right edge now sits at `Width − 84`, with a comfortable 30-px gap to the gear icon's left edge at `Width − 54`. At 150% DPI the gap scales to ~46 raw pixels, well clear of any visual collision.

No other layout was affected — the font buttons and status label are docked inside the same right-padded area and shift left in lockstep with the padding bump.


## [4.19.113] – 2026-05-17

### Added (↻ refresh button in TermLens header + automatic refresh when the shared SQLite database is modified by another process — typically the Supervertaler Workbench desktop app)

Companion to the Workbench v1.10.68 / v1.10.69 refresh feature. When both products are open against the same `supervertaler.db` and the user edits terms in Workbench, the Trados plugin's in-memory term index goes stale until the user manually presses F5 (`RefreshTermbaseAction`). Symptoms identical to the Workbench-side bug it mirrors: TermLens keeps showing terms that have been deleted in the other tool, misses newly-added ones, and right-clicking a stale term to edit pops up an empty dialog because the underlying `term_id` no longer exists.

**Two pieces ship together:**

**1. Visible ↻ button in the TermLens header.** Placed left of the existing `A` / `A` font zoomer, styled to match the SuperMemoryToolbar refresh button (24×24 px, light-gray glyph, hover effect, click-feedback). Clicking it raises a new `TermLensControl.RefreshRequested` event; the host (`TermLensEditorViewPart.OnTermLensRefreshRequested`) routes it through `NotifyTermAdded()` — the same code path F5 has always used, so we get the full reload (settings re-read, `LoadTermbase(forceReload: true)`, MultiTerm re-detect, segment redraw) without forking the logic. Brief visual cue: button flips to `✓` and disables for 500 ms so the click registers even when the reload is sub-second. Tooltip explains the use case explicitly and mentions the F5 equivalent. `TermLensControl.CurrentDbPath` exposed so the watcher (below) knows what file to monitor.

**2. Automatic refresh via `FileSystemWatcher`.** Installed on the active `supervertaler.db` at the end of `Initialize()`, right after the existing `LoadTermbase` / `LoadMultiTermTermbases` chain. Three layers of "don't misfire" gating, matching the Workbench v1.10.69 implementation:

 - **2-second debounce** — `_dbDebounceTimer` (re)starts on every `Changed` / `Created` / `Renamed` event; the actual reload only runs once the file has been quiet for 2 s, so a burst of writes (typical: INSERT + activation-table update + SELECT-verify) collapses into one reload.
 - **Snapshot gating** — before reloading, the handler queries five cheap aggregates (`COUNT` + `MAX(id)` from `termbases`; `COUNT` + `MAX(id)` + `MAX(modified_date)` from `termbase_terms`) and compares to the snapshot taken at the end of the last own-reload. If nothing in the termbase tables changed (e.g. it was a TM write that happened to touch the same `.db` file), the reload is skipped entirely.
 - **Own-write integration** — every reload path (`NotifyTermAdded` / `NotifyTermInserted` / `NotifyTermDeleted`) refreshes the snapshot at the end via `RefreshTermbaseDbSnapshot`. So an own-write updates the snapshot synchronously; when the watcher debounce fires ~2 s later for that same write, the comparison sees no change and the reload is correctly skipped. **Zero spurious rebuilds from own-writes.**

The watcher is wrapped in `try / catch` at setup and teardown sites — failure (network drive, exotic filesystem, missing `FileSystemWatcher` permissions) is non-fatal: F5 and the new ↻ button still work as manual fallbacks. Teardown happens cleanly on `Dispose` so we don't leak watchers across document close / reopen cycles. Also re-installed by `NotifyTermAdded` in case Settings just pointed at a different `supervertaler.db`.

End-user effect: edit terms in Workbench → switch to Trados → display already reflects the change (within ~2 s of the Workbench write completing), no F5 / ↻ click needed. The button still lives in the header for the explicit "do it now" trigger and for cases where the watcher couldn't be installed.

Diagnostic output goes to `System.Diagnostics.Debug.WriteLine` (visible in DebugView): `[TermLens] DB auto-refresh: watching supervertaler.db (2s debounce)` on setup, `[TermLens] DB changed externally — auto-refreshing index (termbases:X→Y, terms:N→M)` on each actual refresh, snapshot failures and watcher-setup failures also logged.


## [4.19.112] – 2026-05-17

### Changed (All in-app help links repointed at the new help.supervertaler.com site)

- The Supervertaler help system has migrated off GitBook to a self-hosted Astro/Starlight site at `https://help.supervertaler.com`, deployed via Cloudflare Pages from the public `Supervertaler/Supervertaler-Help` repo. The new site has per-product sidebar filtering (a Trados page shows only the Trados tree; a Workbench page shows only the Workbench tree), a custom search component that groups results by product, and proper folder-based URLs (`/trados/...` instead of GitBook's flat-with-collision-suffix slugs). The migration was driven by GitBook free-plan limitations (no support for two distinct product spaces) and by the recurring pain of search results mixing Workbench and Trados topics.
- Every help-URL constant in `Core/HelpSystem.cs` has been rewritten from the old GitBook slug shape to the new Astro folder-based URLs. The mapping is 1-to-1 against `src/generated/sidebar.js` in the `Supervertaler-Help` repo (which is the authoritative URL map). All 37 constants now point at `https://help.supervertaler.com/trados/...`, with trailing slashes (Astro's canonical form, so the browser hits the page directly instead of via a 301 redirect). The `bidirectional` parameter wasn't needed here; the routing just goes to a different host.
- Two further hardcoded help URLs were also repointed: the `HelpUrl` constant in `Controls/UsageStatisticsDialog.cs` (Help button on the first-run stats dialog) and a comment-only reference in `Controls/TermLensPopupForm.cs`. Verified end-to-end: every help action from inside Trados Studio – Help menu, F1 in panels, "Help" buttons in dialogs – now lands on a 200-OK page on the new domain.
- The old `supervertaler.gitbook.io/help/...` URLs continue to work during a two-week handover window so that any users on older installed versions still get a working help system. After that window, GitBook will be replaced with a single "we've moved" notice and unpublished. The redirect window matters specifically because Trados plugin installs propagate slowly via the RWS App Store update mechanism.
- Implementation note: this commit just bumps the version and ships the URL constants that landed in `717f9a5` (Re-point all help URLs at help.supervertaler.com). No behavioural changes beyond the URL repointing; no UI changes; no new features.


## [4.19.111] – 2026-05-16

### Changed (AutoPrompt-generated prompts now embed the Translator's Comment methodology by default)

- Real translation work routinely runs into mechanical defects in the source: typos, broken words across whitespace, hanging mid-sentence breaks, doubled spaces, stray punctuation, reference-numeral mismatches that are unambiguous in context, missing diacritics. The cleanest workflow for handling these – established in a gold-standard project prompt and validated against real Trados Studio translation work – is for the translator AI to silently correct obvious defects and append a single concise translator's comment at the end of the segment in the form `⟦TC: short description of the fix⟧` (using the mathematical white square brackets U+27E6 / U+27E7, which do not occur in source documents and so are safe as out-of-band markers extractable in post-processing).
- The Trados AutoPrompt meta-prompt (`PromptGenerator.BuildMetaPrompt`) now contains an explicit TRANSLATOR-COMMENT METHODOLOGY block that requires the LLM to embed this silent-correction-with-flagged-comment methodology in every prompt it generates, regardless of source language or domain. The block specifies the exact bracket characters, the rules for one-marker-per-segment, the no-empty-markers rule, the inline `[bracketed text]` convention for translator-supplied words, the comment-body style guidance (5–20 words, noun-phrase, no first-person), the placement (final content of the segment, single space separator), and a list of hard exclusions (numerical values, dates, dosages, claim language, statutory references, headings, identifiers, ambiguous cases).
- The generated prompt is also required to include: (a) the silent-correction methodology in its TRANSLATION MANDATE section, with defect categories adapted to the actual source language (e.g. Dutch -d/-t typos, German missing umlauts, French accent slips, Spanish/Italian conjugation typos), (b) a dedicated TRANSLATOR COMMENT FORMAT section near the end with the exact spec and 4–6 example comment bodies adapted to the language and domain, (c) a check in PREFLIGHT SELF-CHECK and POST-TRANSLATION INTEGRITY that every silent correction has its corresponding marker and no segment without corrections has a marker, (d) a note in OUTPUT FORMAT that ⟦ and ⟧ are the deliberate out-of-band comment delimiter and the sole exception to the "ASCII output only" rule.
- The methodology is always-on for every AutoPrompt-generated prompt in every domain – users who prefer not to use it can edit the generated prompt to remove the TC sections. Per-project opt-out via a UI toggle is a possible future enhancement.
- The markers currently appear inline in the target text. Extraction into Trados Studio segment comments is not yet wired up; this is a separate follow-up. The current change ships the methodology in the prompts so the translator AI produces the markers reliably, ready for whichever extraction pipeline gets built next. The Trados Studio comment panel already exists, so the extraction step is small once the spec is locked.
- Shipped in parallel with Supervertaler Workbench v1.10.46 which makes the same change to its Python meta-prompt so both products' AutoPrompts agree on the methodology and the bracket format.


## [4.19.110] – 2026-05-16

### Changed (AutoPrompt now generates proper Markdown instead of plain text)

- AutoPrompt-generated translation prompts were saved as `.md` files but their content was plain text dressed up as a numbered list (`1. ROLE`, `2. TECHNICAL DOMAIN`, etc., with no Markdown headings, no bullet markers, no bold, no tables). The shared `prompt_library/` folder also held Workbench-generated prompts that *did* use proper Markdown (`## H2` headings, `-` bullets, `**bold**`, `| ... |` tables), so the two products were producing inconsistent artefacts in the same folder despite both using the same `.md` extension. Opening a Trados-generated prompt in Obsidian, VS Code, GitHub, or any Markdown-aware viewer rendered as a wall of text instead of a navigable document.
- `PromptGenerator.BuildMetaPrompt` now contains an explicit Markdown-formatting block telling the LLM to: open with a `# H1` title and one or two `## H2` subtitles; render every major numbered section as a `## H2` heading; use `### H3` for subsections (e.g. `### Absolute requirements`, `### Absolute prohibitions`); use `-` bullet lists for enumerable content; use `**bold**` for emphasised and locked terms; render the PROJECT-SPECIFIC GLOSSARY as a proper Markdown table with header row + alignment row; use `---` horizontal rules between major sections; use fenced code blocks only for actual code/file-path examples.
- The instruction explicitly notes that this Markdown requirement applies only to the **generated prompt itself**, not to the inner "OUTPUT FORMAT" rule that the generated prompt imposes on the translator AI (which still says "translation only, no markdown formatting in the translation output"). The two are different concerns – the prompt is a document for humans + LLMs to read; the translator's per-segment output remains plain target-text only.
- Effect: next AutoPrompt-generated prompt opens cleanly in any Markdown viewer, is far easier to scan and edit (find Section 13 instantly via outline), and matches the format Supervertaler Workbench already produces. Pre-existing plain-text prompts continue to work unchanged – they're still valid system prompts for the translator AI.


## [4.19.109] – 2026-05-16

### Changed (Prompt manager: filename is now the authoritative display name)

- The prompt manager tree displayed the YAML `name:` field from each prompt file's frontmatter, not the filename on disk. Renaming a .md file in Explorer (without editing the YAML inside) left the tree showing the old name even after clicking Refresh, because the tree built itself from `prompt.Name` (which the loader read from YAML and only fell back to filename when YAML had no `name:`). Two sources of truth for what to call a prompt is a confusing UX trap.
- The on-disk filename is now the single source of truth. `PromptLibrary.ParsePromptFile` unconditionally sets `prompt.Name = Path.GetFileNameWithoutExtension(filePath)`, ignoring any YAML `name:` field that may exist for backward compatibility with older files (`ParseYamlFrontmatter`'s `case "name"` is now a no-op). `PromptLibrary.SavePrompt` no longer writes `name:` to the YAML frontmatter at all.
- Effect for users: rename `MyPrompt.md` to `Better Name.md` in Explorer, click Refresh, and the tree shows the new name immediately. No need to also edit the YAML inside the file.
- Backward compatibility: existing prompt files with a YAML `name:` field still load fine – the field is silently ignored on read, and is dropped from the file the next time the prompt is saved through the UI. No mass migration runs. Existing in-app rename flows still work because `SavePrompt`'s "rename file when `prompt.Name` differs from the on-disk filename" logic is unchanged – `prompt.Name` is now set from the user's new name in the Edit dialog, and the file is renamed to match.
- Shipped in parallel with Supervertaler Workbench v1.10.43 which makes the same change to its Python `UnifiedPromptLibrary` so both products stay in sync on the shared `prompt_library/` folder.


## [4.19.108] – 2026-05-15

### Changed (Usage-statistics dialog rewritten: informational, default-on)

- The first-run anonymous-usage-statistics dialog has been rewritten from "Would you like to opt in?" to a polite informational note: *"Supervertaler for Trados sends one anonymous ping at startup so I can see how many people use the plugin. No personal data, no translation content, no termbase info — just plugin version, OS, Trados version, and system locale. If you'd rather not, switch it off below or any time in Settings. — Michael"*. The default action (Enter, Esc, X-close, or the bold "Keep it on" button) keeps stats enabled; only an explicit "Turn it off" click switches them off. The underlying telemetry behaviour is unchanged – same single anonymous ping at startup with the same payload (plugin version, OS, Trados version, locale, virtualization host).
- A new `usageStatisticsAskedV2` flag controls whether the dialog has been shown. The legacy `usageStatisticsAsked` flag stays in settings for backwards compatibility but is no longer checked, so every existing user – including those who clicked No to the previous opt-in dialog – gets the rewritten dialog once after updating, and their previous answer is treated as "you weren't asked under this framing yet." Users who already had stats on stay on (just see the new dialog confirming); users who said No previously get a fresh ask under the new framing (default-on, click "Turn it off" to keep stats off).
- *Background:* the previous opt-in dialog produced near-zero opt-ins – the live stats dashboard showed essentially one active user (me, on dev installs) across the whole plugin user base – because asking users to actively share data they don't benefit from gets near-universal "no thanks." The new framing is honest about what is collected (still: nothing personal, just version + OS + locale once per session) but defaults to enabled so the data actually reflects real plugin usage instead of the opt-in cohort.


## [4.19.107] – 2026-05-14

### Changed (Grok list trimmed to one current model)

- **Grok list is now just Grok 4.3** (`grok-4.3`), xAI's current flagship. The previous entries – Grok 4.20, Grok 4.1 Fast, and Grok 4.20 (Reasoning) – were stale, and some of those slugs are in xAI's 15 May 2026 API retirement. Grok 4.3 is a single unified model (xAI dropped the separate reasoning / non-reasoning variants), priced $1.25 / $2.50 per 1M tokens. Grok stays a supported provider; the list is just current and minimal now. Anyone on an old Grok model keeps working via the Model ID field, and xAI redirects retired slugs to `grok-4.3` anyway.


## [4.19.106] – 2026-05-14

### Changed (Claude list trimmed; Opus pricing corrected)

- **Removed Claude Opus 4.6 from the curated list.** Opus 4.7 is a step-change improvement over 4.6 at the same price, so keeping the previous flagship around was pure redundancy – same reasoning as the recent Gemma and Mistral Nemo trims. The Claude list is now a clean three: **Sonnet 4.6** (recommended), **Haiku 4.5** (fast/cheap), **Opus 4.7** (highest quality). Removed from both the Claude and OpenRouter lists; anyone still on Opus 4.6 keeps working via the Model ID field.
- **Fixed stale Claude Opus pricing in the cost estimator.** `TokenEstimator` had Opus at $15 / $75 per 1M tokens; the correct rate is **$5 / $25** (as Anthropic's docs and this changelog's own v4.19-era Opus 4.7 entry both state), so Opus cost estimates were running ~3× too high. Sonnet ($3/$15) and Haiku ($1/$5) were already correct.


## [4.19.105] – 2026-05-14

### Changed (Mistral list trimmed to two models)

- **Removed Mistral Nemo from the curated list.** Mistral Nemo (`open-mistral-nemo`) is a July 2024 model, since superseded; for translators it offered no real benefit over Mistral Small – similar (negligible) cost, lower quality, and the "open weights" angle is moot when it's called through Mistral's hosted API anyway (the Ollama provider covers genuinely local models). The Mistral list is now a clean two-tier split: **Mistral Large** (flagship – `mistral-large-latest`) and **Mistral Small** (fast, cost-effective workhorse – `mistral-small-latest`), both of which auto-track to the current release via their `-latest` aliases. Anyone still on `open-mistral-nemo` keeps working by entering it in the Model ID field.


## [4.19.104] – 2026-05-14

### Changed (Gemini model lineup refresh)

- **Gemini 2.5 Flash → Gemini 3.1 Flash-Lite** (`gemini-3.1-flash-lite`) as the recommended Gemini model – cheaper ($0.25 / $1.50 per 1M tokens), faster, and positioned by Google for high-volume work like translation. `gemini-2.5-flash` is removed from the curated list; anyone still on it keeps working (it now shows in the Model ID field).
- **Removed Gemma 4 31B from the Gemini list.** It sat within ~1% of Gemma 4 26B MoE on quality while the 26B MoE is 2–2.5× faster, so keeping both was redundant. Gemma 4 26B MoE stays as the open-source option.


## [4.19.103] – 2026-05-14

### Added (AI Settings: custom model ID field – [#24](https://github.com/Supervertaler/Supervertaler-for-Trados/issues/24))

- **New "Custom model ID" field in AI Settings**, below the Model dropdown, available for every cloud provider. Enter an exact model ID to use a model that isn't in the curated dropdown – a new release, a preview model, or an OpenRouter router such as `openrouter/free`. When filled it overrides the dropdown selection; leave it blank to use the model picked from the dropdown.
- Previously only OpenRouter allowed a custom model, and only by typing over the dropdown's display text (a "Name – Description" string) – confusing, since you had to overwrite a description with a bare ID, with no cue that the field was even editable. The OpenRouter dropdown is now a normal locked list like every other provider, and the dedicated Custom model ID field gives all providers one clear, consistent way to enter a model that isn't listed. A model ID saved this way that isn't in the curated list is shown back in the Custom model ID field when Settings reopens (so existing OpenRouter custom-model users migrate seamlessly).

### Changed (OpenAI model lineup: GPT-5.4 → GPT-5.5)

- **The curated OpenAI model is now GPT-5.5** (`gpt-5.5`), OpenAI's new frontier model (released April 2026), replacing GPT-5.4 in both the OpenAI and OpenRouter dropdowns. It's more capable and, at $5 / $30 per 1M tokens, cheaper than GPT-5.4 was.
- **GPT-5.4 Mini stays** as the recommended everyday default — there is no "GPT-5.5 mini", and at ~$0.75 / $4.50 per 1M tokens GPT-5.4 Mini is roughly 7× cheaper than GPT-5.5, which is the right choice for high-volume batch translation. The split is deliberate: GPT-5.4 Mini for everyday/batch work, GPT-5.5 for AutoPrompt and complex tasks.
- Users who had GPT-5.4 explicitly selected are not migrated — `gpt-5.4` still works on the API and now simply shows in the Custom model field; its cost-estimate pricing is kept under the legacy section.

### Fixed (AI Settings: "Connected" status text clipped)

- After clicking **Test Connection**, the green "✓ Connected" status was drawn starting underneath the Test Connection button – the label sat at x=250 while the 160px-wide button reaches x=280, and the button is in front in z-order, so the first few characters were hidden. The status label now starts past the button's right edge.


## [4.19.102] – 2026-05-14

### Added (SuperSearch: search project translation memories — three search modes)

- SuperSearch can now search the project's translation memories, not just its SDLXLIFF files. A new mode dropdown in the search bar offers three modes:
  - **Project files** – the original behaviour: search the project's SDLXLIFF files.
  - **Files + TMs** – search the project files *and* the project's translation memories, in one merged result list.
  - **TMs only** – concordance mode: search only the project's translation memories, the way Studio's built-in Concordance does.
- The selected mode is persisted across sessions until the user changes it (`superSearchMode` setting).
- **New "TMs" button** next to "Files" — opens a selection dialog listing the project's translation memories with checkboxes, so a search can be narrowed to specific TMs. Mirrors the existing Files filter; session-scoped, resets when the project changes.
- TM discovery reads the project's `.sdlproj` for attached translation-provider TMs and also scans the project's `Tm` subfolder. File-based `.sdltm` only — server-based (GroupShare) TMs aren't searched in this version. The project's files and TMs are re-discovered on every search, so a file or TM added to the project mid-session is picked up without reopening it.
- TM hits go through the Trados TM API's concordance search (source-side and/or target-side, following the Scope dropdown) and are then post-filtered with the same Aa / .* / Word options as file search, so the search options behave consistently across files and TMs.
- TM results appear in the same grid: the **File** column is renamed **File/TM** and shows the TM name in blue for TM rows; the **#** column shows the concordance match score. TM results are not navigable — double-clicking a TM row shows a hint pointing to the preview pane (where the text can be selected and copied) — and they are never touched by Replace / Replace All, which still operate only on SDLXLIFF file results. In **TMs only** mode the Replace bar is disabled.
- All project discovery (enumerating SDLXLIFF files, parsing the `.sdlproj`, probing TM paths) runs on a background thread, so the Trados UI thread never blocks on it — during start-up or during a search.
- New file `Core/TmSearcher.cs`; new TM API assembly references; the search-source mode is the `SuperSearchSourceMode` enum.


## [4.19.101] – 2026-05-14

### Added (SuperSearch: Match whole word)

- New **Word** checkbox in the SuperSearch search bar, alongside **Aa** (case sensitive) and **.\*** (regex). When ticked, the query only matches complete words – searching for "cat" no longer matches "category" or "scatter". Applies to both search and Replace / Replace All, so a whole-word search followed by Replace All won't quietly replace partial matches. Ignored when **.\*** (regex) is on – use `\b` in the pattern instead.

### Improved (SuperSearch: tooltips on every control)

- Every control in the SuperSearch search and replace bars now has a tooltip. Previously only **Aa**, **.\*** and **Files** had them; the search box, the Search / Stop buttons, the scope dropdown, the Replace toggle, the **?** button, and the whole replace bar (replacement box, Replace, Replace All) were undocumented.


## [4.19.100] – 2026-05-14

### Added (SuperSearch can be docked as a tab in the Supervertaler Assistant panel)

- **New setting: Settings → General → Panels → "Show SuperSearch as a tab in the Supervertaler Assistant panel".** When enabled, SuperSearch is hosted as a 4th tab inside the Supervertaler Assistant panel (alongside Chat, Batch Operations, and Reports) instead of occupying its own dockable panel – handy for translators who want fewer floating panels. Off by default; the standalone panel remains the default experience.
- Implementation: all of SuperSearch's search / replace / navigate logic was extracted out of `SuperSearchViewPart` into a new host-agnostic `SuperSearchController` (a process-wide singleton), so the same control instance can be hosted by either the standalone ViewPart or the Assistant tab. Both hosts share one control, so search results survive switching between tabs.
- **Requires a Trados restart** to switch hosting modes – a WinForms control can only have one parent, so the host is decided at panel-creation time. The settings screen labels this "(restart required)".
- **Gated on a Supervertaler Assistant licence.** The Assistant panel's upgrade overlay covers the whole panel when unlicensed, which would hide a SuperSearch tab too – so without a licence the toggle is ignored and SuperSearch stays in its own, fully functional panel. This protects SuperSearch's availability rather than restricting it: trial users always have SuperSearch in its original location.
- **Alt+S** and the editor right-click **SuperSearch** action are now mode-aware: they activate whichever host is in use and, in tab mode, switch the Assistant panel to the SuperSearch tab. When SuperSearch is hosted in the tab, opening the standalone panel shows a short placeholder pointing to the new location (Trados always registers the standalone ViewPart from `plugin.xml`, so it can't be hidden outright).

### Fixed (SuperSearch preview pane: text wasn't selectable or copyable – [Supervertaler-Workbench#202](https://github.com/Supervertaler/Supervertaler-Workbench/issues/202))

- A user reported (via the Workbench issue tracker) that text in SuperSearch's results couldn't be selected or copied, so there was no way to copy a previous translation out of SuperSearch to reuse it. For patent and technical translators, SuperSearch is most valuable as a cross-file concordance – "how did I translate this phrase before?" – and the natural next step, copying the prior target verbatim, wasn't possible.
- The fix targets the **preview pane** (the full source/target boxes below the results grid), not the results grid itself: the grid is a `DataGridView` with full-row selection, where individual cell text genuinely can't be selected, and the preview pane shows the complete untruncated text anyway. The preview boxes are read-only `RichTextBox` controls, which already supported mouse selection and Ctrl+C – but had no right-click menu and didn't respond to Ctrl+A.
- Both preview boxes now have a right-click context menu with **Copy** (the current selection, or the whole box if nothing is selected), **Select All**, **Copy source**, and **Copy target**, plus **Ctrl+A** select-all support.


## [4.19.99] – 2026-05-13

### Changed (Workbench Chat: AI Settings dropdown + QuickLauncher menu)

- Workbench's floating Sidekick window was retired in Workbench v1.10.4 and its Chat surface was promoted into Workbench itself. The Trados plugin now reflects that in three user-facing places:
  - **AI Settings → "QuickLauncher prompts go to:" dropdown** – the second option, formerly labelled *Workbench Sidekick*, is now *Workbench Chat*. The dropdown tooltip is rewritten to describe the current destination (Workbench's AI tab → Chat sub-tab) instead of the retired Sidekick window.
  - **Editor right-click → QuickLauncher submenu** – the *&Send to Supervertaler Sidekick* item (only shown when the global setting routes to Workbench) is now *&Send to Supervertaler Workbench Chat*. Tooltip updated to match.
  - **Trados edit-history actionName** – when the Workbench bridge inserts a translation into the active document, the edit-history label that Trados records (visible in Studio's undo display) was *Supervertaler Sidekick*; now *Supervertaler Workbench*.
- The on-disk persisted setting value (`settings.QuickLauncherTarget == "WorkbenchSidekick"`) is **deliberately unchanged**. It's a stable internal identifier – existing users' saved preferences continue to resolve without migration. Same rationale as the Python-side `sidekick_bridge_server.py` module name, the `SidekickBridge` C# class, the `WorkbenchSidekickClient` C# class, and the on-disk `sidekick-bridge.json` handshake filename: all wire-protocol surfaces that pre-date the v1.10.4 retirement and that we keep stable so deployed plugin versions and Workbench installs keep talking to each other.
- Pairs with Workbench v1.10.24, which retargets the bridge to land on the AI top tab's Chat sub-tab (instead of the right-panel Chat next to the editor) when a QuickLauncher prompt arrives. The user explicitly asked for QuickLauncher prompts from Trados to land on the *full-width* Chat surface – they're general-purpose AI conversations, not segment-level translation work, so they deserve more screen real estate to read the response.


## [4.19.98] – 2026-05-12

### Fixed (Proof-read report segments scrambled after ticking an issue's checkbox – [#28](https://github.com/Supervertaler/Supervertaler-for-Trados/issues/28))

- A user reported that after running a proof-read and ticking a checkbox in the report to mark an issue as addressed, the remaining issue cards reordered non-sequentially (e.g. 844, 633, 623, 493, 494, 495). Until the first checkbox click the segments were in order; the click-then-relayout cycle scrambled them.
- Root cause: `List<T>.Sort` in .NET is not stable. The comparer in `OnResultsPanelResize` (`ReportsControl.cs`) sorted prompt-log cards by timestamp (newest first) and returned `0` for any pair of issue cards, expecting them to keep their original insertion order. An unstable sort is free to reorder equal-keyed elements – and did, the moment a card was removed.
- Fix: explicit `SegmentNumber` tie-break for two issue cards. Equal-class issue cards now have a deterministic order regardless of how they sit in `_resultsPanel.Controls`, so any future removal path that triggers a relayout keeps the report sequential.


## [4.19.97] – 2026-05-11

### Added (Supervertaler blue icon on every plugin dialog title bar)

- Every plugin dialog (Settings, Prompt Editor, Termbase Editor, Term Picker, About, Setup, all the Add/Edit/Bulk/Merge/Save dialogs, Usage Statistics, Supported Models, Distill Choice, Legacy Memory Bank Migration, SuperMemory Quick Add) now shows the blue Supervertaler icon in its title bar (and in the Alt+Tab switcher and Windows taskbar) instead of the generic WinForms placeholder icon.
- Bundled the existing `sv-icon-512.png` as a multi-resolution `sv-icon.ico` (16, 20, 24, 32, 40, 48, 64, 96, 128, 256 px) embedded resource, loaded once by a new `Core/IconHelper.AppIcon` and assigned to every Form constructor. The 16-px frame is hand-tuned in the generated .ico so the title bar render stays crisp at 100% DPI; Windows picks the larger frames automatically for high-DPI displays and the taskbar.


## [4.19.96] – 2026-05-11

### Fixed (Prompt Editor: top fields don't align with Prompt content's text area)

- Name / Description / Default-mode combo all anchored their right edges 12 px from the dialog edge, but the Prompt content textbox below has a vertical scrollbar inside it, so its actual **text area** ends ~17 px earlier than its border. The top row looked misaligned with the content area below. Added a 17-px scrollbar-gutter offset to the right margin of Name, Description and the Default combo (Width / x adjusted by the same amount), so now all four right edges align with the Prompt content's text area, not its scrollbar-inclusive border.


## [4.19.95] – 2026-05-11

### Fixed (Prompt Editor: "Default:" combo not aligned with Description / Prompt content right edges)

- The **Description** and **Prompt content** textboxes anchor to the dialog's right edge (so they stretch with the form), but the **Default:** label and combo on the Mode row sat at fixed x coordinates with no right anchor. On a resized or DPI-scaled dialog the combo's right edge drifted leftward relative to Description, making the layout look unbalanced. Anchored both controls to the right edge, with the combo's right margin set to the same 12 px used by the other textboxes — now all four right edges line up regardless of dialog size.


## [4.19.94] – 2026-05-11

### Fixed (Prompt Editor: "Default:" label butting up against the combo box)

- v4.19.93 fixed the checkbox-to-label gap but left the label-to-combo gap too tight — "Default:" was visually merging into the "Assistant" combo dropdown to its right. Pulled `_lblDefaultMode` back to x=420 and pushed `_cboDefaultMode` to x=495 / width=95, leaving ~20 px on both sides of the label.


## [4.19.93] – 2026-05-11

### Fixed (Prompt Editor: "Default:" label still touching "Copy to clipboard" after v4.19.92)

- v4.19.92 pushed `_lblDefaultMode` from x=385 to x=415 but the autoscaled "Copy to clipboard" checkbox is wider in practice than the metric suggested, so the label was still butting up against the checkbox with no visible gap. Pushed `_lblDefaultMode` from x=415 to x=435 and `_cboDefaultMode` from x=470 / width=105 to x=490 / width=100. Combo ends at x=590, still 10 px from the 600 px dialog edge, with no functional change.


## [4.19.92] – 2026-05-11

### Fixed (Prompt Editor: "Default:" label clipped by the "Copy to clipboard" checkbox)

- The Mode row added in v4.19.91 laid out the "Copy to clipboard" checkbox and the "Default:" label slightly too close together — at Segoe UI 9 pt the checkbox's autoscaled text ran into the label, clipping the "D" so the user only saw "efault:". Pushed `_lblDefaultMode` from x=385 to x=415 and `_cboDefaultMode` from x=440 / width=130 to x=470 / width=105. Combo still fits comfortably within the 600 px dialog, the checkbox now has ~25 px breathing room on its right edge, and there's no functional change.


## [4.19.91] – 2026-05-11

### Added (QuickLauncher prompts can now copy to clipboard instead of sending to the AI Assistant)

- A QuickLauncher prompt can now be configured with **two destinations**: the in-Trados AI Assistant (or Workbench Sidekick, per the user's existing global setting) **and** the system clipboard. When both are enabled, the prompt's entry in the **Ctrl+Q QuickLauncher menu** shows a cascading submenu (▶) with two options — *"Send to Supervertaler Assistant"* (`S` accelerator) and *"Copy prompt to clipboard"* (`C` accelerator) — letting the user pick the destination at runtime without leaving the keyboard.
- Use case the feature was built for: a translator who has a claude.ai project set up for the current job and wants to send a per-segment question (using the same variable-substituted prompt that already works inside Trados) into that browser-based project chat instead of the in-Trados chat. The clipboard option does the variable expansion against the active segment exactly as before, copies the result to the system clipboard, and stays out of the way — paste into the external chat (claude.ai, ChatGPT, Gemini, anywhere) and send manually.
- **Default mode** controls which submenu item gets first-Enter activation. The default mode is rendered first in the submenu so the natural keyboard flow (Ctrl+Q → type-to-search → Enter → Enter) fires it without explicit selection.
- Single-mode prompts (the implicit default for every existing prompt — just *"Send to Assistant"*) keep their previous flat menu behaviour: one click, no submenu, no behavioural change. **All current prompts in users' libraries continue to work exactly as before** — the new fields are opt-in via the prompt editor.

### Added (Prompt Editor: Mode row)

- The **Prompt Editor** dialog (Settings → Prompts → Edit / New) now shows a new **Mode** row for prompts whose category starts with `QuickLauncher`. Two checkboxes — `Send to Assistant` (ticked by default) and `Copy to clipboard` — plus a `Default:` dropdown that greys out unless both modes are ticked.
- Built-in (default) prompts can have their mode toggled even though their content stays read-only, because mode selection is a routing preference rather than a content edit — same logic that already let users hide a default prompt from the menu without cloning it first.

### Implementation notes

- New `PromptTemplate.QuickLauncherModes` (`List<string>`, defaults to `["assistant"]`) and `PromptTemplate.DefaultMode` (`string`, defaults to `"assistant"`).
- YAML frontmatter accepts both inline-list syntax (`quicklauncher_modes: [assistant, clipboard]`) and a comma-separated string (`quicklauncher_modes: assistant, clipboard`). Multi-line YAML lists with `- assistant` indented bullets are NOT supported by the existing line-based parser, deliberately — keeps the parser implementation untouched.
- The writer only emits `quicklauncher_modes:` / `default_mode:` when they differ from the implicit defaults, so existing single-mode prompts round-trip without any YAML churn.
- Whitelist of accepted mode values (`assistant`, `clipboard`) — unknown values are silently dropped on parse to keep typos from making prompts unreachable.
- `QuickLauncherAction.Run()` refactored: the per-prompt click handler is split into a dispatcher (`RunPromptInMode`) and two backends (`DispatchToAssistant`, `DispatchToClipboard`). The existing Sidekick / in-Trados-Assistant routing with silent-fallback-on-Sidekick-failure is preserved unchanged within `DispatchToAssistant`.
- Clipboard write uses `TextDataFormat.UnicodeText` and retries up to 3× with 50 ms delays — covers the classic Office / TeamViewer clipboard contention. Silent on success (menu close is itself confirmation); only surfaces a MessageBox if every retry failed.

---

## [4.19.88] – 2026-05-08

### Added (Project field on term entries)

- Term entries now have a **Project** free-text metadata field for tracking which project a term came from (e.g. `PROJ-033 patent` or a job code). Surfaces in the Termbase Editor as a sortable/filterable column, in the term-entry edit dialog as a tooltipped optional field, and in the inline grid editor's row-edit and inline-add paths.
- Schema migration adds `project` and `client` columns to `termbase_terms` if they aren't already present (Workbench-shared databases already had them via the Workbench's own migration; older Trados-only databases pick them up on first launch).
- The Project field is **bookkeeping only** – it is *not* sent to the LLM in translation prompt blocks. Domain and Client cover the LLM-context use case; Project is for the user's own organisation. If you want a term's project context to inform translation, put it in Notes instead.
- The Termbase Editor's search box now also matches against Notes, Domain, Client, and Project (previously only matched Source / Target / Definition).

---

## [4.19.87] – 2026-05-08

### Fixed (Reports tab: cost shown as "free" for unknown models, masking real cost)

- The cost estimator's pricing table only covers the curated provider lists. When users typed a non-curated OpenRouter model ID (e.g. `deepseek/deepseek-v4-pro`) into the model picker, the lookup missed and `EstimateCost` returned `0`, which the Reports tab then rendered as "free" — same label used for genuinely free Ollama models. Misleading: a $0.02 DeepSeek call appeared identical in the log to a free local Ollama call.
- Now `PromptLogEntry` carries an `IsCostKnown` flag set from `TokenEstimator.HasPricing(model)`. When the model isn't in the pricing table the SummaryLine and "Copy all" output show **unknown** instead of "free", with a "(model not in pricing table)" hint in the long-form output. Genuinely free models (Ollama and any explicit zero-rate entry) continue to show "free" exactly as before.

---

## [4.19.86] – 2026-05-08

### Added (Anthropic prompt caching: ~80% input-cost reduction on batch operations)

- **Batch Translate and Batch Proofreader now mark the static portion of the system prompt with `cache_control: ephemeral`**, so the first batch in a run pays a one-time 1.25× cache-write surcharge and every subsequent batch within ~5 minutes pays only 0.1× of the input rate for the cached portion. For a typical 1000-segment Sonnet 4.6 run with full document context, this drops the API bill from ~$3.69 (estimate) down to ~$1.36 (real) – about a 60–65% reduction overall, and ~80% on the cached portion alone.
- New `enablePromptCaching` parameter on `LlmClient.SendPromptAsync`; passed `true` by both batch services. Single-shot callers (chat, AutoPrompt, single-segment translate) leave it `false` to avoid the 1.25× write-surcharge on calls that won't benefit from cache reads.
- Anthropic native: system prompt is sent as a `[{"type":"text","text":"...","cache_control":{"type":"ephemeral"}}]` block.
- OpenRouter → Anthropic: same `cache_control` marker passed through in OpenAI-shape content array.
- Other providers (OpenAI, DeepSeek, Gemini 2.5+) get implicit automatic caching at the provider layer because the system prompt is byte-stable across batches; no marker needed. Grok / Mistral / Ollama: no caching available, flag is a no-op.

### Added (Reports tab: real API-reported token counts and cost, not estimates)

- **The Reports tab now shows real provider-billed token counts and the real cost of each call, with cache-hit tokens broken out separately**, instead of the previous chars/4 + list-price estimate. The `~` prefix on the cost is dropped when actuals are available; when caching contributed, the cache-hit count is shown inline (e.g. "830,000 in (720,000 cached) / 32,000 out · $1.36").
- New `ApiUsage` type in `LlmClient` capturing `RegularInputTokens` / `CacheReadTokens` / `CacheWriteTokens` / `OutputTokens`. Populated from each provider's `usage` block on every successful call.
- Anthropic native: parses `usage.input_tokens` / `output_tokens` / `cache_creation_input_tokens` / `cache_read_input_tokens`.
- OpenAI shape (OpenAI native, OpenRouter, DeepSeek, Mistral, Grok, Custom): parses `usage.prompt_tokens` / `completion_tokens` plus either `prompt_tokens_details.cached_tokens` (OpenAI native) or `cache_creation_input_tokens` / `cache_read_input_tokens` (OpenRouter passing through Anthropic provider).
- New `TokenEstimator.ComputeActualCost` applies cache-aware multipliers per provider: Anthropic 0.1× read / 1.25× write, OpenAI 0.5× read, DeepSeek 0.1× read, Gemini 2.5+ 0.25× read, others passthrough.
- Falls back gracefully to the chars/4 + list-price estimate when usage isn't available (Ollama, parse failures, providers that don't return usage).

### Added (AI Cost Guide: in-app help dropdown + permanent disclaimer link)

- New help dropdown item **AI Cost Guide** added to the Chat / Batch Operations / Reports tab help menus, opening the GitBook page that explains how costs are computed, links to every provider's own usage console, and gives per-model cost-per-document estimates.
- New permanent footer in the Reports tab: "Token counts and costs are estimates · AI Cost Guide" (right-aligned, light text, link). Always visible so users see the disclaimer without having to click anything; tooltip explains the chars/4 heuristic for the legacy estimate path. With the actual-usage feature above, this disclaimer now applies only to providers that don't report usage (Ollama and edge cases) – everything else shows real billed numbers.

### Added (Batch Translate: pre-run truncation warning when document is bigger than `Max segments`)

- When `IncludeDocumentContext` is on and the active document has more segments than the configured `Max segments` cap, the Batch Translate panel now logs a warning before the run starts, naming exactly which segments will be visible to the AI and which will be omitted (per the existing first-80% + last-20% truncation rule). Previously this happened silently inside `TranslationPrompt.BuildSystemPrompt` and users had no way to know their middle-of-document segments weren't being shown to the AI – which can hurt terminology consistency on long jobs.

### Fixed (`DocumentContextMaxSegments` default mismatch)

- The `AiSettings.DocumentContextMaxSegments` property was auto-initialised to `20`, while the AI Settings panel's NumericUpDown range was 100–2000 with a nominal default of `500`. Effect: any user whose persisted JSON didn't yet contain the key was being silently clamped to 100 segments of document context (well below the panel's intended default), which is too small for any non-trivial document. The auto-init now matches the panel's intended default of `500`. Users with an explicitly saved value are unaffected.

### Fixed (cost estimator: Claude Opus 4.6 / 4.7 priced at 1/3 the real rate)

- The estimator's pricing table had Opus 4.6 and 4.7 at $5 / $25 per million input/output tokens. Anthropic's published Opus 4.x rate is $15 / $75 per million – the table was off by 3×, causing the in-app cost estimate to under-shoot the real bill (e.g. estimate ~$0.57 vs Anthropic Console ~$0.92 on a single AutoPrompt call). Corrected to $15 / $75. Sonnet 4.6 ($3 / $15) and Haiku 4.5 ($1 / $5) were already correct.

---

## [4.19.85] – 2026-05-07

### Fixed (Chat header: source preview leaked Trados inline-formatting tags)

- **The "Source: …" preview at the top of the chat panel was showing raw Trados inline-formatting tags – e.g. `Source: "<cf bold=True>SEVT</cf> <cf size=…>` – instead of plain readable text.** The target preview already used `SegmentTagHandler.GetFinalText` (which returns the visible text only), but the source preview called `Source.ToString()` directly, which serialises the segment with all formatting markers.
- Fix at [`AiAssistantViewPart.UpdateContextDisplay`](src/Supervertaler.Trados/AiAssistantViewPart.cs): route the source through `SegmentTagHandler.GetFinalText` too.

---

## [4.19.84] – 2026-05-07

### Fixed (Chat panel: Send / Stop / Clear button labels clipped at high DPI)

- **At 150% Windows display scaling, the bottom-row chat buttons clipped to "Cle" / "Sto" / partial "Send".** The buttons had explicit `Size = new Size(UiScale.Pixels(60 / 48 / 48), 26)` – tight even at 100%, and once the rendered text width at the higher DPI exceeded the pre-scaled width, the labels chopped off.
- Fix at [`AiAssistantControl.cs`](src/Supervertaler.Trados/Controls/AiAssistantControl.cs): switch all three to `AutoSize = true` with `AutoSizeMode.GrowAndShrink` and the previous widths kept as `MinimumSize`. Internal padding (8 px each side) so the labels never touch the button border.

### Fixed (SuperMemory toolbar: "Distill" clipped at high DPI on narrow side panel)

- **In the chat header, "Distill" clipped to "Disti..." at 150% scaling because the toolbar (`Memory Bank` label + dropdown + `?` + Process Inbox + Health Check + Distill) is a single non-wrapping row that ran out of horizontal space when the Trados side panel was at typical width and everything was scaled up.**
- Fix at [`SuperMemoryToolbar.cs`](src/Supervertaler.Trados/Controls/SuperMemoryToolbar.cs): trim the Memory Bank dropdown width from 180 → 130 logical px. Typical bank names ("default", "test-mb", "client-x") still display fully; the saved horizontal room is enough for "Distill" to fit at 150% scaling. Longer names still scroll inside the dropdown when opened.

---

## [4.19.83] – 2026-05-07

### Fixed (TermLens panel: big bold "A" font-size button clipped at the bottom)

- After the v4.19.79 small-A / big-A redesign, the bigger bold "A" on the right of the TermLens header strip was getting its bottom edge clipped at high Windows display scaling – the small regular "A" rendered fully, but the bold one didn't have enough vertical room inside the 28 px-tall header panel.
- Fix at [`TermLensControl.cs`](src/Supervertaler.Trados/Controls/TermLensControl.cs): bump header height 28 → 32, and trim the big-A font from 11 pt → 10 pt. The size delta vs the small 7 pt A is still clearly visible (and tooltips spell out increase / decrease either way), but the bold A now has comfortable clearance even at 150% scaling.

---

## [4.19.82] – 2026-05-07

### Fixed (Settings → Termbases tab: multiple high-DPI clipping issues)

- **At 150% Windows display scaling on Settings → Termbases, three different layout problems were visible at once:**
  - The right-aligned button row clipped to "Ope Export Import − +" – the **Open** button cropped from "Open" to "Ope", and the **− Remove** / **+ Add** buttons lost their text labels entirely (only the symbol remained).
  - In the termbases grid, the bold column headers **"Write"** and **"Terms"** clipped to "Wr..." and "Ter...".
  - The bottom rows ("Panel font size", "Term shortcuts", "Shortcut delay") had their input controls overlapping the labels, because the labels' AutoSize widths grew past the fixed x=130 input column.
- Fix at [`TermLensSettingsForm.cs`](src/Supervertaler.Trados/Settings/TermLensSettingsForm.cs):
  - All five buttons (Open / Export / Import / Remove / Add) now use `AutoSize = true` with `AutoSizeMode.GrowAndShrink` and `MinimumSize` for the previous fixed widths. The right-edge anchoring chain uses each button's measured `PreferredSize.Width` instead of literal pixel offsets.
  - DataGridView column widths bumped: Read/Write 54→80, Project 72→90, CS 40→56, Terms 60→80. (DGV column widths don't participate in AutoScaleMode.Dpi scaling, so they need explicit headroom.)
  - The bottom-row inputs are now positioned at a shared `inputX` computed from the widest of the three labels' actual `PreferredSize.Width`, with the trailing unit labels ("pt", "ms") chained off the input's `Right` edge. NUDs widened from 60/70 to 80/90 to give the digits a comfortable area after autoscale.

---

## [4.19.81] – 2026-05-07

### Fixed (Settings → Prompts: toolbar and system-prompt buttons clipped at high DPI)

- **At 150% Windows display scaling on the Settings → Prompts tab, the toolbar buttons "New", "Restore" and "Refresh" all clipped their last character ("Ne", "Restor", "Refres"), and the two system-prompt buttons below the right-hand pane ("Edit System Prompt" / "Reset to Default") were similarly clipped.** Cause: each button had a hard-coded `Width` (45 / 65 / 130 / 120 px) chosen to fit at 100% scaling – tight even there, and not enough text room left after the AutoScaleMode.Dpi pass at higher DPIs.
- Fix at [`PromptManagerPanel.cs`](src/Supervertaler.Trados/Controls/PromptManagerPanel.cs): switch every toolbar button (via the `CreateToolbarButton` helper) and the two system-prompt buttons to `AutoSize = true` with `AutoSizeMode.GrowAndShrink`. The previous explicit widths are kept as `MinimumSize` so very-short labels still get a comfortable click target. Position the "Reset to Default" button and the status label dynamically against their neighbours' measured `PreferredSize` / `Right` edges instead of fixed x coordinates, so wider buttons at high DPI don't push them on top of each other.

---

## [4.19.80] – 2026-05-07

### Added (Settings: UI scale dropdown can now go below 100%)

- **The Supervertaler UI scale dropdown in Settings → General previously bottomed out at 100%, so users on hi-DPI machines who found Windows' global scaling too aggressive had no way to dial only the plugin back without changing system-wide settings.**
- Add 70%, 80%, 90% to the existing 100% / 110% / 125% / 150% options. Combined with the auto-detected Windows DPI as the base scale, this lets a user on a 4K monitor at 200% Windows scaling drop the plugin to 200% × 0.8 = 160% effective, etc., without affecting the rest of Trados or other apps.
- Floor stops at 70%: below that the system-rendered widgets (NumericUpDown spinners, checkbox boxes) become disproportionately large vs the text, producing weird-looking layouts. The underlying validation in `TermLensSettings.Load` already permits anything `> 0 && <= 3.0`, so the storage layer was always ready.

---

## [4.19.79] – 2026-05-07

### Fixed (About dialog: "Source code available for security audit" clipped at high DPI)

- **At 150% Windows display scaling, the leading "S" of "Source code available for security audit" in the About dialog was clipped behind the shield emoji.** Cause: the link's `Location.X` was `leftPad + 30`, but the shield emoji's AutoSize width grew at high DPI past that 30 px gap, so the wider emoji's bounding box ate into the link.
- Fix at [`AboutDialog.cs`](src/Supervertaler.Trados/Controls/AboutDialog.cs): position the link dynamically from `shieldLabel.Right + 6` instead of a fixed 30 px gap.

### Fixed (TermLens panel: A+/A- font-size buttons hard to tell apart at high DPI)

- **The two font-size buttons at the top of the TermLens panel had the same problem the AI Assistant chat header had** before 4.19.74: design said "A+" and "A−" with a 2-point font-size difference, but at low DPI the +/− glyphs collapsed to thin strokes and both buttons looked like plain "A".
- Fix at [`TermLensControl.cs`](src/Supervertaler.Trados/Controls/TermLensControl.cs): same redesign as the chat header in 4.19.74 – drop the +/− glyphs, use a big bold "A" (11pt) for increase and a small regular "A" (7pt) for decrease, and add explicit "Increase TermLens font size" / "Decrease TermLens font size" tooltips on hover.

---

## [4.19.78] – 2026-05-07

### Fixed (AI Settings: Test Connection text wrapped, Show button clipped at high DPI)

- **At 150% Windows display scaling on Settings → AI Settings, the "Test Connection" button text wrapped to two lines (button was 120 px wide; the scaled text needed more), and the "Show" button next to the API Key field was clipping to "Sho" (button was only 50 px wide).** Same general cause as the NUD fix in 4.19.77 – design-time pixel widths were tight even at 100% and ran out of horizontal room once `AutoScaleMode.Dpi` finished scaling the control.
- Fix at [`AiSettingsPanel.cs`](src/Supervertaler.Trados/Controls/AiSettingsPanel.cs): widen `_btnShowKey` from 50 → 80 and `_btnTestConnection` from 120 → 160. Both now have comfortable padding at any DPI.

---

## [4.19.77] – 2026-05-07

### Fixed (AI Settings: cramped NumericUpDowns at high Windows DPI)

- **At 150% Windows display scaling, the "Batch size" and "Surrounding segments" numeric inputs in Settings → AI Settings rendered with so little room for digits that the value was hard to read** – the system-drawn spinner buttons (which don't scale identically with the rest of the control) ate most of the visual width, leaving only a few pixels for the actual number. The "Surrounding segments" NUD also overlapped its own label, because the label's AutoSize width grew at high DPI but the NUD's x position was fixed at 210 px.
- Fix at [`AiSettingsPanel.cs`](src/Supervertaler.Trados/Controls/AiSettingsPanel.cs): bump both NUDs from `Width = 60` to `Width = 80` so the AutoScaleMode.Dpi pass produces a visibly comfortable text area at any scaling. Also position `_nudSurroundingSegments` dynamically from `_lblSurroundingSegments.Right + 8` instead of the fixed x=210 column the other rows use, so the wider label at high DPI doesn't push it.
- The other two NUDs in this panel (`_nudOllamaTimeout` at width 75, `_nudMaxSegments` at width 80) were already wide enough; no change there.

---

## [4.19.76] – 2026-05-07

### Fixed (Hi-DPI: every Settings dialog and pop-up now scales with Windows display scaling)

- **Earlier today's fix only covered the `BatchTranslateControl` (Batch Operations panel) and the `AiAssistantControl` (Chat header). The other 23 dialogs and panels in the plugin – Settings, AI Settings, Prompts, Termbase Editor, AI Proofreader Reports, About, Setup, every term-add / merge / preview pop-up – still relied on WinForms' default font-based autoscaling, which doesn't reliably propagate through plugin UserControls hosted inside Trados.** Nobody had complained yet, but layout would have squished at 125% / 150% / 200% Windows display scaling on every one of those surfaces.
- Fix: each of those 23 forms / UserControls now sets `AutoScaleMode = AutoScaleMode.Dpi` in its constructor or `BuildUI()` method. This activates WinForms' DPI-based scaling pass, which scales control sizes and positions by the raw `currentDpi / designDpi` ratio – the same mechanism `TermPopup` has used successfully for the term-popup. The two surfaces with their own UiScale-driven layout (`AiAssistantControl`, `BatchTranslateControl`) keep `AutoScaleMode = None` so they don't double-scale on top of UiScale.
- No functional behaviour change at 100% Windows scaling. At 125% / 150% / 200% scaling, plugin dialogs now scale uniformly with the rest of Trados Studio's UI instead of staying at 100% and squishing.

---

## [4.19.75] – 2026-05-07

### Changed (HelpSystem: use canonical GitBook URL slugs, drop reliance on 301 redirects)

- **The `?` help buttons throughout the plugin used to open URLs like `https://supervertaler.gitbook.io/help/trados/termlens`, which GitBook then 301-redirected to the actual published slug `https://supervertaler.gitbook.io/help/features/termlens`.** Two problems with relying on the redirect: (a) URL fragments such as `#reports-tab` are preserved across redirects only because of browser-side fragment carrying (HTTP spec behaviour), not GitBook itself; (b) GitBook may eventually prune legacy redirect entries, especially after repo migrations like the recent docs split into the standalone `Supervertaler-Help` repo.
- Fix at [`HelpSystem.cs`](src/Supervertaler.Trados/Core/HelpSystem.cs): every `Topics.*` constant now holds the canonical slug GitBook actually publishes. The slug is generated from each `## 🧩 Section` header in [`SUMMARY.md`](https://github.com/Supervertaler/Supervertaler-Help/blob/main/SUMMARY.md), so `Overview` becomes `get-started/trados`, `TermLensPanel` becomes `features/termlens`, `BatchTranslate` becomes `features/batch-operations/batch-translate`, and so on. Verified against the live sitemap (`sitemap-pages.xml`): every constant now resolves with HTTP 200 and zero redirects.
- Mirror of the equivalent fix done earlier today on the Workbench side ([`modules/help_system.py`](https://github.com/Supervertaler/Supervertaler-Workbench/blob/main/modules/help_system.py)). Also updates the audit table in [`HELP-LINKS.md`](HELP-LINKS.md) at the repo root – previously last audited 2026-03-13 with the legacy `gitbook.io/trados` base URL.

---

## [4.19.74] – 2026-05-07

### Fixed (Chat panel: long histories caused new responses to vanish)

- **In the AI Assistant Chat tab, after ~23 explain/define prompts or a 40,000+ character AutoPrompt response, the panel would freeze – the OpenRouter / GWDG / Cortecs API would return the response (visible in OpenRouter observability, tokens billed) but the chat would scroll upwards and not display anything new. The only workaround was to clear the chat.** Reported by a user on 2026-05-06; not actually fixed by the v4.19.1 chat-scroll rewrite.
- Root cause at [`AiAssistantControl.UpdateChatScrollRange`](src/Supervertaler.Trados/Controls/AiAssistantControl.cs): the formula `_chatPanel.AutoScrollMinSize = new Size(0, _messageFlow.Top + desiredHeight)` was meant to be a "belt and braces" override of WinForms' automatic scroll-range tracking. But `_messageFlow.Top` is in `_chatPanel`'s scrolled coordinate system: when the user is scrolled down N px into a long chat, Top reads as `-N`, not as the unscrolled top. So at the bottom of a long history the formula collapsed to roughly the viewport height instead of the full content height. WinForms then clamped the scroll position to fit that artificially-small range, the panel snapped back to the top, and the new bubble landed below where the panel now thought the world ended.
- Fix uses `desiredHeight` directly (the actual content height). `_messageFlow` always lives at logical y=0 inside `_chatPanel` – it's the only child, no header above it – so the unscrolled bottom is just the content height. Verified locally and matches the reported symptom exactly.

### Fixed (Plugin layout broke at high Windows DPI scaling)

- **At Windows display scaling above 100% (e.g. 150% on a 2560×1440 panel), the Translate / Proofread radio buttons in Batch Operations overlapped each other, the Scope/Limit/Prompt rows squished together, and the Copy/Paste Clipboard buttons collided.** Reported by a user on a real machine; reproduced locally by flipping Windows scaling to 150%.
- Cause: the existing `UiScale.Factor` defaulted to 1.0 and was only changed by a manual TermLens settings slider. Plugin layouts that called `UiScale.Pixels()` therefore stayed at 100% sizing even as Windows scaled fonts up, and any literal pixel coordinates (e.g. `Location = new Point(100, y)` for the Proofread radio button) couldn't account for the wider AutoSize "Translate" label at higher font sizes.
- Fix at [`UiScale`](src/Supervertaler.Trados/Core/UiScale.cs) now auto-detects the system DPI scale (`Graphics.DpiX / 96`) on plugin startup and uses it as a base layer beneath the user-configurable settings slider. `UiScale.Pixels(N)` and `UiScale.FontSize(N)` now multiply by `SystemScale × UserFactor`, so existing call sites get DPI awareness for free and the slider becomes a per-user multiplier on top of system scaling rather than the only source of scaling. `BatchTranslateControl` and `AiAssistantControl` set `AutoScaleMode = AutoScaleMode.None` so WinForms' built-in font-based autoscaling doesn't apply a second pass on top of UiScale and double up. Every hard-coded `Location` / `Size` / `Width` / `Height` literal in `BatchTranslateControl` is now wrapped through `UiScale.Pixels`, and the Translate / Proofread radio button positions are now computed dynamically from the previous button's measured `PreferredSize` so wider fonts can't push them on top of each other. The Copy / Paste Clipboard buttons get the same dynamic-position treatment.

### Fixed (AI Assistant: A+ / A- font-size buttons hard to tell apart at high DPI)

- **The two font-size buttons at the top of the Chat tab read "A+" and "A−" (with U+2212 minus sign), differing only by the +/− glyph and a 2-point font-size difference. At a user's display DPI the +/− glyphs collapsed into thin strokes and both buttons looked like plain "A". They clicked the smaller-font one expecting "increase" – and got the opposite.**
- Fix at [`AiAssistantControl`](src/Supervertaler.Trados/Controls/AiAssistantControl.cs) drops the +/− glyphs entirely and uses font size as the sole visual cue: a big bold "A" on the right (`UiScale.FontSize(11f)` Bold) and a small regular "A" on the left (`UiScale.FontSize(7f)` Regular). Same pattern Edge and Word reading-view use, immediately readable at any DPI. Both buttons now also have explicit tooltips ("Increase chat font size" / "Decrease chat font size") so anyone hovering sees which is which.

### Improved (Batch Operations: AutoPrompt visible-but-disabled in Proofread mode)

- **Previously the AutoPrompt link disappeared entirely when the user selected Proofread mode**, which led to a confused support thread ("AutoPrompt has vanished – I tested on another machine to make sure it's not my screen resolution"). The link is hidden because AutoPrompt generates a *translation* prompt and doesn't apply to proofread runs, but that intent was invisible.
- Fix at [`BatchTranslateControl`](src/Supervertaler.Trados/Controls/BatchTranslateControl.cs) keeps the link visible in Proofread mode but greys it out and prepends an "Available in Translate mode only" line to its tooltip so users immediately understand why it's disabled rather than thinking it's a bug.

---

## [4.19.73] – 2026-05-06

### Fixed (Proofread: report numbering drifted from the Trados grid after merges/splits)

- **In projects with merged or split segments, the segment numbers shown on Reports-tab issue cards no longer matched the numbers Trados shows in the editor grid.** Every merge added a one-segment offset to all subsequent rows, so on a project with three merges the last segment of the file was reported with a number that was 3 lower than the editor grid said. Reported by a user on a real client project.
- Root cause at [`AiAssistantViewPart.cs`](src/Supervertaler.Trados/AiAssistantViewPart.cs:4310): the v4.8.0 multi-file alignment logic numbered segments by counting iterations through `_activeDocument.SegmentPairs` (`fileSegIdx++` per pair) and only used the parsed segment ID as a heuristic for detecting file boundaries. Iteration count and Trados' own segment IDs diverge after a merge: Trados retires the higher ID of the merged pair (so the editor jumps from 3 to 5), but the iteration count keeps marching 1, 2, 3, 4, 5… → off-by-N from then on. Splits had a similar issue.
- Fix uses `pair.Properties.Id.Id` directly as the per-file segment number when it parses as an int, which is exactly the number Trados shows in the grid and which it preserves correctly across merges and splits. Falls back to the iteration counter only when the segment ID isn't parseable (older formats / exotic filters).

### Fixed (AutoPrompt: blank-line bloat in saved prompts)

- **AutoPrompt-generated prompts were arriving in the Edit Prompt dialog with two or three blank lines between sections** instead of a single blank line. Reported by Michael with a screenshot showing the gaps around `1. ROLE`, `2. TECHNICAL DOMAIN`, etc. Cause: `PromptGenerator.ParseGeneratedPrompt` only `.Trim()`s the AI's response between the `===PROMPT_START===` / `===PROMPT_END===` delimiters, and the meta-prompt didn't tell the model to use single blank-line separators – so the model fell back to its default "looks pretty" spacing, which means 2-3 blank lines around section headings.
- Fix at [`PromptGenerator.cs`](src/Supervertaler.Trados/Core/PromptGenerator.cs) now collapses 3+ consecutive newlines to a single blank line in `ParseGeneratedPrompt`, and the meta-prompt under "OUTPUT INSTRUCTIONS" now spells it out: *"Use exactly ONE blank line between sections, paragraphs, and list blocks. Never insert two or more consecutive blank lines."* Belt-and-braces: the regex catches model output that ignores the instruction; the instruction nudges newer / better models toward the right output without the regex having to clean up.

### Fixed (Batch operations: focus steal back to Translation Results between batches – proper fix)

- **The v4.19.66 batch-boundary fix was not enough on slow runs and on the very last batch.** Re-Activating the Supervertaler Assistant viewpart only at the START of the next "Translating batch N+1" message was timed correctly for fast runs (steal happens in the gap, Activate happens after the steal) but lost the race on slow API calls (where the steal landed mid-gap and stuck) and on the last batch (no next-batch message to counter the steal at all – user ended up on Translation Results when the run finished).
- Fix at [`AiAssistantViewPart.cs OnBatchProgress`](src/Supervertaler.Trados/AiAssistantViewPart.cs:3476) now also activates on `"✓ Batch X complete"` messages and uses the same dual sync + deferred `BeginInvoke` `Activate()` pattern that already worked for `OnNavigateToSegment` (v4.19.71). [`OnBatchCompleted`](src/Supervertaler.Trados/AiAssistantViewPart.cs:3567) at end of run also gets the dual Activate. The deferred call runs after Trados' queued focus steal and reliably wins.

---

## [4.19.72] – 2026-05-06

(Folded into 4.19.73 – no separate AppStore release. Same fixes ship in 4.19.73.)

---

## [4.19.71] – 2026-05-06

### Fixed (Reports tab: clicking an issue card kicked focus to Translation Results)

- **Every click on a proofreading issue card sent the user to Trados' built-in Translation Results pane, forcing a manual switch back to the Supervertaler Assistant pane to read the issue details.** Same root cause as the v4.19.66 batch-boundary fix: `SetActiveSegmentPair` fires Trados' active-segment-changed event, and the built-in Translation Results pane reacts by re-running its TM/MT lookups, which on Trados 18 brings its tab to the front. Reported by Michael while working through proofread results — described it as "annoying" and "I have to switch back to the Supervertaler Assistant pane every time I click into a heading."
- Fix at [`AiAssistantViewPart.OnNavigateToSegment`](src/Supervertaler.Trados/AiAssistantViewPart.cs) now calls `Activate()` on the Supervertaler Assistant view-part after navigating, so the user lands back on Reports immediately. Belt-and-braces: a synchronous `Activate()` handles the case where Trados raises its event inline, and a second `Activate()` posted via `BeginInvoke` handles the case where Trados queues the focus steal for a later UI tick — by running after the steal has already happened, the deferred call reliably wins. Same belt-and-braces pattern that worked for v4.19.66.

---

## [4.19.70] – 2026-05-06

### Fixed (Batch Operations: "Also add issues as Trados comments" jumped into the log area on mode switch)

- **The Trados-comments checkbox migrated down into the log panel the first time the user switched to Proofread mode.** Visible in Michael's screenshot: button + Preview prompt link sat correctly on the action row, but the checkbox was floating ~80 px lower, near the Log header. Caused by the `_btnTranslate.SizeChanged` lambda introduced in v4.19.69 capturing `y` by reference. `y` is the running layout cursor in [`BatchTranslateControl.BuildUI`](src/Supervertaler.Trados/Controls/BatchTranslateControl.cs); by the time the lambda fired (button text change "Translate" → "Proofread" → SizeChanged), `y` had been incremented past the action row by the rest of `BuildUI` (Clipboard buttons, log panel header, etc.), so the checkbox got placed at whatever Y the cursor had landed on. Classic closure-over-mutable-loop-variable trap.
- Fix at [BatchTranslateControl.cs](src/Supervertaler.Trados/Controls/BatchTranslateControl.cs) captures `y` into a local `actionRowY` before the lambda is created. Both the SizeChanged repositioner and the Preview prompt link's initial Y now reference `actionRowY` so they stay anchored to the action row regardless of when the lambda runs.

---

## [4.19.69] – 2026-05-06

### Fixed (CRITICAL: v4.19.68 broke the build)

- **`ClipboardRelay.FormatForProofreading` was passing `List<string> documentSegments` and `int maxDocumentSegments` to `ProofreadingPrompt.BuildSystemPrompt`** at [ClipboardRelay.cs:107-110](src/Supervertaler.Trados/Core/ClipboardRelay.cs), but v4.19.68 changed `BuildSystemPrompt`'s signature to take `List<(string source, string target)> documentSegments` and dropped the `maxDocumentSegments` parameter. The build error was masked by the `bash build.sh` skip in v4.19.68's "no build for now" instruction. Caught while doing the prompt-preview wiring in v4.19.69 — anyone trying to compile v4.19.68 would have hit it immediately. Fixed by updating `FormatForProofreading`'s parameter type to match and updating its caller in [AiAssistantViewPart.OnCopyToClipboardRequested](src/Supervertaler.Trados/AiAssistantViewPart.cs) to call the new `CollectBilingualDocumentContext` for proofread mode (so the clipboard text faithfully matches what the API path would now send).

### Improved (Batch Operations: "Preview prompt" link + checkbox positioning)

- **New "👁 Preview prompt" link next to the Translate / Proofread button** opens a read-only dialog showing EXACTLY what would be sent to the AI for the current configuration — assembled system prompt (incl. termbase, language-specific checks, full bilingual document context for proofread, and the active custom prompt) plus the numbered segment list. Reuses the same `ClipboardRelay` assembly as Copy-to-Clipboard so what users see is what the LLM would receive. Works in both API mode and Clipboard mode without toggling. The dialog has its own "Copy to clipboard" button as a bonus, so it doubles as a "manual paste into web LLM" path. New file: [PromptPreviewDialog.cs](src/Supervertaler.Trados/Controls/PromptPreviewDialog.cs); event wired through [BatchTranslateControl.PreviewPromptRequested](src/Supervertaler.Trados/Controls/BatchTranslateControl.cs) → [AiAssistantViewPart.OnPreviewPromptRequested](src/Supervertaler.Trados/AiAssistantViewPart.cs). Mirrors the equivalent Workbench feature.
- **Fixed the "Also add issues as Trados comments" checkbox text being clipped behind the Proofread button.** The checkbox was hardcoded at `x=140` while the action button at `x=12` is AutoSize – when the text changed from "▶ Translate" to the wider "▶ Proofread", the button extended past x=140 and covered the checkbox's leading characters. Reported by Michael with a screenshot showing "...so add issues as Trados comments" with the leading "Also" hidden. Fixed in [BatchTranslateControl.cs](src/Supervertaler.Trados/Controls/BatchTranslateControl.cs) by binding the checkbox position to `_btnTranslate.SizeChanged` (always sits at `_btnTranslate.Right + 8`). Same logic positions the new Preview prompt link, which sits after the rightmost visible control on the row regardless of mode.

---

## [4.19.68] – 2026-05-06

### Improved (Batch Proofread: full bilingual document context + citation discipline + Evidence field)

- **Batch Proofread now sees the entire document, source AND target, when reviewing each batch – not just source-only with a 500-segment cap.** The previous behaviour gave the model the full source for the whole document but bilingual data only for the 20 segments in the current batch. That made target-side consistency claims unverifiable: when the model said "you used X here but Y elsewhere" for the target, it was guessing about segments outside the current batch. New `CollectBilingualDocumentContext` in [AiAssistantViewPart.cs](src/Supervertaler.Trados/AiAssistantViewPart.cs) collects every segment pair (source + target) from the active document and feeds them into a redesigned `# DOCUMENT CONTENT` block in [ProofreadingPrompt.cs](src/Supervertaler.Trados/Core/ProofreadingPrompt.cs). Truncation is gone – proofreading is exactly the workflow where mid-document segments matter for cross-document consistency, so the 500-cap and the 80/20 split it enforced were actively harmful. Token cost roughly doubles (both sides now sent), which is fine for typical patent / legal / technical docs (<500 segments) and reasonable up to a few thousand on Sonnet/Opus.
- **Segment numbers in the model's output are now document-absolute, not within-batch.** [BatchProofreader.cs:137](src/Supervertaler.Trados/Core/BatchProofreader.cs) used to emit `[SEGMENT 0001]` … `[SEGMENT 0020]` for every batch (resetting per batch). The model couldn't tell whether `[SEGMENT 0007]` in its response meant the 7th segment of the document or the 7th of the current batch. Now the prompt uses `segments[i].Index + 1` everywhere, so the same number means the same segment in the user prompt, in `# DOCUMENT CONTENT`, in the model's response, and in any Evidence: citations the model produces.
- **New `Evidence:` field on every issue card – forces the model to ground terminology consistency claims in specific segment citations.** Output format spec in `ProofreadingPrompt.BuildSystemPrompt` now documents an optional Evidence: line; `ParseBatchResponse` captures it; `ProofreadingIssue.Evidence` carries it through to the UI; [ReportsControl.cs](src/Supervertaler.Trados/Controls/ReportsControl.cs) renders it between Issue and Suggestion in italic grey ("the *why* for the issue"). The right-click menu gains a "Copy evidence" option; "Copy all" includes evidence in the clipboard payload. The new Default Proofreading Prompt requires Evidence: for any consistency claim – inconsistency claims without concrete segment citations from `# DOCUMENT CONTENT` are not allowed.
- **Default Proofreading Prompt rewritten** to (a) stop duplicating the hardcoded base in `ProofreadingPrompt.BuildSystemPrompt` (persona, 5 categories, output format, language-specific Dutch/German/French checks, "no corrections" rule – all already auto-included), and (b) add four behaviours genuinely missing from the base: default-to-OK framing, citation discipline against `# DOCUMENT CONTENT`, a "Source query:" prefix for source-side errors (so source typos get flagged without proposing target changes), and explicit boundaries (don't re-engineer the source, don't propose alternative terminology without a citation, don't flag empty-target segments, patent / legal / technical text is deliberately stiff). Targeted at the false-positive patterns observed in real proofreads: the model fabricating "term X used elsewhere" claims, second-guessing the source's substantive claims, and treating stylistic preferences as errors.
- **One-shot migration refreshes outdated default-prompt copies in existing installs** at [PromptLibrary.cs](src/Supervertaler.Trados/Core/PromptLibrary.cs). `EnsureDefaultPrompts` previously skipped the write when the file existed on disk – which meant nobody who'd ever run an earlier version would get the new prompt. New `RefreshOutdatedDefaultProofreadingPrompt` detects the v4.13.0–v4.19.66 default by its distinctive section headers (`## 1. Accuracy`, `## 2. Completeness`, `## 5. Number & Unit Formatting`) and absence of the new content marker (`# Verifying claims against the document`), and deletes the stale copy so the same `EnsureDefaultPrompts` pass writes the new content. User-cloned/edited prompts have `default: false` in their YAML frontmatter and are never touched.

---

## [4.19.67] – 2026-05-06

### Fixed (Anonymous usage statistics: pings going to a non-existent Cloudflare subdomain)

- **The plugin's anonymous usage pings have been silently failing for ~6 weeks.** The Trados Plugin tab on the stats dashboard showed 8 unique users all-time but zero active in the last 30 days, while the Workbench tab kept getting fresh data. Caused by a wrong-subdomain change in v4.18.20 (28 Mar 2026) that pointed pings at `supervertaler-stats.supervertaler.workers.dev` – a Cloudflare account subdomain that doesn't exist. The actual Worker lives at `supervertaler-stats.michaelbeijer-co-uk.workers.dev` (account subdomains are tied to the Cloudflare account, not the GitHub org). The Workbench's Python client was untouched and kept pinging the correct URL throughout, which is why only the Trados tab went quiet.
- Fix at [UsageStatistics.cs:32](src/Supervertaler.Trados/Core/UsageStatistics.cs) restores the correct URL. The 30-day metric on the dashboard will stay depressed for at least 1–2 weeks until existing installs update via the AppStore – `HttpClient.PostAsync` swallows the exception in the existing `catch` block, so users on broken versions will keep failing silently until they upgrade.

---

## [4.19.66] – 2026-05-04

### Fixed (Batch operations: Translation Results pane stole focus at every batch boundary)

- **During Batch Translate / Batch Proofread, every batch boundary kicked the user away from the Supervertaler Assistant pane back to Trados Studio's built-in Translation Results pane.** With a Supervertaler Assistant tab docked next to Translation Results in the bottom dock strip, the user couldn't watch the live progress log because the tab kept switching itself away. Reported by Michael halfway through a 213-segment patent translation across 8 batches.
- Root cause: `ProcessSegmentPair` writes each translated segment via Trados' supported API, which moves the editor's active-segment cursor to the just-written segment. The built-in Translation Results pane is wired to react to active-segment changes and re-runs TM/MT lookups for the new segment, which on Trados 18 brings its tab to the front. During a batch the writes are fast enough that Trados can't keep up – so the focus steal happens once, in the gap between batches, when the model is busy producing the next batch's response.
- Fix at [AiAssistantViewPart.cs OnBatchProgress](src/Supervertaler.Trados/AiAssistantViewPart.cs): when a `BatchProgressEventArgs.Message` starts with `"Translating batch "` or `"Proofreading batch "` (i.e. a new batch is starting), call `Activate()` on the AiAssistantViewPart to re-bring its tab to the front. Triggering on batch-start rather than batch-end is intentional: Trados' focus steal happens in the gap that follows the batch-complete log line, so re-activating at the START of the next batch happens after the steal and reliably wins. There's a brief flicker (Translation Results visible for a fraction of a second between batches) but the user is back on the Supervertaler Assistant log immediately afterwards. Same fix covers Batch Proofread because `BatchProofreader` and `BatchTranslator` share the `OnBatchProgress` handler.
- No change to actual translation behaviour. If the user explicitly switches to Translation Results mid-batch, they'll be kicked back at the next batch boundary – an acceptable trade-off for the much-more-common "stay on the live log" workflow.

---

## [4.19.65] – 2026-05-04

### Fixed (AutoPrompt tooltip: clarify that Clipboard Mode does not apply)

- **The AutoPrompt link tooltip didn't mention that Clipboard Mode is ignored by AutoPrompt.** Reported by Michael while drafting an auto-prompt with Clipboard Mode ticked – reasonable assumption that "this batch operation goes via clipboard" applied to AutoPrompt too, but the AutoPrompt handler at `AiAssistantViewPart.OnGeneratePromptRequested` doesn't reference `IsClipboardMode` at all and always sends the meta-prompt to whichever AI provider is configured in AI Settings. Added a paragraph to the tooltip in [BatchTranslateControl.cs:286-298](src/Supervertaler.Trados/Controls/BatchTranslateControl.cs) documenting this and pointing out the useful pattern it enables: keep Clipboard Mode ticked, click AutoPrompt to generate the prompt via your paid API, then run the bulk Translate via clipboard against a free web-tier model (ChatGPT / Claude.ai / Gemini). Best of both worlds – paid API for the small-but-clever prompt-writing call, free web tier for the expensive bulk translation.
- No behavioural change. `AutoPopDelay` raised to 12 s so the longer tooltip doesn't dismiss before you finish reading.

---

## [4.19.64] – 2026-05-04

### Fixed (CRITICAL: termbase write target persisted across project switches)

- **Quick-add (Alt+Down) and Add Term dialog (Ctrl+Alt+T) were writing new terms to the *previous* project's Write termbase, even after switching projects.** The Settings → Termbases tab correctly showed all termbases unchecked for the new project (because the in-memory overlay had been applied), but the global `TermLensSettings.json` on disk still held the previous project's `WriteTermbaseIds`. Both `QuickAddTermAction` and `AddTermAction` call `TermLensSettings.Load()` to read the current write targets, which read the **disk** copy – getting the stale list and writing terms to the wrong termbase. Reported by Michael with a reproduction case (new project, all termbases unchecked in settings, Alt+Down still wrote two terms to the patents termbase).
- The fix at [TermLensEditorViewPart.cs:1022-1052 / 1557 / 1582](src/Supervertaler.Trados/TermLensEditorViewPart.cs) calls `_settings.Save()` after every `_settings.ApplyProjectOverlay(...)` so the global settings file is kept in sync with the current project's effective Write/Project termbase IDs. Three call sites updated: project-detected-existing, project-detected-new, settings-reloaded, and post-term-added refresh.
- Anyone running v4.19.55–63 should check their Patents termbase (or whatever they had last set as Write before switching to a new project) for stray terms that ended up there by accident. Audit by sorting the Termbase Editor's Created column descending – stray terms will cluster around the project switches.

### Fixed (AI Settings: misleading "Include TM matches" label and tooltip)

- **The "Include TM matches in AI context" checkbox was documented as Chat / QuickLauncher only, but it also gates AutoPrompt's reference-pair sampling.** The section heading note read *"These settings do not apply to Batch Operations."* and the checkbox tooltip claimed *"Only applies to Chat and QuickLauncher – not to Batch Operations."*. In reality `AiAssistantViewPart.OnGeneratePromptRequested()` reads the same `IncludeTmMatches` flag at line 1159 to decide whether to call `CollectTmReferencePairs()` – which walks the active document and samples up to 50 already-translated, human-confirmed segment pairs to ship into the meta-prompt. AutoPrompt is a Batch Operations feature, so users who turned the checkbox off thinking it didn't matter for AutoPrompt were silently losing in-project translation examples. Reported by Michael with a screenshot annotating the contradictory wording.
- The tooltip's other claim – that the checkbox enables *"Translation Memory fuzzy matches for the active segment"* – was true for Chat / QuickLauncher (live TM lookups, fuzzy + exact) but wrong for AutoPrompt: AutoPrompt doesn't do a TM lookup at all; it samples confirmed segments straight from the active document, so 100% / exact matches that have been applied and confirmed are absolutely included alongside fuzzy-edited and from-scratch translations.
- The fix at [AiSettingsPanel.cs:654-695](src/Supervertaler.Trados/Controls/AiSettingsPanel.cs) renames the checkbox to *"Include TM matches in AI context (Chat, QuickLauncher, AutoPrompt)"*, softens the section note to *"Most of these apply to Chat and QuickLauncher only – see each tooltip for exceptions."*, and replaces the tooltip with a longer description that documents both code paths separately and clarifies that other Batch Operations (Translate, Proofread) are unaffected by the checkbox. No behavioural change – the checkbox already controlled both code paths; this release just makes the UI honest about it.

---

## [4.19.63] – 2026-05-04

### Fixed (AI Assistant: provider name in error messages)

- **Error messages now name the actual provider you selected**, not "OpenAI". OpenRouter / DeepSeek / Mistral / Grok / Custom-OpenAI users were seeing things like "OpenAI indicated tool_calls but no calls found in response" because the provider-label helper at [LlmClient.cs:393](src/Supervertaler.Trados/Core/LlmClient.cs#L393) didn't have an OpenRouter case (fell through to the "OpenAI" default), and one tool-use error string was hardcoded to "OpenAI" rather than going through the helper. Now `OpenAiProviderLabel()` includes OpenRouter, and the tool-use error formats with the dynamic label.
- Reported by a user who saw "OpenAI" in the error while running DeepSeek-V4-Pro through OpenRouter.

### Added (AI Assistant: defensive fallback when tool_calls is empty)

- **When `finish_reason == "tool_calls"` but the response contains no tool-call objects, fall through to the text content** instead of throwing. Some providers (notably DeepSeek through OpenRouter, and some Custom-OpenAI gateways) occasionally set the finish reason but omit the `tool_calls` array – the model intended to answer in plain text after all. With this fix, those turns succeed with the model's text reply rather than blowing up the whole conversation. If neither tool calls nor text is present, the error message remains but now uses the dynamic provider label.

---

## [4.19.62] – 2026-05-02

### Fixed (TSV export/import preserves multi-line fields)

- **Termbase TSV export now escapes newlines, tabs, and backslashes** in every text field (uuid, source/target cell, domain, notes, project, client) using backslash-prefixed sequences (`\n`, `\r`, `\t`, `\\`). Pre-fix, a notes field that contained a multi-paragraph response or any other multi-line content would write actual line breaks into the TSV, breaking the one-record-per-line invariant the matching importer relies on – re-importing the file would split the markdown across many phantom rows with the wrong column alignment, scrambling the termbase. **TSV import** now applies the inverse unescape on the same fields, so the round-trip preserves the original formatting exactly.

  The escape format is asymmetric (only Supervertaler unescapes on import), but it survives Excel and other TSV viewers without mis-splitting rows, which is the more important property for a working file.

  TSVs exported before v4.19.62 that contain literal newlines in fields are not silently fixed by this change – they were already broken on disk. Re-export to get a clean copy. Hand-built TSVs without any backslashes import unchanged because the unescape pass is a no-op on backslash-free values.

---

## [4.19.61] – 2026-05-02

### Added (Termbase Editor: sort by creation time)

- **New "Created" column in the Termbase Editor.** Surfaces the `termbase_terms.created_date` value the schema has stored for every term since v1 (default `CURRENT_TIMESTAMP`). Click the column header to sort by date – click again to reverse. Useful for finding and removing recently-added entries: add a term, realise it was wrong, sort by Created descending, the row is at the top. Read-only, formatted `yyyy-MM-dd HH:mm`. `TermEntry.CreatedDate` (nullable `DateTime`) and the `GetAllTermsByTermbaseId` SELECT both updated to expose the column.

---

## [4.19.60] – 2026-05-02

### Added (route QuickLauncher prompts to Workbench Sidekick)

- **`AiSettings.QuickLauncherTarget`** – new string setting (data-member `quickLauncherTarget`, values `"TradosAssistant"` (default) or `"WorkbenchSidekick"`) that picks where Ctrl+Q QuickLauncher prompts run. The default keeps the existing behaviour: prompt + response stay in the in-Trados Assistant chat. The Workbench-Sidekick option posts the expanded prompt to Supervertaler Workbench's Sidekick Chat over a localhost bridge, with the response rendered in Sidekick instead of in Trados.
- **`Core.WorkbenchSidekickClient`** – inverse of the existing `Core.SidekickBridge`. Discovers the Workbench's bridge via the handshake file at `<root>/workbench/runtime/sidekick-bridge.json`, validates the PID is alive, POSTs to `http://127.0.0.1:<port>/v1/run-prompt` with a Bearer token. On any failure (Sidekick not running, stale handshake, network error) the call site silently falls back to the in-Trados Assistant so a missing Sidekick never blocks the user from running their prompt.
- **AI Settings dropdown** – a new "QuickLauncher prompts go to:" combo box in Settings → AI Settings, with a tooltip explaining the two targets and the fallback behaviour.

The Workbench side of this feature ships in Supervertaler-Workbench (modules/sidekick_bridge_server.py and a new `run_prompt_requested` signal handler in `FloatingAssistant`); upgrade Workbench to the matching version for the route to work.

---

## [4.19.59] – 2026-05-02

### Changed (stronger key for the non-matching-termbase confirmation list)

- **Confirmation list now keyed by termbase name instead of ID.** The persistence field added in v4.19.58 (`confirmedNonMatchingWriteTermbaseIds`) used the SQLite `id` column. With `INTEGER PRIMARY KEY AUTOINCREMENT` ID reuse within one database is impossible, but a user who wipes and recreates `supervertaler.db` could end up with stale ID-based confirmations applying to different termbases. The schema declares `name TEXT NOT NULL UNIQUE` on the `termbases` table, so names are stable across rebuilds in a way IDs aren't. Renamed the field to `ConfirmedNonMatchingWriteTermbaseNames` (data-member `confirmedNonMatchingWriteTermbaseNames`) and switched all lookup/add/remove sites to key on `tb.Name`. Renaming a confirmed termbase now triggers a re-ask on the next tick – which matches the principle that consent should be tied to a stable identity.

---

## [4.19.58] – 2026-05-02

### Added (UX guard for non-matching Write / Project termbases)

- **Confirm dialog when assigning a non-matching termbase as Write or Project.** Pre-v4.19.56 the inversion logic would silently swap source/target columns when the user ticked an unrelated-language-pair termbase as Write, writing terms in the wrong direction. v4.19.56 stopped the silent swap, but the user could still tick a non-matching termbase as Write and have new terms written into a database whose language pair didn't match the project. v4.19.58 catches this at tick-time: when the user ticks Write or Project on a termbase whose `LanguageUtils.CompareTermbaseDirection` reports as `Unrelated`, the Settings → Termbases tab now shows a confirm dialog ("This is an EN-NL termbase, but the active project's source language is German…") with a "Yes, add anyway" / "No" choice. Confirmed termbases are remembered in `TermLensSettings.ConfirmedNonMatchingWriteTermbaseIds` so the user is never re-asked for the same termbase. Unticking the box clears the override so a future re-tick re-asks. Header "tick-all" Write skips non-matching termbases that haven't been individually confirmed, so a bulk select can't sneak unrelated termbases through. Read-only ticks (the Read column) are exempt – there's no harm in *reading* a non-matching termbase, only in writing into it.
- **`TermLensEditorViewPart.GetCurrentProjectSourceLanguage()`** – new public static accessor that mirrors the existing `GetCurrentProjectPath` / `GetCurrentProjectName` helpers, used by the Settings dialog to find out the active project's source language at form-open time.

---

## [4.19.57] – 2026-05-02

### Added (regression guard for the language-direction helper)

- **`LanguageUtils.RunStartupSelfTest`** — exercises `CompareTermbaseDirection` against 16 canonical language-name shapes (full names, BCP-47 codes, abbreviated regions, missing/empty inputs, mismatched pairs, same-language-different-region) at plugin startup. Result is logged to `bridge.log` next to the `TermLensSettings.RunStartupSelfTest` entry. Any future regression in the direction-comparison logic surfaces immediately instead of after users notice term lookups going to the wrong column. Verified all 16 cases pass against the v4.19.56 implementation.

---

## [4.19.56] – 2026-05-02

### Fixed (second Codex 5.5 review-pass sweep)

- **Termbase language-direction logic now consistent across all term-write paths.** The v4.19.55 fix only landed in `LoadAllTerms`. Four other call sites still treated "project source ≠ termbase source" as "inverted" (the old broken pattern), so a project termbase whose language pair didn't match the project would still get its sides swapped on add, merge candidates would be searched with swapped columns, and the term editor would pre-fill into the wrong slots. Consolidated the logic into a single `LanguageUtils.CompareTermbaseDirection` helper returning a 4-state enum (NotApplicable, Aligned, Inverted, Unrelated) and routed all five call sites through it: [TermbaseReader.LoadAllTerms](src/Supervertaler.Trados/Core/TermbaseReader.cs), [TermbaseReader.InsertTerm](src/Supervertaler.Trados/Core/TermbaseReader.cs), [QuickAddProjectTermAction](src/Supervertaler.Trados/QuickAddProjectTermAction.cs), [TermMergeChecker](src/Supervertaler.Trados/Core/TermMergeChecker.cs), [TermEntryEditorDialog.IsProjectDirectionInverted](src/Supervertaler.Trados/Controls/TermEntryEditorDialog.cs).
- **SuperSearch active-file replace no longer destroys inline tags.** The pre-fix path read `pair.Target.ToString()`, did a string replace on the flattened text, then `Clear()`-ed the target and re-added a single cloned `IText` – every tag pair, placeholder tag, and formatting span the segment originally contained was wiped. New helper `ReplaceInActiveSegmentPair` walks the existing target's `IText` children depth-first, simulates a per-`IText` replace, and only applies if the result reconstructs the expected flat output. If the search match straddles a tag boundary, the pre-flight detects the mismatch and the helper returns `SpansInlineTags`; the user gets a clear "match spans inline tags – skipped to preserve formatting" status instead of a silent flatten. Both single-Replace and Replace All active-file paths use the helper.
- **SuperSearch disk replace no longer claims success when no real replacement happened.** When an SDLXLIFF target's text is split across multiple `XmlText` siblings separated by inline-tag elements, `node.InnerText` matches across the boundary but the per-text-node replace inside `ReplaceTextInNodes` only changes nodes whose individual value contains the match. Pre-fix, the code would unconditionally `count++` and save the file even if no `XmlText` value actually changed – Replace All would lie about its work. Now the disk path verifies the post-replace `node.InnerText` matches the expected string before counting; segments that span tag boundaries are surfaced as `skipped, X (match spans inline tags)` in the Replace All summary so the user can edit them by hand.

---

## [4.19.55] – 2026-05-02

### Fixed (bug sweep based on a Codex 5.5 review pass)

- **Termbase inversion no longer mis-handles unrelated language pairs.** [TermbaseReader.cs](src/Supervertaler.Trados/Core/TermbaseReader.cs) treated any project-source ≠ termbase-source mismatch as "inverted", which meant a DE-FR termbase loaded into an EN-NL project would get its sides swapped and indexed under languages it has no terms for. Now the inversion check verifies project-source actually matches termbase-target before swapping; entries from termbases whose language pair doesn't match the project on either side are skipped instead of mis-indexed.
- **Batch Translate completion summary now reflects actual writes.** [BatchTranslator.cs](src/Supervertaler.Trados/Core/BatchTranslator.cs) was incrementing `translated` as soon as the LLM returned a parseable response; the Trados write happened later in [AiAssistantViewPart.OnBatchSegmentTranslated](src/Supervertaler.Trados/AiAssistantViewPart.cs) and write failures were logged but not subtracted, so the final "translated N" report could over-count. Added a `WriteSucceeded` flag on the segment-result event args; the handler now runs synchronously on the UI thread and signals failure back, and the translator's counters reflect the real outcome.
- **Plugin no longer deletes `%LocalAppData%\Supervertaler\` on every Trados start.** [UserDataPath.CleanupLegacyFolders](src/Supervertaler.Trados/Settings/UserDataPath.cs) wiped that folder unconditionally on the assumption it was a stale Workbench artifact – ungated by any migration flag, so any user (or future contributor) putting data there would lose it on the next plugin start. The deletion was added on a hunch; it's now removed.
- **API-key fallback parser scans the whole shared settings file.** [LlmClient.ExtractNestedJsonString](src/Supervertaler.Trados/Core/LlmClient.cs) only searched 2000 chars after `"api_keys"`. The shared Workbench settings.json is already 10 KB; a key past the window would silently fail to load. Removed the cap.
- **Build no longer warns about `System.Memory` / `System.Buffers` / `System.Runtime.CompilerServices.Unsafe` version conflicts.** Trados ships older transitive copies than `Microsoft.Data.Sqlite 8.0.0` needs; MSBuild was picking the older "primary" assemblies and hoping for the best. Added explicit `PackageReference` entries to pin the versions SQLite expects, eliminating the MSB3277 warnings and removing the time-bomb risk that a future SQLite update reaches into newer-version-only APIs.

---

## [4.19.54] – 2026-05-02

### Fixed (critical settings-loss regression introduced in v4.19.52)

- **Every saved setting appeared empty.** v4.19.52's "Sidekick Bridge default-true now applies to upgrading users" commit added a second `[OnDeserializing]` callback to `AiSettings` while one already existed. `DataContractJsonSerializer` rejects multiple methods marked with the same callback attribute and throws `InvalidDataContractException` on the first deserialise attempt. `TermLensSettings.Load()`'s broad `catch` swallowed the exception and returned `new TermLensSettings()`, so from the user's perspective every saved setting vanished: termbase disconnected, all termbases reverted to Read = true (because `DisabledTermbaseIds` came back empty), the "share usage statistics?" prompt re-appeared on every Trados start (because `UsageStatisticsAsked` was reset to `false`), QuickLauncher folders rendered as collapsed submenus instead of inline sections (because `QuickLauncherFlatFolders` was empty), QuickLauncher slot shortcuts disappeared (because `QuickLauncherSlots` was empty), and the Settings dialog opened at minimum size every time (because `SettingsFormWidth/Height` were `0`). The settings.json on disk was never corrupted – it simply wasn't being read. Fix: merged the two `[OnDeserializing]` methods into a single callback. Documented the constraint in the source so future contributors don't repeat the mistake.
- **Edit Prompt dialog truncated pastes longer than 32 767 characters.** `TextBox.MaxLength` defaults to `Int16.MaxValue`, which silently drops anything past that point. Patent-sized prompts hit it instantly. Fix: `MaxLength = int.MaxValue` on the prompt-content textbox.
- **Active-prompt tree node label clipped on the right edge** after closing and reopening Settings. Setting `TreeNode.NodeFont` to bold triggers a long-standing WinForms TreeView bug: the node's display rectangle is measured with the regular font and never re-measured for the bold font, so bold characters past the regular-font width get cut off. Fix: dropped the bold; the 📌 emoji + accent colour are already a strong active-prompt marker.

### Added (regression guard)

- **Plugin startup now runs a serialize/deserialize self-test on `TermLensSettings` and writes the result to `bridge.log`.** Round-trips a default `TermLensSettings` through the same `DataContractJsonSerializer` pipeline that `Load`/`Save` use. Any future `[DataContract]` attribute violation – duplicate `[OnDeserializing]`, malformed `[DataMember]`, etc. – surfaces immediately in the plugin log instead of after users notice their settings have disappeared.
- **`TermLensSettings.Load()` no longer silently swallows exceptions.** Unexpected failures are logged with full type, message, and stack trace to `<root>/trados/settings/settings-load-errors.log` so a future regression in this code path is observable.

---

## [4.19.52] – 2026-05-01

### Fixed (Sidekick Bridge default-true behaviour for existing users)

- **`AiSettings.SidekickBridgeEnabled` now correctly defaults to `true` for users with an existing `settings.json`.** Reported by a user upgrading from v4.19.49: the bridge silently skipped startup with `guard: AiSettings.SidekickBridgeEnabled=false – bridge skipped`, despite the property having an initialiser of `= true`. Root cause: `DataContractJsonSerializer` skips constructors during deserialisation, so property initialisers don't run when reading a JSON file written before the property existed – the property defaults to the bool type default (`false`) instead. Fix: added an `[OnDeserializing]` callback `SetDefaultsBeforeDeserialization` that explicitly sets the default to `true` before the JSON parser runs. Any value present in the JSON still overrides, so users who have explicitly disabled the bridge keep that setting.

---

## [4.19.51] – 2026-05-01

### Diagnostics (Sidekick Bridge)

- **Bridge log now ALSO writes to `%TEMP%\Supervertaler-bridge.log`** as a guaranteed-writable fallback. Diagnosing v4.19.50 where the user reported no `bridge.log` even appearing: this rules out the case where `UserDataPath.Root` resolves to a custom location, or where the user data folder isn't writable.
- **Lifecycle tracing extended** – `BridgeLog.Write` now also fires from `AiAssistantViewPart.Initialize()` entry, so we can tell whether the ViewPart is even being instantiated by Trados (ViewParts are lazy – they only initialise when the user activates the panel).
- **First log line of every session now records** the resolved `UserDataPath.Root`, `TradosRuntimeDir`, and `SidekickBridgeFile` paths so we can immediately see where the plugin is looking for its data folder.

---

## [4.19.50] – 2026-05-01

### Diagnostics (Sidekick Bridge)

- **Sidekick Bridge now writes a visible log file** at `~/Supervertaler/trados/runtime/bridge.log` recording every step of startup: which lifecycle gate was hit, whether `HasAssistantAccess` passed, each port-bind attempt and its outcome, the bound port number, and any exceptions with full type and message. The log is truncated on every plugin start so it always reflects the current Trados session. Useful for diagnosing why no `bridge.json` file appears – the most common cause on Windows is `HttpListener` requiring URL ACL registration for non-admin processes, which the log will now make obvious.

---

## [4.19.49] – 2026-05-01

### Added (Sidekick Bridge – plugin half)

- **Localhost HTTP bridge that exposes the active Trados project context to external Supervertaler clients.** Powers the new "Trados-aware mode" in Supervertaler Workbench's floating Sidekick Chat (Workbench side ships in v1.9.411). The bridge listens only on `127.0.0.1` on a random high port, requires a per-session Bearer token, and starts only when the user has Assistant access (paid or trial). Two endpoints are live:
  - `GET /v1/active-context` – returns a snapshot with active segment, surrounding segments, TM matches, termbase hits, and project metadata (same fields the in-Trados Chat already gathers, so answer quality is identical)
  - `POST /v1/insert-translation` – inserts text into the active target segment via the same path as the Apply-To-Target button
- **Discovery via handshake file.** The bridge writes `~/Supervertaler/trados/runtime/bridge.json` with port, token, PID, and start time on startup; deletes it on shutdown. Stale files from hard kills are detected by clients checking PID liveness.
- **Hidden setting** `AiSettings.SidekickBridgeEnabled` (default `true`). No UI checkbox – privacy-conscious users can flip it off by editing `settings.json` directly.

### Notes

- The bridge runs in `AiAssistantViewPart`'s lifecycle: starts in `InitializeFullIfNeeded` (the same gate that already controls in-Trados Chat), stops in `Dispose`.
- All endpoint handlers marshal back to the UI thread before touching `_activeDocument`; the listener itself runs on a dedicated background thread and is concurrency-safe with single-request-at-a-time semantics.
- Workbench Sidekick Chat client + UI ships in v1.9.411 of Supervertaler Workbench; until then the bridge is dormant (no client). The two halves can be released independently.

---

## [4.19.48] – 2026-05-01

### Fixed (AI Settings dialog right-edge clipping)

- **Provider/Model dropdowns and the "Show" button no longer slide under the AutoScroll vertical scrollbar in the AI Settings tab.** When the dialog was resized larger than its default size, the dropdowns and the API-key Show button extended right up to the panel's right edge — but the right edge included the AutoScroll scrollbar's reserved area, so the controls visually clipped against (or hid behind) the scrollbar. Reported by a user. Fix: `LayoutProviderModelRows`, `LayoutApiKeyRow`, and `LayoutTermbasesList` now compute their right margin from `ClientSize.Width` (which excludes the scrollbar) rather than `Width` (which includes it). The dropdowns now keep a clean 16 px gap to the visible right edge regardless of the AutoScroll bar.

---

## [4.19.47] – 2026-05-01

### Fixed (chat status-bar model picker now syncs with AI Settings dialog)

- **Changing the provider/model via the chat status-bar picker now stays in sync with the AI Settings dialog.** Previously, picking a model from the chat picker (e.g. *OpenAI → GPT 5.4 Mini*) would update the chat status bar and the file on disk, but the in-memory copy of `_settings` held by `TermLensEditorViewPart` wasn't refreshed. Opening the Settings dialog from the TermLens panel's gear icon would therefore show stale values (e.g. *OpenRouter / DeepSeek V4 Flash*) that didn't match the chat picker's selection. Reported by a user. The chat picker's `OnModelChangeRequested` now calls a new `TermLensEditorViewPart.NotifyAiSettingsChanged()` after saving — a lightweight refresh that reloads only the `AiSettings` portion of the on-disk settings, without rebuilding termbases.

---

## [4.19.46] – 2026-05-01

### Added (TermLens popup – keyboard-driven metadata, snappier auto-close)

- **Press `I` in the TermLens popup to show extra metadata for the highlighted match.** The same metadata that previously only appeared on mouse hover (definitions, domain, notes, URL, synonyms) can now be revealed entirely from the keyboard. Press `I` again to hide it. The hint at the bottom of the popup has been updated to `← → cycle  ·  Enter insert  ·  E edit  ·  I info  ·  Esc close`. Only affects the floating Ctrl-tap popup; the dockable TermLens pane is unchanged.
- **The TermLens popup now closes automatically on any mouse movement, focus loss, or unrelated keypress.** Previously it would stay open after switching to another application until the user pressed Ctrl or Esc. Now: moving the mouse more than ~4 px, switching to another window, or pressing any key outside the popup's keyboard set (cycle / Enter / E / I / Esc) closes it immediately. Pure modifier presses (Ctrl/Shift/Alt on their own) are ignored so the popup doesn't tear itself down on the keyup that follows the opening Ctrl-tap. The popup is fully keyboard-driven; the cost of this snappier auto-close is that mouse-clicking a chip to insert no longer works (the dockable pane still supports mouse interaction). Only affects the floating Ctrl-tap popup.

---

## [4.19.45] – 2026-05-01

### Added (DeepSeek provider)

- **DeepSeek V4 Pro and DeepSeek V4 Flash added as a dedicated AI provider.** DeepSeek now appears in the AI provider dropdown alongside OpenAI, Claude, Gemini, Mistral, and others. The integration uses DeepSeek's OpenAI-compatible API (`api.deepseek.com/v1`). Enter your DeepSeek API key in **Settings → AI Settings** to use it. V4 Pro is the default (flagship); V4 Flash is listed for high-volume, cost-sensitive workloads.
- **DeepSeek V4 Pro and V4 Flash also available via OpenRouter.** Both models are now included in the OpenRouter model dropdown under the IDs `deepseek/deepseek-v4-pro` and `deepseek/deepseek-v4-flash`, enabling access through an existing OpenRouter API key without a separate DeepSeek account.

---

## [4.19.44] – 2026-04-30

### Changed (Help system – base URL now includes `/help` slug)

- **`HelpSystem.DocsBaseUrl` updated to `https://supervertaler.gitbook.io/help`** to match the renamed GitBook site slug. (GitBook's free plan requires a non-empty slug, so the merged Supervertaler help site uses `/help` as its path; full URLs are now `…/help/trados/<page>` for Trados topics and `…/help/workbench/<page>` for Workbench topics.) Topic constants in `HelpSystem.Topics` are unchanged from v4.19.43 – they continue to start with `trados/`, just appended to the new base. Existing call-sites unaffected.

---

## [4.19.43] – 2026-04-30

### Changed (Help system – unified GitBook site for both products)

- **The Supervertaler GitBook site now hosts documentation for both Supervertaler for Trados and Supervertaler Workbench in a single space**, with content organised under `/trados/` and `/workbench/` URL prefixes. Previously the Workbench shipped its own separate VitePress site at `help.supervertaler.com` which had drifted out of sync with reality; the Trados plugin's own GitBook (`supervertaler.gitbook.io/trados`) was the up-to-date one. Rather than maintain two parallel docs systems on two different toolchains, the Workbench docs were imported alongside the Trados docs in the existing GitBook space. The merged `SUMMARY.md` uses GitBook's `# Part` headings to give each product its own visually-divided sidebar section ("Trados Plugin" and "Workbench") so the two product surfaces don't visually compete.
- **`HelpSystem.cs` topic paths now carry a `trados/` prefix** to match the new file-path layout. Existing call-sites (`HelpSystem.OpenHelp(Topics.TermLensPanel)` etc.) are unchanged – the indirection through the `Topics` constants insulates them from the path rename. The `DocsBaseUrl` constant is now `https://supervertaler.gitbook.io` (root) rather than `…/trados`; the trailing `/trados/` is part of every topic path. **Important: this assumes the GitBook space slug has been renamed from `/trados` to `/` (root) on the GitBook admin side** – if you keep the old slug, every help link will resolve to `…/trados/trados/<page>` (broken). Rename the slug before publishing this version.
- **Documentation site title** to be renamed from "Supervertaler for Trados Help" to "Supervertaler Help" in the GitBook admin (cosmetic; no code change required).

---

## [4.19.42] – 2026-04-30

### Fixed (NullReferenceException when switching projects during batch translation)

- **Switching to a different Trados project while batch translation is running no longer throws "object reference not set to an instance of an object".** Root cause: `OnBatchSegmentTranslated` checked `_activeDocument != null` at the top of the method, but `OnActiveDocumentChanged` could null the field between that check and the subsequent `ProcessSegmentPair` call if the user switched projects at that moment. Fixed by capturing `_activeDocument` in a local variable before the null check so the reference cannot change mid-method. Reported by a user.

---

## [4.19.41] – 2026-04-29

### Fixed (TermLens popup – stayed visible behind editor dialog when E pressed)

- **Pressing E on a TermLens popup match now closes the popup before the term-entry editor opens.** Previously the popup's pixels stayed painted on screen behind the modal editor dialog until the dialog closed. Root cause: `EditCurrentMatch` registered the editor-open inside a `FormClosed` event handler, but the editor's `ShowDialog` blocks the message pump before the area underneath the popup gets repainted, so the popup remained fully visible. Fixed by hiding the popup synchronously, then deferring `Close()` plus `HandleEditCurrentTerm` to the owner form's message loop via `BeginInvoke`. The owner pump processes pending WM_PAINTs for the freshly-uncovered area first, so the editor opens onto a clean screen.

---

## [4.19.40] – 2026-04-29

### Fixed (Alt+Down no longer writes to the project termbase)

- **Alt+Down (Quick-add to write termbases) no longer adds to the project termbase, even when it is also ticked in the Write column.** Previously `QuickAddTermAction` iterated all IDs in `WriteTermbaseIds` without filtering, so any termbase marked as both Write and Project would receive the term from both shortcuts – making Alt+Up and Alt+Down behave identically. The project termbase ID is now skipped in the write-set loop; Alt+Up (project-only) and Alt+Down (write-set-only) are now fully exclusive. The "no write termbases found" warning also notes that the project termbase is excluded so users understand why a write-only configuration might appear empty.

---

## [4.19.39] – 2026-04-29

### Added (Forbidden Term System – active warnings in TermLens)

- **TermLens now actively warns when the source segment contains a target translation that has been marked forbidden in the termbase.** Previously the plugin simply filtered forbidden terms out of all SQL lookups (`WHERE COALESCE(t.forbidden, 0) = 0`), so the user had no idea a term existed at all – equivalent to silent suppression. Now forbidden matches show up in the TermLens chip flow with a strong red background (`#E53935`, hover `#C62828`), white target text, and a strikethrough on the target term (the source is rendered normally – it isn't the source that's forbidden, it's the translation). Hovering the chip shows a `🚫 Forbidden – do not use` tag at the top of the popup. Background colour was deliberately picked to be unmistakably distinct from the soft pink used for project-termbase entries (`#FFE5F0`); a first attempt at salmon (`#FFDAD6`) was too close to pink and got changed in the same release.
- **Term Entry Editor dialog has a "Forbidden term (warn when used in translation)" checkbox** below the Non-translatable checkbox, drawn in dark red so it reads as a warning option. Toggling it persists to `termbase_terms.forbidden` via new `forbidden` parameters on `TermbaseReader.InsertTerm` and `UpdateTerm`. The checkbox correctly reflects DB state when reopening an existing term – required adding `forbidden` to the column list in `GetTermById`, which had been quietly omitting it.
- **Termbase Editor grid now has a 🚫 column** showing the forbidden state for every term at a glance, sized to match the existing NT column. Read-only by design (clicking the cell would otherwise change the visual state without persisting, which was confusing during testing); editing is done through the term-editor dialog. The grid row is patched with the new value when the dialog returns OK so the column updates immediately.

### Fixed (Termbase settings – column sort scrambled checkbox assignments)

- **Sorting the termbase list in Settings → Termbases by Termbase / Terms / Languages no longer corrupts Read / Write / Project / CS checkbox assignments.** Root cause: the save path read checkboxes by row index (`_dgvTermbases.Rows[i]`) and looked up the corresponding termbase via the parallel `_termbases[i]` list, which assumed visual row order matched the underlying list. After a column header click the DataGridView reorders rows in place but the parallel list stays put, so checkbox state for one termbase was being saved against another's ID. Fixed by storing the `TermbaseInfo` (or `MultiTermTermbaseInfo`) reference in each row's `Tag` when the grid is populated, and rewriting the save loop plus all selected-row operations (Open, Distill, Remove, Import, Export) to look up the termbase via `row.Tag` instead of by index. Sort is fully re-enabled.

---

## [4.19.38] – 2026-04-26

### Changed (TermLens popup – feedback when E is pressed on a MultiTerm match)

- **Pressing E on a MultiTerm (green) match in the floating TermLens popup now flashes a short hint in place of the keyboard-shortcut footer instead of silently doing nothing.** The hint reads *"MultiTerm entries are read-only – edit them in Trados → Termbase Viewer."* in muted red, then auto-restores the regular hint after 3.5 seconds. Same E-key behaviour as before for non-MultiTerm matches: editor opens. Decision rationale: Trados Studio 2026 is expected to replace MultiTerm with a SQLite-backed terminology system, so MultiTerm write support isn't worth investing in – the right escape hatch is to send users to Trados's own MultiTerm editor for now.

---

## [4.19.37] – 2026-04-26

### Changed (Term Picker shortcut → Ctrl+Shift+P)

- **The Term Picker dialogue now opens via Ctrl+Shift+P (was Ctrl+Shift+T in v4.19.36).** Ctrl+Shift+T also collides with a Trados Studio binding. Ctrl+Shift+P doesn't collide with anything in either Trados or Supervertaler, and follows the VS Code-style "command palette" convention (P for Picker). Plugin-internal action ID stays `TermLens_TermPicker` so any user-customised remappings survive the rename. Stale "Ctrl+Shift+T" comment in `AddTermAction.cs` (which describes Ctrl+Alt+T, not the picker shortcut at all) fixed as a drive-by.

### Fixed (TermLens popup – grows to fit long segments)

- **The popup no longer truncates chips on long source segments.** Previously the popup was hard-capped at 560 pixels wide regardless of screen size, so a patent-style sentence pushing past 100 characters would show the source segment fine but truncate the target chip text with an ellipsis (e.g. "De onderhavige uitvinding heeft in het algemeen betrekkin…"). Width now scales with the screen – capped at `min(1200 px, screen.Width − 60)` – and the popup shrinks to the actual content width (so short segments still get compact popups). Height cap raised from half-screen to four-fifths so multi-line chip rows aren't cut off either; `AutoScroll` handles anything beyond that. Reported by a user on a long English-Dutch patent sentence.

---

## [4.19.36] – 2026-04-26

### Changed (Term Picker shortcut → Ctrl+Shift+T)

- **The Term Picker dialogue is now opened via Ctrl+Shift+T (was Ctrl+Shift+L since v4.19.28).** Ctrl+Shift+L collides with Trados Studio's own termbase-entry-listing shortcut, so the user-facing combo had to move. Ctrl-tap remains the preferred trigger for the lighter floating TermLens popup; Ctrl+Alt+G remains the popup's keyboard fallback. Plugin-internal action ID is unchanged (`TermLens_TermPicker`) so any user-customised shortcut remappings survive. Docs updated across keyboard-shortcuts.md, termlens.md, term-picker.md, termlens-popup.md and CLAUDE.md.

---

## [4.19.35] – 2026-04-26

### Added (TermLens popup – E opens the term-entry editor)

- **Pressing E on the highlighted match opens the term-entry editor.** Mirrors the docked panel's right-click "Edit Term…" menu but on a single keystroke, so the editor is reachable without leaving the keyboard. The popup snapshots the entry data, closes itself, then routes through the same `OnTermEditRequested` handler the docked panel uses – so the editor sees the same single-/multi-entry flow it always has, including the multi-termbase editing case for entries that exist in more than one termbase. Read-only MultiTerm entries are skipped (same rule as the right-click menu).

### Changed (TermLens popup – keyboard-only, fewer surprises)

- **Removed the "?" help button.** v4.19.33's button never worked reliably (off-screen in v4.19.33, focus-stealing the popup itself in v4.19.34) and was at odds with the popup's keyboard-only design anyway. F1 plumbing removed too – Trados Studio's own F1 binding takes precedence over application-level message filters and beating it would need a Win32 low-level keyboard hook, more complexity than this is worth. Help for the popup is one click away on the [GitBook docs site](https://supervertaler.gitbook.io/trados/features/termlens/termlens-popup).
- **Popup no longer auto-dismisses on focus loss.** v4.19.34 closed the popup whenever focus moved off it, which meant the chip-hover tooltip (the metadata `TermPopup`) silently killed the popup the moment the mouse passed over a chip – reported by a user moving the mouse toward the now-removed "?" button. Closing is now exclusively via Esc, Enter (after insert), Ctrl-tap (toggle), Ctrl+Alt+G (toggle), or clicking a chip (insert).
- Footer hint updated to `← → cycle  ·  Enter insert  ·  E edit  ·  Esc close`.

---

## [4.19.34] – 2026-04-26

### Fixed (TermLens popup – help affordances)

- **The "?" help button is now actually visible.** v4.19.33 added a manually-positioned Label with `Anchor = Top | Right` whose initial Location was computed against the form's pre-resize 560 px width, then `ResizeToContent` shrank the form before the anchor distance was committed – the button silently ended up off-screen to the right. Replaced with a docked bottom-bar Panel that contains the keyboard hint (`Dock = Fill`) and the "?" button (`Dock = Right`); no manual coordinates, no anchor math.
- **F1 inside the popup now opens the TermLens popup help page instead of the Trados Studio help site.** Trados has an Application-level message filter for F1 that fires before `Form.ProcessCmdKey`, so v4.19.33's `case Keys.F1` handler in `ProcessCmdKey` was never reached. Added a `PopupF1Filter : IMessageFilter` (same pattern as `CtrlTapFilter`) registered once on plugin startup that consumes F1 key-down events when the popup is open and focused, before Trados sees them. The `ProcessCmdKey` handler stays as a fallback in case the application filter chain ever misses.

---

## [4.19.33] – 2026-04-26

### Added (TermLens popup – help affordances)

- **Help is now reachable from the popup itself.** A small "?" button in the top-right corner of the popup opens the dedicated help page (https://supervertaler.gitbook.io/trados/features/termlens/termlens-popup) in the default browser, and F1 does the same – keeping parity with the existing dialogs (Add Term, Settings) that have both a visible question-mark affordance and a keyboard equivalent. The popup stays open so the user can read the docs and come back to picking a term.

### Docs

- New help page **TermLens popup** under TermLens describing when to use the popup vs the Term Picker dialogue, the keyboard model, and a side-by-side comparison.
- Keyboard-shortcut reference, TermLens overview, and Term Picker page updated to reflect the v4.19.28 shortcut swap (Ctrl-tap → popup, Ctrl+Shift+L → Term Picker dialogue, Ctrl+Alt+G now the popup's fallback). Stale references in CLAUDE.md cleaned up.

---

## [4.19.32] – 2026-04-26

### Fixed (TermLens – handler leak across document close/reopen cycles)

- **Term-insert / right-click actions in the docked TermLens panel no longer fan out to dead view-part instances.** `TermLensEditorViewPart.Initialize` subscribed seven event handlers to static singletons (`_control.Value` for `TermInsertRequested` / `TermEditRequested` / `TermDeleteRequested` / `TermNonTranslatableToggled` / `FontSizeChanged`, `_mainPanel.Value.SettingsRequested`, `LicenseManager.Instance.LicenseStateChanged`) but `Dispose` only unsubscribed the per-document handlers – so each time Trados tore down and recreated the view-part (document close/reopen, project switch, layout change), the new instance added another set of subscribers while the old set stayed reachable through the singleton's invocation lists. The dead instances continued to dispatch, so a single chip click in the docked panel could fire `Selection.Target.Replace` two, three, or N times depending on how many times the view-part had been recreated in the session – root cause behind the v4.19.30 popup-double-insert bug, which the popup fix sidestepped by skipping the bubble entirely without addressing the leak itself. `Dispose` now unsubscribes all seven (using `Lazy<T>.IsValueCreated` checks to avoid forcing the singletons into existence purely to call `-=`), and the `LicenseStateChanged` lambda was extracted to a named `OnLicenseStateChanged` method so it can be unsubscribed at all.

---

## [4.19.31] – 2026-04-26

### Fixed (TermLens popup – focus returns to target after insert)

- **After picking a term from the floating TermLens popup, focus now returns to the target cell.** Previously the popup was a borderless modeless form opened with `Show()` followed by `popup.Activate()`, which stole focus from the Trados editor on open and gave Windows no owner relationship to use when assigning focus on close – so the target cell was left without keyboard focus, leaving the user with no caret to keep typing in. The popup is now opened via `Show(ownerForm)` where `ownerForm` is the WinForms host of the docked TermLens panel (i.e. the Trados main window from a Win32 perspective), the same pattern the existing Term Picker dialog uses with `ShowDialog(parent)`. The insertion callback also explicitly calls `ownerForm.Activate()` immediately before `Selection.Target.Replace` runs, so the editor is the foreground window when Trados processes the replace and the target cell is re-focused.

---

## [4.19.30] – 2026-04-25

### Fixed (TermLens popup – term inserted twice)

- **Selecting a term from the floating TermLens popup no longer inserts it twice.** Reported by a user – picking "tank vessel" produced "tank vesseltank vessel" in the target. Root cause: the popup's chips bubbled their `TermInsertRequested` event to the docked panel's existing `OnTermInsertRequested` handler (insertion #1) AND the popup also wired a close-on-insert handler that ran in the same chain. When combined with a separate handler-leak in `TermLensEditorViewPart.Initialize` (the `OnTermInsertRequested` subscription accumulates across view-part lifecycles because `Dispose` doesn't unsubscribe), the bubble path could fire the insertion two or more times per chip click. `TermLensControl.BuildSegmentBlocks` now takes an opt-out `wireInsertBubble` parameter so the popup can request blocks with no bubble wiring; the popup wires its own single handler that funnels both mouse-click and keyboard Enter through one `RequestInsert(oneBased)` method, which uses an `_pendingInsertOneBased` guard to make the insertion exactly-once and defers it to `FormClosed` so focus has returned to the Trados editor before `Selection.Target.Replace` runs. The handler-leak in `TermLensEditorViewPart` itself is untouched in this release – see follow-up issue. The docked panel's chip-click flow is unchanged (still uses the bubble).

---

## [4.19.29] – 2026-04-25

### Changed (TermLens popup – Ctrl-tap is now a toggle)

- **Ctrl-tap now toggles the floating TermLens popup open / closed instead of cycling.** Previously the second Ctrl-tap advanced the highlighted "current" match, on the theory that it kept the user on a single key for both opening and stepping. In practice closing was the more common follow-up – once the right term was visible the user wanted out – so the second press now closes the popup. Cycling stays on the keyboard via Right / Down / Tab (next) and Left / Up / Shift+Tab (previous).

---

## [4.19.28] – 2026-04-25

### Changed (TermLens shortcuts swapped)

- **Ctrl-tap now opens the floating TermLens popup; the Term Picker dialog moves to Ctrl+Shift+L.** After v4.19.27 the new floating popup proved fast enough to deserve the most ergonomic shortcut, so it inherits Ctrl-tap from the Term Picker dialog. The picker dialog (list-based UI) remains available on Ctrl+Shift+L for users who prefer that style. Ctrl+Alt+G is now the explicit-key fallback for the popup (was the picker's fallback).

---

## [4.19.27] – 2026-04-25

### Added (TermLens popup – keyboard-only term insertion)

- **New Ctrl+Shift+L shortcut opens a borderless TermLens popup** that mirrors the docked TermLens panel for the active segment. Aimed at small-screen / laptop workflows where keeping the docked panel always-visible costs too much vertical space, and at translators who want to insert terms without ever reaching for the mouse. The popup reuses the docked panel's already-loaded matcher and termbase index (no reload), renders the same `TermBlock` chips with the same colour scheme, and routes mouse-click insertions through the existing TermLens insertion flow. One match is highlighted as "current" with an amber ring around the source word; arrow keys (Right/Down/Tab and Left/Up/Shift+Tab) cycle the highlight, Enter inserts the current match into the target segment and closes the popup, Escape and click-outside close without inserting. Re-pressing Ctrl+Shift+L while the popup is open advances the highlight instead of stacking a second popup, so the user can stay on a single shortcut while skimming through matches. Initial highlight is always match #1 for predictability. The popup positions itself near the cursor and clamps to the working area; long segments wrap and scroll inside a capped popup width.

---

## [4.19.26] – 2026-04-25

### Fixed (merge prompt – reversed-direction termbases)

- **Quick-Add (Alt+Down / context menu / Alt+Up) now offers the merge prompt for partial duplicates in reverse-direction termbases.** Regression introduced in v4.19.13 when the per-termbase swap was moved inside `InsertTermBatch`: `TermMergeChecker.FindMergeMatches` was left searching with project-direction text, so for any termbase declared in the inverse direction of the project (e.g. an EN→NL termbase used in an NL→EN project), the SQL compared the DB English column against the new Dutch text and vice versa – every potential merge candidate was silently missed and a near-duplicate entry was created instead. `FindMergeMatches` now takes an optional `projectSourceLang` and applies the same per-termbase column swap that `InsertTermBatch` does internally; each match carries a new `MergeMatch.TermbaseInverted` flag so the dialogue and the `AddSynonym` calls can land the new text in the correct source-language vs target-language slot of the existing entry. Reported by a user who noticed Quick-Adding "even more preferably between" against an EN→NL PATENTS termbase that already held a "still more preferably between" entry for the same Dutch source – the merge prompt no longer fired and a separate row was created. `MergePromptDialog` was also reworked to expect callers to pass new-term text in project direction (it previously expected termbase-storage direction, which became inconsistent across callers after v4.19.13) and to use per-match `TermbaseInverted` for the existing-entry display, so the dialogue's "You are adding…" line and synonym question now read in project direction even when the termbase is reversed. `QuickAddProjectTermAction` (Alt+Up, single project termbase) was already pre-swapping at the top of the method – its merge flow worked but is now aligned with the new convention; the swap moved local to the `InsertTerm` call.

---

## [4.19.25] – 2026-04-23

### Fixed (term-entry editor – critical, reversed-direction termbases)

- **The Add and Edit dialogues no longer silently corrupt entries in termbases whose declared direction is the inverse of the project's.** The v4.19.22 fix normalised the dialogue's labels to termbase-declared direction (e.g. English on the left when the termbase is en→nl) but did not reconcile the values being loaded into those fields. The result: in a project translating Dutch → English using a termbase declared English → Dutch, adding or editing a term silently wrote the Dutch text into the `source_term` (English) column and the English text into the `target_term` (Dutch) column. The corrupted entry then stopped matching in TermLens because the matcher's index ended up keyed on the wrong-language string. The Edit path now re-reads the entry from the database by ID before populating the dialogue (via `TermbaseReader.GetTermById`), and the Add path detects when the termbase's declared direction is the inverse of the project's (using the same `LanguageUtils.ShortenLanguageName` + primary-language `StartsWith` comparison `TermbaseReader` uses at load time) and swaps the pre-filled values internally so they land in the correctly-labelled fields and are saved into the correct DB columns. Forward-only fix – pre-existing reversed entries remain reversed in the DB and need to be repaired manually via the "Reverse source/target" right-click action in the Termbase Editor or via `tools/repair_termbase_directions.py` for batch cleanup.

### Changed

- **`build.sh` deploy target moved from Roaming to LocalAppData.** Matches the install scope end-users get from the recommended "This computer for me only" option in Trados Plugin Installer, eliminating the dual-scope state Michael's dev machine used to live in (AppStore install in Local, build.sh install in Roaming). `build.sh` now also removes any leftover spaced-name `.sdlplugin` and Unpacked folder from Roaming so `HandlePendingUpdate` doesn't pick the stale copy.

---

## [4.19.24] – 2026-04-23

### Fixed (update channel consolidation – architectural change)

- **The in-plugin updater now reads exclusively from the RWS App Store catalogue.** Previously, the plugin polled GitHub releases in parallel with Studio's own App Store install tracking, creating a second source of truth for "what's installed". The two channels could disagree: the in-plugin GitHub updater would download an unsigned `.sdlplugin` that Studio flagged with the "Unsigned Trados Studio Plug-in Found" dialogue, and on the next restart the App Store install tracking would silently roll back to its last-approved version – occasionally leaving the user stuck on the previous version after a restart and still seeing the unsigned-plugin warning. With the consolidation, every installed build is RWS-signed, the two channels can no longer fight, and the unsigned-plugin dialogue cannot appear as a result of a plugin update. GitHub releases continue to host release notes as documentation; binary `.sdlplugin` assets are no longer attached.

### Fixed (orphan duplicate installs from non-Roaming install scope)

- **The updater now detects the user's original install scope and writes updates back to the same scope.** Previously, the one-click "Install Update" action always wrote the downloaded `.sdlplugin` to `%AppData%\…\Plugins\Packages\` (Roaming) regardless of where Trados Plugin Installer had originally placed the plugin – if the user had chosen "This computer for me only" during install (which lands in `%LocalAppData%\…\Plugins\`) or "This computer for all users" (`%ProgramData%\…\Plugins\`), the update created an orphan duplicate: the new version in Roaming and the old version still sitting in the original scope, with Studio picking whichever looked newer. The updater now scans all three plugin scopes via two new helpers in `UpdateChecker` (`FindCurrentInstallScopePackagesDir` / `FindCurrentInstallScopeUnpackedDir`), finds the one containing the existing install, and overwrites in place – no more stale copies left behind.

### Changed

- App Store catalogue responses are cached locally for 24 hours (at `%LocalAppData%\Supervertaler.Trados\appstore_cache.json`) to avoid unnecessary API traffic. The catalogue API does not expose `ETag` / `Last-Modified` headers, so conditional `If-None-Match` requests aren't possible – a local TTL is the right shape.
- Update-check failures (network errors, parse errors) are now handled silently – they never block Studio startup or surface error dialogues.
- Documentation (`docs/installation.md`) rewritten: Download and Updating sections now describe the AppStore-only flow; the "old version still showing after update" troubleshooting section is annotated as legacy / manual-surgery only since v4.19.24's updater is scope-aware.

---

## [4.19.23] – 2026-04-21

### Added

- **Rename custom OpenAI-compatible endpoints in AI Settings.** Previously, custom endpoints were auto-named "New Endpoint 1", "New Endpoint 2", etc., with no in-app way to give them meaningful labels (the `Name` field existed on the profile object but had no UI). A new pencil button sits next to the existing **+** / **−** buttons on the Custom OpenAI provider panel and opens a small modal dialog: pre-fills the current name, validates live (OK button disabled when empty, unchanged, or colliding with another profile's name – case-insensitive), and updates the combo in place without firing `SelectedIndexChanged` (so unsaved endpoint / model / API-key edits aren't wiped during rename). The rename persists through the existing save path – `SaveCurrentCustomProfile` rebuilds `CustomOpenAiProfiles` from combo items and `SelectedCustomProfileName` is reconciled to the current selection, so a rename survives Settings-dialog OK and Trados restart. Feature request from a user.

---

## [4.19.22] – 2026-04-21

### Fixed (termbase direction handling – architectural overhaul)

- **Term matching now uses the termbase's declared direction, not the per-entry `source_lang` / `target_lang` columns.** Legacy write-path bugs (pre-v4.19.13) left many termbases with a mix of entries whose per-entry language tags didn't agree with the termbase declaration – and `TermbaseReader.LoadAllTerms` trusted those per-entry tags when deciding whether to invert for reverse-direction projects. The result was entries silently not matching even though their text was correct. `LoadAllTerms` now pulls `source_lang` / `target_lang` from the canonical `termbases` table for the inversion decision, making matching resilient to corrupted per-entry tags. Root-cause fix for the "my termbase entry exists but TermLens says no match" class of bug.
- **The term entry editor now always shows fields in termbase-declared direction.** Previously `TermEntryEditorDialog` inverted field layout when the project direction differed from the termbase's, and this happened only sometimes depending on which entry point opened it – the Termbase Editor grid right-click did not invert, but the TermLens chip right-click did. The inconsistency made reverse-direction termbases especially confusing. Fields, labels, and save flow are now always termbase-direction, matching the Termbase Editor grid. The `projectSourceLang` parameter is retained for source compatibility but ignored.

### Added

- **"Reverse source/target" right-click action in the Termbase Editor.** Lets you fix one or many reversed-direction entries at once. Menu label dynamically shows the count when multiple rows are selected (e.g. *"Reverse source/target (12 entries)"*). The operation swaps `source_term` ↔ `target_term`, `source_lang` ↔ `target_lang`, `source_abbreviation` ↔ `target_abbreviation`, and flips every linked synonym's language tag (`'source'` ↔ `'target'`). All in one SQL transaction, so partial failure leaves the DB untouched. Right-click on the grid now also preserves multi-row selection when the clicked row was already selected – matches standard Windows list behaviour.
- **New `TermbaseReader.ReverseTermDirection(dbPath, termIds)`** helper backing the above. Takes a list of term IDs, opens a single ReadWrite connection, does the swaps atomically.

### Tools

- **New `tools/repair_termbase_directions.py`** – stopword-heuristic repair that scans a Supervertaler DB, classifies each legacy-bad entry as tag-only / text-swap / ambiguous, and applies the safe fixes. Conservative by design; skips short single-word entries where language can't be determined confidently.
- **New `tools/ai_repair_termbase_directions.py`** – LLM-powered repair using Claude Sonnet 4.6 for the entries the heuristic can't handle. Reads the Claude API key straight from the plugin's `settings.json`, batches 50 pairs per call, caches responses locally so reruns are free. Operates on one termbase at a time. See `tools/README.md` for caveats and usage.

---

## [4.19.20] – 2026-04-20

### Fixed

- **"Set as active prompt for this project" now reflects in the Batch Translate dropdown immediately and works for any prompt, regardless of which entry point opened the Settings dialog.** Three issues were stacked on top of each other:
  1. **No live sync** – the Batch Translate dropdown only refreshed when the Settings dialog closed, so right-clicking a prompt → "Set as active prompt for this project" had no visible effect until the user clicked OK. `PromptManagerPanel` now raises a static `ActivePromptChangedGlobal` event; `AiAssistantViewPart` subscribes once in `Initialize` and live-refreshes the Batch dropdown with the pending active path while the dialog is still open. Cancelling the dialog still snaps back to the persisted state via the existing unconditional refresh on close.
  2. **Category filter hid the checkmark** – even after clicking OK, prompts whose `Category` was not `Translate` (or `Proofread`, in proofread mode) were silently filtered out of the dropdown, so the checkmark had nowhere to land. The category filter now makes an exception for the active prompt – it always appears in the dropdown regardless of folder, so marking any prompt as active works uniformly. Path-separator normalisation was also extended to the selection match (previously only the active-marker check handled `/` vs `\`), eliminating a subtle mismatch when the stored path used forward slashes.
  3. **Entry-point-dependent wiring** – an earlier iteration subscribed to the event only when the Settings dialog was opened from the AI Assistant gear. Opening Settings from the TermLens gear (which instantiates `TermLensSettingsForm` in a separate code path) left the event with zero subscribers, making the live-sync appear intermittent. The static event used in the final fix is wired once at plugin init, so it catches the change regardless of which code path created the form.

---

## [4.19.15] – 2026-04-20

### Fixed

- **New prompts created at the tree root in the Prompt Manager are now visible in the Batch Translate dropdown.** Previously, creating a new prompt without first selecting a folder left its `Category` empty. The Batch Translate dropdown filters strictly by `Category == "Translate"` (or `"Proofread"`), so root-level prompts were silently excluded – marking one as the active prompt for the project had no visible effect in the Batch panel (the dropdown stayed on `(None – default)`). New prompts now default to the `Translate` category when no folder is selected, so they appear in the Batch dropdown and respect "Set as active prompt for this project" immediately. Existing root-level prompts with empty categories still need to be moved into the `Translate` folder (or re-categorised in the editor) to become visible.

---

## [4.19.14] – 2026-04-17

### Added

- **Claude Opus 4.7 support.** Anthropic's new flagship model (released 2026-04-16) is now selectable in AI Settings under the Claude provider and via the OpenRouter gateway (`anthropic/claude-opus-4.7`). Opus 4.7 has a 1M-token context window, 128k max output, and is Anthropic's most capable generally available model. Pricing is $5 / input MTok, $25 / output MTok – the same as Opus 4.6. Sonnet 4.6 remains the recommended default for most translation work; reach for Opus 4.7 when you need top-tier reasoning or long-context jobs. See [What's new in Claude Opus 4.7](https://platform.claude.com/docs/en/about-claude/models/whats-new-claude-4-7) for details.

### Fixed (cost estimates)

- **Corrected stale pricing for Claude Opus 4.6 and Haiku 4.5.** The internal pricing table had Opus 4.6 at the pre-4.6 rate of $15 / $75 per MTok – Anthropic dropped Opus pricing to $5 / $25 with the 4.6 release. Haiku 4.5 was listed at $0.80 / $4.00, corrected to the current $1.00 / $5.00. Cost estimates shown in the AI Assistant and Batch Translate were over-stating Opus usage and under-stating Haiku usage – now accurate.

### Note on Opus 4.7 tokenizer

- Claude Opus 4.7 uses a new tokenizer that can use **~1.0×–1.35× more tokens** for the same text compared to earlier models. Our pre-send cost estimates (`chars / 4` heuristic) will under-estimate Opus 4.7 costs by a similar margin. Actual billing is based on Anthropic's token counts.

---

## [4.19.13] – 2026-04-17

### Fixed (critical – termbase data integrity)

- **Wrong-direction entries were silently being written to write termbases with mixed declared directions.** `QuickAddTermAction` (Alt+Down / Ctrl+Alt+Shift+T) made a single swap decision using only the **first** write termbase's declared direction, then applied that same swap to **every** termbase in the batch. If you had two or more write termbases with different declared directions (e.g. one EN→NL and one NL→EN), the text for roughly half of them ended up in the wrong source/target columns. The rot compounded on every Alt+Down press. Existing already-wrong entries remain in the DB – clean them up row-by-row with the existing ⇄ reverse button in the Term Entry Editor. Prevention is now in place:
  - `TermbaseReader.InsertTermBatch` takes a new optional `projectSourceLang` parameter and makes a **per-termbase** swap decision inside its loop. Each termbase now stores the text in its own declared direction regardless of the mix of directions in the batch.
  - `QuickAddTermAction` no longer does a global pre-swap; it always passes the project-direction texts through and lets `InsertTermBatch` handle the per-termbase decision.
- **Duplicate check now detects reverse-direction matches.** `InsertTerm` and `InsertTermBatch` used to consider a term "new" if the same pair existed but was stored in the opposite direction (source↔target). That meant users whose termbases already contained wrong-direction entries from the bug above would end up with the concept stored **twice**, once in each direction, on re-add. The check now matches either `(source=A ∧ target=B)` or `(source=B ∧ target=A)` in the same termbase and rejects both as duplicates.

---

## [4.19.12] – 2026-04-16

### Fixed

- **Ctrl+Q (QuickLauncher) now reliably surfaces the Supervertaler Assistant panel.** Previously, selecting a QuickLauncher prompt would submit the message to the chat backend but the dockable Assistant panel could remain auto-hidden, unpinned, or buried behind another dock tab – so the user saw nothing happen. The Chat tab inside the panel was already being selected correctly by `SubmitMessage`; the missing step was activating the panel itself. `RunQuickLauncherPrompt` now calls `instance.Activate()` before submitting, matching the SuperSearch action pattern. Affects both the context-menu QuickLauncher (Ctrl+Q) and the Ctrl+Alt+digit slot shortcuts, since both funnel through the same entry point.

---

## [4.19.11] – 2026-04-15

### Fixed

- **Distill and Process Inbox now ignore Obsidian plugin sidecar files (`.edtz`)** in the SuperMemory inbox. Previously, a `.edtz` sidecar sitting next to a Markdown note would be handed to Distill as "raw material", and Distill would fail with *"Unsupported file format: .edtz"*. The inbox scanners in both Distill and Process Inbox now filter these out silently – they are editor metadata, not knowledge content. Other bank-enumeration code paths (article counts, future bank-management features) share the same filter via a new `MemoryBankReader.IsIgnoredSidecar` helper.

---

## [4.19.10] – 2026-04-13

### Changed

- **British English consistency** – all user-facing licensing messages and UI labels now use "licence" instead of "license" (e.g., "Please enter a licence key", "Licence activated successfully", Settings → Licence), matching the project's British English convention.

### Added

- **Auto-indexing after Process Inbox and Health Check.** The `05_INDICES/` folder is now automatically populated with three master index files after every successful Process Inbox or Health Check run:
  - `master-terminology.md` – a flat table of all source → target term decisions across the bank, with domain, client, confidence, and status columns.
  - `client-summary.md` – one section per client with their `tldr:` or first paragraph.
  - `domain-summary.md` – one section per domain with their `tldr:` or first paragraph.
  These indices are built by scanning frontmatter directly – no LLM call needed, completes in under a second. They make the bank browsable in Obsidian and provide the scan source for future two-pass context loading ([#22](https://github.com/Supervertaler/Supervertaler-for-Trados/issues/22), enhancement #3).

---

## [4.19.9] – 2026-04-13

### Added

- **Enriched frontmatter schema for SuperMemory articles.** Distill, Process Inbox, and Health Check now produce articles with richer YAML frontmatter: `type`, `domain`, `client`, `language_pair`, `confidence` (high/medium/low), `sources` (original filenames for traceability), `tldr` (one-sentence summary for fast scanning), `created`, and `updated`. This lays the groundwork for confidence-based context loading, staleness detection, and the universal bank vision ([#22](https://github.com/Supervertaler/Supervertaler-for-Trados/issues/22)).
- **Confidence scoring in Distill and Process Inbox.** Every article generated by Distill or Process Inbox now carries a `confidence:` field (high/medium/low) based on the authority and quality of the source material. Low-confidence articles are flagged for human review.
- **Source traceability.** Every article now carries a `sources:` frontmatter field listing the original files it was derived from. Terminology articles always quote exact source and target terms verbatim.

### Changed

- **Health Check gains confidence review and frontmatter backfill.** Health Check now identifies articles with `confidence: low` and reports them as needing verification. It also detects articles missing the enriched frontmatter fields and auto-fills them by inferring values from the article content and folder location.
- **Health Check staleness detection improved.** Articles not updated in more than 4 weeks with newer siblings on related topics are now flagged. When a newer article contradicts an older one, the older article is flagged as potentially superseded.
- **Expandable truncated chat responses.** Long AI assistant responses are now truncated at 3,000 characters (up from 1,500) with a clickable "Show full response" link at the bottom of the bubble. Clicking the link expands the bubble to show the complete response inline – no more right-clicking to copy. Most regular chat responses (translation assessments, terminology advice, etc.) now display in full without truncation.

---

## [4.19.8] – 2026-04-13

### Added

- **"Save to memory bank" from chat.** Right-click any assistant message in the AI Assistant chat and select "Save to memory bank" to save the Q&A pair (your question and the AI's response) as an inbox note in the active memory bank. Run Process Inbox afterwards to compile it into the knowledge base. This closes the feedback loop described in [#22](https://github.com/Supervertaler/Supervertaler-for-Trados/issues/22) – useful answers no longer vanish into chat history.

---

## [4.19.7] – 2026-04-13

### Fixed

- **TSV reimport failed for exported files.** The TSV export wrote language display names as column headers (e.g., "Dutch (BE)", "English (US)") which the importer could not reliably match back to language codes. Export now uses fixed "Source" / "Target" headers, matching the Workbench format. Language names in headers from external TSV files are still recognised as a fallback.

### Changed

- **TSV import confirmation dialog.** Before importing, a confirmation dialog now shows the filename, row count, target termbase name, and language pair – giving you a chance to catch mistakes before anything is written.
- **TSV import progress dialog.** Large imports (e.g., 2,700+ terms) now show a progress bar with a running count instead of freezing the UI with a wait cursor.

---

## [4.19.6] – 2026-04-13

### Changed

- **Distill button now offers a choice.** Clicking Distill shows a small dialog with two options: "Distill inbox (N files)" to automatically distill all non-Markdown files sitting in the active memory bank's `00_INBOX/`, or "Select files…" to open the file picker as before. The inbox option is disabled when the inbox has no distillable files. File names are listed in the dialog so you can see what will be processed.

---

## [4.19.5] – 2026-04-13

### Changed

- **Term Picker shortcut – Ctrl tap (memoQ-style).** Pressing and releasing Ctrl alone (without any other key) now opens the Term Picker dialogue, matching memoQ's behaviour. A quick tap of Ctrl triggers it; holding Ctrl for a combo (Ctrl+C, Ctrl+V, etc.) works normally. Maximum hold duration is 400 ms to prevent accidental triggers. The previous shortcut Ctrl+Alt+G is kept as a fallback.

### Fixed

- **Memory bank dropdown empty after licence reactivation.** Deactivating and reactivating a licence mid-session (or starting Trados unlicensed, then activating) left the memory bank dropdown empty and all AI Assistant event handlers unwired. The full post-licence initialisation now runs automatically when the licence state changes to active, without requiring a Trados restart.

---

## [4.19.4] – 2026-04-11

### Fixed

- **Clipboard Mode now respects the Limit spinner.** Previously, clicking "Copy to Clipboard" ignored the Limit value and always copied every matching segment. Now it applies the same limit as the API batch path – set Limit to 20 to copy only the first 20 segments.

---

## [4.19.3] – 2026-04-11

### Changed

- **SuperMemory help URL updated.** The GitBook page for SuperMemory moved from `/features/ai-assistant/memory-banks` to `/features/ai-assistant/super-memory`. In-plugin help links (the `?` button on SuperMemory features) updated to match.

---

## [4.19.2] – 2026-04-11

### Added

- **Crash-recovery TMX backup.** Batch Translate now writes every translated segment to a TMX file as it arrives from the AI. If Trados crashes mid-run, the backup contains everything received so far – import it into any TM to recover. A pre-checked **"Auto-backup translations to TMX"** checkbox and an **"Open folder…"** link appear below the Translate button; untick to disable for a run. Files are saved to `Supervertaler\trados\batch_backups\` and are standard TMX 1.4 – compatible with Trados, memoQ, Wordfast, and any other CAT tool that imports TMX.

### Fixed

- **Reports tab now shows entries newest-first.** Prompt log cards are sorted by timestamp during relayout, so the most recent batch or chat call always appears at the top regardless of the order entries arrived.
- **Termbase AI filtering – word-boundary matching.** `FilterRelevantTerms` now uses `\b` word-boundary regex instead of plain substring matching, so a term like "claim" no longer incorrectly matches "Disclaimer" or "Proclaim".
- **Termbase AI filtering – initialisation flag.** A bug caused `DisabledAiTermbaseIds` to be reset on every settings save, so per-project termbase exclusions were not preserved correctly across saves. Fixed.

---

## [4.19.1] – 2026-04-10

### Fixed

- **Long AI responses are now truncated in the chat.** Assistant messages longer than 1,500 characters are truncated to the first 1,000 characters in the chat bubble, with a note: *"Response truncated (N more characters). Right-click → Copy for the full text."* The full original text is always available via right-click → Copy and is preserved in the chat history. This is what finally makes long chat sessions usable – a single Health Check report can be 25,000+ characters (15 screens tall), and rendering that inline broke the scroll math in multiple subtle ways. With truncation, every bubble fits comfortably in the viewport.
- **Chat scroll rewrite.** Disabled `FlowLayoutPanel.AutoSize` and switched to manual height management, fixing the long-standing "messages disappear into ghost white space" bug on long chat histories. Users no longer need to click **Clear** before every chat message.
- **Health Check always shows a completion summary.** When the AI reports issues without auto-fixing any files (no `### FILE:` markers in the response), the chat now shows a clear *"Health Check complete – no changes applied"* message instead of leaving the user guessing whether the operation finished.
- **User-initiated actions re-engage auto-scroll.** Clicking Send, Health Check, Process Inbox, Distill, or switching memory banks now resets the "user scrolled up" flag so the progress bubble and response land in view – even if the user had been scrolled up reading history.

### Changed

- **SuperMemory is off by default for new installations.** `IncludeSuperMemoryContext` and `IncludeSuperMemoryInAutoPrompt` now default to `false`. Most translators should start with the simpler workflow (TermLens glossaries + AI context awareness) and opt into SuperMemory once they have a populated bank. Existing users are unaffected – their saved `true` values are preserved.
- **Quick Add dialog redesigned (Ctrl+Alt+M).** The field labels are now language-aware: "Source term (Dutch):" and "Target term (English):" instead of the old "Term / pattern (what's wrong):" and "Correction (correct English form):". New "Save as raw note" checkbox lets you dump ambiguous or context-dependent knowledge into `00_INBOX/` for the AI to compile via Process Inbox, instead of forcing it into a rigid source→target pair. Structured articles now use `→` in their filenames instead of `vs`.
- **SuperMemory page renamed to "SuperMemory" in GitBook.** The help page title, sidebar label, and all cross-references now consistently use "SuperMemory" as the heading (was "Memory banks"). URL is preserved at `/memory-banks` for backwards compatibility.

---

## [4.19.0] – 2026-04-09

Headline release: **SuperMemory multi-bank support**. You can now keep several self-contained translation knowledge bases side by side (one per client, per domain, per language pair) and switch between them in a single click from the Supervertaler Assistant toolbar. The two-level terminology ("SuperMemory" = system, "memory bank" = container) is now reflected consistently across the UI, docs, and help system.

### Added – SuperMemory multi-bank support

- **Memory Bank dropdown** in the Supervertaler Assistant toolbar. Lists every bank under your user-data folder with the active bank pre-selected. Switching is immediate: the next chat turn, batch translation and Process Inbox run all read from the new bank, and chat history is preserved across the switch. The active bank is persisted in settings and survives Trados restarts.
- **Create new banks from the toolbar** – pick `+ New memory bank…` at the bottom of the dropdown, enter a short name (lowercase letters, digits, hyphens or underscores) with a live preview of the final folder name, and the bank is created on disk with the full seven-folder skeleton and activated in one click. No need to touch Settings or File Explorer.
- **Bundled template files** – every new bank ships with the canonical `compile.md`, `lint.md`, `query.md`, `translate_with_kb.md` and "Claude dump" helper templates in `06_TEMPLATES/`, so Process Inbox and Health Check work against a fresh bank out of the box.
- **Heal-on-activation prompt** – if you activate an older bank that is missing its canonical template files (a bank created before this release, or one where you deleted a template by accident), the plugin shows a one-time "Missing memory bank templates" dialog offering to restore them from the built-in defaults. Existing template files are never overwritten.
- **Shared with the Python Supervertaler Assistant** – memory banks live in the shared Supervertaler user-data folder with byte-identical layout, so a bank created in Trados works unchanged in the standalone app and vice versa.
- **Legacy single-bank migration** – the first time you open a multi-bank-aware build against an older single-bank installation, the plugin offers to move your existing `memory-bank/` or `supermemory/` folder into the new `memory-banks/<name>/` layout.

### Improved

- **Distill now archives source files dropped in the inbox.** If you drop a TMX, PDF or DOCX directly into a bank's `00_INBOX/` folder and run Distill on it, the source file is moved to `00_INBOX/_archive/` after a successful distill – mirroring how Process Inbox archives the Markdown files it compiles.
- **Process Inbox now recognises non-Markdown files in the inbox.** Instead of silently ignoring TMX, PDF or DOCX files dropped in `00_INBOX/`, the button lights up and clicking it shows a helpful message pointing you at Distill for binary files. Mixed inboxes (both `.md` and binary files) process the Markdown and warn about the rest.
- **Health Check shows progress instantly.** The feature used to scan the entire bank synchronously on the UI thread before adding any chat message, leaving the user staring at a frozen UI for several seconds on mature banks. The scan now runs on a background thread and a "SuperMemory: Health Check – scanning memory bank …" bubble appears the moment you click the button.
- **Next-steps messages in Distill and Process Inbox summaries.** The chat banner after a successful Distill now suggests running Process Inbox next and then Health Check; the Process Inbox summary suggests running Health Check afterwards. Obsidian review is positioned as the optional step, not the main one.
- **Chat context bar is now green.** The "Dutch (BE) → English (GB) | Source: …" line at the top of the Supervertaler Assistant panel is now rendered in forest green (Material Design Green 800) instead of medium grey, making the current language pair and source segment much easier to spot at a glance.

### Fixed

- **Chat auto-scroll behaves correctly on long chat histories.** A combination of WinForms layout quirks caused the chat panel to scroll into a vast area of ghost white space below the last message, forcing users to click **Clear** as a workaround before every chat operation. The plugin now manually manages the chat panel's `AutoScrollMinSize` based on the actual position of the last bubble, so the scroll range always matches the real content. No more ghost white space, no more bouncing when scrolling to the bottom, no more disappearing messages after clicking Send.
- **Process Inbox button is correctly disabled when the inbox is empty.** Previously, the button was unconditionally re-enabled after every long-running SuperMemory operation (Health Check, Distill, AutoPrompt) even when the inbox had no files, leading to a dead-end click. The toolbar now tracks the last known inbox count and respects it after un-busying.
- **"Thinking…" bubble no longer bounces the chat.** The thinking-bubble animation timer used to re-scroll the chat every 2 seconds, which yanked the user back to the bottom every time they tried to scroll up during a long operation. The per-tick re-scroll has been removed; the initial scroll when the bubble is first added is enough.
- **User scroll is respected during long operations.** If you scroll up to read older content while Health Check or Distill is running, the chat stays where you put it. Scroll back to the bottom and auto-scroll re-engages automatically.
- **Reports tab label for Process Inbox.** The Reports tab used to label Process Inbox runs as "SuperMemory: Compile" (a legacy internal name from an earlier version of the feature). It now matches the toolbar button label: "SuperMemory: Process Inbox".
- **Duplicate "Thinking…" bubbles.** `SetThinking(true)` is now idempotent: calling it a second time (e.g. when a slow operation pre-sets thinking before delegating to the agent runner) does not create a second animated bubble.

### Changed

- **SuperMemory is the brand name; memory banks are the containers.** The two-level terminology (similar to Gmail/inbox or Obsidian/vault) is now reflected consistently. Chat banners, Reports tab labels, and the help menu use "SuperMemory:" as the system prefix. The toolbar dropdown and the "+ New memory bank…" sentinel use "memory bank" for the individual container. Both naming choices are stable going forward.
- **Memory Banks help pages reorganised** – the three overlapping pages (`memory-banks.md`, `context-awareness.md`, `memory-banks/ai-integration.md`) now follow a clear "one canonical home per concept" principle. `context-awareness.md` is the single authoritative menu of context sources with memory banks as one section among several; `memory-banks.md` is the noun page covering what SuperMemory is, what memory banks are, how to create and switch them, and how they sync with the Python assistant; `memory-banks/ai-integration.md` is the power-user deep dive on the loading algorithm.
- **Tooltip text** on Process Inbox, Health Check and Distill buttons clarified to refer to "the active memory bank" rather than "your SuperMemory" generically.
- **Quick Add dialog title** changed from "Add to SuperMemory" to "Quick Add to memory bank".

### Docs

- **New design memo** at `notes/multi-bank-context-composition.md` (not published to GitBook) captures the open design question of whether stacking SuperMemory + TMs + termbases is additive or noisy, with a rough testing plan for future evaluation.

### Also included (since 4.18.49, the last published RWS AppStore release)

All the 4.18.50–4.18.57 improvements are rolled into this release:

- **Shorter panel names** – docking tabs and ribbon buttons show "TermLens" and "SuperSearch" instead of "Supervertaler TermLens" and "Supervertaler SuperSearch".
- **SuperSearch improvements** – resizable preview pane (draggable splitter), visible source/target divider, highlight rendering fix (no more "documentsare" collisions), preview pane click reliability, header label clipping fix, match truncation fix (matches no longer show "Da..." instead of "Dawn").
- **SuperSearch screencast** embedded at the top of the SuperSearch help page.
- **Gemma 4 models** – Google's Gemma 4 31B and Gemma 4 26B MoE added to both the Gemini provider and OpenRouter routes.
- **Proofreading false positives for inline tags** – source and target now use the same plain-text extraction so tag markup never reaches the AI proofreader.

---

## [4.18.57] – 2026-04-07

### Changed
- **Shorter panel names** – docking tabs and ribbon buttons now show "TermLens" and "SuperSearch" instead of "Supervertaler TermLens" and "Supervertaler SuperSearch"; "Supervertaler Assistant" is unchanged

### Docs
- **SuperSearch screencast** – embedded the new SuperSearch demo video at the top of the SuperSearch help page
- **Updated help, README, and website** with video links and playlist

---

## [4.18.56] – 2026-04-06

### Added
- **SuperSearch resizable preview** – the preview pane below the results grid can now be dragged up or down to resize via a splitter bar (with hover highlight)

### Improved
- **SuperSearch preview divider** – added a visible vertical line between the Source and Target preview boxes, and refined the header background colour for better visual separation

---

## [4.18.55] – 2026-04-06

### Fixed
- **SuperSearch highlight rendering** – rewrote the highlight painting to draw yellow backgrounds first, then render text once on top, eliminating the "documentsare" word-collision and truncation artefacts from the previous overlay approach
- **SuperSearch preview pane** – preview now also updates on cell click (not just selection change), making it more reliable when clicking between results
- **SuperSearch "Target" label clipped** – increased the preview header row height so descenders (g, y, p) are no longer cut off

---

## [4.18.54] – 2026-04-06

### Fixed
- **SuperSearch highlight truncation** – search matches in the results grid showed truncated text with ellipsis (e.g. "Da..." instead of "Dawn") because the highlight overlay used `EndEllipsis` text formatting; match text is now clipped cleanly

---

## [4.18.53] – 2026-04-06

### Added
- **Gemma 4 on OpenRouter** – added Gemma 4 31B and 26B MoE to the OpenRouter provider list for users who prefer that route

---

## [4.18.52] – 2026-04-06

### Added
- **Gemma 4 models** – added Google's new open-source Gemma 4 31B and Gemma 4 26B MoE models to the Gemini provider. These use the same API key and endpoint, offer strong multilingual quality, and have a 256K context window

---

## [4.18.51] – 2026-04-06

### Fixed
- **Proofreading false positives for inline tags** – source segments were sent to the AI with raw Trados tag markup (e.g. `<field name="Seq" value="2"/>`) while target segments had tags stripped to plain text, causing the AI to flag every auto-numbering field and inline tag as "removed from the target". Both source and target now use the same plain-text extraction so tag markup never reaches the proofreader

---

## [4.18.50] – 2026-04-06

### Fixed
- **SuperMemory toolbar** – Health Check button stayed greyed out after any operation completed; now correctly restores to active blue like the other buttons

---

## [4.18.49] – 2026-04-06

### Changed
- **Single-tier licensing** – replaced the three-tier pricing model (TermLens / Supervertaler Assistant / Bundle) with a single plan: **Supervertaler for Trados** at €20/month or €200/year. All features are now included in every paid licence. Existing subscribers on older plans are automatically upgraded to full access

### Improved
- **Licence panel** – simplified UI with a single purchase link; removed the "Upgrade" link (no longer applicable)
- **About dialogue** – licence status now shows "Licence: Active" instead of tier-specific names
- **Licence overlay** – the AI Assistant panel now shows a generic "A licence is required" message instead of the old tier-specific "upgrade required" text
- **Licensing documentation** – rewritten for the single-tier model

---

## [4.18.48] – 2026-04-05

### Added
- **SuperSearch** – new dockable ViewPart (View > Supervertaler SuperSearch) for cross-file search, find & replace, and segment navigation across all SDLXLIFF files in a Trados project
  - Searches all SDLXLIFF files in the target language folder (avoids duplicate results from the source language folder)
  - Search scope: Source & Target, Source only, or Target only
  - Case-sensitive and regex search options
  - Results grid with File, Segment #, Source, Target, and Status columns
  - Matching text highlighted in yellow in both the results grid and the preview pane
  - **Preview pane** – single-click a result row to see the full source and target text in a detail pane below the grid, with large font and yellow match highlighting
  - **Click-to-navigate** – double-click a result to jump to that segment in the Trados editor (active file); clear status message for cross-file segments
  - **Find & Replace** – collapsible replace bar for target-only replacements; single replace (via Trados API, undoable) and Replace All (with two-step confirmation dialog for irreversible disk modifications)
  - **File selection** – Files button to include/exclude specific project files from the search
  - **Keyboard shortcut** – Alt+S opens SuperSearch; if text is selected in the editor, it auto-fills the search box and runs the search immediately
  - **Right-click context menu** – SuperSearch option in the editor context menu
  - Regex replace supports capture groups ($1, $2, etc.)
  - Status bar shows result count, file count, and search time in milliseconds

---

## [4.18.47] – 2026-04-05

### Added
- **Incognito Mode** – new toggle in AI Settings that tells the AI to anonymise all personal and project data in its responses. Project names, file paths, TM names, user names, and other identifying information are replaced with plausible placeholders. Useful for screen sharing, recording demos, posting screenshots in forums, or any situation where client data should remain confidential

### Fixed
- **TM discovery** – the "List TMs" and "Find TM" tools now scan all `.sdlproj` files for TM references, correctly resolving `sdltm.file:///` URIs and relative paths. This fixes the issue where TMs stored in project folders or custom locations (outside the default `Translation Memories` folder) were not found
- **Studio Tools help** – removed outdated Claude-only language; added active-development notice with support contact

---

## [4.18.46] – 2026-04-05

### Added
- **Multi-provider Studio Tools** – Studio Tools now works with all major AI providers: OpenAI, Gemini, Grok, and Mistral, in addition to Claude. Each provider uses its native function calling API (OpenAI `tools`, Gemini `functionDeclarations`, Claude `tool_use`). Ollama remains chat-only as local models have inconsistent tool support

### Improved
- **AI Assistant help** – restructured into sub-pages: Context Awareness, File Attachments, Studio Tools, and Providers and Models (matching the SuperMemory help structure)

---

## [4.18.45] – 2026-04-05

### Added
- **Project Statistics tool** – ask "What are the word counts for this project?" to get a full analysis breakdown (perfect, context, exact, fuzzy, new, repetitions) per language direction, read directly from the `.sdlproj` file
- **File Status tool** – ask "What is the translation status of the files?" to see per-file confirmation status (not started, draft, translated, approved, signed off) with segment and word counts
- **Project Termbases tool** – ask "What termbases are attached to this project?" to list termbases with their enabled state, file paths, and language index mappings
- **TM Info tool** – ask "Tell me about my English-Dutch TM" to get TM details (language pair, segment count, file size, creation date) read directly from the `.sdltm` SQLite metadata
- **TM Search tool** – ask "Search the TM for 'compliance'" to find how terms or phrases were translated before, with source/target pairs and usage counts

### Improved
- **Studio Tools help page** – expanded with all 9 tools, 30+ example questions organised by category (projects, statistics, progress, termbases, TMs, TM search, combined)

---

## [4.18.44] – 2026-04-05

### Added
- **Studio Tools** – the Supervertaler Assistant can now query your Trados Studio installation using natural language. Ask about your projects, translation memories, or project templates and the AI will look up the answer directly from your local Studio data. Uses Claude's tool use API to automatically call the right query behind the scenes – no special syntax needed, just ask naturally (e.g. "What projects do I have?", "Tell me about the Client Alpha project", "List my TMs"). The thinking indicator shows what tool is running (e.g. "Checking Trados projects…"). Currently supports four read-only tools: list projects (with optional status filter), get project details (languages, files, path), list translation memories, and list project templates. Claude-only for now – other providers continue to work as before without tool use
- **Studio Tools help page** – new documentation page with feature overview, available tools table, and 15+ example questions organised by category

---

## [4.18.43] – 2026-04-04

### Added
- **SuperMemory knowledge base integration** – SuperMemory articles (client profiles, domain knowledge, style guides, terminology decisions) are now automatically loaded into both AI chat and batch translation prompts. Context is selected based on the active Trados project: client profiles are matched by project name, domain articles by document type, and style/terminology articles are always included. Token budget management (4 000 tokens) with priority-based trimming ensures prompts stay within limits
- **Client detection** – fuzzy-matches the Trados project name against SuperMemory `01_CLIENTS/` profile frontmatter to automatically load the right client profile
- **Domain detection** – reuses the existing document analyser to load relevant domain knowledge articles (legal, medical, technical, etc.)
- **SuperMemory on/off toggle** – new "Include SuperMemory knowledge base in AI context" checkbox in AI Settings, enabled by default
- **Client code field for termbases** – new optional "Client" metadata field on term entries (e.g. "ACME", "GLOBEX"); shown in the term editor dialog and included in AI prompts when present
- **Inbox auto-refresh** – a FileSystemWatcher monitors the SuperMemory `00_INBOX` folder and automatically updates the inbox count when files are added externally (e.g. via the Obsidian Web Clipper)
- **SuperMemory toolbar heading** – the toolbar strip now shows a "SuperMemory" label and a "?" help link that opens the SuperMemory documentation
- **SuperMemory help links** – the Chat tab's help dropdown now includes a "SuperMemory Help" item; the toolbar "?" also links to the docs

### Improved
- **SuperMemory documentation** – rewrote the safety/backup section with practical advice (no git jargon); added Obsidian Web Clipper setup instructions; documented the AI context integration, auto-detection, and token budget

---

## [4.18.40] – 2026-04-04

### Added
- **SuperMemory** – a self-organizing, AI-maintained translation knowledge base that replaces traditional TMs and term bases with a living wiki of interlinked Markdown files. Stores client profiles, terminology decisions, domain conventions, and style preferences in a human-readable vault that the AI consults when translating. Inspired by Andrej Karpathy's LLM knowledge base architecture
- **SuperMemory Quick Add (Ctrl+Alt+M)** – capture terms and corrections from the Trados editor into your SuperMemory vault. Also available via right-click in the editor grid. Pre-fills from source/target selection, adapts the correction label to the target language (e.g. "Correct Dutch form"), and optionally appends the term to the active translation prompt's terminology table for immediate effect on the next Ctrl+T
- **Per-project active prompt** – right-click any translation prompt in the Prompt Manager and choose "Set as active prompt for this project". Shown with a pin icon and bold blue text in the Prompt Manager, and with a checkmark in the Batch Translate dropdown. Saved per project so different projects can use different prompts
- **Active prompt auto-selection in Batch Translate** – opening a project with an active prompt set automatically selects it in the dropdown

### Improved
- **Selectable and copyable proofreading report text** – issue descriptions and suggestions in the Reports tab are now selectable text with right-click context menu (Copy issue description, Copy suggestion, Copy all). Clicking the segment number or card background still navigates to the segment
- **Batch Translate prompt dropdown layout** – fixed the AutoPrompt button disappearing off-screen when long prompt names were selected; the dropdown and button now resize properly with the panel width
- **Updated help documentation** – SuperMemory, keyboard shortcuts, batch translate, prompts, and project settings pages updated

### Fixed
- **Active prompt indicator path comparison** – normalised path separators so the active prompt checkmark displays correctly regardless of stored path format

---

## [4.18.39] – 2026-04-04

### Fixed
- **Term direction inversion broken for same-language locale pairs** – Quick-add (Alt+Up, Alt+Enter) and the term entry editor incorrectly swapped source/target terms in projects where source and target are variants of the same language (e.g. en-US → en-GB), because the language name comparison failed on format differences ("English (United States)" vs "English (US)"). Now normalises both names via `ShortenLanguageName` before comparing, so locale variants match correctly while genuinely reversed directions (e.g. NL→EN project with EN→NL termbase) still invert as expected.

### Added
- **SuperMemory help page** – new documentation page describing the upcoming self-organising, AI-maintained translation knowledge base feature

---

## [4.18.38] – 2026-04-02

### Improved
- **Markdown table rendering in term notes/definitions** – wide tables (cells longer than 40 characters) now render as labelled paragraphs with full inline formatting (bold, italic, emoji, inline code) instead of a cramped monospace grid that stripped all formatting; compact tables still use the monospace layout

---

## [4.18.37] – 2026-04-02

### Added
- **OpenRouter provider** – access 200+ models from OpenAI, Anthropic, Google, Mistral, and others with a single API key. Includes a curated dropdown of 8 recommended models (Claude Sonnet/Opus, GPT-5.4/Mini, Gemini 3.1 Pro/Flash, Mistral Small 4, Qwen 3.6 Plus Free) plus an editable model field for typing any OpenRouter model ID

### Fixed
- **AI Settings provider/model dropdowns overflowing the dialog** – the Provider and Model dropdowns could extend beyond the right edge of the Settings dialog due to incorrect anchor margin calculation; now properly sized to fit the dialog width and resize with it
- **Model dropdown too narrow for descriptions** – the dropdown list now auto-sizes its width to fit the longest model description text

---

## [4.18.36] – 2026-04-02

### Improved
- **Respect Trados "Enabled" checkbox for MultiTerm termbases** – MultiTerm termbases disabled in Trados Project Settings → Termbases are now excluded from TermLens; previously all attached termbases were loaded regardless of the Enabled flag
- **Instant refresh when toggling termbases in Project Settings** – a lightweight 2-second polling timer detects changes to the Trados termbase configuration so enabling or disabling a MultiTerm termbase takes effect immediately without needing to change segments

---

## [4.18.35] – 2026-04-02

### Fixed
- **MultiTerm matches disappear after adding or editing a term** – adding a term via the dialog, editing a term via right-click, or saving AI Assistant settings triggered a full Supervertaler termbase reload that replaced the in-memory index without re-merging MultiTerm entries; the green MultiTerm chips would vanish until navigating to a different segment and back. Fixed by re-loading MultiTerm termbases after every full index rebuild. This bug has been present since MultiTerm support was introduced in v3.4.0.

---

## [4.18.34] – 2026-04-02

### Improved
- **Batch Operations provider/model selector** – the provider label in Batch Operations is now clickable and shows the same cascading flyout menu as the Chat tab, allowing quick model switching without opening AI Settings

---

## [4.18.33] – 2026-04-01

### Added
- **Supervertaler Assistant licensing tier** – new standalone plan for AI-only users (€15/month or €150/year); grants access to AI Assistant, Batch Translate, QuickLauncher, and Ctrl+T without requiring TermLens; termbases (including MultiTerm) are still loaded for AI prompt context
- **Three-tier pricing** – TermLens (€10/month), Supervertaler Assistant (€15/month), TermLens + Supervertaler Assistant (€20/month); annual plans include 2 months free

### Improved
- **Settings gear icon** – switched from ⚙ (Segoe UI Symbol) to the native Windows 11 gear icon (Segoe MDL2 Assets) on both panels
- **Settings tab renamed** – "TermLens" tab in Settings renamed to "Termbases" so it makes sense for all tiers

---

## [4.18.32] – 2026-04-01

### Improved
- **Clipboard Mode uses shorter per-segment language labels** – segment lines now use "Dutch:" / "English:" instead of "Dutch (Netherlands):" / "English (United Kingdom):", saving roughly 2,000 tokens on a 500-segment document; the full language names with variants are still stated once in the system prompt
- **Prompt dropdown auto-selects by project name** – when switching to Batch Operations or changing between Translate and Proofread, the prompt dropdown automatically selects the first prompt whose name contains the current Trados project name (e.g. opening the HAYNESPRO project auto-selects the "HAYNESPRO" prompt)

### Fixed
- **Clipboard Mode now uses the selected prompt** – "Copy to Clipboard" was reading a stale prompt selection from settings instead of the current dropdown value, causing it to use the previous prompt; the dropdown selection is now persisted before building the clipboard prompt

---

## [4.18.31] – 2026-04-01

### Fixed
- **Per-project termbase selection no longer carries over to new projects** – when opening a Trados project for the first time, TermLens now starts with clean defaults (all termbases enabled, no Write or Project flags) instead of inheriting the previous project's selections

---

## [4.18.30] – 2026-04-01

### Added
- **Clipboard Mode** – new workflow for translating and proofreading via any web-based LLM (ChatGPT, Claude, Gemini, etc.) without an API key; tick the "Clipboard Mode" checkbox in Batch Operations, click "Copy to Clipboard" to get a fully formatted prompt with numbered bilingual segments (including status annotations, terminology, and document context), paste into any LLM chat, then click "Paste from Clipboard" to import the translations back into Trados with full tag reconstruction and validation

### Changed
- **`built_in` YAML field renamed to `default`** – prompt files now use `default: true` instead of `built_in: true`; the old field name is still accepted for backward compatibility; all internal C# naming updated accordingly (`IsBuiltIn` → `IsDefault`, `EnsureBuiltInPrompts` → `EnsureDefaultPrompts`, etc.)
- **Term Picker shortcut changed from Ctrl+Alt+G to Ctrl+Alt+Down** – updated shortcut binding, About dialog, and all documentation

---

## [4.18.28] – 2026-03-31

### Fixed
- **Batch Translate/Proofread no longer fails with "Cannot determine source/target language"** – the language pair is now cached when a document is opened and when segments are navigated, so the AI Assistant can still resolve languages even when the editor's ActiveFile is temporarily unavailable

---

## [4.18.27] – 2026-03-30

### Added
- **Text transforms in QuickLauncher** – new `type: transform` prompt type that performs local find-and-replace on the active target segment without calling an AI provider; rules are defined as `find:`/`replace:` pairs in the prompt content body with `\uXXXX` Unicode escape support; built-in "Strip U+2028" transform removes invisible InDesign line separators; cleaned text is automatically copied to the clipboard; see [Text Transforms](https://supervertaler.gitbook.io/trados/features/text-transforms) in the help docs

---

## [4.18.26] – 2026-03-30

### Added
- **Batch size spinner in AI Settings** – new "Batch size" control lets users adjust the number of segments sent per API call during Batch Translate and Batch Proofread (range 5–100, default 20)

### Fixed
- **InDesign U+2028/U+2029 line separators no longer corrupt AI translations** – Unicode line and paragraph separators (used by InDesign IDML as forced line breaks) are now replaced with spaces before sending to the AI provider, preventing spurious line breaks in translations

---

## [4.18.25] – 2026-03-30

### Added
- **Customisable Ollama timeout** – new "Timeout (min)" setting in AI Settings lets users override the automatic timeout (3–10 min based on model size) with a custom value up to 120 minutes, useful for long-running jobs on hardware without a dedicated GPU
- **QuickLauncher flat section display** – right-click any QuickLauncher folder in Settings → Prompts and choose "Show as section in menu" to display its prompts as a flat list with a bold header instead of an expandable submenu

### Fixed
- **Duplicate "What file is this segment from?" prompt** – old file variant (without trailing underscore) is now cleaned up on startup
- **Ollama default timeout for 10–12B models** – models in the 10B–12B range (including TranslateGemma 12B) now default to 5 minutes instead of 3

---

## [4.18.22] – 2026-03-29

### Added
- **QuickLauncher folder submenus** – context menu now mirrors your prompt library's folder structure instead of a flat list, making it easier to organise and find prompts
- **New default prompts** – three Explain prompts (brief, detailed, terminology) and two Files prompts (current filename, segment file source) ship out of the box in dedicated subfolders
- **Old explain prompts auto-cleaned** – retired explain prompt variants are automatically deleted on startup to avoid duplicates

### Changed
- **Domain → Category rename** – the `Domain` property on prompts has been renamed to `Category` throughout the UI and codebase for clarity
- **Support links updated** – About dialog, support docs, and help pages now point to the Supervertaler forum on TranslationTech; Groups.io and ProZ.com references removed

---

## [4.18.21] – 2026-03-29

### Fixed
- **Line breaks preserved in Visio and similar formats** – when the source segment stores line breaks as literal `\n` in text content (Visio `.vsdx`, Excel) rather than as separate placeholder tags (DOCX `w:br`), the newlines are now correctly preserved in the translated target instead of being silently dropped

---

## [4.18.20] – 2026-03-28

### Fixed
- **Gemini API key no longer exposed in error messages** – API key moved from URL query parameter to `x-goog-api-key` request header
- **Licence file corruption no longer silently resets to trial** – a warning is now shown when the licence file is unreadable, prompting the user to re-enter their key
- **Gemini response parsing more robust** – regex anchored to the `candidates[].content.parts[].text` response structure to avoid matching wrong fields
- **Unicode escapes in translations handled correctly** – `\uXXXX` sequences in LLM responses are now unescaped to the correct characters

### Changed
- **Diagnostic loggers removed** – temporary term-content loggers in TermbaseReader and TermMatcher that wrote to disk on every segment navigation have been removed
- **ARM64 SQLite DLL declared in manifest** – `runtimes/win-arm64/native/e_sqlite3.dll` now has a corresponding `<File>` entry in the plugin manifest
- **Default model names stay in sync** – provider defaults now reference the `LlmModels` arrays directly instead of hardcoded strings
- **Multi-word term matching optimised** – the list of multi-word terms is now cached and only rebuilt when the termbase index changes
- **Ping URL updated** – usage statistics endpoint updated to the `supervertaler.com` domain
- **Term reader resilient to schema changes** – `ReadTermEntry` uses column name lookup instead of hardcoded positional indices

---

## [4.18.19] – 2026-03-28

### Fixed
- **Line endings preserved in translation** – when the LLM emits a bare newline instead of a `<tN/>` tag placeholder, the correct line ending is now restored in the target segment: soft-return tags (DOCX `↵`) are re-inserted by cloning the source tag; for file formats where the newline is already embedded in text content (Visio, Excel, plain text), the newline is preserved as-is in the target IText node

---

## [4.18.18] – 2026-03-28

### Changed
- **Batch Translate reports consolidated** – a multi-batch translate operation now appears as a single entry in the Reports tab (showing combined token count, cost, and total duration) instead of one entry per sub-batch

---

## [4.18.17] – 2026-03-27

### Added
- **Mistral AI support** – Mistral Large, Mistral Small, and Mistral Nemo are now available as a first-class provider alongside OpenAI, Claude, Gemini, Grok, and Ollama

---

## [4.18.16] – 2026-03-27

### Added
- **Cost protection** – QuickLauncher prompts are now standalone (no chat history sent), preventing accidental high costs from accumulated context; chat messages are constrained by a token budget that automatically trims old messages; a cost warning dialog appears when estimated input exceeds $0.50
- **Enriched API error messages** – common errors (403 model not enabled, 401 invalid key, 429 rate limit, 402 insufficient funds) now show user-friendly guidance instead of raw JSON
- **Help system improvements** – fixed off-by-one in Settings tab help links; Settings help button now shows a dropdown preview consistent with other panels; added help topics for General, QuickLauncher, and Usage Statistics

### Changed
- **OpenAI models streamlined** – replaced GPT-4.1, GPT-4.1 Mini, and o4-mini with GPT-5.4 and GPT-5.4 Mini; default model is now GPT-5.4 Mini; existing users are auto-migrated
- **GPT-5.x API compatibility** – use `max_completion_tokens` instead of `max_tokens` for GPT-5.x models
- **Default prompts** – "Built-in" renamed to "Default" throughout (folder, UI labels, docs); default prompts are now immutable (use Clone to modify); "Show in QuickLauncher menu" checkbox for hiding prompts
- **QuickLauncher menu** – section headers ("Default" / "Custom") replace star indicators
- **AutoPrompt hidden in Proofread mode** – the AutoPrompt link is no longer shown when Batch Operations is in Proofread mode
- **AutoPrompt language softened** – replaced aggressive meta-prompt wording ("FORBIDDEN", "NON-NEGOTIABLE") with clearer, firmer alternatives to reduce LLM safety-filter refusals

### Documentation
- AI Cost Guide updated with GPT-5.4 / GPT-5.4 Mini pricing
- Model lists updated across all docs
- "Built-in Prompts" → "Default Prompts" in help navigation
- Star note added to README

---

## [4.18.15] – 2026-03-26

### Changed
- **Default max segments reduced to 20** – the "Include full document content in AI context" setting now defaults to 20 segments instead of 500, avoiding unexpectedly high API costs for new users

---

## [4.18.14] – 2026-03-26

### Added
- **Generate project brief** – new built-in QuickLauncher prompt that produces a comprehensive Markdown summary of the current project (subject matter, terminology, named entities, translation challenges) for pasting into other AI tools
- **Restore button tooltip** – the Restore button in the Prompt Manager now shows "Restore all built-in prompts to their defaults"

### Changed
- **Prompts now saved as `.md`** – prompt files use standard Markdown (`.md`) extension instead of `.svprompt`; existing `.svprompt` files are auto-migrated on startup; a new `type: prompt` YAML field identifies prompt files
- **QuickLauncher heading opens Prompts tab** – clicking the "Supervertaler QuickLauncher" heading in the Ctrl+Q menu now correctly opens Settings → Prompts instead of AI Settings
- **App label spacing** – added spacing between the "App:" label and dropdown in the prompt editor dialog

---

## [4.18.12] – 2026-03-25

### Fixed
- **Settings accessible when trial expires** – the gear button on both the TermLens and Supervertaler Assistant panels now opens the Settings dialog even when the trial has expired or no licence is active, so users can enter a licence key
- **AI Assistant gear button visible above overlay** – the settings and help buttons are no longer hidden behind the licence overlay
- **Per-project settings now reliably remembered** – fixed a bug where `NotifySettingsChanged` reloaded global settings without re-applying the per-project overlay, causing termbase paths and enabled/disabled lists to revert when switching projects
- **Per-project settings auto-saved on first encounter** – opening a Trados project that has no Supervertaler settings file now auto-creates one from the current configuration
- **Per-project settings saved on shutdown** – settings are now saved when Trados closes, not just on project switch or settings dialog OK

### Changed
- **Clearer expired trial message** – both panels now say "Click the ⚙ button above" instead of the vague "Enter a license key in Settings → License"
- **Human-readable project settings filenames** – per-project files now use the format `{hash} - {project name}.json` instead of just `{hash}.json`; existing files are auto-migrated on load
- **Pretty-printed project settings JSON** – project settings files are now indented for readability

---

## [4.18.11] – 2026-03-25

### Added
- **MultiTerm termbases in AI Settings** – MultiTerm termbases now appear in the "Termbases included in AI prompts" checklist on the AI Settings tab, matching what's shown on the TermLens tab

### Changed
- **Feature renamed: AutoPrompt** – "Analyse Project & Generate Prompt" is now called **AutoPrompt** throughout the UI, docs, and log labels
- **TermScan** naming consistent in docs – the automatic glossary extraction step is consistently referred to as TermScan

---

## [4.18.10] – 2026-03-24

### Added
- **Unified prompt library schema** – prompts now use a consistent YAML frontmatter format (`category`, `app`, `built_in`) shared between Supervertaler Workbench and Supervertaler for Trados
- **App-specific prompt filtering** – prompts tagged `app: "workbench"` are hidden in Trados; prompts tagged `app: "trados"` are hidden in Workbench; `app: "both"` (default) shows everywhere
- **App dropdown in prompt editor** – new "App" field lets you choose whether a prompt is for Both, Trados only, or Workbench only
- **Variable insertion menu reorganised** – Ctrl+, now groups variables into Common and Trados-specific sections
- **MultiTerm diagnostic logging** – loading failures are now logged to `%LocalAppData%\Supervertaler.Trados\multiterm_debug.log` instead of being silently swallowed

### Changed
- **Prompt YAML keys standardised** – `domain` → `category`, `sv_quickmenu`/`quick_run` → `quickmenu`, `quicklauncher_label` → `quickmenu_label`; legacy keys are still accepted for backward compatibility
- **Prompt library cleaned up** – removed duplicate folders and files, fixed malformed YAML frontmatter, standardised variable names (`{{SOURCE_TEXT}}` → `{{SOURCE_SEGMENT}}`)

### Removed
- **Example project removed** – the bundled example project contained a client reference and has been removed from the repo, all releases, and the help system

---

## [4.18.9] – 2026-03-24

### Added
- **Markdown rendering in TermLens popup** – Notes and Definition fields now render Markdown formatting (tables, bold, italic, headings, bullet lists, code blocks) instead of plain text
- **Resizable TermLens popup** – drag the bottom-right corner grip to resize the popup; width is remembered for the rest of the session
- **Copy raw Markdown from AI Assistant** – right-click → Copy on a chat bubble now copies the original Markdown (preserving tables and formatting) instead of stripped plain text

### Changed
- **Wider TermLens popup** – default maximum width increased from 500px to 700px so tables display more clearly
- **AI Assistant uses proper Markdown tables** – system prompt now instructs the AI to use valid Markdown table syntax with pipe delimiters and separator rows
- **User data folder restructured** – Trados settings files (`settings.json`, `license.json`, `chat_history.json`) now live under `trados/settings/` instead of directly in `trados/`; auto-migrated on first run

### Fixed
- **In-plugin purchase links now use live checkout** – the "Buy" links in Settings → License were still pointing to test mode URLs

---

## [4.18.8] – 2026-03-24

### Fixed
- **In-plugin purchase links now use live checkout** – the "Buy" links in Settings → License were still pointing to test mode URLs

---

## [4.18.7] – 2026-03-24

### Changed
- **Trial period reduced to 14 days** – down from 90 days; the free trial now runs for 14 days from first launch
- **Live Lemon Squeezy checkout** – switched from test mode to live payment processing

---

## [4.18.6] – 2026-03-23

### Added
- **Prompt inspector in Reports tab** – every AI API call can now be logged with the full system prompt, messages, response, token counts, and estimated cost; enable via "Log prompts and responses to Reports tab" in AI Settings
- **Expandable prompt sections** – click "Show system prompt...", "Show messages...", or "Show response..." to view the full text; press Escape to collapse; "Copy" copies a single section, "Copy all" copies everything
- **Batch translate and proofread logging** – batch operations now appear in the Reports tab when prompt logging is enabled
- **Prompt name in Reports tab** – entries show the prompt template name (e.g. "QuickLauncher · Explain in Context · 14:32:05")
- **Clone prompt** – right-click any prompt in the Prompt Manager and select "Clone" to create a copy with "(2)" appended
- **QuickLauncher menu heading** – Ctrl+Q menu now shows "Supervertaler QuickLauncher" at the top; click it to open Settings → Prompts tab

### Fixed
- **Tracked changes no longer corrupt term additions** – Add Term, Quick-Add, Non-Translatable, QuickLauncher prompts, and Expand Selection now strip deleted tracked changes, adding only the final text
- **QuickLauncher segment context** – QuickLauncher prompts now pass clean segment text without tracked changes markup
- **Prompt log cards no longer squashed** – the resize handler was recalculating prompt log card heights using the proofreading layout logic, hiding all expandable sections
- **New prompts in subfolders now saved correctly** – creating a prompt while a subfolder was selected would create a wrongly-named folder instead of placing the file in the correct subfolder

---

## [4.18.4] – 2026-03-23

### Fixed
- **Tracked changes no longer confuse AI features** – proofreading, batch translation, AI chat, and prompt generation now read only the final (accepted) text from segments with tracked changes, instead of including both deleted and inserted text
- **GPT-5.4 replaces GPT-5.3** – updated to OpenAI's latest flagship model

### Changed
- **Updated LLM model lineup** – OpenAI: GPT-4.1, GPT-4.1 Mini, GPT-5.4, o4-mini; Gemini: added 3.1 Pro (Preview); Ollama: Qwen 3 bumped to 14B
- **Descriptive model tooltips** – each model now shows a short description to help translators choose the right one

---

## [4.18.3] – 2026-03-23

### New Features
- **Delete prompt folders** – right-click any folder in the prompt library tree and select "Delete Folder" to remove it and all prompts inside (with confirmation dialog)
- **Refresh button in Prompt Manager** – toolbar "Refresh" button reloads prompts from disk, reflecting any changes made outside Trados (e.g. in Windows Explorer or Supervertaler Workbench)
- **UI scale setting** – new General tab in Settings with UI scale selector for high-DPI displays
- **Chat font size controls** – A+/A− buttons in the AI Assistant to adjust chat bubble font size; persisted across sessions

### Fixed
- **Right-click context menu on prompt folders** – right-clicking a folder in the prompt library now correctly selects the folder before showing the context menu (WinForms TreeView doesn't auto-select on right-click)

### Changed
- **DPI-aware UI rendering** – TermLens, AI Assistant chat bubbles, term blocks, and settings dialog now scale correctly on high-DPI displays using the new `UiScale` helper

---

## [4.18.2] – 2026-03-22

### Fixed
- **Manifest version sync** – the `pluginpackage.manifest.xml` version was stuck at 4.18.0.0 while the DLL and plugin.xml were at 4.18.1.0. All three version files are now correctly synced to 4.18.2.

### Changed
- **TermLens colour coding docs** – renamed "Lavender" to "Purple" for abbreviation match chips, as it is more recognisable as a colour name.

---

## [4.18.1] – 2026-03-22

### Fixed
- **Editing terms in inverted-direction termbases corrupted entries** – when editing a term via the multi-entry editor (right-click → Edit Term) and the termbase direction was opposite to the project direction (e.g. EN→NL termbase in a NL→EN project), the source and target terms were saved swapped. This caused the edited entry to disappear from TermLens on the next refresh and left corrupted data in the termbase. The multi-entry editor now correctly detects and handles inverted termbase directions, matching the behaviour of the single-entry editor.

### Added
- **Diagnostic logging for duplicate source terms** – temporary logging to `%LocalAppData%\Supervertaler.Trados\termlens_diag.log` when multiple entries share the same source term. Helps diagnose any remaining edge cases. Will be removed in a future release.

---

## [4.18.0] – 2026-03-22

### Added
- **TreeView-based Prompt Manager** – the Prompts settings tab has been completely redesigned. The flat grid has been replaced with a folder-based tree view that mirrors the on-disk `prompt_library` structure. Click any prompt to see its content in the detail pane on the right. Click "System Prompt" to view and edit the system prompt. Folders can be created, and prompts can be dragged and dropped between folders.
- **QuickLauncher keyboard shortcuts** – assign Ctrl+Alt+1 through Ctrl+Alt+0 to individual QuickLauncher prompts in Settings → Prompts. Each shortcut runs the assigned prompt instantly without opening the menu. Shortcuts are shown next to prompt names in the Ctrl+Q menu.
- **Prompt reordering** – prompts within a folder can be moved up or down using the ▲/▼ buttons in the toolbar. The order is persisted via a `sort_order` field in the YAML frontmatter and applies to the Ctrl+Q menu as well.
- **Quick model switching** – click the provider/model label at the bottom of the AI Assistant chat to change models without opening Settings. Shows "Click to change model" tooltip on hover.

### Changed
- **Improved term popup readability** – definition and notes text is now darker and more readable in the TermLens hover popup.
- **Multi-line term fields** – Definition and Notes in the term entry editor now have expand/collapse buttons (▲/▼) to toggle between compact and expanded views.
- **Resizable Prompts panel** – the divider between the tree and detail pane in Settings → Prompts can now be dragged left or right to resize.
- **Prompt generator respects TM toggle** – the "Analyse Project & Generate Prompt" feature now respects the "Include TM matches in AI context" setting. Previously, TM reference pairs were always collected regardless of this toggle.

### Fixed
- **HttpClient timeout causing prompt generation failures** – the .NET HttpClient's default 100-second timeout was silently overriding our per-request timeout settings (up to 10 minutes). This caused all long-running API calls (prompt generation, large batch translations) to fail after exactly 100 seconds across all providers. Fixed by setting HttpClient.Timeout to infinite and managing timeouts via CancellationToken as intended.
- **Silent timeout errors** – when an API request timed out, the thinking indicator would disappear with no error message. Now shows a diagnostic message with model name, prompt size, and max output tokens to help troubleshoot.
- **Expand buttons in term editor** – fixed z-order issue where the Definition expand button was hidden behind the text box. Both expand buttons now render correctly.

---

## [4.17.1] – 2026-03-21

### Changed
- **Auto-restart after update** – clicking "Install Update" now offers to automatically restart Trados Studio, instead of asking the user to close and reopen it manually. Saves time on the lengthy Trados startup cycle.

---

## [4.17.0] – 2026-03-21

### Added
- **Document attachments** – attach documents directly to AI Assistant messages for context. The AI receives the full extracted text alongside your message. Supported formats: DOCX, DOC, PDF, RTF, PPTX, PPT, XLSX, XLS, CSV, TSV, TMX, SDLXLIFF, XLIFF/XLF, TBX, TXT, Markdown, HTML, JSON, and XML. Drag and drop files onto the chat input, or use the attach button to browse.
- **Quick model switching** – click the provider/model label at the bottom of the AI Assistant chat to instantly switch between models and providers without opening the Settings dialogue. A dropdown menu shows all available models grouped by provider, with the current selection highlighted.
- **Multi-line Definition and Notes fields** – the term entry editor now uses expandable multi-line text areas for Definition and Notes fields, with a pop-open button to toggle between compact (3 lines) and expanded (8 lines) views. Line breaks in definitions and notes are now preserved correctly.

### Changed
- **Unified attach button** – the image-only attach button has been replaced with a universal paperclip (📎) icon that handles both images and documents. The file dialogue is organised into categories: Images, Documents, Spreadsheets, Translation files, and Text files. The tooltip lists all supported formats.
- **Improved term popup formatting** – definition and notes labels are now bold in the hover popup, and multi-line content uses hanging indentation so continuation lines align with the first line of text rather than the label.

---

## [4.16.2] – 2026-03-20

### Added
- **TermScan filtering for prompt generation** – "Analyse Project & Generate Prompt" now scans your document's source segments and filters termbase terms down to only those that actually appear in the document. This produces dramatically smaller, more focused glossaries in the generated prompt (e.g. 123 relevant terms from 2,680 total). The status message shows the filter count: "filtered X relevant from Y total".

### Changed
- **Chat avatars** – each message in the AI Assistant now has a small avatar header: a gray "AI" circle with "Supervertaler Assistant" for AI responses, and a blue person silhouette with "You" for your messages. Makes it easy to tell who said what at a glance.
- **Animated thinking indicator** – the AI Assistant now shows a persistent animated bubble in the chat area while waiting for a response. The bubble cycles through reassuring messages ("Thinking…", "Still working on it…", "Generating response…", etc.) so you always know the AI is still processing. Previously, the thin "Thinking…" label at the bottom could disappear during long operations, making it look like the request had silently failed.
- **System-initiated messages styled as assistant bubbles** – messages triggered by buttons (e.g. "Analyse Project & Generate Prompt") now display with assistant styling (gray, left-aligned) instead of user styling, since you didn't type them yourself.
- **TM reference pairs filtered to confirmed segments** – the prompt generator now only includes human-confirmed segments (Translated, Approved, or Signed-off) as reference pairs. Unconfirmed AI-generated translations are excluded to avoid feeding unverified output back as "correct" references. Pairs are sampled evenly across the document for diversity.

### Fixed
- **Stale prompt dropdown** – deleting prompts from the Prompt Manager no longer leaves ghost entries in the Batch Operations prompt dropdown. The dropdown now refreshes whenever the Prompt Manager closes, regardless of whether you clicked OK or Cancel (because deletions happen immediately on disk).
- **API timeout for large output requests** – prompt generation and other requests that produce long AI responses (> 8,192 tokens) now use a 10-minute timeout instead of timing out prematurely. This prevents the "thinking" indicator from disappearing mid-generation on complex documents.

---

## [4.16.1] – 2026-03-20

### Added
- **One-click plugin update** – the "Update Available" dialogue now has an "Install Update" button that downloads and installs the new version directly, without opening a browser. Just click, restart Trados, and you're running the latest version.
- **"What's new" link in update dialogue** – view the release notes before updating.

### Fixed
- **Prompt generation truncation** – the "Analyse Project & Generate Prompt" feature no longer cuts off long prompts. Output token limit increased from 4,096 to 32,768, allowing comprehensive prompts with large glossaries and TM reference pairs.
- **Correct version in plugin packages** – the `.sdlplugin` manifest now reads the version from the project file instead of using a hardcoded value. The DLL, manifest, and plugin.xml versions are guaranteed to match.
- **Stale assembly references** – fixed two action entries in plugin.xml that were stuck on old version numbers (4.5.0 and 4.10.0). The version bump script now uses pattern matching to catch all references, preventing this from recurring.

---

## [4.16.0] – 2026-03-20

### Added
- **Interactive term popup** – hovering over a term chip now shows an interactive popup instead of a standard tooltip. The popup supports word-wrapped text, stays open when you move the mouse into it, and renders URLs as clickable links.
- **URL metadata field** – term entries now support a URL field for linking to reference material. URLs appear as clickable links in the hover popup, and can be edited in the term entry editor and termbase editor grid.
- **Dismissible proofreading issues** – each issue card in the Reports tab now has a checkbox; ticking it removes the card from the list so you can track which issues you have addressed.

### Changed
- **Proofreading scope labels** – dropdown labels now use correct Trados terminology: "Translated only" and "Translated + approved/signed-off" instead of the previous MemoQ-style "Confirmed" labels.
- **Faster popup close** – the hover popup close delay was reduced from 200ms to 150ms for a snappier feel.

### Fixed
- **Popup text truncation** – long definitions, notes, and other metadata no longer get cut off in the hover popup. Text now word-wraps correctly within the popup.
- **Popup spacing** – removed excessive vertical spacing between metadata lines in the hover popup.

---

## [4.15.0] – 2026-03-20

### Added
- **Grok (xAI) provider support** – Grok is now available as a first-class AI provider alongside OpenAI, Claude, Gemini, Ollama, and Custom OpenAI-compatible endpoints. Three models included: Grok 4.20 (Reasoning), Grok 4.20, and Grok 4.1 Fast. All models support multimodal input (text + images).
- **Source synonym indicator** – the ≡ synonym indicator on term chips now also appears when the entry has source-side synonyms, not just target-side ones.
- **Source synonyms in tooltip** – hovering over a term chip now shows source-side synonyms (prefixed with "Also:") alongside the existing target-side synonym bullets.

### Fixed
- **Merge prompt direction** – the "Similar Term Found" merge dialog now correctly displays terms in the project's language direction when working with inverted termbases.

---

## [4.14.1] – 2026-03-19

### Added
- **Synonym indicator on term chips** – a small indigo ≡ icon now appears in the top-right corner of a term chip when the entry has target synonyms, so you can see at a glance which terms have alternative translations without hovering.
- **"Open Plugins folder" link in update dialog** – when a new version is available, the update notification now includes a clickable link that opens the Plugins/Unpacked folder in Explorer. Essential for Mac/Parallels users who must manually delete the old unpacked folder before installing an update.

### Fixed
- **Metadata indicator always visible** – the amber metadata dot (definition/domain/notes) now appears on all term chips, not only on chips that also have a shortcut badge.
- **Merge prompt respects project direction** – the "Similar Term Found" dialog now displays terms in the project's language direction when working with an inverted termbase (e.g. NL→EN project using an EN→NL termbase). Previously, source and target labels were swapped.

---

## [4.14.0] – 2026-03-19

### Added
- **Analyse Project & Generate Prompt** – new feature that analyses your document's content, terminology, and TM data to automatically generate a comprehensive domain-specific translation prompt using AI. Accessible via the link next to the prompt selector on the Batch Operations tab. The generated prompt appears in the AI Assistant chat, where you can refine it through conversation. Right-click any assistant message → "Save as Prompt…" to save the result to your prompt library.
- **Save as Prompt** – right-click any AI Assistant response and choose "Save as Prompt…" to save it as a reusable `.svprompt` file in your prompt library. The default name is your Trados project name, with automatic version numbering (v2, v3, etc.) if a prompt with that name already exists.

### Changed
- **British English spelling** – all user-facing text now uses British English spelling throughout the plugin and documentation (analyse, customise, organised, etc.).
- **Documentation improvements** – removed duplicate page headings from all help pages, added cross-references for the new Analyse Project & Generate Prompt feature, and added comprehensive documentation for the new feature.

### Fixed
- **Save as Prompt dialog** – fixed buttons being cut off at the bottom of the dialog under certain DPI scaling settings.
- **Synonym language tags in inverted termbases** – when editing a term from an inverted termbase (e.g. EN→NL termbase used in an NL→EN project), synonyms were saved with swapped language tags, causing them to appear on the wrong side. Now correctly reverses the language tags when saving.

---

## [4.13.0] – 2026-03-19

### Changed
- **Simplified built-in prompts** – replaced the 9 domain-specific translate prompts (Medical, Legal, Patent, Financial, Technical, Marketing, IT, Professional Tone, Preserve Formatting) with a single **Default Translation Prompt**. The default prompt is a general-purpose starting point that users can duplicate and customise for their specific domain. The Default Proofreading Prompt and all QuickLauncher prompts are unchanged.
- **Automatic cleanup of retired prompts** – on first launch after the update, the old domain-specific translate prompt files are automatically removed from the prompt library (only if they still contain the original built-in content – user-modified copies are preserved).

---

## [4.12.5] – 2026-03-19

### Fixed
- **Fixed duplicate plugin crash** – the `.sdlplugin` package filename (`Supervertaler.Trados`) did not match the `<PlugInName>` in the manifest (`Supervertaler for Trados`), causing Trados to create a second copy of the package under the manifest name. Two copies of the same plugin loaded simultaneously, crashing Trados on startup. The package filename now matches the manifest name. The build script also cleans up the old-name package and unpacked folder to prevent recurrence.

---

## [4.12.4] – 2026-03-19

### Added
- **Automatic stale-plugin detection** – when a new `.sdlplugin` is installed but Trados is still running the old extracted version, the plugin now detects the version mismatch at startup and prompts the user to restart. On restart, the old Unpacked folder is cleaned up and Trados re-extracts the new version automatically. Searches all three possible plugin locations (Roaming, Local, All Users) so the detection works regardless of which install option was chosen.

### Changed
- **Simplified install location guidance** – the installation docs now recommend accepting the default installer option ("All your domain computers") instead of manually switching to "This computer for me only". On non-domain PCs the two options are identical, and accepting the default avoids inconsistency between updates.

---

## [4.12.3] – 2026-03-19

### Fixed
- **Usage statistics checkbox now reflects opt-in choice** – when a user clicked "Yes" in the first-launch usage statistics dialog, the setting was saved to disk but the in-memory settings object was not updated. This caused the checkbox in Settings to appear unchecked until Trados was restarted. The opt-in choice is now synced into the live settings immediately.

---

## [4.12.2] – 2026-03-19

### Added
- **Parallels / Mac warning in first-run setup** – when running inside Parallels Desktop on a Mac, the setup dialog now shows a yellow warning panel advising users to keep their data folder on the Windows side (`C:\` drive). If the user selects a Mac-side path (`\\Mac\Home\...`), a confirmation dialog explains that SQLite databases do not work reliably on network-mounted filesystems. Non-Parallels users see no change.
- **Parallels / Mac documentation** – new "Running on a Mac (Parallels)" section in the installation help, and a new "Database errors on Mac (Parallels)" troubleshooting entry
- **Updated installer screenshot** – annotated screenshot showing the recommended "This computer for me only" option

---

## [4.12.1] – 2026-03-19

### Fixed
- **Version numbers now consistent across all plugin files** – the plugin.xml and pluginpackage.manifest.xml version attributes were out of sync with the assembly version, which could cause the wrong version to display in Trados. All version files are now aligned. Also rewrote `bump_version.py` to update all three version files (.csproj, plugin.xml, manifest) in a single command.

---

## [4.12.0] – 2026-03-19

### Added
- **`{{TM_MATCHES}}` prompt variable** – QuickLauncher prompts can now include translation memory fuzzy matches (≥70%) from the active segment. The variable expands to a formatted list showing match percentage, TM name, source, and target text. Available in the variable picker (Ctrl+,) and documented in the help system.
- **3 new built-in QuickLauncher prompts** – "Explain (within project context)" uses `{{PROJECT}}` for document-aware term explanation; "Translate segment using fuzzy matches as reference" combines `{{TM_MATCHES}}` with `{{SURROUNDING_SEGMENTS}}` for context-aware translation; "Translate selection in context of current project" uses `{{PROJECT}}` for full-document term translation
- **Opt-in anonymous usage statistics** – on first launch after this update, a dialog asks whether you'd like to share anonymous usage data to help improve the plugin. Only plugin version, OS version, Trados version, and system locale are sent – once per session, on startup. No personal data, translation content, or termbase information is ever collected. The setting can be changed at any time in Settings. Includes Parallels/VM detection to understand how many users run Trados on a Mac. Data is sent to a first-party Cloudflare Worker endpoint (no third-party trackers). ([#7](https://github.com/Supervertaler/Supervertaler-for-Trados/issues/7))

---

## [4.10.12] – 2026-03-18

### Added
- **Termbase rename** – double-click a termbase name in TermLens Settings (or press **F2**) to rename it
- **fix_reversed_entries.py** – `tools/` script to detect and swap term entries that were stored in the wrong direction in a termbase

---

## [4.10.11] – 2026-03-18

### Fixed
- **Term display and immediate chip appearance restored for inverted-direction termbases** – when a project's translation direction is the opposite of a write termbase's declared direction (e.g. NL→EN project using an EN→NL termbase), TermLens now correctly indexes and matches terms after loading from disk (F5 and segment navigation both work), and newly added terms appear as chips immediately after Alt+Down or Alt+Up
- **Edit Term Entry dialog now follows project direction** – column labels, text fields, synonyms, and abbreviation fields are presented in project source → target order (e.g. Dutch | English in a NL→EN project) regardless of the termbase's declared direction; saves still write to the correct termbase columns

---

## [4.10.10] – 2026-03-18

### Fixed
- **Term direction now respects termbase language pair** – when adding terms via Alt+Down, Alt+Up, Ctrl+Alt+T, or the right-click menu, the plugin now compares the active project's source language against the write termbase's source language and swaps source/target text when they are inverted (e.g. working in a NL→EN project but writing to an EN→NL termbase); previously terms were silently inserted in the wrong direction

---

## [4.10.9] – 2026-03-18

### Changed
- **Lavender chip colour for abbreviation matches** – TermLens chips that matched via a source abbreviation now render with a light lavender background instead of the regular blue, making them instantly distinguishable from full-term matches; the shortcut badge on abbreviation chips is purple to match

---

## [4.10.8] – 2026-03-18

### Fixed
- **Smart selection no longer swallows the next word when selection has trailing space** – selecting a single word with a trailing space (e.g. by shift+arrow-key overshoot) now correctly adds just that word to the termbase; previously the expansion algorithm would land past the space and consume the following word (e.g. "trimethoxysilaan of" instead of "trimethoxysilaan")

---

## [4.10.7] – 2026-03-18

### Added
- **Persistent chat history** – the AI Assistant conversation is now saved to disk after each message and restored automatically when Trados restarts; history persists until you click the **Clear** button

---

## [4.10.6] – 2026-03-18

### Added
- **Variable picker in Prompt Editor** – press **Ctrl+,** in the prompt content field to open a variable menu listing all available variables with descriptions; selecting one inserts it at the cursor (mirrors the variable insertion shortcut in the Trados Studio editor)

### Changed
- **CS checkbox replaces Case dropdown in TermLens settings** – the per-termbase case sensitivity control is now a compact checkbox column (header: **CS**) instead of a dropdown showing Insensitive on every row; ticked = case-sensitive, unticked = case-insensitive; the column sits alongside the existing Read/Write/Project checkboxes

---

## [4.10.5] – 2026-03-18

### Added
- **QuickLauncher built-in prompts** – three prompts now ship as built-ins and are created on first run (or via Restore): *Assess how I translated the current segment*, *Define*, and *Explain (in general)*

### Changed
- **Style guide prompts removed** – the five language-specific style guides (Dutch, English, French, German, Spanish) are no longer shipped as built-in prompts; users who want style guide prompts can create their own in the Prompts tab
- **Built-in prompts use `{{SOURCE_LANGUAGE}}`/`{{TARGET_LANGUAGE}}`** – all specialist prompt content updated from legacy `{source_lang}`/`{target_lang}` single-brace format to the current double-brace standard

### Fixed
- **Delete button label clipped in Prompts tab** – the Delete button was too narrow (55 px), causing the label to be cut off; widened to 65 px

---

## [4.10.4] – 2026-03-18

### Fixed
- **Prompts tab: double-click opens wrong prompt after column sort** – after sorting the prompt list by clicking a column header, double-clicking a row now opens the correct prompt; previously it used the visual row index, which diverged from the data list order after sorting
- **"Surrounding segments" spinner overlap** – the spinner for the Surrounding segments setting in AI Settings was positioned too close to its label and appeared partially overlapping it; moved right to give the label room

### Changed
- **`{{PROJECT}}` display in chat** – when a QuickLauncher prompt containing `{{PROJECT}}` is sent, the chat bubble now shows a compact summary (e.g. `[source document – 47 segments]`) instead of the full source document text; the complete text is still sent to the AI unchanged

---

## [4.10.3] – 2026-03-18

### Added
- **`{{PROJECT_NAME}}` variable** – replaced with the Trados project name in QuickLauncher prompts
- **`{{DOCUMENT_NAME}}` variable** – replaced with the active file name in QuickLauncher prompts
- **`{{SURROUNDING_SEGMENTS}}` variable** – replaced with N source segments before and after the active segment, numbered with their actual per-file Trados segment numbers and the active segment marked `← ACTIVE`; N is configurable in Settings → AI Settings → Surrounding segments (default: 5)
- **`{{PROJECT}}` variable** – replaced with all source segments in the active document, numbered with actual Trados segment numbers; multi-file projects include `=== File N ===` headers at file boundaries where segment numbering restarts
- **Surrounding segments setting** – new spinner in AI Settings: "Surrounding segments" (default: 5, range 1–20); controls both the `{{SURROUNDING_SEGMENTS}}` QuickLauncher variable and the context window in the AI Assistant chat

### Changed
- **AI Assistant surrounding context** – was previously hardcoded to 2 segments on each side; now uses the new "Surrounding segments" setting (default 5)

### Notes
- Segment numbers in `{{SURROUNDING_SEGMENTS}}` and `{{PROJECT}}` match the numbers shown in the Trados editor (per-file, 1-based); the same numbering logic used by the AI Proofreader results
- `{{PROJECT}}` is evaluated lazily – only when the prompt template actually contains `{{PROJECT}}`; it has no cost unless used
- Sending a 10,000-word patent as `{{PROJECT}}` to a Sonnet-class model costs approximately $0.04–0.05 per call

---

## [4.10.2] – 2026-03-17

### Changed
- **`quicklauncher_label` YAML field** – the optional short label for the QuickLauncher menu is now set with `quicklauncher_label:` in `.svprompt` frontmatter; the old name `quickmenu_label` still works as a backward-compatible alias

---

## [4.10.1] – 2026-03-17

### Added
- **`{{SOURCE_SEGMENT}}` and `{{TARGET_SEGMENT}}` variables** – renamed from `{{SOURCE_TEXT}}` / `{{TARGET_TEXT}}` for clarity; the old names continue to work as aliases
- **Ctrl+Q shortcut** – opens the QuickLauncher prompt menu directly from the keyboard; note that Trados's default "View Internally Source" shortcut must be removed first (File → Options → Keyboard Shortcuts)
- **QuickLauncher help page** – new documentation page covering variables, examples, and setup

### Fixed
- **QuickLauncher prompts appear immediately** – newly created or edited prompts now appear in the right-click menu without restarting Trados
- **Case column width** – the Case column in TermLens settings was too narrow to display "Insensitive" in full; widened to fit

---

## [4.10.0] – 2026-03-17

### Added
- **QuickLauncher** – new editor right-click menu entry listing all prompts marked as QuickLauncher; selecting a prompt fills in the current segment's source text, target text, selection, and language pair as variables and submits the expanded prompt directly to the Supervertaler Assistant chat, enabling one-click AI actions without switching panels
- **QuickLauncher prompt support** – prompts are marked as QuickLauncher by adding `sv_quickmenu: true` to their YAML frontmatter (compatible with the same flag used in Supervertaler Workbench), or by setting `category: QuickLauncher`; an optional `quickmenu_label:` field sets a short display name in the menu
- **Segment-level prompt variables** – `{{SOURCE_TEXT}}`, `{{TARGET_TEXT}}`, and `{{SELECTION}}` variables are now substituted in prompts at runtime using the current segment context (compatible with Supervertaler Workbench variable names)
- **Legacy category normalisation** – the old internal category name `quickmenu_prompts` is automatically normalised to `QuickLauncher` when loading prompt files, ensuring forward compatibility

---

## [4.9.0] – 2026-03-17 ([#4](https://github.com/Supervertaler/Supervertaler-for-Trados/issues/4))

### Added
- **Unified user data folder** – Supervertaler for Trados now stores all data (settings, licence, projects, prompts) in a single shared folder alongside Supervertaler Workbench (default: `~/Supervertaler/`); the folder is configured via a shared `%APPDATA%\Supervertaler\config.json` pointer so both products automatically read from the same location
- **First-run setup dialog** – on first launch, a dialog lets you choose the data folder; if an existing Workbench installation is detected its path is pre-filled so you can share data immediately with one click
- **Automatic data migration** – existing settings, licence, project overlays, and custom prompts are copied from the old `%LocalAppData%\Supervertaler.Trados\` location to the new shared folder on first run; old files are left in place as a backup
- **Shared prompt library** – prompts are now read from and written to the shared `prompt_library/` folder; any prompt created in Workbench is immediately visible in the Trados plugin and vice versa, with no configuration required

---

## [4.8.1] – 2026-03-17

### Changed
- **Ctrl+Alt+T opens full Term Entry Editor** – pressing Ctrl+Alt+T to add a new term now opens the full Term Entry Editor dialog instead of the simple Add Term dialog, giving immediate access to definition, domain, notes, and synonym fields when adding a term

### Fixed
- **TermLens subscript/superscript matching** – terms containing Unicode subscript digits (₀–₉) or superscript digits (⁰¹²³⁴⁵⁶⁷⁸⁹) such as H₂O, CO₂, and mm² were not recognised in segments because the tokeniser split them at the script character; the word pattern now includes these Unicode ranges and index keys are normalised so matching works correctly
- **Context-aware help links** – F1 help in the Batch Operations and Reports tabs now opens the correct documentation page for the active context rather than falling back to the generic help home page

---

## [4.8.0] – 2026-03-16

### Added
- **AI Proofreader** – new batch proofreading mode in the Batch Operations tab; select "Proofread" to check translated segments for errors using AI; results appear in the new Reports tab as clickable issue cards with segment number, issue description, and suggestion; clicking a card navigates to the corresponding segment in the editor
- **Reports tab** – new tab in the Supervertaler Assistant panel displaying proofreading results; shows issue count, run duration, and a scrollable list of issue cards; Clear button to reset results
- **Proofread scopes** – five scope options for proofreading: Confirmed only, Translated + Confirmed, All segments, Filtered segments, and Filtered (confirmed only)
- **Segment navigation from reports** – clicking an issue card in the Reports tab navigates directly to the relevant segment in the Trados editor, using the segment's ParagraphUnitId and SegmentId for accurate navigation in multi-file projects
- **Per-file segment numbering** – issue cards in the Reports tab show the actual per-file segment number (matching the Trados editor grid) rather than a cross-file batch index
- **"Also add issues as Trados comments" checkbox** – in the Batch Operations tab (Proofread mode), option to insert proofreading issues as Trados segment comments alongside the Reports tab display
- **Prompt category filtering** – the prompt dropdown in Batch Operations now filters by mode: only "Translate" prompts appear in Translate mode, only "Proofread" prompts in Proofread mode

### Changed
- **Prompt file extension** – built-in and user prompts now use `.svprompt` file extension (previously `.md`), matching the Supervertaler desktop application; existing `.md` prompt files are still loaded for backward compatibility
- **Prompt YAML key renamed** – the `domain` key in prompt YAML frontmatter is now `category`; the parser accepts both for backward compatibility
- **Prompt categories renamed** – "Domain Expertise" and "Style Guides" categories are now "Translate"; "Proofreading" is now "Proofread"
- **Case sensitivity per-termbase dropdown simplified** – removed "Default" option; per-termbase case sensitivity is now simply "Insensitive" (default) or "Sensitive"

---

## [4.7.0] – 2026-03-16

### Added
- **Case-sensitive matching** – new global setting "Case-sensitive matching" (default: off) plus per-termbase override in the settings grid; when enabled, terms only match if the source text has the same letter case as the indexed term; per-termbase setting can be Sensitive or Insensitive
- **Mouse wheel scrolling in AI Assistant chat** – the chat message panel now supports mouse wheel scrolling; previously only the scrollbar worked

### Changed
- **Database schema migration** – `case_sensitive` column automatically added to the `termbases` table on first use; fully backward-compatible

---

## [4.6.0] – 2026-03-16

### Added
- **Abbreviation fields on term entries** – each term entry now has optional **Source Abbreviation** and **Target Abbreviation** fields; when a source abbreviation appears in a segment, TermLens highlights it and shows the target abbreviation underneath, with the full term pair available in the +N tooltip
- **Pipe-separated abbreviation variants** – abbreviation fields support multiple variants separated by `|` (e.g., `GC|G.C.|gc|g.c.`); each variant is indexed and matched independently, so all common forms of an abbreviation are recognised
- **Abbreviation-aware insertion** – clicking or Alt+digit-inserting an abbreviation-matched chip inserts the target abbreviation (first variant) instead of the full target term
- **Abbreviation in AI prompts** – AI translation prompts now include abbreviation pairs alongside their full terms, so the AI knows both forms
- **Abbreviation columns in Term Editor** – the Add/Edit Term dialog includes Source Abbreviation and Target Abbreviation text fields between the primary term fields and the synonyms section
- **Abbreviation columns in Termbase Editor** – the termbase grid shows SrcAbbr and TgtAbbr columns for viewing and editing abbreviations inline

### Changed
- **Database schema migration** – `source_abbreviation` and `target_abbreviation` columns automatically added to existing databases on first write; fully backward-compatible with older Supervertaler databases

---

## [4.5.0] – 2026-03-16

### Added
- **Ctrl+T quick translate** – press **Ctrl+T** to instantly translate the active segment using the same provider, model, and prompt configured in the Batch Translate tab; the translation is applied directly to the target cell, with full tag preservation for segments containing inline formatting; also available via right-click context menu ("Translate active segment"); rebindable in Trados Studio's keyboard shortcut settings
- **AI Settings link in Batch Translate tab** – clickable "AI Settings…" link below the provider display opens the Settings dialog directly on the AI Settings tab for quick access to provider, model, API key, and AI context configuration

### Changed
- **Ctrl+Alt+A retired** – the old standalone single-segment translation shortcut has been unbound; the action is kept for backward compatibility but now redirects to the same Ctrl+T batch-translate pipeline with full tag support

### Fixed
- **Tagged segments not applied in batch translate** – segments containing inline Trados tags (bold, italic, field codes, page numbers, etc.) were translated by the AI but the translation was never written back to the target; the reconstructed target was written to the document model but the Trados editor's own buffer overwrote it with the old (empty) content; now uses the Trados `ProcessSegmentPair` API which handles the edit transaction correctly
- **Last segment in batch sometimes lost** – the final segment translated in a batch could silently lose its translation because no subsequent segment navigation forced the Trados editor to commit the pending edit; the new `ProcessSegmentPair` approach bypasses the editor buffer entirely for tagged segments, and non-tagged segments continue to use the proven `Selection.Target.Replace` path

---

## [4.4.0] – 2026-03-16

### Added
- **Tag-aware AI translation** – segments containing inline Trados tags (bold, italic, field codes, etc.) are now fully supported for both Batch Translate and single-segment AI translation (Ctrl+Alt+A); previously, tags were silently stripped and lost in the target
- **SegmentTagHandler** – new serialization/reconstruction engine that converts Trados `ITagPair` and `IPlaceholderTag` objects into numbered placeholders (`<t1>...</t1>`, `<t2/>`), sends them through the LLM translation pipeline, then reconstructs the target segment with the original tag objects cloned and repositioned to match the translated word order
- **Graceful fallback** – if the LLM drops or corrupts tag placeholders, the plugin falls back to plain-text insertion (stripping placeholders) instead of failing silently

### Improved
- **Translation prompt tag instructions** – replaced generic CAT tool tag preservation instructions with specific numbered placeholder format and examples, improving LLM tag preservation accuracy

---

## [4.3.0] – 2026-03-14

### Added
- **Per-project settings** – switching between Trados projects now automatically saves and restores the Supervertaler database path, enabled/disabled termbases, write targets, project termbase, and AI context termbase filters; settings are stored per-project in `%LocalAppData%\Supervertaler.Trados\projects\` and applied automatically when the active document changes
- **Per-project settings documentation** – new help page documenting how per-project settings work, what's saved per-project vs globally, and how the automatic switching behaves
- **Privacy policy** – [supervertaler.com/privacy](https://supervertaler.com/privacy/)

### Fixed
- **"Add & Edit" crash in Similar Term Found dialog** – pressing "Add & Edit" when merging a term caused an `ArgumentOutOfRangeException` (ordinal 10) because `GetTermById()` had an off-by-one error in its column indexing for optional fields (domain, notes, is_nontranslatable); each field was read one position past its actual column index, and the last field fell off the end of the result set
- **Licence null-status crash** – when the Lemon Squeezy API returned a null or empty `status` field during activation, the licence was treated as invalid even though the key was activated; now treats null status as active when the licence has a valid instance ID
- **Trial period mismatch** – `LicenseInfo.cs` used a hardcoded 14-day trial window while `LicenseManager.cs` used 90 days; unified both to the 90-day constant
- **AI Settings termbase list stale after database switch** – switching Supervertaler databases in the TermLens settings tab didn't update the AI Settings tab's termbase checklist until the dialog was closed and reopened; the AI context panel now refreshes immediately when the termbase list changes
- **Term Picker shortcut documented incorrectly** – the About dialog and help docs showed `Ctrl+Shift+G` but the actual shortcut is `Ctrl+Alt+G`; corrected all references

### Improved
- **Keyboard shortcuts documentation** – added Mac/Parallels equivalents (Ctrl → Control, Alt → Option) to all shortcut tables for users running Trados in Parallels
- **Support email** – updated to support@supervertaler.com
---

## [4.2.2-beta] – 2026-03-13

### Fixed
- **Licence tab help link** – the ? button on the Licence tab now opens the Licensing & Pricing page instead of incorrectly opening TermLens Settings
- **Backup tab help link** – the ? button on the Backup tab now opens the dedicated Backup & Restore page instead of using a stale anchor link
- **Licensing help URL** – corrected the GitBook URL slug from `licensing-and-pricing` (404) to `licensing` (the actual filename-based slug)

### Changed
- **UK English in documentation** – changed all instances of "license" (US) to "licence" (UK) in the online help pages

### Added
- **Example Project link in help menus** – both the TermLens and Supervertaler Assistant help menus (? button) now include an "Example Project" link that opens the documentation page for the downloadable example project
- **Example Project documentation** – new docs page with step-by-step instructions, screenshots, and the example package (patent translation with termbase, TM, and MultiTerm termbase)
- **Help link reference** – new `HELP-LINKS.md` in the repo root documents every help link in the plugin with its online URL and which UI element triggers it

---

## [4.2.1-beta] – 2026-03-12

### Improved
- **Settings toolbar buttons** – the TermLens tab toolbar buttons now use descriptive labels ("+ Add", "− Remove") instead of cryptic symbols; all five buttons (Open, Export, Import, + Add, − Remove) have tooltips explaining their function

### Fixed
- **"Max segments" label overlap** – the "Max segments:" label in AI Settings no longer runs into the number input box

---

## [4.2.0-beta] – 2026-03-12

### Added
- **Update checker** – on startup, the plugin checks GitHub Releases for a newer version and shows a dialog with Download, Skip This Version, and Remind Me Later buttons. Checks once per session, respects skipped versions, and never blocks Trados startup (runs in background)

---

## [4.1.0-beta] – 2026-03-12

### Added
- **Settings backup and restore** – new **Backup** tab in the Settings dialog with Export and Import buttons; export saves all plugin settings (termbase paths, toggle states, font size, shortcut preferences, AI provider keys, model selections, prompt configuration) to a JSON file; import validates the file, creates an automatic backup of current settings, and applies the imported configuration immediately
- **Open settings folder** – clickable link in the Backup tab opens the `%LocalAppData%\Supervertaler.Trados\` folder in Explorer for easy access to settings files
- **Open prompts folder** – clickable link in the Prompts tab opens the `%LocalAppData%\Supervertaler.Trados\prompts\` folder in Explorer

### Fixed
- **Restore button clipped in Prompts tab** – the "Restore" button width was too narrow, causing the label to be truncated

---

## [4.0.2-beta] – 2026-03-12

### Added
- **Dual-mode Alt+digit term shortcuts** – two configurable shortcut styles for inserting terms 10+ (choose in Settings > TermLens > Term shortcuts):
  - **Sequential** (default) – type the term number digit by digit: Alt+4, Alt+5 inserts term 45. Clean sequential badge numbers (10, 11, 12, ...). 1-second timer between digits.
  - **Repeated digit** – press the same digit key multiple times: Alt+5, Alt+5 inserts term 14. Supports up to 5 tiers (45 terms). No timer ambiguity.
- **Term Picker wrap-around navigation** – pressing Down on the last term jumps to the first, and Up on the first jumps to the last

### Changed
- **Term Picker numbering** – the Term Picker now always uses plain sequential numbers (1, 2, 3, ...) regardless of the shortcut style setting, since navigation is done with arrow keys and Enter

---

## [4.0.1-beta] – 2026-03-12

### Fixed
- **Merge dialog buttons clipped** – the "Similar Term Found" dialog's button bar (Add as Synonym, Add & Edit, Keep Both, Cancel) was invisible or partially clipped inside Trados's WPF-hosted plugin environment; replaced the Dock-based panel layout with flat absolute positioning so buttons render reliably at any DPI
- **Merge dialog button text truncated** – widened the "Add as Synonym" and "Add & Edit..." buttons so their labels are no longer cut off

### Added
- **Merge dialog button tooltips** – each button now shows a tooltip on hover explaining what it does

---

## [4.0.0-beta] – 2026-03-12

### Changed
- **90-day free trial** – extended from 14 days; no credit card required, no sign-up
- **Support & Community link** in About dialog now points to `supervertaler.com/trados/#support` (Groups.io mailing list, ProZ forum, GitHub Issues) instead of directly to GitHub Issues; future-proofed so support channels can be updated without rebuilding the plugin
- **Version display** – About dialog now shows the full informational version string (e.g. "4.0.0-beta") rather than the numeric assembly version

### Fixed
- **Shield emoji clipping** – "Source code available for security audit" link in the About dialog was partially obscured by the shield emoji; offset increased to prevent overlap
- **Tooltips on About dialog links** – Documentation and Support & Community links now show tooltips on hover

---

## [4.0.0] – 2026-03-11

### Added
- **Licensing system** – Lemon Squeezy-powered license key activation with two paid tiers: **TermLens** (€10/month – terminology features) and **TermLens + Supervertaler Assistant** (€15/month – all features including AI); 14-day free trial with full access on first install; 30-day offline cache for validation; 2 machine activations per key
- **License tab in Settings** – dedicated tab for entering license keys, activating/deactivating licenses, verifying license status, and managing subscriptions; shows trial countdown, plan name, masked key, and last verification date
- **License status in About dialog** – color-coded license status (blue for trial, green for active, red for expired) with a clickable link that opens the License settings tab directly
- **Feature gating** – TermLens panel and terminology actions require Tier 1+; AI Assistant panel and AI translate action require Tier 2; graceful overlays and messages guide users to purchase or upgrade
- **Security transparency** – "Source code available for security audit" link in the About dialog with tooltip explaining the plugin's network behaviour; links to the public GitHub repository
- **Enhanced AI Assistant context** – the AI chat assistant now sees the full document content (all source segments) so it can determine the document type (legal, medical, technical, etc.) and provide context-appropriate assistance; also includes project/file metadata, surrounding segments, and term definitions/domains/notes
- **AI Context settings** – three new settings in AI Settings: "Include full document content" (with configurable max segments), and "Include term definitions and domains"

### Changed
- **About dialog** – removed duplicate "Plugin Help" link (Documentation link remains); added clickable license status that opens Settings → License tab; added security audit note with GitHub link

### Fixed
- **Settings sync between panels** – changing settings from the TermLens gear icon now immediately reflects in the AI Assistant panel and vice versa; previously each panel had its own in-memory copy that could get out of sync

---

## [3.4.2] – 2026-03-10

### Added
- **Merge prompt for similar terms** – when adding a term whose source or target already exists in the termbase (but with a different translation), a dialog offers to add the new text as a synonym instead of creating a near-duplicate entry; works with Alt+Down, Alt+Up, and Ctrl+Alt+T
- **"Add & Edit" option in merge dialog** – alongside the quick "Add as Synonym" button, the merge dialog now offers "Add & Edit…" which merges the synonym and opens the full Term Entry Editor so the user can review or add metadata before closing
- **Term metadata in tooltips** – hovering over a term chip now shows Domain and Notes fields alongside Definition (previously only Definition was displayed)
- **Metadata indicator on badges** – the shortcut badge number on term chips turns black (instead of white) when the term has metadata (definition, domain, or notes), giving a visual cue to hover for more info

### Changed
- **"MultiTerm Help"** – renamed the context menu item from "MultiTerm Support" to "MultiTerm Help" for consistency
- **"Supervertaler Assistant Help"** – renamed the AI Assistant help menu item from "Assistant Help" to "Supervertaler Assistant Help"
- **Dialog title casing** – "Edit Term Entry" and "Add Term Entry" renamed to sentence case ("Edit term entry" / "Add term entry")

### Fixed
- **Shift+Enter in AI Assistant** – Shift+Enter now correctly inserts a newline in the chat input instead of being intercepted by Trados Studio; uses a thread-local `WH_GETMESSAGE` hook to intercept the key press before Trados's message filters can consume it
- **Paste newlines in AI Assistant** – pasting text with bare `\n` line endings (e.g. copied from Trados segments) now displays correctly; the chat input normalises `\n` to `\r\n` on paste
- **Smart selection expansion** – partial word selections now expand to the shortest matching word at the boundary instead of the longest, preventing over-expansion when selecting near short words (e.g. selecting "o" no longer expands to "output" when "of" is adjacent)
- **Merge dialog cutoff** – the "Similar Term Found" dialog is now wider (520Ã–310) to prevent text truncation on longer term pairs

---

All notable changes to Supervertaler for Trados (formerly TermLens) will be documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Version numbers follow [Semantic Versioning](https://semver.org/).

---

## [3.4.1] – 2026-03-10

### Added
- **Select All / Deselect All** links for termbases in AI Settings → AI Context section

### Fixed
- **Settings TextBox overlap** – the database file path TextBox no longer extends over the "Create New..." button when the settings dialog is resized

---

## [3.4.0] – 2026-03-09

### Added
- **MultiTerm termbase support** – TermLens now automatically detects MultiTerm .sdltb
  termbases attached to the active Trados project and displays their terms alongside
  Supervertaler terms; MultiTerm terms appear as green chips in the TermLens panel
- **Read-only MultiTerm terms** – MultiTerm terms are read-only: right-click context menus
  do not show Edit, Delete, or Non-Translatable options for green (MultiTerm) chips;
  tooltips show "[MultiTerm – read-only]"
- **MultiTerm in settings** – detected MultiTerm termbases appear in the Supervertaler
  Settings dialog with a "[MultiTerm]" label and light green row tint; Read checkbox
  toggles visibility; Write and Project columns are always disabled (read-only)
- **Auto-refresh on termbase changes** – when terms are added or removed from a MultiTerm
  .sdltb termbase (e.g. via Trados's native Term Recognition panel), TermLens automatically
  detects the file modification and reloads terms on the next segment change
- **JET 4.0 / ACE OLEDB driver support** – .sdltb files are opened via the built-in
  Microsoft.Jet.OLEDB.4.0 driver (available in all 32-bit Windows processes) with fallback
  to ACE OLEDB 12.0–16.0; no additional driver installation required for Trados Studio
  (which runs as an x86 process)
- **API fallback** – if no OleDb driver can open an .sdltb file, the plugin attempts to
  use Trados's built-in ITerminologyProviderManager API for per-segment term search with
  LRU caching (200 segments)

### Changed
- **Cleaned up MultiTerm diagnostic logging** – removed verbose reflection-based logging
  from the MultiTerm detection and fallback provider code; the multiterm_debug.log file is
  no longer written

---

## [3.3.3] – 2026-03-09

### Added
- **Help button on dialogs** – the Termbase Editor and Edit Term Entry dialogs now show a
  `?` button in the title bar that opens the relevant online help page (matching the pattern
  already used by the Supervertaler Settings dialog)

---

## [3.3.2] – 2026-03-09

### Added
- **Context-sensitive help** – the `?` button on TermLens and Supervertaler Assistant
  panels now opens a dropdown menu with a direct link to the relevant online help page
  and an "About" option; F1 opens contextual help from every dialog (settings, Add Term,
  Term Picker, Termbase Editor, Prompt Editor, Bulk Add NT)
- **HelpSystem** – new `Core/HelpSystem.cs` provides a centralized topic registry and
  URL launcher for all help pages

### Changed
- **Help URL slug** – documentation URLs updated from `gitbook.io/superdocs` to
  `gitbook.io/help` for a cleaner, more intuitive path
- **About dialog access** – the `?` button now shows a dropdown instead of directly
  opening About; About is still accessible via the dropdown menu

---

## [3.3.1] – 2026-03-08

### Added
- **Resizable chat input** – drag the top edge of the chat input area upward to make it
  taller when composing multi-line messages with Shift+Enter; drag down to shrink it back

### Fixed
- **Settings dialog too wide** – the Supervertaler Settings window could become excessively
  wide and extend off-screen; now capped at 800px maximum width, and persisted size is
  validated on restore
- **Chat spacing** – removed remaining double-spacing in AI responses caused by duplicate
  paragraph marks in table rendering
- **Termbases list in AI Settings** – the CheckedListBox no longer stretches the dialog
  horizontally; long termbase names scroll within the list via horizontal scrollbar

---

## [3.3.0] – 2026-03-08

### Added
- **AI Assistant** – project-aware chat interface in a separate dockable Trados panel;
  supports multi-turn conversations with full context from the active segment (source,
  target, termbase terms, TM matches); responses render as Markdown with headings, bold,
  italic, inline code, code blocks, tables, and lists; right-click to copy or apply
  suggestions directly to the target segment
- **Image attachments in chat** – paste images from clipboard (Ctrl+V), drag and drop
  image files, or browse with the attach button; thumbnails appear in an attachment strip
  below the input; images are sent to the AI using each provider's vision/multimodal API
  (OpenAI, Claude, Gemini, Ollama); click thumbnails in chat bubbles to view full-size
- **AI context control** – new "AI Context" section in AI Settings lets you choose which
  termbases contribute terms to AI prompts (independent of TermLens display settings) and
  toggle whether TM (Translation Memory) fuzzy matches are included in the AI context
- **TM match integration** – when enabled in settings, TM fuzzy matches for the active
  segment are included in the AI Assistant's system prompt, showing match percentage,
  source/target text, and TM name so the AI can leverage existing translations
- **Ollama support for AI Assistant** – local Ollama models can be used for the chat
  assistant with configurable endpoint
- **Custom OpenAI-compatible endpoints** – profile-based configuration for any
  OpenAI-compatible API (e.g., Azure OpenAI, LM Studio, vLLM); multiple profiles
  supported with separate endpoint, model, and API key per profile
- **Chat tooltips** – all chat input buttons (Send, Stop, Clear, Attach) now show
  descriptive tooltips explaining their function and keyboard shortcuts

### Changed
- **Attachment icon** – replaced the paperclip emoji (📎) with a clearer photo icon
  from Segoe MDL2 Assets for better visibility in the chat input area
- **Chat rendering** – eliminated extra blank lines between paragraphs in AI responses
  for more compact, readable output
- **Shift+Enter for newlines** – the chat input now supports Shift+Enter to insert line
  breaks without sending the message (Enter alone sends)
- **AI Settings layout** – the AI Context section now repositions dynamically based on
  the selected provider, eliminating wasted space when provider-specific panels (Ollama,
  Custom OpenAI) are hidden; the termbases checklist is taller to show more entries
  without scrolling

### Fixed
- **TermLens header text cutoff** – the word count and match summary in the TermLens
  panel header is no longer truncated by the floating gear and help buttons; added right
  padding to account for the button overlay

---

## [3.2.0] – 2026-03-08

### Added
- **Help / About dialog** – "?" button next to the settings gear opens an About dialog
  showing plugin version, author info, keyboard shortcuts reference, and links to
  website, documentation, and support; email address copies to clipboard on click
- **NT filter in Termbase Editor** – "NT only" checkbox in the toolbar filters the
  term list to show only non-translatable entries; composes with the search filter
- **Bulk Add NT** – "Bulk Add NT" button in the Termbase Editor opens a dialog where
  you can paste multiple non-translatable terms (one per line) for batch import;
  reports how many were added and how many duplicates were skipped
- **Copy cell in Termbase Editor** – Ctrl+C now copies the current cell value instead
  of the entire row; right-click context menu includes a "Copy cell" option
- **Duplicate prevention** – all term insert and update paths now check for existing
  entries with the same source and target term (case-insensitive) in the same
  termbase; quick-add shortcuts (Alt+Down/Up, Ctrl+Alt+T, Ctrl+Alt+N) show a clear
  message when a duplicate is detected; bulk operations report how many duplicates
  were skipped

### Changed
- **Renamed "glossary" to "termbase"** – all user-facing labels, context menus,
  dialogs, and settings now use "termbase" consistently instead of the previous mix
  of "glossary" and "termbase"
- **Shortened language names** – language pair displays throughout the UI
  (Termbase Editor title bar, settings grid, Add Term dialog) now show short names
  like "English" instead of "English (United States)"
- **Sentence case context menus** – right-click menu items in the TermLens panel now
  use sentence case ("Mark as non-translatable") instead of title case
- **Settings dialog database label** – the file path label in settings now reads
  "Database" instead of "Termbase" to avoid confusion with individual termbases
  inside the database

### Fixed
- **Alt+Up word expansion** – quick-add to project termbase (Alt+Up) now expands
  partial word selections to full word boundaries, matching Alt+Down behaviour

---

## [3.1.0] – 2026-03-06

### Added
- **Prompt manager / library** – 14 built-in prompts (domain expertise for Medical,
  Legal, Patent, Financial, Technical, Marketing, IT; style guides for Dutch, English,
  French, German, Spanish; project prompts for professional tone and formatting);
  prompts stored as Markdown files with YAML frontmatter, compatible with Supervertaler
  desktop prompt format
- **Prompt selector in Batch Translate** – dropdown between Scope and Provider lets you
  pick a prompt before translating; selected prompt persists across sessions
- **Prompts tab in Settings** – third tab in the Settings dialog with system prompt
  viewer/editor and full prompt library management (create, edit, delete, restore
  built-in prompts)
- **Composable prompt assembly** – base system prompt (tag preservation, number
  formatting) + custom prompt (domain/style instructions) + glossary terms; custom
  system prompt override available for advanced users
- **Supervertaler desktop prompt discovery** – automatically scans
  `~/Supervertaler_Data/` and `%AppData%\Supervertaler\` for shared prompt libraries
- **Variable substitution** – prompts support `{source_lang}`, `{target_lang}`,
  `{{SOURCE_LANGUAGE}}`, `{{TARGET_LANGUAGE}}` placeholders, replaced at translation
  time with the document's language pair

### Changed
- **Prompts tab side-by-side layout** – the Settings dialog Prompts tab now shows the
  custom prompt library on the left and the system prompt on the right, making better
  use of the available space
- **Prompt variable display simplified** – prompt editor shows only the standard
  `{{SOURCE_LANGUAGE}}` / `{{TARGET_LANGUAGE}}` placeholders; legacy `{source_lang}` /
  `{target_lang}` aliases still work silently for backward compatibility

### Fixed
- **TermLens glossary list no longer cut off** – the TermLens settings tab now uses
  Dock-based panel layout instead of absolute pixel positioning, so the glossary grid
  scales correctly across screen resolutions and DPI settings
- **Prompt library Source column resizable** – the Source column in the prompt list now
  uses proportional FillWeight sizing instead of a fixed width
- **Plugin manifest version updated** – `plugin.xml` now reports v3.1.0 (was stuck at
  2.0.1 since the rename)
- **Windows on ARM support** – the plugin now works on Windows on ARM (Parallels on
  Apple Silicon Macs, Surface Pro X, etc.); ships ARM64 native SQLite binary alongside
  x64 and x86; properly detects process architecture and copies the correct native
  library where SQLitePCLRaw can find it
- **SQLitePCLRaw initialization order** – `AssemblyResolve` handler is now registered
  before native library preloading, and `Batteries_V2.Init()` is called explicitly to
  prevent `TypeInitializationException` on non-standard environments
- **Improved error diagnostics** – database creation errors now show the full inner
  exception chain for easier troubleshooting

---

## [3.0.0] – 2026-03-06

### Added
- **AI batch translation** – translate segments in bulk using LLM providers; supports
  OpenAI (GPT-4o, GPT-4o mini, o1, o3-mini), Anthropic (Claude 3.5 Sonnet, Haiku,
  Opus), and Google (Gemini 2.0 Flash, Gemini 1.5 Pro); configurable via the new AI
  Settings panel accessible from the Batch Translate tab
- **AI single-segment translate** – press **Ctrl+Alt+A** or right-click → "AI Translate
  Current Segment" to translate just the active segment using the configured AI provider
- **Glossary-aware AI prompts** – AI translations automatically include matched
  terminology from your TermLens glossaries in the prompt, so the AI respects your
  approved terms, including non-translatable terms
- **Four batch translate scopes** – "Empty segments only" (default), "All segments",
  "Filtered segments", and "Filtered (empty only)"; filtered scopes translate only
  segments visible in Trados's advanced display filter
- **Live filtered segment counts** – the Batch Translate tab updates segment counts
  in real time when you change the Trados display filter
- **AI Settings panel** – configure provider, model, API key, and temperature directly
  in the Batch Translate tab; settings persist across sessions
- **Batch translate progress** – real-time log panel shows translation progress,
  segment-by-segment results, and any errors; cancel button to stop mid-batch

### Changed
- **Batch Translate tab** – no longer a placeholder; fully functional with scope
  selector, segment counts, translate/cancel buttons, and scrollable log panel
- **AI Settings integrated into Settings dialog** – the gear icon in TermLens now
  opens a tabbed settings dialog with separate tabs for Glossary and AI configuration

---

## [2.1.0] – 2026-03-06

### Added
- **Non-translatable terms** – mark terms as non-translatable (brand names, product
  codes, abbreviations that stay the same across languages); the source term is copied
  verbatim as the target
- **Ctrl+Alt+N quick-add shortcut** – select text in the source or target column and
  press Ctrl+Alt+N to instantly mark it as non-translatable in all Write glossaries
- **Right-click toggle** – right-click any term block and choose "Mark as
  Non-Translatable" or "Mark as Translatable" to toggle the flag without opening a
  dialog
- **Non-translatable checkbox in Add Term dialog** – when checked, the target field
  auto-fills with the source text and becomes read-only
- **Yellow visual distinction** – non-translatable terms appear with a light yellow
  background (#FFF3D0) in the TermLens panel, the Term Picker popup, and the Glossary
  Editor; color precedence: yellow (non-translatable) > pink (project) > blue (regular)
- **NT column in Glossary Editor** – checkbox column to view and toggle
  non-translatable status per term
- **Select/deselect all in Settings** – click the Read, Write, or Project column
  headers to toggle all checkboxes at once; tooltips explain the feature

### Changed
- **Database schema migration** – the `is_nontranslatable` column is automatically
  added to existing databases on first access; fully backward-compatible

---

## [2.0.1] – 2026-03-05

### Changed
- **Faster quick-add term workflow** – Alt+Down and Alt+Up now use incremental
  in-memory index updates instead of reloading the entire termbase database;
  batch inserts use a single SQLite transaction instead of one connection per
  glossary; right-click edit and delete also use the incremental path
- **License changed to source-available** – source code remains viewable and
  forkable for personal use; binary redistribution restricted to copyright holder

---

## [2.0.0] – 2026-03-05

### Added
- **Tabbed ViewPart UI** – the plugin now uses a tabbed panel with separate tabs for
  TermLens (glossary), AI Assistant, and Batch Translate; AI features are placeholder
  tabs that will be implemented in upcoming releases

### Changed
- **Renamed from TermLens to Supervertaler for Trados** – the plugin is now part of the
  Supervertaler product family; the TermLens glossary panel retains its name as a feature
  within the larger plugin
- **New assembly name** – `Supervertaler.Trados.dll` (was `TermLens.dll`); namespace changed
  from `TermLens` to `Supervertaler.Trados`
- **New plugin identity** – Trados treats this as a new plugin; users upgrading from TermLens
  should uninstall the old plugin first
- **Settings auto-migration** – settings are automatically copied from the old
  `%LocalAppData%\TermLens\` location to `%LocalAppData%\Supervertaler.Trados\` on first run

### Fixed
- **Word alignment in TermLens panel** – unmatched words now align vertically with
  matched term source text (fixed margin/padding mismatch and switched to consistent
  GDI+ text rendering)

---

## [1.6.0] – 2026-03-05

### Added
- **F2 expand selection to word boundaries** – press F2 after making a rough
  partial text selection in the source or target pane; the selection automatically
  expands to encompass the complete words at each end (e.g. selecting "et recht"
  becomes "het rechtstreeks")
- **Smart word expansion for term adding** – the Add Term dialog and Quick Add
  Term action now auto-expand partial selections to full word boundaries before
  populating the term pair, so you no longer need pixel-perfect text selection
- **Multiple Write glossaries** – the Write column in Settings now allows checking
  multiple glossaries; new terms are inserted into all Write-checked glossaries at
  once

### Changed
- **Term Picker shortcut** – changed from Ctrl+Shift+G to **Ctrl+Alt+G**
- **Quick Add action renamed** – "Quick add term to glossaries set to 'Read'" →
  "Quick Add Term to Glossary Set to 'Write'" (reflecting its actual behaviour)

### Fixed
- **Duplicate terms in Term Picker** – when the same source term matched at
  multiple positions in a segment (e.g. "cap" appearing twice), it was listed
  multiple times in the picker; matches are now deduplicated and renumbered
  sequentially

---

## [1.5.0] – 2026-03-04

### Added
- **Standalone database creation** – "Create New…" button in Settings creates a fresh
  Supervertaler-compatible SQLite database from scratch, so TermLens can function
  independently without Supervertaler installed
- **Glossary management** – "+" and "−" buttons in Settings to create and delete
  individual glossaries inside a database; new glossary dialog collects name, source
  language, and target language
- **TSV import** – bulk import terms from tab-separated files matching Supervertaler's
  format (pipe-delimited synonyms, `[!forbidden]` markers, UUID-based duplicate
  detection); flexible header mapping supports multiple column name conventions
- **TSV export** – export all terms from a glossary to the same TSV format, so files
  are fully interchangeable between Supervertaler and TermLens
- **Alt+Down quick-add shortcut** – adds the current source/target text directly to
  the Write glossary (replaces the previous Ctrl+Alt+Shift+T binding)
- **Alt+Up quick-add to project glossary** – new action that adds the current
  source/target text directly to the Project glossary (no dialog)

### Changed
- **Project column is now single-select** – the Project column in Settings uses
  radio-button behavior (only one glossary can be the project glossary at a time),
  matching the single Write glossary pattern
- **Context menu reorganised** – the "Add Term to TermLens" actions are now grouped
  under a separator in the editor context menu, with clearer names ("Add Term to
  TermLens (dialogue)" and "Quick add Term to glossaries set to 'Read'")
- **A+/A− button font sizes** – adjusted for better visual balance (A+ uses 9pt,
  A− uses 7pt instead of both using 7.5pt)

### Fixed
- **Term block text truncation** – TermBlock now recalculates its size when the font
  changes (via `OnFontChanged` override), preventing clipped text after A+/A− resizing

---

## [1.4.0] – 2026-03-04

### Added
- **Adjustable font size** – A+ and A− buttons in the TermLens panel header let you
  increase or decrease the font size on the fly while working; also configurable via a
  "Panel font size" control in the Settings dialog; size persists across Trados restarts
- **Dialog size persistence** – the Term Picker dialog remembers its window size and
  column widths between invocations (and across Trados restarts); the Settings dialog
  also remembers its window size

### Changed
- **Subtler expand indicator in Term Picker** – replaced the ► symbol next to source
  terms with a small ▸ triangle in the # column; less visually distracting while still
  indicating which rows have expandable synonyms
- **Double-digit shortcut badges** – numbers 10+ in the TermLens panel now use a
  pill-shaped (rounded rectangle) badge instead of a circle, so double-digit numbers
  are no longer clipped
- **Wider Project column** – increased from 62 px to 72 px in the Settings dialog so
  the "Project" header is no longer truncated

---

## [1.3.0] – 2026-03-04

### Added
- **Alt+digit term insertion** – press Alt+1 through Alt+9 to instantly insert the
  corresponding matched term into the target segment; Alt+0 inserts term 10; for
  segments with 10+ matches, two-digit chords are supported (e.g. Alt+1 then 3
  within 400ms inserts term 13)
- **Term Picker dialog** – press Ctrl+Shift+G to open a modal dialog listing all
  matched terms for the current segment; select by clicking, pressing Enter, or
  typing the term number
- **Synonym expansion in Term Picker** – rows with multiple target translations
  show a ► indicator; press Right arrow to expand and reveal all alternative
  translations, Left arrow to collapse
- **Bulk synonym loading** – target synonyms from the `termbase_synonyms` table are
  now loaded at startup alongside term entries, so the +N badges and Term Picker
  expansion show the correct synonym counts
- **Project glossary column in Settings** – a new "Project" checkbox column in the
  settings dialog lets you mark glossaries as project glossaries; project terms are
  shown in pink, all others in blue (replaces the previous database-driven priority
  colouring which was unreliable)

### Changed
- **Coloring is user-controlled** – pink/blue term colouring is now determined by
  the user's "Project" setting per glossary, not by the database's ranking or
  is_project_termbase fields
- **Wider settings columns** – the Read, Write, and Project checkbox columns in the
  settings dialog are now wide enough for their headers to be fully visible

---

## [1.2.0] – 2026-03-04

### Added
- **Add Term to TermLens** – right-click context menu action in the Trados editor to
  add a new term from the active segment's source and target text; opens a confirmation
  dialog where you can edit the term pair and optionally add a definition before saving
- **Quick add Term to TermLens** – a second context menu action that bypasses the dialog
  and saves the source/target text directly as a new term for faster workflow
- **Keyboard shortcuts** – Add Term defaults to Ctrl+Alt+T, Quick Add to
  Ctrl+Alt+Shift+T (both reassignable via Trados keyboard shortcut settings)
- **Settings: Read/Write columns** – the termbase list in settings is now a grid with
  separate Read and Write checkboxes; Read controls which termbases are searched,
  Write selects the single termbase that receives new terms (radio-button style)

### Changed
- **ViewPart docks above the editor** – TermLens now opens above the translation grid
  (previously docked at the side) and opens pinned/visible instead of auto-hidden
- **Term badge sizing** – the "+N" synonym count badges on term blocks are no longer
  truncated; width calculations now use ceiling rounding instead of integer truncation

---

## [1.1.0] – 2026-03-04

### Changed
- **Renamed project from Termview to TermLens** – all files, namespaces, class names,
  plugin IDs, settings paths, and documentation updated consistently
- **Migrated from System.Data.SQLite to Microsoft.Data.Sqlite** – eliminates the
  `EntryPointNotFoundException` caused by version-fingerprint hash conflicts in Trados
  Studio's plugin environment; uses SQLitePCLRaw with `e_sqlite3.dll` instead of
  `SQLite.Interop.dll`
- Settings path moved from `%LocalAppData%\Termview\` to `%LocalAppData%\TermLens\`
- Updated README with richer description and build instructions

### Technical
- `AppInitializer` now pre-loads `e_sqlite3.dll` by full path via `LoadLibrary` and
  registers `AssemblyResolve` for all managed DLLs we ship (Microsoft.Data.Sqlite,
  SQLitePCLRaw, System.Memory, System.Buffers, etc.)
- `pluginpackage.manifest.xml` Include entries updated to match new dependency set

---

## [1.0.0] – 2026-03-03

First public release.

### Added
- **Word-by-word source segment display** – every word of the active source segment
  is shown in a flowing left-to-right layout, updated as you navigate between segments
- **Terminology highlighting** – words that match a loaded termbase are shown in
  a coloured block (blue for regular terms, pink for project termbases) with the
  target-language translation displayed directly underneath
- **Multi-word term matching** – multi-word entries (e.g. "machine translation") are
  matched and highlighted as a single block, taking priority over single-word matches
- **Click to insert** – clicking a term block inserts the target translation at the
  cursor position in the target segment
- **Termbase settings** – gear button (⚙) in the panel header opens a settings
  dialog for selecting a Supervertaler termbase (`.db`) file; settings are saved to
  `%LocalAppData%\TermLens\settings.json` and the termbase is auto-loaded on startup
- **Auto-detect** – if no termbase is configured, TermLens automatically checks the
  default Supervertaler data directories (`~/Supervertaler_Data/resources/` and
  `%LocalAppData%\Supervertaler/resources/`)
- **Live termbase preview** – the settings dialog shows the termbase name, total
  term count, and source/target language pair after a file is selected

### Technical
- Reads Supervertaler's SQLite termbase format (`supervertaler.db`) directly –
  no separate export step needed
- Docks as a ViewPart below the Trados Studio editor (compatible with Studio 2024 / Studio18)
- Built on .NET Framework 4.8 with strong-name signing (`PublicKeyToken=6afde1272ae2306a`)
- Packaged in OPC format (`.sdlplugin`) as required by the Trados plugin framework
