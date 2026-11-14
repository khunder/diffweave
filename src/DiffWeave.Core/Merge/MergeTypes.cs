using DiffWeave.Core.Diff;

namespace DiffWeave.Core.Merge;

/// <summary>The classification of a merge region.</summary>
public enum RegionKind
{
    /// <summary>Base, left and right agree; the region is always resolved to the base text.</summary>
    Stable,

    /// <summary>Only left changed the base.</summary>
    LeftChanged,

    /// <summary>Only right changed the base.</summary>
    RightChanged,

    /// <summary>Left and right changed the base identically (under the diff options).</summary>
    BothChangedSame,

    /// <summary>Left and right changed the base differently.</summary>
    Conflict,
}

/// <summary>An immutable region of a merge.</summary>
/// <param name="Index">Position in <see cref="MergeSession.Regions"/>; also the region id used by plugins.</param>
/// <param name="Kind">The classification.</param>
/// <param name="Base">Base lines of the region.</param>
/// <param name="Left">Left lines of the region.</param>
/// <param name="Right">Right lines of the region.</param>
/// <param name="IsWhitespaceOnlyConflict">For conflicts: left and right are equal when all whitespace is ignored.</param>
public sealed record MergeRegion(int Index, RegionKind Kind, LineRange Base, LineRange Left, LineRange Right, bool IsWhitespaceOnlyConflict = false)
{
    /// <summary>Whether this region is a conflict.</summary>
    public bool IsConflict => Kind == RegionKind.Conflict;

    /// <summary>Whether this region differs from the base on at least one side.</summary>
    public bool IsChange => Kind != RegionKind.Stable;
}

/// <summary>The chosen content of a region in the output.</summary>
public enum ResolutionChoice
{
    /// <summary>Not resolved yet; contributes no lines to the output (shown as a placeholder).</summary>
    Unresolved,

    /// <summary>The base lines.</summary>
    Base,

    /// <summary>The left lines.</summary>
    Left,

    /// <summary>The right lines.</summary>
    Right,

    /// <summary>Left lines followed by right lines.</summary>
    LeftThenRight,

    /// <summary>Right lines followed by left lines.</summary>
    RightThenLeft,

    /// <summary>Custom lines (hand edits or accepted proposals), see <see cref="RegionResolution.CustomLines"/>.</summary>
    Custom,
}

/// <summary>The resolution state of one region.</summary>
/// <param name="Choice">The choice.</param>
/// <param name="CustomLines">The lines for <see cref="ResolutionChoice.Custom"/>; <see langword="null"/> otherwise.</param>
public sealed record RegionResolution(ResolutionChoice Choice, IReadOnlyList<string>? CustomLines = null)
{
    /// <summary>The unresolved state.</summary>
    public static RegionResolution Unresolved { get; } = new(ResolutionChoice.Unresolved);

    /// <summary>Whether a choice has been made.</summary>
    public bool IsResolved => Choice != ResolutionChoice.Unresolved;
}

/// <summary>Which side wins when a whitespace-only conflict is solved automatically.</summary>
public enum WhitespaceConflictPreference
{
    /// <summary>Take the left text.</summary>
    Left,

    /// <summary>Take the right text.</summary>
    Right,
}

/// <summary>Options for <see cref="MergeSession"/>.</summary>
public sealed record MergeOptions
{
    /// <summary>Default options.</summary>
    public static MergeOptions Default { get; } = new();

    /// <summary>
    /// Options of the two underlying diffs (base→left, base→right) and of the left/right comparison. With
    /// whitespace or case ignored, changes that differ only in ignored ways are classified as
    /// <see cref="RegionKind.BothChangedSame"/>; auto-resolution then takes the side given by
    /// <see cref="WhitespaceConflictPreference"/>.
    /// </summary>
    public DiffOptions Diff { get; init; } = DiffOptions.Default;

    /// <summary>
    /// When true, <see cref="MergeSession.AutoResolve"/> also resolves conflicts flagged
    /// <see cref="MergeRegion.IsWhitespaceOnlyConflict"/> by taking the side given by <see cref="WhitespaceConflictPreference"/>.
    /// </summary>
    public bool AutoSolveWhitespaceConflicts { get; init; }

    /// <summary>The side taken for whitespace-only conflicts and for identical-under-options changes.</summary>
    public WhitespaceConflictPreference WhitespaceConflictPreference { get; init; } = WhitespaceConflictPreference.Left;
}

/// <summary>The kind of change reported by <see cref="MergeSession.Changed"/>.</summary>
public enum MergeChangeKind
{
    /// <summary>A region was resolved or re-resolved.</summary>
    Resolve,

    /// <summary>A region was unresolved.</summary>
    Unresolve,

    /// <summary><see cref="MergeSession.AutoResolve"/> ran.</summary>
    AutoResolve,

    /// <summary>The output was edited by hand.</summary>
    ManualEdit,

    /// <summary>Several operations grouped by <see cref="MergeSession.BeginBatch"/>, or accepted proposals.</summary>
    Batch,

    /// <summary>An operation was undone.</summary>
    Undo,

    /// <summary>An operation was redone.</summary>
    Redo,
}

/// <summary>Arguments of <see cref="MergeSession.Changed"/>.</summary>
/// <param name="kind">What happened.</param>
/// <param name="regions">Indexes of the regions whose resolution changed.</param>
public sealed class MergeChangedEventArgs(MergeChangeKind kind, IReadOnlyList<int> regions) : EventArgs
{
    /// <summary>What happened.</summary>
    public MergeChangeKind Kind { get; } = kind;

    /// <summary>Indexes of the regions whose resolution changed.</summary>
    public IReadOnlyList<int> Regions { get; } = regions;
}

/// <summary>Conflict marker flavour for <see cref="MergeSession.ExportWithConflictMarkers"/>.</summary>
public enum ConflictMarkerStyle
{
    /// <summary>git's default "merge" style: &lt;&lt;&lt;&lt;&lt;&lt;&lt; left ======= right &gt;&gt;&gt;&gt;&gt;&gt;&gt;.</summary>
    Merge,

    /// <summary>"diff3" style, which also shows the base after |||||||.</summary>
    Diff3,
}

/// <summary>Labels written after the conflict markers.</summary>
/// <param name="Left">Label after &lt;&lt;&lt;&lt;&lt;&lt;&lt;.</param>
/// <param name="Base">Label after ||||||| (diff3 style).</param>
/// <param name="Right">Label after &gt;&gt;&gt;&gt;&gt;&gt;&gt;.</param>
public sealed record ConflictMarkerLabels(string Left = "LOCAL", string Base = "BASE", string Right = "REMOTE")
{
    /// <summary>The default labels.</summary>
    public static ConflictMarkerLabels Default { get; } = new();
}
