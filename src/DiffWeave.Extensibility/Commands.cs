namespace DiffWeave.Extensibility;

/// <summary>
/// A command contributed by a plugin (or by the application itself). The UI shows it in the menu named by
/// <see cref="MenuPath"/>, binds <see cref="Shortcut"/> and calls <see cref="Execute"/>.
/// </summary>
public sealed record CommandContribution
{
    /// <summary>Unique id, conventionally prefixed with the plugin id (e.g. "diffweave.samples.whitespace.normalize").</summary>
    public required string Id { get; init; }

    /// <summary>The menu item text.</summary>
    public required string Title { get; init; }

    /// <summary>
    /// Menu placement as '/'-separated path of top-level menu and optional submenus, e.g. "Tools" or
    /// "Tools/Whitespace". <see langword="null"/> means the command is not shown in a menu (shortcut or palette only).
    /// </summary>
    public string? MenuPath { get; init; } = "Tools";

    /// <summary>Optional keyboard gesture in Avalonia <c>KeyGesture</c> syntax, e.g. "Ctrl+Shift+W". The host ignores gestures that clash with built-in ones.</summary>
    public string? Shortcut { get; init; }

    /// <summary>Optional tooltip / longer description.</summary>
    public string? Description { get; init; }

    /// <summary>Predicate deciding whether the command is currently enabled. <see langword="null"/> means always enabled. Must be fast and must not throw.</summary>
    public Func<CommandContext, bool>? CanExecute { get; init; }

    /// <summary>The action. Exceptions are caught and reported by the host.</summary>
    public required Func<CommandContext, CancellationToken, Task> Execute { get; init; }
}

/// <summary>What a command can see and do when it runs.</summary>
/// <param name="Editor">The active document, or <see langword="null"/> when no file comparison or merge is open.</param>
public sealed record CommandContext(IEditorContext? Editor);

/// <summary>The kind of document shown by an editor.</summary>
public enum EditorKind
{
    /// <summary>Two files side by side.</summary>
    TwoWayDiff,

    /// <summary>Three files side by side without an output.</summary>
    ThreeWayDiff,

    /// <summary>A three-way (or two-way) merge with an editable output pane.</summary>
    Merge,

    /// <summary>A directory comparison.</summary>
    DirectoryComparison,
}

/// <summary>Severity of a message shown to the user.</summary>
public enum MessageSeverity
{
    /// <summary>Informational.</summary>
    Information,

    /// <summary>Warning.</summary>
    Warning,

    /// <summary>Error.</summary>
    Error,
}

/// <summary>
/// The host's view of the active document, implemented by the application. All members must be called on the UI thread
/// unless stated otherwise; returned snapshots are immutable and may be used on any thread.
/// </summary>
public interface IEditorContext
{
    /// <summary>The kind of document.</summary>
    EditorKind Kind { get; }

    /// <summary>The display paths of the input panes, in pane order (base/left/right for merges, A/B for two-way diffs).</summary>
    IReadOnlyList<string> PanePaths { get; }

    /// <summary>The language id of the document, see <see cref="IFileTypeService"/>.</summary>
    string? LanguageId { get; }

    /// <summary>Returns an immutable copy of the lines of the given input pane (0-based pane index).</summary>
    IReadOnlyList<string> GetPaneLines(int pane);

    /// <summary>Returns a snapshot of the merge, or <see langword="null"/> when <see cref="Kind"/> is not <see cref="EditorKind.Merge"/>.</summary>
    MergeSnapshot? GetMergeSnapshot();

    /// <summary>
    /// Offers proposals to the user. The application shows them as a preview; nothing is applied until the user accepts,
    /// and accepted proposals are undoable. Returns the number of proposals the user accepted.
    /// </summary>
    Task<int> OfferProposalsAsync(MergeAssistantResult result, string sourceDisplayName, CancellationToken cancellationToken);

    /// <summary>Shows a non-modal message to the user (status bar / notification).</summary>
    void ShowMessage(string message, MessageSeverity severity = MessageSeverity.Information);
}

/// <summary>Registry of command contributions.</summary>
public interface ICommandRegistry
{
    /// <summary>All registered commands, in registration order.</summary>
    IReadOnlyList<CommandContribution> Commands { get; }

    /// <summary>Raised (on any thread) when commands are added or removed.</summary>
    event EventHandler? Changed;

    /// <summary>Registers a command. Dispose the returned object to remove it. Throws when the id is already taken.</summary>
    IDisposable Register(CommandContribution command);

    /// <summary>
    /// Evaluates <see cref="CommandContribution.CanExecute"/> safely (a throwing predicate yields <see langword="false"/>).
    /// </summary>
    bool CanExecute(string commandId, CommandContext context);

    /// <summary>
    /// Executes a command, isolating failures: exceptions are logged and returned as <see langword="false"/>,
    /// never propagated. Cancellation yields <see langword="false"/> as well.
    /// </summary>
    Task<bool> ExecuteAsync(string commandId, CommandContext context, CancellationToken cancellationToken = default);
}
