"""Machine checks of the example's translations, then (with --merge) puts the
languages that pass into the plugin's example-project.json. Free: no AI.

    python -I tools\\example_project\\check_example.py            # report only
    python -I tools\\example_project\\check_example.py --merge    # and merge what passes

Per language, from tools\\example_project\\out\\<lang>.json:
- every id answered, nothing empty;
- the same markup: **bold**, *italic*, [text](url) with the same url;
- the same numbers (0.6 and 0,6 count as the same), the product names and codes kept;
- every term's translation used where the English term occurs (a warning, not a
  failure: inflected languages change the ending);
- an exact-match TM entry (band 100) identical to the document's translation of
  that sentence (it is copied there if not, and said);
- judge_example.py's scores, when present: any segment below 4 is listed and
  blocks the merge of that language.
"""
import json, os, re, sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
DATA = os.path.join(REPO, "src", "Supervertaler.Trados", "Resources", "example-project", "example-project.json")
OUT = os.path.join(HERE, "out")

KEEP = ["Acme", "AquaLine", "AL-50", "E1", "E2", "E3", "ON/OFF", "+1 555 0100", "IP"]


def doc_texts(data):
    for b in data["document"]:
        if b.get("id"): yield b["id"], b["text"]
        for i in b.get("items") or []: yield i["id"], i["text"]
        for r in b.get("rows") or []:
            for c in r: yield c["id"], c["text"]


def markup(t):
    return (t.count("**"), len(re.findall(r"(?<!\*)\*(?!\*)", t)), sorted(re.findall(r"\]\(([^)]+)\)", t)))


def numbers(t):
    t = re.sub(r"\]\([^)]+\)", "", t)  # not the url
    return sorted(n.replace(",", ".") for n in re.findall(r"\d+(?:[.,]\d+)?", t))


def check(data, lang, res, verdicts):
    errors, warnings, fixes = [], [], []
    r = res.get("result") or {}
    for part, src in (("document", dict(doc_texts(data))),
                      ("tm", {t["id"]: t["source"] for t in data["tm"]}),
                      ("terms", {t["id"]: t["source"] for t in data["terms"]})):
        got = r.get(part) or {}
        for i, en in src.items():
            tr = (got.get(i) or "").strip()
            if not tr: errors.append(f"{part} {i}: missing"); continue
            if markup(en) != markup(tr): errors.append(f"{part} {i}: markup differs: {tr}")
            if numbers(en) != numbers(tr): errors.append(f"{part} {i}: numbers differ: {tr}")
            for k in KEEP:
                if k in en and k not in tr: errors.append(f"{part} {i}: '{k}' not kept: {tr}")
    terms = r.get("terms") or {}
    docs = r.get("document") or {}
    for t in data["terms"]:
        tt = (terms.get(t["id"]) or "").lower()
        stem = tt[: max(4, int(len(tt) * 0.7))]
        for i, en in doc_texts(data):
            if t["source"].lower() in en.lower() and stem and stem not in (docs.get(i) or "").lower():
                warnings.append(f"term '{t['source']}' = '{terms.get(t['id'])}' not seen in document {i}: {docs.get(i)}")
    for t in data["tm"]:
        if t["band"] == "100":
            d = docs.get(t["for"])
            if d and (r.get("tm") or {}).get(t["id"]) != d:
                r.setdefault("tm", {})[t["id"]] = d
                fixes.append(f"tm {t['id']} set to the document's translation of {t['for']}")
    for v in verdicts.get(lang, []):
        if v.get("score", 5) < 4:
            errors.append(f"judge {v['id']}: {v['score']} – {v.get('why', '')}")
    return errors, warnings, fixes


def main():
    merge = "--merge" in sys.argv
    data = json.load(open(DATA, encoding="utf-8"))
    vpath = os.path.join(HERE, "out", "verdicts.json")
    verdicts = json.load(open(vpath, encoding="utf-8")) if os.path.exists(vpath) else {}
    merged = []
    for f in sorted(os.listdir(OUT)) if os.path.isdir(OUT) else []:
        if not f.endswith(".json") or f == "verdicts.json": continue
        lang = f[:-5]
        res = json.load(open(os.path.join(OUT, f), encoding="utf-8"))
        errors, warnings, fixes = check(data, lang, res, verdicts)
        print(f"== {lang}: {len(errors)} error(s), {len(warnings)} warning(s), {len(fixes)} fix(es)"
              f"{'' if lang in verdicts else ', not judged yet'}")
        for e in errors: print("  ERROR", e)
        for w in warnings: print("  warn ", w)
        for x in fixes: print("  fixed", x)
        if merge and not errors and lang in verdicts:
            r = res["result"]
            data["languages"][lang] = {"document": r["document"], "tm": r["tm"], "terms": r["terms"]}
            merged.append(lang)
    if merge:
        with open(DATA, "w", encoding="utf-8", newline="\n") as fh:
            json.dump(data, fh, ensure_ascii=False, indent=2)
            fh.write("\n")
        print("merged:", ", ".join(merged) or "nothing")


if __name__ == "__main__":
    main()
