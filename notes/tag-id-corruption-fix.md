# `update_segments` tag-ID corruption — root cause and fix

Repo: `Supervertaler-for-Trados`
Reported from: job PROJ-001 (a technical manual), segments 498, 500, 552, 559
Date: 2026-08-03

---

## The symptom, restated precisely

Four segments of the shape

> Set the **I/O** switch (11) to the **O** position to stop the unit.

— two separate bold tag pairs with ordinary text between them. After
`update_segments` wrote them, Studio's Tag Verifier reported, per segment, one
tag pair removed and a different one added, plus a duplicated underlying tag id
paired with a missing one ("Duplicated tag with id '79'" / "Missing tag with id
'78'"). In the editor both pairs showed the *same* id where they should have
shown two consecutive ones.

Three things about it were diagnostic:

1. `check_tags` passed. It counts tags, and the count was right.
2. `get_segments` showed the tags as completely normal, because the normalised
   view shows markers, not underlying ids.
3. **Rewriting the segment with identical text did not clear it.** The same
   duplicate/missing pair came back after a fresh save.

Point 3 is the one that identifies the bug. A random or transient fault would
not reproduce identically on a clean rewrite. Something in the write path was
reading the corrupt state back in and re-applying it.

---

## Root cause

`AiAssistantViewPart.BridgeUpdateSegments` built the tag map it hands to
`SegmentTagHandler.ReconstructTarget` like this:

```csharp
var sourceSer   = Core.SegmentTagHandler.Serialize(sp.Source);
var targetSer   = Core.SegmentTagHandler.Serialize(sp.Target);
var combinedMap = BuildCombinedTagMap(sourceSer.TagMap, targetSer.TagMap);
```

and `BuildCombinedTagMap` resolves numbering collisions **in favour of the
target**:

```csharp
foreach (var kv in targetTagMap)
{
    var clone = CloneTagInfo(kv.Value);
    if (clone != null)
        combined[kv.Key] = clone;      // ← target overwrites source
}
```

That rule is correct where it came from — the bilingual re-import path
(`AiAssistantViewPart.cs` ~line 12203), where the proofreader's `<b>` / `<tN>`
markers were rendered from the *target* cell of the exported table, so target
tags are the right referent, and where the documented "proofreader moved the
bold to a different word, and the bold only exists in the target" case genuinely
needs target tags.

It is wrong for `update_segments`, for two independent reasons:

- The markers I send come from `get_segments`' **source** field, which is
  rendered from `sourceSer.TagMap`. Source and target are serialised with
  separate counters, so `<t1>` in the source field and `<t1>` in the target
  field are not the same tag. Preferring the target silently rebinds every
  marker to a different object.
- More fundamentally, **Studio's Tag Verifier compares the target's tag ids
  against the source's.** Whatever the caller meant, a tag cloned from the
  target can only pass verification by coincidence.

### How that produced *these* exact errors

The four segments arrived with pre-existing fuzzy-match targets, and — as
documented at length in the job notes — that fuzzy band was contaminated with
boilerplate from unrelated jobs, so the draft targets carried tag structure that
did not correspond to this source at all.

Concretely, source serialises to `{1 → pair id 78, 2 → pair id 79}`. The stale
draft target serialises to `{1 → pair id 79}` — one bold pair, carrying the
wrong id. `BuildCombinedTagMap` then yields:

| key | from source | after target overwrite | tag id written |
|---|---|---|---|
| 1 | pair id 78 | **target's pair id 79** | 79 |
| 2 | pair id 79 | (target has no key 2) | 79 |

Both markers resolve to id 79. Duplicate 79, missing 78 — exactly what the
verifier reported, and exactly what you saw in the editor.

### Why the rewrite could not heal it

After that write the target holds two pairs, both id 79. The next
`update_segments` call re-serialises the target, gets `{1 → 79, 2 → 79}`, and
lets it win again. The corrupt state is its own input. That is why writing the
same correct text a second time changed nothing, and why deleting a tag by hand
in Studio was the only way out — that pulls a fresh tag from the source.

Single-tag segments mostly escaped because with one pair the target's tag often
happened to be the right one; two pairs makes a mismatch nearly certain.

---

## The fix

### 1. Source-authoritative tag map in the bridge write path

`AiAssistantViewPart.BridgeUpdateSegments` now uses `sourceSer.TagMap` only.
`BuildCombinedTagMap` is untouched and still used by the re-import path.

```csharp
bool reconstructed = false;
var sourceSer = Core.SegmentTagHandler.Serialize(sp.Source);
var tagMap = sourceSer.TagMap ?? new Dictionary<int, Core.TagInfo>();

var resolved = Core.Export.BilingualTagNamer.ResolveSemanticNames(targetText, tagMap);

bool hasAnyMarker = tagMap.Count > 0
    || resolved.IndexOf("<t", StringComparison.Ordinal) >= 0;
if (hasAnyMarker)
    reconstructed = Core.SegmentTagHandler.ReconstructTarget(
        sp.Target, sp.Source, resolved, tagMap);
```

A marker number the source does not have now falls into `ReconstructTarget`'s
existing unknown-tag-number branch, which keeps the content and drops the
wrapper — losing one bold run, rather than writing a tag Studio rejects.

This also makes the damage self-healing: re-sending an affected segment now
clones the source's tags and repairs it, which answers your actual question —
next time I can fix those four segments myself with a plain `update_segments`
call.

### 2. A tag number can only be materialised once

`SegmentTagHandler.AddElementsToContainer` cloned `tagMap[N].OriginalMarkup`
every time it saw `<tN>`, so a caller repeating a marker produced two tags with
the same id — a second, independent route to "Duplicated tag with id 'N'", and
one no count-based check can ever catch. `ReconstructTarget` now threads a
`HashSet<int> usedTagNumbers` through the walk; a repeat falls into the same
unknown-number branch. Existing callers are unaffected (the parameter is
optional and defaults to null, which disables the guard).

### 3. `update_segments` now audits its own writes

After each successful reconstruction the bridge compares the *multiset* of tag
ids in the written target against the source's, and reports any difference in a
new per-item `warning` field (plus a count in the response `note`). A multiset,
not a set — the whole failure mode is one id appearing twice while another goes
missing, which a set comparison hides.

This is the part that changes how the tool behaves for me: the mismatch now
appears in the write's own response, instead of only surfacing later in
`run_verification` — which reads last-saved state and which I only ran because
of an unrelated hunch. Silent success on a corrupt write is what let this ship.

The audit never fails the write: it is wrapped in try/catch, and `TagId` is read
reflectively so an SDK change degrades to "no audit" rather than a build break.

### 4. Sharper tool description

`Resources/mcp-tools.json` now tells the caller explicitly to copy markers from
the segment's **SOURCE** field rather than its existing target, to use each
marker at most once, and that a `warning` on a result item means re-send that
segment with the source's markers to repair it.

---

## Files changed

| File | Change |
|---|---|
| `src/Supervertaler.Trados/AiAssistantViewPart.cs` | source-authoritative tag map in `BridgeUpdateSegments`; new `DescribeTagIdMismatch` / `CollectTagIds` / `CollectTagIdsInto` / `SafeTagId` helpers; warning plumbed into the result item and the response note |
| `src/Supervertaler.Trados/Core/SegmentTagHandler.cs` | `usedTagNumbers` guard threaded through `ReconstructTarget` → `AddElementsToContainer` |
| `src/Supervertaler.Trados/Core/SupervertalerBridge.cs` | `warning` member on `BridgeUpdateResultItem` |
| `src/Supervertaler.Trados/Resources/mcp-tools.json` | `update_segments` description and `target` field description |

No change is needed in `Supervertaler.McpServer` — `BridgeClient` relays the
bridge's JSON verbatim, so the new `warning` field flows through untouched.

**Not compiled.** I had no .NET Framework 4.8 or Trados SDK available in this
session, so this needs a build before it goes anywhere near a live job.

---

## Suggested repro

1. Open a document with a segment containing two separate bold runs.
2. Give it a draft target whose tag structure differs from the source (a fuzzy
   match from an unrelated TM does it; failing that, hand-edit one tag away).
3. `update_segments` with the correct translation, copying the markers from the
   source field.
4. Save, then `run_verification`.

Before the fix: duplicated/missing tag id on that segment, reproducible on
rewrite. After: clean, and a second write on an already-corrupt segment repairs
it.

---

## One thing I did not change

The bilingual re-import path (`AiAssistantViewPart.cs` ~12203) still uses
`BuildCombinedTagMap` with target-wins. Its rationale is real and documented,
and I couldn't test a change to it. But it carries the same latent risk, and the
rule that preserves its stated use case while removing the corruption route is
*gap-fill* rather than *overwrite*:

```csharp
foreach (var kv in targetTagMap)
{
    if (combined.ContainsKey(kv.Key)) continue;   // source wins where it has a tag
    var clone = CloneTagInfo(kv.Value);
    if (clone != null) combined[kv.Key] = clone;
}
```

The documented case — "the bold only exists in the target, source TagMap is
empty" — still works, because there is no source entry to lose to. Worth
considering once the re-import path has a test around it.
