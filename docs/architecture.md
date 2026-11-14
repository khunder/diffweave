# DiffWeave architecture

DiffWeave is split into three layers with strictly one-way dependencies:

```
┌──────────────────────────────────────────────────────────────┐
│ DiffWeave.App  (Avalonia UI, assembly "diffweave")           │  views, view models, text view control,
│                                                              │  themes, command line, plugin host UI
└──────────────┬──────────────────────────────┬────────────────┘
               │                              │
┌──────────────▼───────────────┐   ┌──────────▼─────────────────┐
│ DiffWeave.Core (UI-free)     │──▶│ DiffWeave.Extensibility    │  public plugin API,
│ Text · Diff · Merge ·        │   │ (zero dependencies,        │  versioned (ApiVersion "1.0")
│ Directories · Plugins        │   │  ExtensibilityApi)         │
└──────────────────────────────┘   └──────────▲─────────────────┘
                                              │ compile-time only
                                   ┌──────────┴─────────────────┐
                                   │ plugins (own load context) │
                                   └────────────────────────────┘
```

* **DiffWeave.Extensibility** contains only contracts and small immutable data types. It has no package or project
  dependencies so that plugins compiled against it never drag anything else in. Its major version only changes on
  breaking changes (see [extensibility.md](extensibility.md)).
* **DiffWeave.Core** contains every algorithm and has no UI dependency, so it is fully unit-testable and could back
  a command-line tool. It implements the extensibility contracts (registries, plugin loading).
* **DiffWeave.App** renders Core's models. It never compares text itself.

The contract between Core and App is documented in [CONTRACT.md](CONTRACT.md).

## Core namespaces

| Namespace | Responsibility | Main types |
| --- | --- | --- |
| `DiffWeave.Core.Text` | Reading/writing text files without losing information | `TextDocument`, `EncodingDetector`, `LineEnding` |
| `DiffWeave.Core.Diff` | Line and inline diff, side-by-side alignment | `LineDiffer`, `InlineDiffer`, `Diff3`, `TwoWayAlignment`, `ThreeWayAlignment`, `LineAlignment` |
| `DiffWeave.Core.Merge` | Three-way merge state machine | `MergeSession`, `MergeRegion`, `MergeOutput`, `MergeAlignment` |
| `DiffWeave.Core.Directories` | Two/three-way directory comparison | `DirectoryComparer`, `DirectoryComparisonNode` |
| `DiffWeave.Core.Plugins` | Plugin discovery, loading, registries, settings | `PluginManager`, `PluginLoadContext`, `MergeAssistantRunner`, `JsonSettingsStore` |

## Data flow

### Comparing two files

```
bytes ──EncodingDetector──▶ TextDocument (lines + per-line EOL + encoding + BOM)
          │
          ▼
LineDiffer.Diff(a.Lines, b.Lines, DiffOptions) ──▶ DiffResult (hunks)
          │
          ▼
TwoWayAlignment (rows: line index per pane or filler, RowKind, DifferenceGroups)
          │                                   │
          ▼                                   ▼
App: virtualized text view draws rows    InlineDiffer.Diff(lineA, lineB) on demand
     (only visible rows), bands,          for the visible Changed rows
     overview ruler from DifferenceGroups
```

`TextDocument` keeps the exact line terminators of every line, the encoding and the BOM, so saving an unmodified
document reproduces the original bytes. New content is saved with `TextDocument.WithLines`, which keeps encoding, BOM,
dominant line ending and final-newline state.

### Line diff pipeline

1. **Normalization and interning.** Each line is normalized according to `DiffOptions` (trailing / amount / all
   whitespace, case) and mapped to an integer id through one dictionary shared by both files. All further work
   compares integers.
2. **Prefix/suffix stripping.** Common leading and trailing lines are removed.
3. **Discarding unmatched lines.** Lines whose id does not occur on the other side cannot be part of any common
   subsequence; they are marked as changed and removed before the core algorithm runs. This keeps the result minimal
   but makes "completely different" files (the O(N·D) worst case) linear.
4. **Core algorithm.**
   * *Myers*: the linear-space divide-and-conquer variant (forward and backward searches meeting at the middle snake),
     iterative to avoid deep recursion. Unless `DiffOptions.Minimal` is set, a cost limit (≈ √(N+M) like git's xdiff,
     at least 256 edit steps) makes the search fall back to the furthest-reaching split, which bounds the
     run time on huge inputs with many repeated lines.
   * *Patience*: anchors on lines unique in both ranges, longest increasing subsequence of the anchors (patience
     sorting), recursion between anchors, Myers where no unique lines exist.
   * *Histogram*: git's variant of patience that anchors on the least frequent common line (occurrence count ≤ 64)
     and extends the match in both directions; Myers as fallback.
5. **Hunk building and sliding.** The changed-line markers become hunks. Pure insertions and deletions whose
   position is ambiguous (repeated lines) are slid to a canonical position: onto an adjacent hunk when possible,
   otherwise as low as possible (git's behaviour without the indent heuristic).
6. **Blank-line hunks** are flagged `IsIgnorable` when `IgnoreBlankLines` is on (GNU `diff -B` semantics).

The tests cross-check the Myers implementation against an LCS reference and against `git diff --no-index --minimal`.

### Alignment

`LineAlignment` stores one `int[]` per pane (line index or `-1` filler per row), a `RowKind[]`, and reverse maps
(line → row), so all queries are O(1) or O(log groups). Inside a hunk, lines are paired by similarity (Dice
coefficient of character bigrams, monotonic dynamic programming, for hunks up to 250 000 cells; larger hunks pair
top-down); unpaired lines between two pairs are zipped top-down so no filler row is wasted.

Difference groups are maximal runs of non-equal rows (never spanning two merge regions) and drive
next/previous-difference navigation and the overview ruler.

### Three-way comparison and merge

```
base ─┬─ LineDiffer ─▶ hunks(base→left)  ─┐
      └─ LineDiffer ─▶ hunks(base→right) ─┴─▶ Diff3.Combine ─▶ chunks ─▶ MergeSession regions
```

`Diff3.Combine` walks both hunk lists in base order and coalesces hunks that overlap **or touch** (same rule as
git's `xdl_merge`: a change ending where the other side's change starts is a conflict). A chunk changed by both sides
is `BothChangedSame` when both sides produced equal text under the diff options, otherwise `Conflict`. Conflicts whose
sides are equal when all whitespace is removed are flagged `IsWhitespaceOnlyConflict`.

`MergeSession` keeps the immutable region list and a mutable `RegionResolution` per region. Every mutation
(`Resolve`, `ResolveCustom`, `Unresolve`, `AutoResolve`, `ApplyManualEdit`, `ApplyProposals`, batches) is recorded as
one undo operation containing the before/after resolution of each touched region; undo/redo replays them. The output
(`MergeOutput`) is rebuilt lazily after a change: lines, line→region map, region→output-range map and a four-pane
`MergeAlignment` (base, left, right, output) where output lines are placed next to the lines they were taken from and
unresolved regions keep a placeholder row.

Hand edits of the output are mapped back to regions (the edited range's regions become `Custom`), so the region
model stays the single source of truth; see `MergeSession.ApplyManualEdit`.

The randomized tests verify against `git merge-file` that clean merges produce identical output and that conflicts
produce identical conflict markers (both "merge" and "diff3" styles) for well-separated edits. Note that git joins
conflicts separated by three lines or fewer into one; DiffWeave keeps them separate, like KDiff3.

### Directory comparison

`DirectoryComparer.CompareAsync` scans the 2 or 3 trees (ignore globs, symlink policy, cycle detection when following
links), queues the files present on every side, compares them in parallel (size first, then 64 KiB block comparison,
or size + time only), then aggregates statuses bottom-up into an immutable `DirectoryComparisonNode` tree.

### Plugins

See [extensibility.md](extensibility.md). In short: `PluginManager` scans plugin folders, validates `plugin.json`,
checks the API version range, loads each plugin into its own collectible `AssemblyLoadContext` (sharing
`DiffWeave.Extensibility` and `DiffWeave.Core` with the host), and hands it a per-plugin `IPluginHost` whose
registrations are tracked and removed on unload.

## Threading model

* **Immutable results** (`TextDocument`, `DiffResult`, all `LineAlignment` subclasses, `Diff3Chunk`s, `MergeOutput`,
  `MergeSnapshot`, directory trees) can be created on any thread and shared freely.
* **Long computations** (`LineDiffer.Diff`, `TwoWayAlignment.Create`, `ThreeWayAlignment.Create`,
  `MergeSession.Create`, `DirectoryComparer.CompareAsync`) are synchronous CPU work (except the directory comparer,
  which is async) and accept a `CancellationToken`. The App runs them with `Task.Run` and cancels the previous run
  when options change.
* **`MergeSession` is single-threaded**: create it on a worker thread, then use it only from the UI thread. Its
  `Changed` event fires synchronously on the thread that made the change.
* **Registries** (`CommandRegistry`, `MergeAssistantRegistry`, `EditorExtensionRegistry`,
  `MergeLifecycleHookRegistry`) are thread-safe copy-on-write lists; their `Changed` events can fire on any thread, so
  the App marshals them to the UI thread.
* **Plugin code** never runs unguarded: merge assistants and decoration providers run on the thread pool with a
  timeout; commands and hooks run on the caller's context with exceptions caught; results are validated before the
  UI sees them.

## Performance

Measured on the test suite's synthetic inputs (Release builds are faster still):

| Scenario | Result |
| --- | --- |
| 200 000 lines, 2 000 scattered edits, Myers/Patience/Histogram | well under 1 s each |
| 200 000 lines vs 200 000 completely different lines | linear (unmatched-line discarding) |
| 60 000 lines over a 50-symbol alphabet, unrelated orders | bounded by the cost heuristic |
| Two-way + three-way alignment + merge session of 200 000 lines | well under 2 s |

Memory is dominated by the lines themselves; alignments cost a few integers per row and pane.
