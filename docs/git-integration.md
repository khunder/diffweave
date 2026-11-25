# Using DiffWeave with git

DiffWeave accepts the same command line as KDiff3, so it works as `git difftool` and `git mergetool`.
The exit code follows git's `trustExitCode` convention:

| Exit code | Meaning |
| --- | --- |
| `0` | The merge is fully resolved **and** saved (or a comparison window was closed normally). |
| `1` | The merge was cancelled, left with unresolved conflicts, or not saved. git keeps the file conflicted. |
| `2` | Invalid command line or unreadable input. |

Make sure `diffweave` is on your `PATH` (or use the absolute path to the executable in the commands below).

## Merge tool

```sh
git config --global merge.tool diffweave
git config --global mergetool.diffweave.cmd \
  'diffweave --auto --L1 "$MERGED (Base)" --L2 "$MERGED (Local)" --L3 "$MERGED (Remote)" -o "$MERGED" "$BASE" "$LOCAL" "$REMOTE"'
git config --global mergetool.diffweave.trustExitCode true
# optional: do not keep *.orig backups
git config --global mergetool.keepBackup false
```

The resulting `~/.gitconfig` section:

```ini
[merge]
    tool = diffweave
[mergetool "diffweave"]
    cmd = diffweave --auto --L1 \"$MERGED (Base)\" --L2 \"$MERGED (Local)\" --L3 \"$MERGED (Remote)\" -o \"$MERGED\" \"$BASE\" \"$LOCAL\" \"$REMOTE\"
    trustExitCode = true
```

What the arguments mean:

* `$BASE`, `$LOCAL`, `$REMOTE` become panes **A** (common ancestor), **B** (your side) and **C** (the branch being
  merged). `$MERGED` is the file in your working tree; `-o "$MERGED"` writes the result there.
* `--L1/--L2/--L3` label the panes (git's temporary file names are not very readable).
* `--auto` first merges without showing a window. When every change merges cleanly (one-sided changes, identical
  changes on both sides), the result is written and DiffWeave exits with `0` immediately. When real conflicts remain,
  the window opens with the first unresolved conflict selected.
* Add `--qall` if you never want a window from a scripted run: unresolved conflicts then exit with `1`.

`$BASE` may be missing (add/add conflicts); DiffWeave then shows an empty base pane.

### Walkthrough: `git mergetool --tool=diffweave`

```sh
git merge feature            # CONFLICT (content): Merge conflict in Stock.cs
git mergetool --tool=diffweave
```

1. git starts DiffWeave with the four files. Auto-merge has already taken every non-conflicting change; the status
   bar shows e.g. `2 conflicts, 2 unresolved` and the output pane (bottom) shows an *unresolved conflict*
   placeholder where a decision is needed.
2. For each conflict choose a side: **Ctrl+1 / Ctrl+2 / Ctrl+3** (or **A / B / C** while the focus is in an input
   pane, or the toolbar buttons) take A, B or C; *B+C* / *C+B* take both sides. **Ctrl+PgDn / Ctrl+PgUp** jump to the
   next / previous unresolved conflict, **Ctrl+Down / Ctrl+Up** to the next / previous difference.
3. Edit the output pane freely if needed: typing, cut/copy/paste and **Ctrl+Z / Ctrl+Y** all work, and hand edits
   share one undo history with the A/B/C choices.
4. **Ctrl+S** saves (encoding, BOM and line endings of the local file are preserved) and the window can be closed.
5. DiffWeave exits with `0`, so git marks `Stock.cs` as resolved; finish with `git commit`.

Closing without saving (or saving while conflicts are still unresolved, which writes git-style conflict markers)
exits with `1`; git then reports the merge as failed and keeps the file in the conflicted state.

## Diff tool

```sh
git config --global diff.tool diffweave
git config --global difftool.diffweave.cmd 'diffweave --L1 "$LOCAL" --L2 "$REMOTE" "$LOCAL" "$REMOTE"'
git config --global difftool.prompt false

git difftool HEAD~1 -- src/   # one window per changed file
git difftool --dir-diff HEAD~1 # compare whole trees in the directory view
```

## Drop-in replacement for KDiff3

DiffWeave accepts KDiff3's command line, so anything configured for KDiff3 can call DiffWeave instead:

* **git's built-in `kdiff3` driver**: keep `merge.tool=kdiff3` / `diff.tool=kdiff3` and point the path at DiffWeave:

  ```sh
  git config --global mergetool.kdiff3.path /path/to/diffweave
  git config --global difftool.kdiff3.path /path/to/diffweave
  ```

  git then runs `--auto --L1 … --L2 … [--L3 …] -o "$MERGED" [$BASE] $LOCAL $REMOTE` for merges (two inputs when the
  base is missing) and `--L1 … --L2 … $LOCAL $REMOTE` for diffs; `/dev/null` (the missing side of an added or deleted
  file) shows as an empty pane. `git difftool -d` works too: the working-tree symlinks git creates are followed and
  compared by content.
* **Git Extensions**: use the KDiff3 argument layout with the DiffWeave executable as the path:

  | Setting | Value |
  | --- | --- |
  | Merge tool command | `/path/to/diffweave "$BASE" "$LOCAL" "$REMOTE" -o "$MERGED"` |
  | Diff tool command | `/path/to/diffweave "$LOCAL" "$REMOTE"` |

  Keep `mergetool.<tool>.trustExitCode = true`: closing without saving a fully resolved merge exits `1`, so the
  file stays conflicted.

Other KDiff3 options are accepted for compatibility: `--noauto` and `--auto-none` (the window opens, which is the
default anyway), and `--cs VALUE`, `--config FILE`, `--confighelp`, `--query` and `-u` (ignored with a warning). One
difference: in DiffWeave `--qall` means "never open the window from `--auto`".

If DiffWeave is a framework-dependent build and the .NET runtime is in a private folder, `DOTNET_ROOT` must be set in
the environment Git Extensions runs in, or use a self-contained build
(`dotnet publish src/DiffWeave.App -c Release -r linux-x64 --self-contained -o out/diffweave`).

## Command line reference

```
diffweave [OPTIONS] [FILE_A [FILE_B [FILE_C]]]
diffweave [OPTIONS] DIR_A DIR_B [DIR_C]

-m, --merge            Merge the inputs (without -o the output file is chosen when saving)
-o, --output FILE      Write the merge result to FILE (implies --merge); also --out
-b, --base FILE        Use FILE as the common base (A); the positional files become B and C
    --L1/--L2/--L3 LABEL   Pane labels for A, B, C
    --fname LABEL      Label for the next unlabelled input (repeatable); -L is accepted too
    --auto             Merge without UI when nothing remains unresolved, write the output, exit 0
    --qall             Quiet: with --auto never open the window (exit 1 if conflicts remain)
-h, --help             Help
    --version          Version
```

One file only (`diffweave notes.txt`) compares it against an empty second pane.

## How this is tested

`tests/DiffWeave.App.Tests/Cli/GitMergetoolIntegrationTests.cs` creates a temporary repository, produces a real
conflicted `git merge`, configures `merge.tool`/`mergetool.diffweave.cmd`/`trustExitCode` exactly as above, and runs
`git mergetool --tool=diffweave` against the built `diffweave` executable:

* with `--qall`, the conflicts stay unresolved, the tool exits `1` and git keeps the file conflicted;
* with the test-only option `--test-resolve-all B` (which simulates the user choosing B for every remaining
  conflict before saving) the tool exits `0`, git marks the file resolved and the merge can be committed; the
  non-conflicting changes from the other branch are present in the result.
