using DiffWeave.Core.Diff;
using DiffWeave.Extensibility;

namespace DiffWeave.Core.Merge;

/// <summary>
/// A three-way merge: the immutable list of regions computed from base, left and right, plus the mutable resolution
/// state of every region with undo/redo. Not thread-safe: use it from one thread (the UI thread); the
/// <see cref="MergeOutput"/> and snapshots it hands out are immutable and may be used anywhere.
/// </summary>
/// <remarks>
/// <para>Stable regions start resolved to <see cref="ResolutionChoice.Base"/>; all other regions start unresolved.
/// Call <see cref="AutoResolve"/> to apply KDiff3's automatic merge.</para>
/// <para>Hand edits of the output (<see cref="ApplyManualEdit"/>) are mapped back to regions: see that method.</para>
/// </remarks>
public sealed class MergeSession
{
    private MergeSession()
    {
        throw new NotImplementedException();
    }

    /// <summary>Creates a session. Runs the two diffs synchronously (use a background thread for big files).</summary>
    public static MergeSession Create(IReadOnlyList<string> baseLines, IReadOnlyList<string> left, IReadOnlyList<string> right, MergeOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    /// <summary>The options the session was created with.</summary>
    public MergeOptions Options => throw new NotImplementedException();

    /// <summary>Base lines.</summary>
    public IReadOnlyList<string> BaseLines => throw new NotImplementedException();

    /// <summary>Left lines.</summary>
    public IReadOnlyList<string> LeftLines => throw new NotImplementedException();

    /// <summary>Right lines.</summary>
    public IReadOnlyList<string> RightLines => throw new NotImplementedException();

    /// <summary>The regions, covering all input lines in order.</summary>
    public IReadOnlyList<MergeRegion> Regions => throw new NotImplementedException();

    /// <summary>Number of conflict regions (resolved or not).</summary>
    public int ConflictCount => throw new NotImplementedException();

    /// <summary>Number of unresolved regions (of any kind).</summary>
    public int UnresolvedCount => throw new NotImplementedException();

    /// <summary>Number of unresolved conflict regions.</summary>
    public int UnresolvedConflictCount => throw new NotImplementedException();

    /// <summary>Whether <see cref="Undo"/> is possible.</summary>
    public bool CanUndo => throw new NotImplementedException();

    /// <summary>Whether <see cref="Redo"/> is possible.</summary>
    public bool CanRedo => throw new NotImplementedException();

    /// <summary>Raised after every change of the resolution state (including undo/redo).</summary>
#pragma warning disable CS0067 // implemented in phase 2
    public event EventHandler<MergeChangedEventArgs>? Changed;
#pragma warning restore CS0067

    /// <summary>The resolution of a region.</summary>
    public RegionResolution GetResolution(int region) => throw new NotImplementedException();

    /// <summary>The lines a region would produce with the given choice (custom: the current custom lines, or empty).</summary>
    public IReadOnlyList<string> GetLinesForChoice(int region, ResolutionChoice choice) => throw new NotImplementedException();

    /// <summary>
    /// Automatic merge with KDiff3 semantics: resolves unresolved single-sided changes to the changed side, identical
    /// changes to (the preferred side of) both, and - with <see cref="MergeOptions.AutoSolveWhitespaceConflicts"/> -
    /// whitespace-only conflicts to the preferred side. Already resolved regions are left alone. One undo step.
    /// </summary>
    /// <returns>The number of regions resolved.</returns>
    public int AutoResolve() => throw new NotImplementedException();

    /// <summary>Sets the resolution of a region (one undo step). <see cref="ResolutionChoice.Custom"/> requires <see cref="ResolveCustom"/>.</summary>
    public void Resolve(int region, ResolutionChoice choice) => throw new NotImplementedException();

    /// <summary>Resolves a region to custom lines (one undo step).</summary>
    public void ResolveCustom(int region, IReadOnlyList<string> lines) => throw new NotImplementedException();

    /// <summary>Resets a region to unresolved (one undo step). Stable regions revert to <see cref="ResolutionChoice.Base"/>.</summary>
    public void Unresolve(int region) => throw new NotImplementedException();

    /// <summary>
    /// Applies a hand edit of the output: lines <c>[outputStart, outputStart+removedCount)</c> of the current output
    /// are replaced by <paramref name="insertedLines"/>. One undo step.
    /// </summary>
    /// <remarks>
    /// Mapping rules: the affected regions are those whose output range intersects the edited range. A pure
    /// insertion (<paramref name="removedCount"/> = 0) at position p goes to an unresolved region positioned at p if
    /// there is one, otherwise to the region owning line p-1 (appending), otherwise (p = 0) to the region owning
    /// line 0. The first affected region becomes <see cref="ResolutionChoice.Custom"/> with its unedited prefix, the
    /// inserted lines and the unedited suffix of the last affected region; the other affected regions become
    /// <see cref="ResolutionChoice.Custom"/> with no lines.
    /// </remarks>
    /// <returns>The indexes of the affected regions.</returns>
    public IReadOnlyList<int> ApplyManualEdit(int outputStart, int removedCount, IReadOnlyList<string> insertedLines) => throw new NotImplementedException();

    /// <summary>
    /// Applies accepted assistant proposals as custom resolutions in one undo step. Proposals for unknown regions are
    /// ignored. Returns the number applied.
    /// </summary>
    public int ApplyProposals(IEnumerable<ResolutionProposal> proposals) => throw new NotImplementedException();

    /// <summary>
    /// Groups all operations until the returned object is disposed into a single undo step (nesting allowed).
    /// <see cref="Changed"/> is raised once at the end with <see cref="MergeChangeKind.Batch"/>.
    /// </summary>
    public IDisposable BeginBatch() => throw new NotImplementedException();

    /// <summary>Undoes the last operation.</summary>
    public bool Undo() => throw new NotImplementedException();

    /// <summary>Redoes the last undone operation.</summary>
    public bool Redo() => throw new NotImplementedException();

    /// <summary>The current output (cached until the next change).</summary>
    public MergeOutput BuildOutput() => throw new NotImplementedException();

    /// <summary>The next unresolved conflict after <paramref name="region"/> (-1 to search from the start), or -1.</summary>
    public int FindNextUnresolvedConflict(int region) => throw new NotImplementedException();

    /// <summary>The previous unresolved conflict before <paramref name="region"/>, or -1.</summary>
    public int FindPreviousUnresolvedConflict(int region) => throw new NotImplementedException();

    /// <summary>
    /// The output with git-style conflict markers around unresolved conflicts (and unresolved changes, which are
    /// written as conflicts too). Resolved regions are written as resolved.
    /// </summary>
    public IReadOnlyList<string> ExportWithConflictMarkers(ConflictMarkerStyle style = ConflictMarkerStyle.Merge, ConflictMarkerLabels? labels = null)
        => throw new NotImplementedException();

    /// <summary>Creates the immutable snapshot handed to plugins.</summary>
    public MergeSnapshot CreateSnapshot(string? baseName = null, string? leftName = null, string? rightName = null, string? outputName = null, string languageId = "plaintext")
        => throw new NotImplementedException();
}
