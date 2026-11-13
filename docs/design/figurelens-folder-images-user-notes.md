# FigureLens with images in a folder: notes for the Help page

User-facing notes, kept while building, for the Help page on FigureLens (`/trados/figurelens/`, topic
`HelpSystem.Topics.Images`). Built 13 Nov 2026 for the release **after** 18.20.206. Nothing here has been tried in
Trados Studio yet: the folder scan, the matching, the size limits, the run and the window were each tested outside
Studio (`core\tests\FolderFiguresTests.cs`, `tools\figurelens_folder_test.ps1`), with no AI request made.

## CHANGELOG line (not in CHANGELOG.md yet)

The release tools read the first version number in each `## [...]` heading, so there is no safe place in
`CHANGELOG.md` for a release that has no number yet. When 206 is out and the next section exists, add under
**Added**:

- **FigureLens now describes drawings that came as separate files.** The drawings of a patent are usually sent as
  image files beside the text, not as pictures in the Word document. FigureLens counted the files in the folder you
  chose and then said "No images to describe", because step 2 only ever looked at pictures inside the documents. Now,
  when the project's documents hold no images, **Describe images with AI** uses the image files in the folder: choose
  it with **Already have the images in a folder? Choose it…**. Each file is matched to a figure by its name
  (`Figure 3.png`, `fig3.jpg`, `Figuur 2.png`, `PROJ-001 Fig. 3A (amended).png`; `Fig 1-2.png` for one sheet with two
  figures) and sent together with what the text says about that figure. A file whose name gives no figure number is
  described all the same, without one. PNG, JPG, GIF and WebP are sent as they are; BMP, TIFF, EMF and WMF are converted
  first; PDF files are not read. The window says before you start how many requests it will make and how many files it
  matched. **Describe from the text only** now also works without any images: it writes one row for each figure the
  text describes. When the documents do hold images, nothing changes.

and under **Fixed**:

- **A description run that failed could wipe out your figures.md.** If every request failed – a wrong API key, no
  network – *Describe images with AI* still replaced figures.md, with a table saying "not analysed" in every row; any
  corrections you had made to the file were gone. A run in which no image could be described now leaves the file
  exactly as it was and tells you why, in the FigureLens window itself. The messages used to go only to the Batch
  Operations log, which is behind the window.
- **A description run can be stopped.** While it runs, the button reads **Stop**. A stopped run saves nothing and
  leaves an existing figures.md as it was.
- **An image too large for the AI provider is scaled down before it is sent**, instead of being sent whole and
  refused. Your file is not changed. A file over 50 MB, an empty file, and a file that is not really an image
  (whatever its name says) are skipped with the reason in their row, before any request is paid for.
- **A wrongly chosen folder no longer slows the FigureLens window.** It used to list every file in the folder each
  time it refreshed. It now stops counting at 200 image files.

## For the Help page

### When the images are not in the document

- **When this applies**: the project's Word documents hold no images at all. FigureLens says so under *Your
  documents*: "No images in the 1 document of this project. The 4 image files in the folder below will be used."
- **If the documents do hold images**, FigureLens works from those, as before, and does not look at other files in
  the folder. It is one or the other, never both.
- **Choose the folder**: under step 1, **Already have the images in a folder? Choose it…** (later: **Change…**).
  Step 1's button stays grey: there is nothing to extract, and nothing needs extracting.
- **Which files are used**:
  - image files in the folder and in its sub-folders one level down;
  - PNG, JPG, GIF, WebP: sent as they are;
  - BMP, TIFF, EMF, WMF: converted to PNG first (in memory; your file is not changed). Of a TIFF with several
    pages only the first is used, and its row says so;
  - PDF, Word and other files are not used. The folder line says how many: "(4 image files; 1 other file not used:
    PDF and other formats are not read)". To use a drawing that is in a PDF, save its pages as PNG first.
- **How a file gets its figure number: from its name, never from the picture.**
  - `Figure 3.png`, `fig3.jpg`, `FIG-3.png`, `Figuur 3.png`, `03.png` → figure 3
  - `PROJ-001 Fig. 3A (amended).png` → figure 3A: the word and number can be anywhere in the name
  - `Fig 1-2.png`, `Figs 3 and 4.png`, `Figures 5A-5C.png` → one file holding several figures; it is sent once, with
    the text for each of them
  - `Figure 1 - 13 Nov.png`, `Figure 3 (2).png` → figure 1, figure 3: a second number only counts where it cannot be
    anything else
  - `scan0042.png`, `page 2.png` → no figure number. The file is still described, and its row in figures.md reads "no
    figure number in the file name". Rename the file (`Figure 2.png`) and run again to give it one.
- **What is sent with each file**: the figure label and every sentence of the text that opens by naming that figure
  ("Figuur 3 toont …", "Fig. 3 is …"). Sentences that mention the figure further on ("In figuur 3 is …") are not
  picked up. A letter is ignored when looking the text up: `Figure 3A.png` gets the sentences for figure 3, 3A and 3B.
- **What the window tells you before you start** (beside the button): "4 AI requests to Claude, one per image file in
  the folder. 3 matched to a figure by file name; 1 with no figure number in the name will be described without one.
  1 figure in the text has no file."
- **Cost**: one request per image file.
  - more than 25 files: FigureLens asks first, naming the number and the folder;
  - more than 200: it refuses, and asks for a folder that holds only this project's drawings;
  - it always asks before replacing a figures.md that already exists.
- **In figures.md**:
  - a line under the heading says the images came from the folder and that figure numbers come from file names;
  - two files that give the same figure are both described, and each names the other;
  - a file for a figure the text never describes says so;
  - a figure the text describes that has no file is listed with its text and "no image file for this figure".

### Describe from the text only

- Needs no images. With no images in the documents it writes one row for each figure the text describes, with the
  matching file from the folder where there is one.
- If the text describes no figures (no sentence opens with "Figure 1 …" or "Figuur 1 …"), the button is grey and says
  so.

### Stopping, and runs that fail

- While a run is on, **Describe images with AI** reads **Stop**. A stopped run saves nothing.
- If no image could be described (wrong API key, no network), nothing is saved, and a red line in the window gives
  the reason the provider gave for the first image.
- In both cases a figures.md that was already there is left exactly as it was.
- If only some images fail, the file is written; the failed rows say "not analysed" and why.

### Limits

- 200 image files per run.
- A file over 50 MB is skipped.
- An image larger than about 3.5 MB, or wider or taller than 7,900 pixels, is scaled down before sending (to at most
  2,200 pixels on its long side). A WebP image that large cannot be scaled here: save it as PNG or JPG.

## Scale

Tested at 4, 200 and 201 image files, and at a folder of 5,050 other files. Designed for up to 200 image files per
run, one at a time, with one image in memory; and for a wrongly chosen folder of 100,000 files, where the window
stops looking after 5,000 entries or 201 image files instead of listing everything.

## Not done, on purpose

- **Reading the figure number off the drawing** for a file whose name gives none (second round; it changes the
  question put to the AI).
- **PDF drawings** (needs a PDF library).
- **Step 1 can overwrite your own files**: extracting into a folder that already holds your own `Figure 01.png`
  replaces it. Unchanged by this work.
- **Supervertaler for memoQ** has the same gap in its FigureLens window.
