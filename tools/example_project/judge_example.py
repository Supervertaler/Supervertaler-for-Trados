"""Blind check of the example's translations by Claude Opus 5.5, as in the
19 Nov model tests: every document text, TM entry and term scored 1-5 against
the English, with a reason for anything below 5.

COSTS MONEY: run only on Michael's go. Without --go it prints an estimate.

    python tools\\example_project\\judge_example.py --keys <api-keys.json> [--go] [--only de,nl]

(Not with -I: the anthropic package is in the user site-packages.)
Reads tools\\example_project\\out\\<lang>.json, writes out\\verdicts.json
({lang: [{id, score, why}]}), which check_example.py reads.
"""
import argparse, json, os, sys
from concurrent.futures import ThreadPoolExecutor

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
DATA = os.path.join(REPO, "src", "Supervertaler.Trados", "Resources", "example-project", "example-project.json")
OUT = os.path.join(HERE, "out")
MODEL = "claude-opus-5-5"
PRICE_IN, PRICE_OUT = 4.00, 20.00  # $ per million tokens (Claude Opus 5.5, Nov 2026)

SYSTEM = """You review translations of an invented water-heater manual (the example project of a translation plugin).
For every item, compare the translation with the English and score it:
5 = correct and idiomatic as it is; 4 = correct, a reviser might polish wording; 3 = a real error a professional
would fix (meaning, terminology, grammar, register); 2 = several errors; 1 = wrong or unusable.
Inline markup (**bold**, *italic*, [text](url)) must be kept around the corresponding words. Numbers may use the
target language's decimal separator. Terms must be used consistently with the TERMS section.
Answer with one JSON object and nothing else: {"items": [{"id": "<part>:<id>", "score": n, "why": "<short reason if below 5>"}]}
covering every item."""


def items(data, res):
    r = res["result"]
    out = []
    for t in data["terms"]: out.append(("terms:" + t["id"], t["source"], r["terms"].get(t["id"])))
    for b in data["document"]:
        for x in [b] + (b.get("items") or []) + [c for row in (b.get("rows") or []) for c in row]:
            if x.get("id"): out.append(("document:" + x["id"], x["text"], r["document"].get(x["id"])))
    for t in data["tm"]: out.append(("tm:" + t["id"], t["source"], r["tm"].get(t["id"])))
    return out


def judge(key, data, lang, res):
    its = items(data, res)
    msg = f"Target language key: {lang}\n\n" + "\n".join(
        f"[{i}]\nEN: {en}\nTR: {tr}" for i, en, tr in its)
    import anthropic
    client = anthropic.Anthropic(api_key=key)
    # Streaming: a long answer must not hit the HTTP timeout. Effort high: this
    # is a quality check, not bulk work (Opus 5.5 defaults to medium).
    with client.messages.stream(model=MODEL, max_tokens=64000, system=SYSTEM,
                                extra_body={"thinking": {"type": "adaptive"}, "output_config": {"effort": "high"}},  # SDK 0.72 predates both
                                messages=[{"role": "user", "content": msg}]) as st:
        r = st.get_final_message()
    if r.stop_reason in ("refusal", "max_tokens"):
        raise RuntimeError(f"{lang}: the judge stopped ({r.stop_reason})")
    text = "".join(c.text for c in r.content if c.type == "text")
    s, e = text.find("{"), text.rfind("}")
    got = json.loads(text[s:e + 1])["items"]
    verdicts = [{"id": g["id"].split(":", 1)[1] if ":" in g["id"] else g["id"], "part": g["id"].split(":", 1)[0],
                 "score": g["score"], "why": g.get("why", "")} for g in got]
    return lang, verdicts, r.usage.input_tokens, r.usage.output_tokens


def main():
    sys.stdout.reconfigure(encoding="utf-8")  # Greek, Japanese … on a Windows console
    ap = argparse.ArgumentParser()
    ap.add_argument("--keys", required=True)
    ap.add_argument("--go", action="store_true")
    ap.add_argument("--only", default="")
    a = ap.parse_args()
    data = json.load(open(DATA, encoding="utf-8"))
    langs = sorted(f[:-5] for f in os.listdir(OUT) if f.endswith(".json") and f != "verdicts.json") if os.path.isdir(OUT) else []
    if a.only: langs = [l for l in langs if l in a.only.split(",")]
    print(f"{len(langs)} language(s) to judge; estimate about ${len(langs) * (8000 * PRICE_IN + 12000 * PRICE_OUT) / 1e6:.2f}")
    if not a.go or not langs:
        print("Not sent: add --go to send." if langs else "Nothing translated yet.")
        return
    key = json.load(open(a.keys, encoding="utf-8"))["claude"]
    todo = [(l, json.load(open(os.path.join(OUT, l + ".json"), encoding="utf-8"))) for l in langs]
    with ThreadPoolExecutor(4) as ex:
        res = list(ex.map(lambda t: judge(key, data, t[0], t[1]), todo))
    vpath = os.path.join(OUT, "verdicts.json")
    all_v = json.load(open(vpath, encoding="utf-8")) if os.path.exists(vpath) else {}
    for lang, v, _, _ in res: all_v[lang] = v
    json.dump(all_v, open(vpath, "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    ti = sum(r[2] for r in res); to = sum(r[3] for r in res)
    for lang, v, _, _ in res:
        low = [x for x in v if x["score"] < 4]
        print(f"{lang}: average {sum(x['score'] for x in v) / max(1, len(v)):.2f}, below 4: {len(low)}")
    print(f"in {ti} out {to} cost ${ti / 1e6 * PRICE_IN + to / 1e6 * PRICE_OUT:.4f}")


if __name__ == "__main__":
    main()
