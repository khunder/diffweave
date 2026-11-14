namespace DiffWeave.Extensibility;

/// <summary>The role of an editor pane.</summary>
public enum PaneRole
{
    /// <summary>Base / A pane of a three-way view.</summary>
    Base,

    /// <summary>Left / B pane (also the first file of a two-way diff).</summary>
    Left,

    /// <summary>Right / C pane (also the second file of a two-way diff).</summary>
    Right,

    /// <summary>The merge output pane.</summary>
    Output,
}

/// <summary>The kind of an editor decoration.</summary>
public enum DecorationKind
{
    /// <summary>A glyph in the gutter next to a line.</summary>
    GutterMark,

    /// <summary>Background highlight of whole lines.</summary>
    LineHighlight,

    /// <summary>Highlight of a character range inside one line.</summary>
    InlineHighlight,
}

/// <summary>
/// A decoration to draw in an editor pane. Colours are never specified directly: <see cref="StyleKey"/> names a theme
/// resource (e.g. "Decoration.Info", "Decoration.Warning", "Decoration.Error", "Decoration.Accent"), so decorations
/// follow light and dark themes.
/// </summary>
public sealed record EditorDecoration
{
    /// <summary>The decoration kind.</summary>
    public required DecorationKind Kind { get; init; }

    /// <summary>The (0-based) line in the pane's text.</summary>
    public required int Line { get; init; }

    /// <summary>Number of lines covered (line highlights).</summary>
    public int LineCount { get; init; } = 1;

    /// <summary>Start column (inline highlights), in UTF-16 code units.</summary>
    public int Column { get; init; }

    /// <summary>Length (inline highlights), in UTF-16 code units.</summary>
    public int Length { get; init; }

    /// <summary>The theme style key, see the type remarks.</summary>
    public string StyleKey { get; init; } = "Decoration.Info";

    /// <summary>The glyph for gutter marks (one or two characters).</summary>
    public string? Glyph { get; init; }

    /// <summary>Optional tooltip text.</summary>
    public string? Tooltip { get; init; }
}

/// <summary>The input of an <see cref="IEditorDecorationProvider"/>.</summary>
/// <param name="Role">The pane being decorated.</param>
/// <param name="Path">Path or display name of the pane's file.</param>
/// <param name="LanguageId">Language id of the file.</param>
/// <param name="Lines">The pane's lines (immutable snapshot).</param>
public sealed record DecorationRequest(PaneRole Role, string? Path, string LanguageId, IReadOnlyList<string> Lines);

/// <summary>Contributes decorations to editor panes.</summary>
public interface IEditorDecorationProvider
{
    /// <summary>Unique id.</summary>
    string Id { get; }

    /// <summary>
    /// Computes decorations for a pane. Called on a background thread whenever a pane's text changes; must be
    /// reasonably fast. Exceptions are caught and the provider's decorations are skipped.
    /// </summary>
    Task<IReadOnlyList<EditorDecoration>> GetDecorationsAsync(DecorationRequest request, CancellationToken cancellationToken);

    /// <summary>Raised when the provider wants its decorations recomputed (e.g. its settings changed).</summary>
    event EventHandler? Invalidated;
}

/// <summary>Registry of editor extensions.</summary>
public interface IEditorExtensionRegistry
{
    /// <summary>All registered decoration providers.</summary>
    IReadOnlyList<IEditorDecorationProvider> DecorationProviders { get; }

    /// <summary>Raised when providers are added or removed, or a provider raised <see cref="IEditorDecorationProvider.Invalidated"/>.</summary>
    event EventHandler? Changed;

    /// <summary>Registers a provider; dispose the result to remove it.</summary>
    IDisposable RegisterDecorationProvider(IEditorDecorationProvider provider);

    /// <summary>
    /// Collects decorations from all providers, isolating failures (a throwing or timed-out provider contributes nothing).
    /// </summary>
    Task<IReadOnlyList<EditorDecoration>> CollectDecorationsAsync(DecorationRequest request, CancellationToken cancellationToken);
}
