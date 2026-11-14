# DiffWeave Core ↔ App contract

Owner: CORE. APP requests changes in `docs/CONTRACT-REQUESTS.md`; answers and API changes are recorded in the
changelog at the end of this file.

Status legend: **ready** = implemented and tested, **stub** = signature final, body throws `NotImplementedException`
until implemented (see changelog).

All line numbers, rows, region and pane indexes are **0-based**. Lines never contain terminators.
Everything in Core is UI-free. Unless stated otherwise, types are immutable and thread-safe; `MergeSession` is the
exception (single-threaded, use it on the UI thread).

## 1. `DiffWeave.Core.Text` (ready)

| API | Notes |
| --- | --- |
| `TextDocument.Load(path, Encoding? forced = null)`, `LoadAsync(...)`, `FromBytes(bytes, forced)`, `FromString(text, enc, bom)`, `FromLines(lines, eol, finalNewline, enc, bom)` | Detection order: BOM (UTF-8/16/32) → valid UTF-8 → BOM-less UTF-16 heuristic → Latin-1 fallback. |
| `Lines`, `LineCount`, `GetLineEnding(i)`, `LineEndingsList`, `DominantLineEnding`, `HasMixedLineEndings`, `HasFinalNewline`, `Encoding`, `HasBom` | `"a\n"` = 1 line; `""` = 0 lines; only the last line may have `LineEnding.None`. |
| `ToText()`, `ToBytes()`, `Save(path)`, `SaveAsync(path)` | Exact byte round-trip (EOLs per line, BOM, missing final newline). Save writes a temp file then renames. |
| `WithLines(lines)` | New content keeping encoding, BOM, dominant EOL and final-newline state → use this to save merge output. |
| `EncodingDetector.Detect(bytes)`, `LooksBinary(bytes)` | |
| `LineEnding { None, LF, CRLF, CR }`, `LineEndings.Split(text, endings)`, `ToText()` | |

## 2. `DiffWeave.Core.Diff`

| API | Status |
| --- | --- |
| `DiffOptions { Algorithm (Myers/Patience/Histogram), Whitespace (None/IgnoreTrailing/IgnoreAmount/IgnoreAll), IgnoreCase, IgnoreBlankLines, Minimal }` (record, `DiffOptions.Default`) | ready |
| `LineDiffer.Diff(a, b, options, ct) → DiffResult { Hunks, LineCountA, LineCountB, AreEqual, EditCount }` | stub |
| `DiffHunk(StartA, CountA, StartB, CountB, IsIgnorable)`; `IsIgnorable` = blank-line-only hunk with `IgnoreBlankLines` | ready |
| `LineDiffer.Normalize / LinesEqual / EqualIgnoringWhitespace / IsBlank` | stub |
| `InlineDiffer.Diff(left, right, InlineDiffOptions { Granularity Word/Character, Whitespace, IgnoreCase, MergeGap }) → InlineDiffResult { Left, Right : TextRange[] }` | stub |
| `LineRange(Start, Count)`, `TextRange(Start, Length)` | ready |
| `LineAlignment` (abstract): `PaneCount`, `RowCount`, `GetLine(row, pane)` (−1 = filler), `GetRowKind(row)`, `GetRow(pane, line)`, `GetNearestLine(row, pane)`, `GetLineCount(pane)`, `DifferenceGroups`, `FindGroupAt(row)`, `FindNextGroup(row, skipWs)`, `FindPreviousGroup(row, skipWs)` | ready |
| `RowKind { Equal, Changed, Added, Removed, Conflict, WhitespaceOnly }` | ready |
| `DifferenceGroup(Index, StartRow, RowCount, Kind, IsWhitespaceOnly)` | ready |
| `TwoWayAlignment.Create(a, b, options, ct)`: panes `PaneA=0`, `PaneB=1`; `.Diff`, `.LinesA`, `.LinesB` | stub |
| `ThreeWayAlignment.Create(base, left, right, options, ct)`: panes `BasePane=0`, `LeftPane=1`, `RightPane=2`; `GetChange(row) : ThreeWayChange`, `GetChunkIndex(row)`, `Chunks` | stub |
| `Diff3.Compute(base, left, right, options, ct) → Diff3Chunk(Change, Base, Left, Right)[]` | stub |

Two-way row kinds: `Added` = only in B (filler in A), `Removed` = only in A, `Changed` = paired lines that differ,
`WhitespaceOnly` = paired lines that differ only in whitespace, blank added/removed lines, lines equal only thanks
to whitespace/case options, rows of ignorable hunks. Three-way: `Changed` (one side, or both identically),
`Conflict`, `WhitespaceOnly`. Inline highlighting is computed on demand by the UI per visible changed row with
`InlineDiffer.Diff(lineA, lineB)`.

## 3. `DiffWeave.Core.Merge`

`MergeSession.Create(base, left, right, MergeOptions? options, ct)` (stub until v2) — then:

| Member | Notes |
| --- | --- |
| `Regions : MergeRegion(Index, Kind, Base, Left, Right, IsWhitespaceOnlyConflict)` | immutable, covers all lines; `RegionKind { Stable, LeftChanged, RightChanged, BothChangedSame, Conflict }` |
| `GetResolution(region) : RegionResolution(Choice, CustomLines)` | `ResolutionChoice { Unresolved, Base, Left, Right, LeftThenRight, RightThenLeft, Custom }`. Stable regions start as `Base`, others `Unresolved`. |
| `AutoResolve() : int` | KDiff3 semantics, one undo step; App calls it right after `Create` (unless the user disabled auto-merge). |
| `Resolve(region, choice)`, `ResolveCustom(region, lines)`, `Unresolve(region)` | one undo step each |
| `ApplyManualEdit(outputStart, removedCount, insertedLines) : int[]` | hand edits of the OUTPUT pane; mapping rules in XML doc |
| `ApplyProposals(proposals) : int` | accepted assistant proposals → `Custom`, one undo step |
| `BeginBatch() : IDisposable` | group operations into one undo step |
| `Undo()`, `Redo()`, `CanUndo`, `CanRedo` | |
| `BuildOutput() : MergeOutput { Lines, Alignment : MergeAlignment, GetRegionOfLine(line), GetOutputRange(region) }` | cached until next change |
| `MergeAlignment` | 4 panes: `BasePane=0, LeftPane=1, RightPane=2, OutputPane=3`; `GetRegion(row)`, `GetFirstRow(region)`, `GetRowCount(region)`, `IsUnresolvedPlaceholder(row)` (draw "unresolved" text in the output cell) |
| `ConflictCount`, `UnresolvedCount`, `UnresolvedConflictCount`, `FindNextUnresolvedConflict(region)`, `FindPreviousUnresolvedConflict(region)` | |
| `Changed : EventHandler<MergeChangedEventArgs(Kind, Regions)>` | after every change incl. undo/redo |
| `ExportWithConflictMarkers(ConflictMarkerStyle.Merge/Diff3, ConflictMarkerLabels)` | git-compatible markers for unresolved regions |
| `CreateSnapshot(baseName, leftName, rightName, outputName, languageId) : MergeSnapshot` | for plugins |

`MergeOptions { Diff : DiffOptions, AutoSolveWhitespaceConflicts, WhitespaceConflictPreference (Left/Right) }`.

Saving the merge result: `outputDoc = leftDoc.WithLines(session.BuildOutput().Lines)` (or base's doc, per the user's
EOL/encoding preference) then `outputDoc.Save(path)`.

## 4. `DiffWeave.Core.Directories`

`DirectoryComparer.CompareAsync(roots (2 or 3), DirectoryCompareOptions?, IProgress<DirectoryCompareProgress>?, ct)
→ DirectoryComparisonResult { Roots, Root : DirectoryComparisonNode }` (stub).
Node: `Name`, `RelativePath` ('/'-separated), `Sides : DirectoryEntrySide(Kind, FullPath, Size, LastWriteTimeUtc, LinkTarget)[]`,
`Status : EntryStatus { Identical, Different, OnlyInA, OnlyInB, OnlyInC, MissingInA, MissingInB, MissingInC, TypeMismatch, Error }`,
`EqualSides : SidesEqual flags (AB, AC, BC)`, `Children` (directories first, then name), `IsDirectory`, `Error`.
Options: `Recursive`, `IgnorePatterns` (globs), `Mode` (SizeAndTime / Content / both), `Symlinks` (CompareTargets/Follow/Skip),
`TimeTolerance`, `IgnoreCaseInNames`, `MaxParallelism`. `FilesEqualAsync(a, b, options)`.

## 5. Extensibility (`DiffWeave.Extensibility`, API version `ExtensibilityApi.ApiVersion = "1.0"`) — ready

Plugin model: `IDiffWeavePlugin { Initialize(IPluginHost), Shutdown() }`, `PluginManifest` (plugin.json:
`id, name, version, apiVersion, entryAssembly, entryType, description, authors`), `ApiVersionRange` ("1.0" = [1.0,2.0),
interval notation), `PluginInfo(Manifest, Directory, State, Error)`, `PluginState`.
`IPluginHost { Plugin, HostApiVersion, Services, Commands, MergeAssistants, EditorExtensions, LifecycleHooks, FileTypes, Settings, Logger }`.

Contracts the **App implements**: `IEditorContext` (active document: `Kind`, `PanePaths`, `LanguageId`,
`GetPaneLines(pane)`, `GetMergeSnapshot()`, `OfferProposalsAsync(result, sourceName, ct)` → preview/accept/reject,
`ShowMessage`). Pass it in `CommandContext(editor)` when executing commands.

Contracts the **App consumes** (via `DiffWeave.Core.Plugins.PluginManager`, ready):

```csharp
var plugins = new PluginManager(new PluginManagerOptions {
    PluginDirectories = [userPluginDir, appPluginDir],
    Settings = JsonSettingsStore.Load(settingsPath) });
plugins.LoadAll();                               // never throws for plugin failures
plugins.Plugins                                  // PluginInfo list for a "Plugins" dialog (state + error)
plugins.Commands.Commands / .Changed             // CommandContribution: Id, Title, MenuPath ("Tools/Sub"), Shortcut, CanExecute, Execute
await plugins.Commands.ExecuteAsync(id, new CommandContext(editor));   // isolated: returns false on failure
plugins.MergeAssistants.Assistants / .Changed    // empty → UI shows "No merge assistants installed"
var run = await plugins.RunAssistantAsync(assistant, new MergeAssistantRequest { Snapshot = session.CreateSnapshot(...) });
// run.Result.Proposals → preview; accepted → session.ApplyProposals(accepted)  (one undo step)
await plugins.EditorExtensions.CollectDecorationsAsync(new DecorationRequest(PaneRole.Left, path, languageId, lines), ct);
await plugins.LifecycleHooks.RunBeforeSaveAsync(new MergeLifecycleContext(snapshot));    // veto → don't save
plugins.FileTypes.DetectLanguage(path, firstLine);
plugins.Log.Entries / .EntryAdded                // plugin log view
```

Decorations use theme style keys (`EditorDecoration.StyleKey`, e.g. `Decoration.Info|Warning|Error|Accent`), never colours.
The App should define brushes for those keys in its themes.

## Changelog

- **v1** — initial API surface (Text, Extensibility, Plugins: ready; Diff algorithms, alignments, merge, directories: stubs).
