using DiffWeave.Extensibility;

namespace DiffWeave.Samples.WhitespacePlugin;

/// <summary>
/// Highlights trailing whitespace in every pane. Can be switched off with the plugin setting
/// "showTrailingWhitespace" (bool), which also demonstrates <see cref="ISettingsStore"/> and invalidation.
/// </summary>
public sealed class TrailingWhitespaceDecorations : IEditorDecorationProvider
{
    /// <summary>The settings key.</summary>
    public const string SettingKey = "showTrailingWhitespace";

    private readonly ISettingsStore _settings;

    /// <summary>Creates the provider.</summary>
    public TrailingWhitespaceDecorations(ISettingsStore settings)
    {
        _settings = settings;
        _settings.Changed += (_, key) =>
        {
            if (key == SettingKey)
            {
                Invalidated?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    /// <inheritdoc/>
    public string Id => "diffweave.samples.whitespace.trailing";

    /// <inheritdoc/>
    public event EventHandler? Invalidated;

    /// <inheritdoc/>
    public Task<IReadOnlyList<EditorDecoration>> GetDecorationsAsync(DecorationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_settings.Get(SettingKey, true))
        {
            return Task.FromResult<IReadOnlyList<EditorDecoration>>([]);
        }

        var result = new List<EditorDecoration>();
        for (int i = 0; i < request.Lines.Count; i++)
        {
            if ((i & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            string line = request.Lines[i];
            int end = line.Length;
            while (end > 0 && char.IsWhiteSpace(line[end - 1]))
            {
                end--;
            }

            if (end < line.Length)
            {
                result.Add(new EditorDecoration
                {
                    Kind = DecorationKind.InlineHighlight,
                    Line = i,
                    Column = end,
                    Length = line.Length - end,
                    StyleKey = "Decoration.Warning",
                    Tooltip = "Trailing whitespace",
                });
            }
        }

        return Task.FromResult<IReadOnlyList<EditorDecoration>>(result);
    }
}
