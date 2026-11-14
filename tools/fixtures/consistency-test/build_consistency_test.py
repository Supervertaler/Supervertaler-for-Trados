#!/usr/bin/env python3
"""Builds the consistency test document and its answer key.

A small invented Dutch document (Acme / PROJ-001) for measuring, with any model in a few
minutes, how Batch Translate handles passages a document repeats:

    consistency-test-nl.txt      the source: one paragraph per line, one sentence per paragraph
    consistency-test-key.json    every planted pair by segment number, for the counter
    consistency-test-key.md      the same, to read

Everything is written from the definitions in this file, so the segment numbers in the key
cannot drift from the document. Nothing here comes from a client's text.

    python tools/fixtures/consistency-test/build_consistency_test.py

Rules the text keeps, so that Trados Studio's default segmentation gives exactly one segment
per line: one sentence per line; no colon, semicolon, question or exclamation mark; no
abbreviation with a full stop; decimals with a comma.
"""
import io, json, os, re, sys

HERE = os.path.dirname(os.path.abspath(__file__))

BOILER = "Lamphouder volgens een van de voorgaande conclusies, met het kenmerk dat "

P1 = "alle informatie die een partij in verband met deze overeenkomst aan de andere partij verstrekt"
P2 = "elke afwijking van de overeengekomen specificaties die het normale gebruik van het product aanzienlijk beperkt of onmogelijk maakt"
P3 = "de dag waarop de producten volgens de planning op het magazijn van de afnemer worden afgeleverd"
W = "chakel de pomp uit en trek de stekker uit het stopcontact voordat u het deksel van de behuizing verwijdert"
S = "de vier bouten kruiselings aan met een momentsleutel tot een aanhaalmoment van 12 newtonmeter is bereikt"
Q0 = "voor het bevestigen van een lamp aan het stuur van een fiets"
Q1 = "de klembeugel is voorzien van een verende lip die in een richting dwars op de lengteas van het stuur in een uitsparing van de lampvoet grijpt"
Q2 = "de lampvoet afneembaar aan de klembeugel is bevestigd door middel van een schuifverbinding met een borgnok"
Q3 = "de verende lip aan zijn vrije uiteinde is omgebogen voor het vormen van een haak die achter de rand van de uitsparing grijpt"


def value_list(opening, first, step, count, unit, decimals=False):
    """One long sentence listing values: 'X, bij voorkeur Y, bij verdere voorkeur Z, ... en bij hoogste voorkeur Q.'"""
    def fmt(v):
        return (("%.1f" % v).replace(".", ",") if decimals else "%d" % v) + " " + unit
    parts = [opening + " " + fmt(first), "bij voorkeur " + fmt(first + step)]
    for i in range(2, count - 1):
        parts.append("bij verdere voorkeur " + fmt(first + i * step))
    return ", ".join(parts) + " en bij hoogste voorkeur " + fmt(first + (count - 1) * step) + "."


LIST_UNDER_A = value_list("Vervang de afdichting van de pomp uiterlijk na", 500, -10, 31, "bedrijfsuren")
LIST_UNDER_B = value_list("Vervang de pakking van de pomp uiterlijk na", 500, -10, 31, "bedrijfsuren")
LIST_OVER_A = value_list("In een alternatieve uitvoeringsvorm bedraagt de breedte van de klembeugel minimaal", 10.0, 0.5, 34, "mm", True)
LIST_OVER_B = value_list("In een alternatieve uitvoeringsvorm bedraagt de breedte van de lampvoet minimaal", 10.0, 0.5, 34, "mm", True)

# (id, text). The id is how the pairs below name a segment; the number is its line.
LINES = [
    # ---- part 1: a contract -------------------------------------------------------------
    ("c-title", "Deel 1 Leveringsovereenkomst PROJ-001"),
    ("c-parties", "Deze overeenkomst wordt gesloten tussen Acme, hierna de afnemer, en de leverancier."),
    ("c-h1", "Artikel 1 Definities"),
    ("c-def-info", "Onder vertrouwelijke informatie wordt verstaan " + P1 + ", ongeacht of dit schriftelijk of mondeling gebeurt."),
    ("c-def-defect", "Onder een gebrek wordt verstaan " + P2 + "."),
    ("c-def-date", "Onder de leverdatum wordt verstaan " + P3 + "."),
    ("c-h2", "Artikel 2 Levering"),
    ("c-pack", "De leverancier levert de producten in de verpakking die in bijlage 1 is beschreven."),
    ("c-plate", "Elk product draagt een afneembaar typeplaatje met het serienummer."),
    ("c-late", "Indien de leverancier voorziet dat " + P3 + " niet wordt gehaald, meldt hij dit onmiddellijk."),
    ("c-inspect", "De afnemer controleert de producten binnen vijf werkdagen na ontvangst."),
    ("c-audit", "De afnemer mag de fabriek van de leverancier eenmaal per jaar bezoeken om de productie te controleren."),
    ("c-label", "De leverancier bevestigt op elke doos een etiket met het ordernummer."),
    ("c-h3", "Artikel 3 Garantie"),
    ("c-repair", "De leverancier herstelt kosteloos " + P2 + ", mits de afnemer dit binnen dertig dagen meldt."),
    ("c-part", "De garantie geldt niet voor een onderdeel dat de afnemer zelf heeft vervangen."),
    ("c-penalty-a", "Een boete van 500 euro per dag is verschuldigd wanneer de leverancier de producten later dan de leverdatum aflevert, tot een maximum van tien procent van de orderwaarde."),
    ("c-h4", "Artikel 4 Geheimhouding"),
    ("c-secret", "De ontvangende partij gebruikt " + P1 + " uitsluitend voor het doel waarvoor zij is verstrekt."),
    ("c-reworded", "Informatie die een partij met betrekking tot deze overeenkomst aan de andere partij geeft, blijft eigendom van die partij."),
    ("c-term5", "Deze verplichting blijft na het einde van de overeenkomst nog vijf jaar van kracht."),
    ("c-h5", "Artikel 5 Aansprakelijkheid"),
    ("c-long", "De leverancier is aansprakelijk voor directe schade die de afnemer lijdt als gevolg van een gebrek, met dien verstande dat deze aansprakelijkheid per gebeurtenis beperkt is tot het bedrag dat de afnemer in de twaalf maanden voorafgaand aan de gebeurtenis voor de betrokken producten heeft betaald, en dat de leverancier in geen geval aansprakelijk is voor gederfde winst, gemiste besparingen, schade door bedrijfsstilstand of andere indirecte schade, tenzij de schade het gevolg is van opzet of bewuste roekeloosheid van de leverancier."),
    ("c-long-force", "Geen van de partijen is gehouden tot het nakomen van een verplichting indien zij daartoe verhinderd is door een omstandigheid die niet aan haar schuld te wijten is en die evenmin krachtens de wet of de in het verkeer geldende opvattingen voor haar rekening komt, zoals brand, overstroming, een staking bij een vervoerder of een uitvoerverbod, op voorwaarde dat zij haar wederpartij binnen drie werkdagen schriftelijk op de hoogte brengt."),
    ("c-h6", "Artikel 6 Duur en wijziging"),
    ("c-period", "Deze overeenkomst wordt aangegaan voor een periode van drie jaar."),
    ("c-punct-a", "Wijzigingen van deze overeenkomst zijn alleen geldig indien zij schriftelijk zijn overeengekomen en door beide partijen zijn ondertekend."),
    ("c-notice", "Elke partij kan de overeenkomst schriftelijk opzeggen met een opzegtermijn van zes maanden."),
    ("c-law", "Op deze overeenkomst is Nederlands recht van toepassing."),
    ("c-punct-b", "Wijzigingen van deze overeenkomst zijn alleen geldig, indien zij schriftelijk zijn overeengekomen en door beide partijen zijn ondertekend."),
    ("c-penalty-b", "Een boete van 250 euro per dag is verschuldigd wanneer de leverancier de producten later dan de leverdatum aflevert, tot een maximum van tien procent van de orderwaarde."),
    ("c-annex", "De bijlagen maken een onderdeel uit van deze overeenkomst."),
    # ---- part 2: a manual ---------------------------------------------------------------
    ("m-title", "Deel 2 Handleiding Acme-pomp P100"),
    ("m-h1", "Hoofdstuk 1 Veiligheid"),
    ("m-warn", "S" + W + "."),
    ("m-fumes", "Gebruik de pomp nooit in een ruimte waar brandbare dampen aanwezig kunnen zijn."),
    ("m-h2", "Hoofdstuk 2 Installatie"),
    ("m-ground", "Plaats de pomp op een vlakke en stevige ondergrond."),
    ("m-bolts", "Draai " + S + "."),
    ("m-hose", "Bevestig vervolgens de afneembare slang aan de uitlaat van de pomp."),
    ("m-hosecheck", "Controleer of de slang stevig vastzit voordat u de pomp inschakelt."),
    ("m-gland", "Zet de wartel stevig vast met de hand en gebruik daarbij geen tang."),
    ("m-h3", "Hoofdstuk 3 Dagelijks gebruik"),
    ("m-fill", "Vul het reservoir met schoon water tot aan de markering."),
    ("m-valve", "Het ventiel is een onderdeel dat u zelf kunt vervangen."),
    ("m-pressure", "De pomp levert een aanzienlijk hogere druk wanneer het filter schoon is."),
    ("m-h4", "Hoofdstuk 4 Onderhoud"),
    ("m-warn-filter", "Let op, s" + W + " of het filter vervangt."),
    ("m-filter", "Verwijder het afneembare filter en spoel het af onder stromend water."),
    ("m-close", "Plaats het filter terug en sluit het deksel."),
    ("m-bolts-again", "Na het sluiten van het deksel draait u " + S + "."),
    ("m-leak", "Controleer vervolgens of er geen water lekt langs de afdichting."),
    ("m-gauge", "Wacht vervolgens tot de drukmeter een stabiele waarde aangeeft."),
    ("m-list-under-a", LIST_UNDER_A),
    ("m-h5", "Hoofdstuk 5 Storingen"),
    ("m-defect-a", "Als het rode lampje brandt, wacht dan minimaal 10 minuten en maximaal 5 minuten voordat u de pomp opnieuw inschakelt."),
    ("m-service", "Neem contact op met Acme wanneer de storing aanhoudt."),
    ("m-reworded", "Trek altijd eerst de stekker uit het stopcontact wanneer de pomp een vreemd geluid maakt."),
    ("m-defect-b", "Als het gele lampje brandt, wacht dan minimaal 10 minuten en maximaal 5 minuten voordat u de pomp opnieuw inschakelt."),
    ("m-h6", "Hoofdstuk 6 Elektrische delen"),
    ("m-warn-fuse", "S" + W + " en de zekering controleert."),
    ("m-list-under-b", LIST_UNDER_B),
    ("m-h7", "Hoofdstuk 7 Opslag"),
    ("m-drain", "Laat de pomp volledig leeglopen voordat u haar opbergt."),
    ("m-store", "Bewaar de pomp op een droge plaats waar de temperatuur niet aanzienlijk schommelt."),
    ("m-long", "Wanneer de pomp langer dan drie maanden niet wordt gebruikt, verdient het aanbeveling om het reservoir te legen, het filter te reinigen en te laten drogen, de slang los te koppelen en opgerold naast de pomp te bewaren, de bouten van het deksel licht in te vetten zodat zij niet vastroesten, en de pomp af te dekken met een doek die lucht doorlaat, omdat een afgesloten plastic hoes condensvorming veroorzaakt die de elektrische aansluitingen kan aantasten."),
    # ---- part 3: patent style, the claims at the end -------------------------------------
    ("p-title", "Deel 3 Octrooiaanvraag Lamphouder"),
    ("p-field", "De uitvinding heeft betrekking op een lamphouder " + Q0 + "."),
    ("p-prior", "Bekende lamphouders hebben het nadeel dat de lamp bij trillingen losraakt."),
    ("p-aim", "Het doel van de uitvinding is een lamphouder te verschaffen die dit nadeel niet heeft."),
    ("p-desc-1", "Volgens de uitvinding wordt dit doel bereikt doordat " + Q1 + ", zodat de lamp ook bij sterke trillingen op haar plaats blijft."),
    ("p-fig1", "Figuur 1 toont een zijaanzicht van de lamphouder."),
    ("p-fig2", "Figuur 2 toont een doorsnede van de klembeugel."),
    ("p-desc-2", "In een voorkeursuitvoering geldt dat " + Q2 + ", wat het opladen van de lamp vergemakkelijkt."),
    ("p-material", "De klembeugel 2 is vervaardigd uit een stevige kunststof."),
    ("p-top", "De lampvoet 3 heeft een vlakke bovenzijde waarop de lamp rust."),
    ("p-twin-a", "De borgnok 12 valt in de opening 14 van de geleiding 16 en wordt daarna vergrendeld met de schuif."),
    ("p-list-over-a", LIST_OVER_A),
    ("p-thin", "De verende lip 4 is aanzienlijk dunner dan de rest van de klembeugel."),
    ("p-desc-3", "Bij voorkeur geldt dat " + Q3 + ", waardoor de lamp niet kan loskomen."),
    ("p-long-use", "Bij het monteren van de lamphouder legt de gebruiker de klembeugel om het stuur, drukt hij de twee helften naar elkaar toe totdat de sluiting vastklikt, schuift hij de lampvoet van voren in de geleiding totdat de borgnok in de opening valt, en controleert hij ten slotte door licht aan de lamp te trekken of alles goed vastzit, waarna de lamp met de draaiknop in de gewenste stand kan worden gezet zonder dat de lamphouder daarbij verschuift."),
    ("p-twin-b", "De borgnok 22 valt in de opening 14 van de geleiding 16 en wordt daarna vergrendeld met de schuif."),
    ("p-wear", "Het onderdeel dat het meest slijt, is de verende lip."),
    ("p-trial", "Een proef heeft uitgewezen dat de klemkracht na duizend ritten niet aanzienlijk afneemt."),
    ("p-slide", "De schuif is afneembaar zodat zij bij slijtage kan worden vervangen."),
    ("p-list-over-b", LIST_OVER_B),
    ("p-notool", "De lamphouder kan zonder gereedschap aan het stuur worden bevestigd."),
    ("p-click", "Vervolgens wordt de lamp in de lampvoet geschoven tot de borgnok hoorbaar vastklikt."),
    ("p-long-adv", "Een belangrijk voordeel van deze constructie is dat de krachten die tijdens het rijden op de lamp werken niet door de schuifverbinding maar door de verende lip worden opgenomen, waardoor de schuifverbinding licht kan worden uitgevoerd en de lamp toch met één hand en zonder gereedschap kan worden verwijderd wanneer de fiets onbeheerd wordt achtergelaten, ook wanneer de gebruiker dikke handschoenen draagt."),
    ("p-claims", "Conclusies"),
    ("p-claim-1", "Lamphouder " + Q0 + ", omvattende een klembeugel die om het stuur kan worden geklemd en een lampvoet waarop de lamp kan worden geplaatst, met het kenmerk dat " + Q1 + "."),
    ("p-claim-2", BOILER + Q2 + "."),
    ("p-claim-3", BOILER + Q3 + "."),
    ("p-claim-4", BOILER + "de klembeugel uit glasvezelversterkte kunststof bestaat."),
    ("p-claim-5", BOILER + "de lampvoet een oplaadpoort omvat."),
    ("p-claim-6", BOILER + "de schuifverbinding een zwaluwstaartprofiel heeft."),
    ("p-claim-7", BOILER + "de borgnok met een drukknop kan worden ontgrendeld."),
    ("p-claim-8", BOILER + "de uitsparing een afgeronde bodem heeft."),
    ("p-claim-9", BOILER + "de haak een hoek van 45 graden met de verende lip insluit."),
    ("p-claim-10", "Fiets voorzien van een lamphouder volgens een van de voorgaande conclusies."),
]

# kind, first id, second id, what a correct result is, what step 1's finder does with it
PAIRS = [
    ("verbatim", "c-def-date", "c-late", "must agree", "reports a difference"),
    ("verbatim", "c-def-defect", "c-repair", "must agree", "reports a difference"),
    ("verbatim", "c-def-info", "c-secret", "must agree", "reports a difference"),
    ("verbatim", "m-warn", "m-warn-filter", "must agree", "reports a difference"),
    ("verbatim", "m-warn", "m-warn-fuse", "must agree", "reports a difference"),
    ("verbatim", "m-warn-filter", "m-warn-fuse", "must agree", "reports a difference"),
    ("verbatim", "m-bolts", "m-bolts-again", "must agree", "reports a difference"),
    ("verbatim", "p-field", "p-claim-1", "must agree", "reports a difference"),
    ("verbatim", "p-desc-1", "p-claim-1", "must agree", "reports a difference"),
    ("verbatim", "p-desc-2", "p-claim-2", "must agree", "reports a difference"),
    ("verbatim", "p-desc-3", "p-claim-3", "must agree", "reports a difference"),
    ("reworded", "c-def-info", "c-reworded", "should agree where the wording is the same", "cannot see it"),
    ("reworded", "m-warn", "m-reworded", "should agree where the wording is the same", "cannot see it"),
    ("twin-number", "c-penalty-a", "c-penalty-b", "must agree but for the number", "reports a difference"),
    ("twin-number", "p-twin-a", "p-twin-b", "must agree but for the number", "reports a difference"),
    ("punctuation", "c-punct-a", "c-punct-b", "must agree word for word", "reports a difference"),
    ("defect-twin", "m-defect-a", "m-defect-b", "must agree but for the colour, and a note on both or on neither", "reports a difference, and a note on only one"),
    ("list-under-limit", "m-list-under-a", "m-list-under-b", "must agree but for the changed word", "reports a difference"),
    ("list-over-limit", "p-list-over-a", "p-list-over-b", "must agree but for the changed word", "reports a difference, but step 2 cannot show the earlier one"),
]

# Ordinary words that invite another English word each time. Not checked by steps 1 or 2:
# here so the same document can measure that third problem later.
SYNONYMS = [
    ("afneembaar", ["afneemba"], ["removabl", "detachabl"]),
    ("aanzienlijk", ["aanzienlijk"], ["considerabl", "significant", "substantial", "material", "marked", "appreciabl", "much", "far"]),
    ("bevestigen", ["bevestig"], ["attach", "fasten", "fix", "secur", "mount", "fit"]),
    ("stevig", ["stevig"], ["firm", "secure", "sturd", "solid", "strong", "robust", "rigid", "stable", "tight"]),
    ("onderdeel", ["onderdeel"], ["part", "component"]),
    ("controleren", ["controle"], ["check", "inspect", "verif", "examin", "make sure", "ensure", "test"]),
    ("vervolgens", ["vervolgens"], ["then", "subsequent", "next", "afterwards", "thereafter", "after that"]),
]

BOILERPLATE_IDS = ["p-claim-%d" % i for i in range(2, 10)]


def tokens(text):
    """Token count as the plugin's tokeniser counts: a word, a run of spaces, or one other character each."""
    return len(re.findall(r"[^\W_]+(?:['’-][^\W_]+)*|\s+|[^\w\s]|_", text, re.U))


def words(text):
    return len(re.findall(r"[^\W_]+(?:['’-][^\W_]+)*", text, re.U))


def main():
    ids = [i for i, _ in LINES]
    assert len(ids) == len(set(ids)), "duplicate id"
    texts = [t for _, t in LINES]
    assert len(texts) == len(set(texts)), "two identical lines: consistent repetitions would merge them"
    for i, t in LINES:
        assert not re.search(r"[:;?!]", t), (i, "a colon, semicolon, ? or ! would split the segment")
        assert not re.search(r"\.\s", t), (i, "a full stop inside the line would split the segment")
        assert "\n" not in t
    number = {i: n + 1 for n, (i, _) in enumerate(LINES)}

    limit = 400
    for i in ("m-list-under-a", "m-list-under-b"):
        assert 300 < tokens(dict(LINES)[i]) < limit, (i, tokens(dict(LINES)[i]))
    for i in ("p-list-over-a", "p-list-over-b"):
        assert tokens(dict(LINES)[i]) > limit + 40, (i, tokens(dict(LINES)[i]))

    key = {
        "document": "consistency-test-nl.txt",
        "language": "nl-NL into en-GB",
        "segments": [{"n": number[i], "id": i, "words": words(t), "tokens": tokens(t), "text": t} for i, t in LINES],
        "pairs": [{"kind": k, "a": number[a], "b": number[b], "a_id": a, "b_id": b, "correct": c, "finder": f}
                  for k, a, b, c, f in PAIRS],
        "boilerplate": {"segments": [number[i] for i in BOILERPLATE_IDS],
                        "phrase": BOILER.strip(),
                        "correct": "no pair among these segments on the strength of the formula alone"},
        "synonyms": [{"word": w, "stems": stems, "english": eng,
                      "segments": [number[i] for i, t in LINES if any(s in t.lower() for s in stems)]}
                     for w, stems, eng in SYNONYMS],
        "token_limit": limit,
    }
    with io.open(os.path.join(HERE, "consistency-test-nl.txt"), "w", encoding="utf-8-sig", newline="\r\n") as f:
        f.write("\n".join(texts) + "\n")
    with io.open(os.path.join(HERE, "consistency-test-key.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(key, f, ensure_ascii=False, indent=1)
        f.write("\n")

    md = []
    md.append("# Consistency test document: answer key")
    md.append("")
    md.append("Written by `build_consistency_test.py`; do not edit by hand. The document is invented (Acme / PROJ-001).")
    md.append("")
    md.append("- Source: `consistency-test-nl.txt`, Dutch, for translation into British English.")
    md.append("- **%d segments** expected in Trados Studio: one per line (plain text file type, default segmentation)." % len(LINES))
    md.append("- Part 1, a contract: segments %d-%d. Part 2, a manual: %d-%d. Part 3, patent style with the claims at the end: %d-%d."
              % (number["c-title"], number["c-annex"], number["m-title"], number["m-long"], number["p-title"], number["p-claim-10"]))
    md.append("")
    md.append("## Planted pairs")
    md.append("")
    md.append("| Kind | Segments | Apart | A correct result | Step 1's finder |")
    md.append("|---|---|---|---|---|")
    for k, a, b, c, f in PAIRS:
        md.append("| %s | %d and %d | %d | %s | %s |" % (k, number[a], number[b], number[b] - number[a], c, f))
    md.append("")
    md.append("- **verbatim**: the same passage, word for word, inside two different sentences.")
    md.append("- **reworded**: the same thing said with small changes; at most six identical words in a row, so the finder does not pair them.")
    md.append("- **twin-number**: the same sentence but for one number.")
    md.append("- **punctuation**: the same words; one has a comma the other lacks.")
    md.append("- **defect-twin**: the same sentence but for one word, and both say \"minimaal 10 minuten en maximaal 5 minuten\", which cannot be right. A model that flags one should flag both.")
    md.append("- **list-under-limit / list-over-limit**: two long lists of values that differ in one word. Under %d tokens the earlier one can be shown to the AI (step 2), provided its translation stays under the limit too; over it, it cannot." % limit)
    md.append("")
    md.append("## Boilerplate")
    md.append("")
    md.append("Segments %s open with the same formula (\"%s...\"). The finder must not pair them with each other on the strength of that formula."
              % (", ".join(str(number[i]) for i in BOILERPLATE_IDS), BOILER.strip()))
    md.append("")
    md.append("## Long segments")
    md.append("")
    md.append("| Segment | Words | Tokens |")
    md.append("|---|---|---|")
    for i, t in LINES:
        if words(t) >= 55:
            md.append("| %d | %d | %d |" % (number[i], words(t), tokens(t)))
    md.append("")
    md.append("## Words that invite another English word each time")
    md.append("")
    md.append("Not checked by steps 1 or 2; the counter only lists what it finds.")
    md.append("")
    md.append("| Dutch | Segments |")
    md.append("|---|---|")
    for s in key["synonyms"]:
        md.append("| %s | %s |" % (s["word"], ", ".join(str(n) for n in s["segments"])))
    md.append("")
    with io.open(os.path.join(HERE, "consistency-test-key.md"), "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(md))
    print("%d segments written" % len(LINES))
    for i in ("m-list-under-a", "p-list-over-a", "c-long", "m-long", "p-long-use", "p-long-adv", "p-claim-1"):
        print("  %-16s segment %3d: %3d words, %3d tokens" % (i, number[i], words(dict(LINES)[i]), tokens(dict(LINES)[i])))


if __name__ == "__main__":
    main()
