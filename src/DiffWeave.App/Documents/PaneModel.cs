using DiffWeave.App.Editor;
using DiffWeave.Extensibility;

namespace DiffWeave.App.Documents;

/// <summary>One pane of a diff or merge document.</summary>
public sealed class PaneModel
{
    internal PaneModel(int index, PaneRole role, string badge, string badgeResourceKey, IEditorTextSource source, InputFile? input)
    {
        Index = index;
        Role = role;
        Badge = badge;
        BadgeResourceKey = badgeResourceKey;
        Source = source;
        Input = input;
    }

    /// <summary>Pane index in the document's <see cref="PaneMapping"/>.</summary>
    public int Index { get; }

    /// <summary>The role exposed to plugins.</summary>
    public PaneRole Role { get; }

    /// <summary>Short badge text ("A", "B", "C", "Output").</summary>
    public string Badge { get; }

    /// <summary>Resource key of the badge colour.</summary>
    public string BadgeResourceKey { get; }

    /// <summary>The text shown in the pane.</summary>
    public IEditorTextSource Source { get; }

    /// <summary>The input file (null for the output pane).</summary>
    public InputFile? Input { get; internal set; }

    /// <summary>Diff decorations (line kinds, inline changes, gaps).</summary>
    public IEditorDecorationSource? DiffDecorations { get; internal set; }

    /// <summary>Decorations contributed by plugins.</summary>
    public StaticDecorationSource PluginDecorations { get; } = new();

    /// <summary>Placeholder rows (unresolved conflicts in the output pane).</summary>
    public IReadOnlyList<PhantomRow> PhantomRows { get; internal set; } = [];

    /// <summary>Header title.</summary>
    public string Title { get; internal set; } = string.Empty;

    /// <summary>Header path.</summary>
    public string? Path { get; internal set; }

    /// <summary>Header info (encoding, EOL, line count).</summary>
    public string Info { get; internal set; } = string.Empty;
}
