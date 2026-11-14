using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace DiffWeave.App.Editor;

/// <summary>
/// Resolves editor brushes from theme resources (see Themes/*.axaml). Cached per control and rebuilt whenever
/// the actual theme variant or the resources change. Missing keys fall back to transparent so a broken theme
/// never crashes rendering.
/// </summary>
public sealed class EditorPalette
{
    private readonly Dictionary<string, IBrush> _cache = new(StringComparer.Ordinal);
    private readonly Control _owner;

    /// <summary>Creates a palette bound to <paramref name="owner"/>'s resource scope.</summary>
    public EditorPalette(Control owner) => _owner = owner;

    /// <summary>Editor background.</summary>
    public IBrush Background => Get("DW.Editor.Background");

    /// <summary>Text foreground.</summary>
    public IBrush Foreground => Get("DW.Editor.Foreground");

    /// <summary>Gutter background.</summary>
    public IBrush GutterBackground => Get("DW.Editor.GutterBackground");

    /// <summary>Line numbers.</summary>
    public IBrush LineNumber => Get("DW.Editor.LineNumber");

    /// <summary>Line number of the caret line.</summary>
    public IBrush LineNumberCurrent => Get("DW.Editor.LineNumberCurrent");

    /// <summary>Selection fill.</summary>
    public IBrush Selection => Get("DW.Editor.Selection");

    /// <summary>Selection fill when unfocused.</summary>
    public IBrush SelectionInactive => Get("DW.Editor.SelectionInactive");

    /// <summary>Caret.</summary>
    public IBrush Caret => Get("DW.Editor.Caret");

    /// <summary>Current-line highlight.</summary>
    public IBrush CurrentLine => Get("DW.Editor.CurrentLine");

    /// <summary>Visible whitespace glyphs.</summary>
    public IBrush Whitespace => Get("DW.Editor.Whitespace");

    /// <summary>Border of the current difference region.</summary>
    public IBrush CurrentRegion => Get("DW.Editor.CurrentRegion");

    /// <summary>Separator between gutter and text.</summary>
    public IBrush GutterSeparator => Get("DW.Editor.GutterSeparator");

    /// <summary>Line background for a kind.</summary>
    public IBrush LineBrush(HighlightKind kind) => Get(KindKey(kind, "Line"));

    /// <summary>Inline highlight for a kind.</summary>
    public IBrush InlineBrush(HighlightKind kind) => Get(KindKey(kind, "Inline"));

    /// <summary>Strong marker colour (gutter, ruler, bands outline) for a kind.</summary>
    public IBrush MarkBrush(HighlightKind kind) => Get(KindKey(kind, "Mark"));

    /// <summary>Drops cached brushes.</summary>
    public void Invalidate() => _cache.Clear();

    private static string KindKey(HighlightKind kind, string part) => $"DW.Kind.{kind}.{part}";

    /// <summary>Returns the brush with the given resource key.</summary>
    public IBrush Get(string key)
    {
        if (_cache.TryGetValue(key, out IBrush? b))
        {
            return b;
        }

        IBrush result = Brushes.Transparent;
        if (_owner.TryFindResource(key, _owner.ActualThemeVariant, out object? value))
        {
            result = value switch
            {
                IBrush brush => brush,
                Color c => new ImmutableSolidColorBrush(c),
                _ => Brushes.Transparent,
            };
        }

        if (result is ISolidColorBrush s and not IImmutableBrush)
        {
            result = new ImmutableSolidColorBrush(s.Color, s.Opacity);
        }

        _cache[key] = result;
        return result;
    }
}
