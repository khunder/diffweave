# DiffWeave UI architecture

The desktop application (`src/DiffWeave.App`, assembly `diffweave`) is an Avalonia 12 app on top of the UI-free
`DiffWeave.Core`. This document describes how the UI is put together and where plugins attach.

```
Program ── CommandLineParser ──► NonInteractive (--auto, no UI)
   │
   └── App (themes, palettes) ── MainWindow ── MainWindowViewModel
                                   │              ├── Tabs: DocumentTab ─► DiffDocumentView | DirectoryView | StartPage
                                   │              ├── AssistantsViewModel ─► AssistantsPanel
                                   │              └── PluginService (core PluginManager)
                                   └── find / go-to bars, menus, toolbar, status bar
```

## Folders

| Folder | Content |
| --- | --- |
| `Cli/` | KDiff3-compatible command line parser, exit codes. |
| `Editor/` | Control-independent editor model: text sources, decorations, layers, `PaneMapping`, palette. |
| `Controls/` | `DiffTextView` (the editor), `EditorPane` (header + view + scroll bars), `DiffBand`, `OverviewRuler`, `ScrollSynchronizer`. |
| `Documents/` | Documents shown in tabs: `ComparisonDocument` (2/3-way), `MergeDocument`, `DirectoryDocument`, input files. |
| `Views/` | `MainWindow`, `DiffDocumentView`, `DirectoryView`, `AssistantsPanel`, `StartPage`. |
| `ViewModels/` | `MainWindowViewModel`, `AssistantsViewModel`, `DocumentTab`. |
| `Services/` | Settings (XDG JSON), dialogs, plugin host, app icon. |
| `Themes/` | `Palette.Standard.axaml` (light/dark theme dictionaries), `Palette.ColorBlind.axaml`, `Styles.axaml`. |

## The editor control: `DiffTextView`

A purpose-built, virtualized monospace text view. No third-party editor control is used: the available ones either
do not target Avalonia 12 or do not offer the layer and gutter hooks needed for diff/merge panes.

* **Text model** — `IEditorTextSource` (`LineCount`, `GetLine`, `Replace`, `Undo/Redo`, `Changed`). Implementations:
  `ReadOnlyTextSource` (input panes), `EditableTextSource` (tests, scratch text) and `MergeOutputSource`, which applies
  every edit through `MergeSession.ApplyManualEdit` and uses the session's undo stack.
* **Row model** — rows are lines, several rows per line with word wrap, plus *phantom rows* (placeholders such as
  "unresolved conflict" that are not text). A prefix-sum array maps lines ↔ rows; without wrap/phantoms rows are
  lines and no array is needed. Only rows intersecting the viewport are realized (`GetVisibleRows`), and per-row
  `FormattedText` layouts are cached (bounded) — a 200 000-line file renders and scrolls like a small one.
* **Layers** — `Render` builds one `EditorRenderContext` per frame and paints the ordered `Layers` collection:
  1. `BackgroundLayer` — line backgrounds by `HighlightKind`, current line, gap markers, current-region frame, phantom rows;
  2. `InlineHighlightLayer` — character ranges (word/char diff, plugin ranges: fill, underline or box);
  3. `FindMatchLayer` — all matches of the find query on visible lines;
  4. `TextLayer` — the text (tabs expanded, optional whitespace glyphs);
  5. `SelectionLayer` and 6. `CaretLayer`.
  The gutter (line numbers, change markers, decoration marks) is painted last. `Layers` is an observable collection:
  a plugin-facing host can insert additional `IEditorLayer`s.
* **Decorations** — layers do not compute anything themselves; they read a `DecorationSink` filled by every
  `IEditorDecorationSource` in `DecorationSources`, queried for the visible line range only. Sources in use:
  `AlignmentDecorationSource` (diff kinds per block, inline diff via `InlineDiffer` computed lazily and cached per line,
  gap markers), the merge output source (colour by resolution, resolved-conflict marks) and one
  `StaticDecorationSource` per pane holding plugin decorations.
* **Colours** — never hard-coded: `EditorPalette` resolves `DW.Editor.*` and `DW.Kind.{Kind}.Line|Inline|Mark`
  brushes from theme dictionaries (light/dark), and the colour-blind palette (Okabe–Ito based) overrides the
  `DW.Kind.*` keys when enabled.
* **Editing** — caret, selection (mouse drag, double-click word, triple-click/gutter line), keyboard navigation,
  clipboard, undo/redo, smart Home, auto-indent on Enter, tab size, word wrap, show whitespace, find (case, regex,
  whole word, wrap-around), go to line. Read-only panes support selection and copy.
* **Accessibility** — `DiffTextViewAutomationPeer` (control type Document), `AutomationProperties.Name` "A: file" etc. on every pane;
  all commands are reachable from the keyboard.

## Panes, bands, ruler and scrolling

Panes show only real lines (no filler lines). The Core's `LineAlignment` (`TwoWayAlignment`, `ThreeWayAlignment`,
`MergeAlignment`) is turned into a `PaneMapping`: per pane the row of each line and the number of lines before
each row, plus the list of `DiffBlock`s (one per difference group, with a line span in every pane and a highlight
kind per pane). Alignment rows are the common coordinate system:

* `ScrollSynchronizer` maps the line at the middle of the scrolled view to every other pane; horizontal offsets are
  copied. Scrolling through a block that exists only in one pane keeps the others on the insertion point.
* `DiffBand` (between adjacent panes) draws a Bézier ribbon from each block's lines on the left to its lines on the
  right; clicking selects the block.
* `OverviewRuler` draws all blocks scaled to its height, one lane per pane, plus the visible area; click/drag jumps.

`DiffDocumentView` builds the layout: `A | B` for two files, `A | B | C` for three, and for merges the inputs on top
(base hidden for two-way merges) with the editable OUTPUT pane below a splitter.

## Documents and view models

* `DiffDocument` — panes, mapping, current block, navigation; implements `IEditorContext` for plugins.
* `ComparisonDocument` — recomputes the alignment on the thread pool with the current `DiffOptions` (whitespace,
  case, blank lines). Blocks that only differ in ignored ways are not highlighted or navigated.
* `MergeDocument` — wraps `MergeSession`; choices (A/B/C, B+C, C+B, unresolve, auto-merge, resolve all), conflict
  navigation, save (template document = local input, so encoding, BOM and EOL are preserved; unresolved regions are
  written with conflict markers), lifecycle hooks, plugin snapshots. The mapping is rebuilt from the new
  `MergeAlignment` after every change.
* `DirectoryDocument` — runs `DirectoryComparer`, flattens the tree into filterable rows, copies entries.
* `MainWindowViewModel` — tabs, commands (menus, toolbar and key bindings bind to the same `RelayCommand`s),
  status bar, settings, file-change polling, unsaved-changes guard, exit code.

Threading: heavy work (diffs, directory scans, assistants, decoration providers) runs with `Task.Run`/async APIs and
continues on the UI thread through `await`; registry events raised off the UI thread are marshalled by
`PluginService`. File changes are detected by polling timestamps on a UI timer (no watcher threads).

## How plugins attach

`PluginService` creates the Core `PluginManager` with the folders `$XDG_CONFIG_HOME/diffweave/plugins` (default
`~/.config/diffweave/plugins`) and `<appdir>/plugins`, loads them at start-up and on *Plugins › Reload*.

| Extension point | UI integration |
| --- | --- |
| `ICommandRegistry` contributions | *Plugins* menu (sub-menus from `MenuPath`), optional key binding from `Shortcut` (ignored if it clashes). Executed with `CommandContext(activeDocument)`. |
| `IMergeAssistant` | Listed in the *Assistants* panel (*View › Assistants Panel*, hidden by default). "Run on current conflict" / "All unresolved" call `PluginManager.RunAssistantAsync` with a `MergeSnapshot`; proposals are shown with rationale, confidence and a preview (unified diff of current output → proposal). Accept/Accept all apply them with `MergeSession.ApplyProposals` (undoable); Reject drops them. Without assistants the panel shows "No merge assistants installed" and a link to the docs. |
| `IEditorContext.OfferProposalsAsync` | Opens the panel with the offered proposals; the task completes with the number accepted. |
| `IEditorDecorationProvider` | Collected per pane on a background thread when a document opens, when plugins change and (debounced) after output edits. `GutterMark` → gutter dot, `LineHighlight` → background layer, `InlineHighlight` → underline in the inline layer. Style keys map to theme kinds: `Decoration.Info/Warning/Error/Accent`. |
| `IMergeLifecycleHook` | `BeforeResolve`/`AfterResolve` around every A/B/C choice, `BeforeSave` before writing; a veto cancels the action and shows the hook's message in the status bar. |

No AI functionality is part of the application; the assistants panel is a neutral host for proposal-producing
plugins (see `docs/ai-extension-points.md`). The repository's sample plugin (`samples/DiffWeave.Samples.WhitespacePlugin`)
is a deterministic whitespace normalizer used by the UI tests.

## Settings

`~/.config/diffweave/settings.json` (or `$XDG_CONFIG_HOME`, or `DIFFWEAVE_CONFIG_DIR`): theme (system/light/dark),
palette (standard/colour-blind), font family and size, tab size, word wrap, whitespace glyphs, line numbers,
whitespace/case/blank-line options, last layout, recent comparisons, window size, assistants panel visibility.
Missing or corrupt files fall back to defaults.

## Tests

`tests/DiffWeave.App.Tests` (NUnit + Avalonia.Headless with Skia rendering): editor virtualization, row model,
editing/undo, clipboard, mouse, scroll synchronization, ruler/band navigation, main window commands and shortcuts,
merge choosing/editing/saving, unsaved-changes guard, ignore options, directory view, plugin host with the sample
plugin, lifecycle hooks, CLI parsing, the non-interactive path, a real `git mergetool` run, and screenshot tests that
write light and dark PNGs to `<test bin>/screenshots/`.
