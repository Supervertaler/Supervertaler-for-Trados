# SuperSearch

{% hint style="info" %}
You are viewing help for **Supervertaler for Trados** -- the Trados Studio plugin. Looking for help with the standalone app? Visit [Supervertaler Workbench help](https://help.supervertaler.com).
{% endhint %}

SuperSearch is a cross-file search and replace tool that lets you find text across **all SDLXLIFF files** in your Trados project -- not just the file you currently have open. It lives in its own dockable panel, so you can keep it visible while you translate.

Matching text is highlighted in yellow in the results grid, making it easy to spot exactly where the search term appears in each segment.

## Opening the Panel

There are three ways to open SuperSearch:

| Method | Description |
| ------ | ----------- |
| **View menu** | Go to **View > Supervertaler SuperSearch** |
| **Right-click** | Right-click in the editor and choose **SuperSearch** from the context menu |
| **Keyboard** | Press **Alt+S** |

The panel docks at the bottom of the editor by default, but you can drag it anywhere -- left, right, floating, or even to a second monitor. Trados remembers the position between sessions.

{% hint style="info" %}
**Quick search from the editor:** Select a word or phrase in the source or target segment, then press **Alt+S** (or right-click > **SuperSearch**). The selected text is automatically entered in the search box and the search runs immediately.
{% endhint %}

## Searching

Type your search query in the text box and press **Enter** (or click **Search**).

### Search Options

| Option | Description |
| ------ | ----------- |
| **Scope** dropdown | Choose *Source & Target* (default), *Source only*, or *Target only* |
| **Aa** checkbox | Case-sensitive search -- when unchecked, "Hello" matches "hello", "HELLO", etc. |
| **.\*** checkbox | Treat the query as a regular expression (see [Regex tips](#regex-tips) below) |

SuperSearch scans every `.sdlxliff` file in your project folder and displays all matching segments in the results grid. The status bar shows the number of results, the number of files searched, and how long the search took.

### Results Grid

Each row shows one matching segment:

| Column | Description |
| ------ | ----------- |
| **File** | The file name (hover for the full path) |
| **#** | Segment number within the file |
| **Source** | Source text -- matching text is highlighted in yellow |
| **Target** | Target text -- matching text is highlighted in yellow |
| **Status** | Confirmation status (Not Translated, Draft, Translated, Translation Approved, Signed Off, etc.) |

## File Selection

By default, SuperSearch searches all SDLXLIFF files in the project. The **Files** button in the search bar shows how many files are included:

- **Files (16)** -- all 16 files in the project are included
- **Files (12/16)** -- 12 out of 16 files are included (4 excluded)

Click the **Files** button to open the file selection dialog:

1. A list shows all SDLXLIFF files in the project with checkboxes
2. **Check** the files you want to include in the search
3. **Uncheck** files you want to exclude
4. Use **Select All** or **Select None** to quickly toggle everything
5. Click **OK** to apply

{% hint style="info" %}
File selection persists for the current session. When you switch to a different project, all files are included again by default.
{% endhint %}

## Navigating to a Segment

**Double-click** a row (or select it and press **Enter**) to jump to that segment in the editor.

- If the segment is in the **currently active file**, Trados navigates to it directly.
- If the segment is in a **different file**, SuperSearch attempts to switch to that file and navigate to the segment. If the file is not loaded in the editor, you may need to open it first.

## Find & Replace

Tick the **Replace** checkbox to reveal the replace bar. Replace always operates on **target text only** -- source text is never modified.

| Action | Description |
| ------ | ----------- |
| **Replace** | Replaces the match in the currently selected result. The segment must be in the active file -- double-click it first to navigate there. |
| **Replace All** | Replaces all target matches across all files. A confirmation dialog shows how many segments in how many files will be affected. |

### How Replace All works

- For the **active file**: changes go through the Trados API, so they appear immediately and are tracked in Trados's undo history.
- For **other files**: the SDLXLIFF XML is modified directly on disk. You need to reopen those files to see the changes.

{% hint style="warning" %}
**Replace All cannot be undone** for files modified on disk. Always review the search results carefully before replacing. Consider saving your project first.
{% endhint %}

{% hint style="info" %}
Replace respects the same **Aa** (case sensitivity) and **.\*** (regex) settings as search. When using regex, you can use capture groups in the replacement (e.g., `$1`, `$2`).
{% endhint %}

## Regex Tips

When the **.\*** checkbox is enabled, the search query is treated as a .NET regular expression. Some useful patterns:

| Pattern | Matches |
| ------- | ------- |
| `\bword\b` | "word" as a whole word (not "keyword" or "wording") |
| `(word1\|word2)` | Either "word1" or "word2" |
| `\d+` | One or more digits |
| `"[^"]*"` | Anything inside double quotes |
| `\s{2,}` | Two or more consecutive whitespace characters |

{% hint style="info" %}
Regex replace supports capture groups. For example, search for `(\w+)\s+(\w+)` and replace with `$2 $1` to swap two words.
{% endhint %}

## Keyboard Shortcuts

| Shortcut | Action |
| -------- | ------ |
| **Alt+S** | Open SuperSearch (with selected text, if any) |
| **Enter** (in search box) | Start search |
| **Enter** (in results grid) | Navigate to selected segment |
| **Double-click** (result row) | Navigate to selected segment |

## Tips

- Select a term in the editor and press **Alt+S** to instantly search for it across the entire project.
- Use **Source only** scope to find segments where a particular term appears, then check how it was translated across files.
- Use **Target only** scope with Replace to fix a consistent mistranslation across the entire project.
- Use the **Files** button to limit the search to specific files -- useful in large projects where you only want to search a subset.
- The status bar shows the number of results, the file count, and the search time in milliseconds.
- You can resize columns by dragging the column header borders.

## See Also

- [Supervertaler Assistant](ai-assistant.md) -- AI-powered chat and context
- [Batch Operations](batch-operations.md) -- Batch translate and proofread
- [Keyboard Shortcuts](keyboard-shortcuts.md) -- All shortcuts in one place
