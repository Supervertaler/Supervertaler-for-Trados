# Proofread active segment: notes for the help page

User-facing notes, kept while building, for the Help page (the AI Proofreader page, `trados/ai-proofreader/`, is where
the window's **?** and **F1** go; a section there, or a page of its own, is still to be written). Built in November 2026
for 20.209.
**Not yet tried in Trados Studio**: everything below about the Editor (the shortcut, the menu item, the write, the
status, Track Changes) is what the code does and what its test checks on a stand-in document, to be confirmed in
Studio.

## What it does

- Has the AI proofread **the segment you are in**, and offers its correction in a small window, without leaving the
  grid. It is the third of the Editor's one-segment actions, beside **Translate active segment** (Alt+T) and
  **FuzzyFixer** (Ctrl+Alt+F).
- It is the same check as **Proofread** on the Batch Operations tab, for one segment: the same proofreading prompt
  (the one selected there for proofreading, or the project's own), the same memory bank, the termbase terms that
  occur in the segment, and the same rules for what a correction may change.
- It uses the model in the **Translation** list on the AI Settings tab, as a full proofreading run does. It is not a
  Light task.
- **It does not send the whole document**, whatever "Include full document content in AI context" says. It sends the
  **three segments before and the three after** your segment, each with its current translation if it has one, as
  context. They are not proofread.
- It makes **no proofreading report**. The findings on the Reports tab from your last full run, and the saved report,
  stay exactly as they were. (The request does get the usual request card on the Reports tab, labelled Proofread.)

## How to use it

- **Ctrl+Alt+P** in the Editor, or right-click in the Editor > **Proofread active segment**. Like every Supervertaler
  shortcut, it can be changed in Studio under File > Options > Keyboard Shortcuts.
- A window opens at once and names the model it is asking. While it is open the Editor waits. Closing the window
  (Escape, the X) cancels the request.
- **No issue**: the window says "No issues found in this segment." There is nothing to write.
- **An issue with a correction**: the window shows the issue and the suggestion in words, and under them the corrected
  translation as changes to your translation: removed words struck through in red, added words underlined in green.
  **Enter** (or Alt+W) writes it; **Escape** (or Alt+D, or Discard) throws it away. Nothing is written before Enter.
- **An issue, but nothing to write**: the window shows the issue and says why nothing can be written, for example that
  the AI gave no corrected translation, or that its correction lost a tag or a line break. If the AI did give a
  correction, it is shown so you can copy from it by hand.
- The **?** in the window's title bar, or **F1**, opens the AI Proofreader help page.

## What it cannot check from one segment

- Anything that needs the rest of the document: a term translated differently forty segments earlier, a heading that
  no longer matches its table of contents, numbering that runs on. For that, proofread from the Batch Operations tab,
  which sends the whole document.
- It always checks the translation against its source. The "Translation only (no source)" box on the Batch Operations
  tab does not apply to it.
- "Also add issues as Trados comments" does not apply either: no comment is added to the segment.

## How the write behaves

- It is the write **Apply** on the Reports tab makes.
- Only what the correction changes is changed. Your non-breaking spaces and hyphens are kept wherever the correction
  left the text alone, and so are your tags, including a tag only the target has (bold you added by hand).
- A correction that loses a tag or a line break is never written.
- The segment is set to **Draft**, also if it was confirmed, so that you look at it again. The window says so
  beforehand for a confirmed segment.
- With **Track Changes** on, the correction is recorded word by word: only the changed words are marked.
- Ctrl+Z undoes the write.
- If the segment was changed while the window was open (an Apply all still running on the Reports tab, or an AI
  agent writing over MCP), nothing is written and a message says so.

## When it does not start

A message says why, and nothing is sent:

- there is no active segment;
- the segment is locked;
- the segment has no translation yet;
- a batch translation is running;
- no AI provider is set up, or its API key is missing.

## When nothing comes back

The window says "Nothing was written." and why:

- the AI request failed (the provider's own message is shown);
- the AI's reply held no verdict that could be read. That is not shown as "no issues": try again, or try another
  model.

## Cost

One AI request per use. The proofreading prompt and the memory bank are the same from one segment to the next, so a
provider that caches prompts (Claude, OpenAI, Gemini) charges the full price for them only the first time in a while.

## Shortcut

Ctrl+Alt+P was chosen because no other Supervertaler action uses it and P stands for proofread (Alt+P is TermPicker).
On a keyboard layout where AltGr+P types a character (US-International types an o with a diaeresis), Ctrl+Alt+P is
the same key press; if it types instead of proofreading, give the action another key in Studio's Keyboard Shortcuts.
Whether Studio itself uses Ctrl+Alt+P somewhere has not been checked.
