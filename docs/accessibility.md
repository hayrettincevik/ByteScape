# Accessibility

Scope: keyboard use, screen readers (Narrator, NVDA, JAWS — all read WPF through UI Automation), Windows contrast themes
(high contrast) and text contrast. The summary below follows the WCAG 2.1 AA criteria that procurement usually asks about;
an Accessibility Conformance Report (VPAT) can be filled in from it.

Method: every window is opened with data and checked **through UI Automation** (the tree a screen reader sees); the
contrast of every text is measured against the colour behind it; the map is walked with the keyboard and read and selected
with a real UI Automation client (the interface screen readers use, from the window handle, on another thread). Nine
permanent tests: `ByteScape.Tests/AccessibilityTests.cs`. The tests check the information a screen reader will read, not
how a screen reader speaks it; the manual check at the end covers that.

## The map

- **Keyboard** (the map takes focus with Tab or a click; `Controls/TreemapView.cs`): ↑ / ↓ previous / next block in the
  same folder (largest first), Home / End largest / smallest, → the largest block inside the folder, ← the folder itself (at
  the top of the view it zooms out one level), Ctrl+↓ zooms into the selected folder, Ctrl+↑ zooms out, Shift+F10 or the
  menu key opens the context menu next to the block; Enter opens in Explorer, Delete deletes, Backspace goes back. When the
  selected block is not drawn (it is inside a folder drawn as one block), the map zooms into that folder: the selected block
  is always visible. The arrow keys never move focus out of the map; Tab moves on to the next control.
- **UI Automation** (`Controls/TreemapAutomation.cs`): the map is a *tree* ("Disk map"), every drawn block a *tree item*,
  nested like the folders. Each block's name is "name, kind, size, share of its folder" — e.g. "Documents, folder,
  12.4 GB, 38%"; the help text describes the keys. Blocks report their position on screen and in their set ("2 of 10");
  the selection pattern is supported (a screen reader's "select" command selects on the map). A block selected with the
  keyboard is announced with a **focus event**: the screen reader reads the new block. Summary blocks ("214 small items")
  are read but cannot be selected.

## Windows

- **No unnamed controls**: icon-only tool buttons are named by the first words of their tooltip ("Back (Backspace)" →
  "Back"; the full tooltip stays as help text); text boxes, sliders, combo boxes and option buttons are linked to the label
  next to them (the name follows the translated label; `Controls/Accessibility.cs` → `A11y.LinkLabels`); search boxes,
  tabs and check boxes in lists are named.
- **List rows** are read by the text in their columns ("Google Chrome, Google LLC, 505 MB, …"), not by the row object's
  type name.
- **Decorative glyphs are not read** (`Glyph`): characters from the icon font (inside buttons, the search magnifier, tab
  icons) never reach the screen reader as meaningless characters.
- **Nothing is mouse-only**: the drive cards in the start window, the "♻ … reclaimable" and "☁ …" links in the status
  bar, the link in the About window and the colour picker's palette are buttons — reachable with Tab, chosen with Enter or
  Space.
- **No key conflicts**: when focus is on a button, Enter presses that button; in lists and combo boxes, Enter, Delete,
  Backspace and digits stay with that control.

## Contrast and high contrast

- **Text contrast (WCAG AA, 4.5:1)**: every text in every window is measured; all are at least 4.5:1 (large text at
  least 3:1), including accent buttons, status texts and safety labels.
- **Windows contrast themes** (Settings → Accessibility → Contrast themes): while a theme is on, ByteScape uses the
  theme's system colours instead of its own (`ThemeManager`): surfaces in the window colour, text and borders in the window
  text colour, selections in the highlight colour with highlight text; it follows a theme change at once. The map is drawn
  the same way: blocks in the window colour, borders and names in the text colour, the selected block in the highlight
  colour; blinking and hover shading are off; meaningful tints (comparison, program highlight) stay, and the text on them
  takes the most readable system colour. Tested: every text in every window is drawn with one of the theme's colour pairs,
  and the map is painted with system colours.

## WCAG 2.1 AA summary

| Criterion | Status | How |
|---|---|---|
| 1.1.1 Non-text content | Supports | Icon buttons are named; decorative glyphs hidden; map blocks are named with text |
| 1.3.1 Info and relationships | Supports | Labels linked to inputs; the map tree nests like the folders; list columns |
| 1.4.1 Use of colour | Supports | Safety level, trend, stale project: text next to the colour; block kind in its name and the details panel |
| 1.4.3 Contrast (minimum) | Supports | Every text ≥ 4.5:1 (large text ≥ 3:1), measured by a test |
| 1.4.4 Resize text | Partially | Display scaling (100–200%) works and the map's font size is adjustable; WPF does not follow Windows' "Make text bigger" setting |
| 1.4.11 Non-text contrast | Partially | Full in contrast themes; in the normal theme the map's block borders are adjustable (Settings → border contrast) |
| 2.1.1 Keyboard | Supports | Every function including the map works with the keyboard; a path box and Browse replace dropping files |
| 2.1.2 No keyboard trap | Supports | Arrow keys stay in the map, Tab / Shift+Tab leave it |
| 2.4.3 Focus order | Supports | WPF's visual order |
| 2.4.7 Focus visible | Supports | WPF focus rectangle; the map selection has a thick frame; drive cards and palette squares a thick border |
| 4.1.2 Name, role, value | Supports | Every control has a name and a role (UI Automation test); map: tree / tree item, selection pattern |
| Windows contrast themes | Supports | System colours, the map included |

**Known limits**: the lines of the history and forecast charts are not offered to screen readers separately (the same
values are read in the list next to them); the map's block colours do not tell file kinds apart for a colour-blind user
(the kind is written in the block's name and in the details panel); the first-start tour is keyboard-driven (→ / Enter /
← / Esc) but its card is not announced as a dialog.

## Five-minute manual check (recommended before each release)

1. Turn on Narrator with Ctrl+Win+Enter; start ByteScape; in the start window, Tab to a drive card (the drive's name,
   kind and free space should be read), press Enter, then "Scan".
2. Tab to the map: "Disk map, tree" and the help text. ↓: the largest block should be read ("…, folder, 12.4 GB, 38%").
   Move with → / ← / ↑ / ↓; Ctrl+↓ enters a folder; Shift+F10 opens the menu.
3. Tab through the toolbar: every button should be read by its name ("Back", "Cleanup"…), never as "E72B" or similar.
4. Settings → Accessibility → Contrast themes → "Desert" or "Night sky": with ByteScape open, its colours should switch to
   the theme at once.
