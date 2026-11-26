"""Makes the prepared languages of the example project (first-run plan, step 1).

One request per language to GPT-6.1 Sol (the starter's model), with the whole
English example: the document, the ten TM entries and the fifteen terms. The
answer is checked by machine (check_example.py) and then scored blind by Claude
Opus 5.5 (judge_example.py) before anything goes into example-project.json.

COSTS MONEY: run only on Michael's go. Without --go it only prints what it would
send and an estimate.

    python -I tools\\example_project\\translate_example.py --keys <api-keys.json> [--go] [--only de,nl]

Writes tools\\example_project\\out\\<lang>.json (one per language) and prints the
tokens and the cost. Nothing is written into the plugin's data here: merge.py
does that after the checks.
"""
import argparse, json, os, sys, time, urllib.request, urllib.error
from concurrent.futures import ThreadPoolExecutor

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
DATA = os.path.join(REPO, "src", "Supervertaler.Trados", "Resources", "example-project", "example-project.json")
OUT = os.path.join(HERE, "out")

# The 22 approved languages (plan, 22 Nov 2026): key -> name used in the prompt.
LANGS = {
    "nl": "Dutch (Netherlands)", "de": "German (Germany)", "fr": "French (France)", "es": "Spanish (Spain)",
    "it": "Italian", "pt": "Portuguese (Portugal)", "pt-BR": "Portuguese (Brazil)", "pl": "Polish",
    "sv": "Swedish", "da": "Danish", "nb": "Norwegian (Bokmål)", "fi": "Finnish", "cs": "Czech",
    "hu": "Hungarian", "ro": "Romanian", "el": "Greek", "tr": "Turkish", "ru": "Russian", "uk": "Ukrainian",
    "ja": "Japanese", "zh-Hans": "Chinese (Simplified)", "ko": "Korean",
}
MODEL = "gpt-6.1-sol"
PRICE_IN, PRICE_OUT = 2.00, 10.00  # $ per million tokens


def texts(data):
    doc = []
    for b in data["document"]:
        if b.get("id"): doc.append((b["id"], b["text"]))
        for i in b.get("items") or []: doc.append((i["id"], i["text"]))
        for r in b.get("rows") or []:
            for c in r: doc.append((c["id"], c["text"]))
    return doc


def prompt(data, lang_name):
    doc = texts(data)
    system = f"""You are a professional technical translator. Translate an English installation and operating guide for an
invented water heater (Acme AquaLine AL-50) into {lang_name}, as it would appear in a real product manual for that
market. It is the example project of a translation plugin, so it must be idiomatic and correct, and consistent.

Rules:
- Translate the TERMS first. Then use exactly those translations everywhere in the DOCUMENT and the TM, inflected as
  the grammar needs.
- Keep the inline markup exactly: **bold**, *italic* and [link text](url) around the corresponding words; do not
  translate the url. Keep the product names "Acme", "AquaLine", "AL-50", "AL-40" and the display codes E1, E2, E3 as
  they are; keep the button labels ON/OFF, + and – as they are.
- Numbers and units: same values; use the decimal separator and spacing usual in {lang_name} technical texts
  (e.g. 0,6 MPa where a comma is usual). Keep "230 V ~ 50 Hz", "IP"-style codes and the phone number "+1 555 0100".
- A DOCUMENT text with several sentences stays several sentences.
- The TM entries are sentences from the manual of the earlier model. Translate each one so that wherever it says the
  same as a DOCUMENT sentence, it uses the SAME words as your translation of that sentence: only the parts that differ
  in English may differ in {lang_name}. (They are fuzzy matches for a repair tool.)
- Address the reader as a product manual in {lang_name} normally does, and consistently.

Answer with one JSON object and nothing else:
{{"terms": {{"<id>": "<translation>", ...}}, "document": {{"<id>": "<translation>", ...}}, "tm": {{"<id>": "<translation>", ...}}}}
with every id below."""
    user = {
        "terms": {t["id"]: t["source"] for t in data["terms"]},
        "document": {i: t for i, t in doc},
        "tm": {t["id"]: t["source"] for t in data["tm"]},
    }
    return system, json.dumps(user, ensure_ascii=False, indent=1)


def post(url, body, headers):
    payload = json.dumps(body).encode("utf-8")
    for attempt in range(5):
        req = urllib.request.Request(url, data=payload, headers={"Content-Type": "application/json", **headers})
        try:
            with urllib.request.urlopen(req, timeout=600) as r:
                return json.loads(r.read().decode("utf-8"))
        except urllib.error.HTTPError as e:
            msg = e.read().decode("utf-8", "replace")[:300]
            if e.code in (429, 500, 502, 503, 504) and attempt < 4:
                time.sleep(5 * (attempt + 1)); continue
            raise RuntimeError(f"HTTP {e.code}: {msg}")


def run(key, data, lang):
    system, user = prompt(data, LANGS[lang])
    r = post("https://api.openai.com/v1/chat/completions",
             {"model": MODEL, "max_completion_tokens": 16000, "response_format": {"type": "json_object"},
              "messages": [{"role": "system", "content": system}, {"role": "user", "content": user}]},
             {"Authorization": "Bearer " + key})
    u = r["usage"]
    content = r["choices"][0]["message"]["content"] or ""
    try: parsed = json.loads(content)
    except Exception: parsed = None
    res = {"lang": lang, "model": MODEL, "stop": r["choices"][0]["finish_reason"],
           "in": u["prompt_tokens"], "out": u["completion_tokens"], "result": parsed, "raw": None if parsed else content}
    os.makedirs(OUT, exist_ok=True)
    json.dump(res, open(os.path.join(OUT, lang + ".json"), "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    return res


def main():
    sys.stdout.reconfigure(encoding="utf-8")  # Greek, Japanese … on a Windows console
    ap = argparse.ArgumentParser()
    ap.add_argument("--keys", required=True, help="the Supervertaler api-keys.json")
    ap.add_argument("--go", action="store_true", help="really send (costs money)")
    ap.add_argument("--only", default="", help="comma-separated language keys")
    a = ap.parse_args()
    data = json.load(open(DATA, encoding="utf-8"))
    langs = [l for l in LANGS if not a.only or l in a.only.split(",")]
    system, user = prompt(data, "German (Germany)")
    est_in = (len(system) + len(user)) / 3.5
    est_out = len(user) / 3.0 * 1.3
    print(f"{len(langs)} language(s); about {est_in:.0f} tokens in and {est_out:.0f} out each; "
          f"estimate ${len(langs) * (est_in * PRICE_IN + est_out * PRICE_OUT) / 1e6:.2f} (reasoning tokens come on top)")
    if not a.go:
        print("Not sent: add --go to send.")
        return
    key = json.load(open(a.keys, encoding="utf-8"))["openai"]
    with ThreadPoolExecutor(6) as ex:
        res = list(ex.map(lambda l: run(key, data, l), langs))
    ti = sum(r["in"] for r in res); to = sum(r["out"] for r in res)
    bad = [r["lang"] for r in res if r["result"] is None]
    print(f"in {ti} out {to} cost ${ti / 1e6 * PRICE_IN + to / 1e6 * PRICE_OUT:.4f}; unreadable: {bad or 'none'}")


if __name__ == "__main__":
    main()
