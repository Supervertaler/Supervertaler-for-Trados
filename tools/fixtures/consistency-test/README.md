# Consistency test document

A small invented Dutch document (Acme / PROJ-001) for measuring, with any model, in a few minutes and for
cents, how Batch Translate handles passages a document repeats: step 1 (the report at the end of a run,
`docs/design/restated-passages.md`) and step 2 (showing the AI its earlier translation,
`docs/design/in-run-references.md`). Nothing in it comes from a client's text.

| File | What |
|---|---|
| `consistency-test-nl.txt` | The source. 105 lines, one sentence each: 105 segments in Trados Studio. |
| `consistency-test-key.md` | The answer key to read: every planted pair, by segment number. |
| `consistency-test-key.json` | The same for the counter. |
| `build_consistency_test.py` | Writes the three files above. Change the document here, never by hand. |
| `..\..\consistency_test_count.ps1` | The counter: takes a run's backup TMX and reports pair by pair. |
| `..\..\consistency_test_count_test.ps1` | The counter's own test, with made-up runs. |

## What is in the document

Three parts in one file: a supply contract (segments 1-32), a pump manual (33-66) and a patent-style text with its
claims at the end (67-105). Planted in it, each with a known answer:

- 13 pairs where a passage stands word for word inside two different sentences, 1 to 26 segments apart: definitions
  restated in later clauses, a warning and a step repeated across chapters, claims restated in the description
  that comes before them;
- 1 long claim with several "waarbij" clauses and the summary, earlier in the description, that repeats it;
- 2 pairs that say the same with small rewording (the finder cannot see these);
- 2 pairs of near-identical sentences that differ in one number;
- 1 pair that differs in a comma only;
- 1 pair of near-identical sentences that both contain the same defect ("at least 10 minutes and at most 5
  minutes"), to see whether the AI flags both;
- 8 long cascades of preferred values, the shape that gave replies with no text and failed writes on a real
  job: minimum values, maximum values and ranges, each with a near-twin further on. Two pairs are under the
  400-token limit and two over it;
- a formula that opens 8 segments (the dependent claims), which must pair nothing;
- 15 one-sentence paragraphs of 60 to 220 words in all;
- 7 ordinary words ("afneembaar", "aanzienlijk", "bevestigen", "stevig", "onderdeel", "controleren",
  "vervolgens") that invite a different English word each time, in unrelated sentences.

## Setting up a run, so that two runs can be compared

1. In Trados Studio, make a **new project** from `consistency-test-nl.txt`: Dutch (Netherlands) into English
   (United Kingdom). Studio opens it with its plain text file type.
2. **No translation memory, no EverTM, no termbase.** If your project template adds any, remove them in the
   wizard. Do not pre-translate.
3. Open the file. It must show **105 segments**, all empty. If the count differs, stop: the key's numbers will not
   fit.
4. In Supervertaler's Batch Operations tab:
   - **"Send TM matches to the AI" unticked.** This tick box is the only thing that stops TM matches being sent;
     the project's own Lookup ticks do not.
   - Batch size **10**.
   - Scope **All segments**.
   - Memory bank: **no project bank**. A bank adds text that differs from day to day.
   - The **same prompt** for every run you want to compare. The default translation prompt is fine. A prompt
     written by AutoPrompt before this feature tells the AI there is no memory between requests; regenerate it or
     use the default.
   - Backup TMX **on** (it is what the counter reads).
   - "Retry segments left empty" as you normally have it.
5. Pick the model and run Batch Translate once.
6. Count (the log names the backup file):

```
powershell -NoProfile -ExecutionPolicy Bypass -File tools\consistency_test_count.ps1 -Tmx "D:\Supervertaler\trados\batch_backups\<the run's file>.tmx" -PromptLog "D:\Supervertaler\trados\logs\prompts\<date>.jsonl" -Since <date>T<hh:mm>
```

   `-PromptLog` and `-Since` (the time the run started, as `2026-11-15T10:30`) are what let it say which segments
   were shown an earlier translation, and find notes that were written as Trados comments. Without them it still
   says which pairs agree.

For a **second run** on the same project (another model, another build): select all segments, clear the targets
(right-click, Clear Target Segment), and run again with the same settings. A run on the segments a first run left
empty is added with `-Tmx2`.

## Reading the result

- "must agree" pairs that DIFFER are what steps 1 and 2 exist for. With step 2 working, the pairs marked "shown"
  should agree.
- A pair in the "same request" was translated in one request: the AI saw both, and step 2 has nothing to add.
- The long lists over the limit are never shown to the AI. They are there to measure that limit.
- The reworded pairs are "not judged": the finder cannot see them. The counter only says how long a wording the
  two translations share.
- The synonym table is not checked by steps 1 or 2. It is there for the third problem, later.
