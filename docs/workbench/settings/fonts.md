Font settings control the typeface and size used in the translation grid and Sidekick panels.

## What you can change

- **Font family** – any font installed on your system; the dropdown shows available fonts
- **Font size** – point size for the grid; Sidekick uses the same family at its own size
- **Global UI font scale** – a single slider that scales every UI element (menus, tabs, settings, Sidekick, Clipboard history, SuperLookup, status bar) at once

## Choosing a font

- For general translation work, a clear humanist sans-serif (Segoe UI, Inter, Calibri) keeps long sessions comfortable
- For technical or code translation, a monospaced font (Consolas, JetBrains Mono) can help align numbers and symbols
- For right-to-left languages (Arabic, Hebrew), choose a font with good RTL glyph coverage

## Global UI font scale (Retina / high-DPI displays)

Settings → AI Settings → **🖥️ Global UI Font Scale** holds a single slider (50%–200%, default 100%) that scales the entire application UI – not just the grid. Useful when you find Qt's defaults uncomfortably small on a MacBook Retina screen, a 4K monitor, or any high-DPI display.

The slider covers:

- The grid (segment numbers, type column, source and target text)
- Sidekick (Menu tree, QuickTrans, Clipboard history, SuperLookup web resources)
- Tabs, settings panels, AI tools, status bar, menus
- Termbase and TM panes

Apply the change with the **Apply** button next to the slider; most areas update immediately. Sidekick and Clipboard pick up the new size when they are next opened, so close and reopen them once after changing the slider.

If you've also customised the grid font size (above), that value still applies on top of the scale – so a 12 pt grid font at 150% renders at 18 pt. Grid zoom (Ctrl+= / Ctrl+-) continues to work at any scale.

## Tips

- Font changes apply immediately in the grid – no restart needed
- If glyphs for a specific language appear as boxes, install a font with full Unicode coverage for that script (Noto Sans is a good all-rounder)
- On a 4K or Retina display, try 125% or 150% UI scale before reaching for individual font-size sliders – it keeps every panel proportional
- The four title-bar chrome glyphs in Sidekick (⚙ – □ ×) deliberately don't scale, because they live in fixed-size buttons and scaling the glyph alone would overflow the button

## Related pages

- [View Settings](view.md)
- [Theme (Light/Dark Mode)](theme.md)
