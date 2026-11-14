using DiffWeave.Extensibility;

namespace DiffWeave.Samples.WhitespacePlugin;

/// <summary>
/// Plugin entry point (named in plugin.json). Registers the whitespace merge assistant, a command that runs it on
/// the active merge, and a decoration provider that marks trailing whitespace.
/// </summary>
public sealed class WhitespacePlugin : IDiffWeavePlugin
{
    /// <summary>Id of the "normalize whitespace conflicts" command.</summary>
    public const string CommandId = "diffweave.samples.whitespace.resolve";

    private IPluginHost? _host;

    /// <inheritdoc/>
    public void Initialize(IPluginHost host)
    {
        _host = host;
        var assistant = new WhitespaceMergeAssistant();
        host.MergeAssistants.Register(assistant);
        host.EditorExtensions.RegisterDecorationProvider(new TrailingWhitespaceDecorations(host.Settings));
        host.Commands.Register(new CommandContribution
        {
            Id = CommandId,
            Title = "Resolve whitespace-only conflicts",
            MenuPath = "Tools/Whitespace",
            Shortcut = "Ctrl+Alt+W",
            Description = "Proposes a resolution for every unresolved conflict whose sides differ only in whitespace.",
            CanExecute = ctx => ctx.Editor?.Kind == EditorKind.Merge,
            Execute = async (ctx, ct) =>
            {
                IEditorContext editor = ctx.Editor!;
                MergeSnapshot? snapshot = editor.GetMergeSnapshot();
                if (snapshot is null)
                {
                    return;
                }

                MergeAssistantResult result = await assistant.RunAsync(new MergeAssistantRequest { Snapshot = snapshot }, ct).ConfigureAwait(true);
                if (result.Proposals.Count == 0)
                {
                    editor.ShowMessage("No whitespace-only conflicts found.");
                    return;
                }

                int accepted = await editor.OfferProposalsAsync(result, assistant.DisplayName, ct).ConfigureAwait(true);
                host.Logger.Info($"{accepted} of {result.Proposals.Count} whitespace proposals accepted.");
            },
        });
        host.Logger.Info("Whitespace sample plugin initialized.");
    }

    /// <inheritdoc/>
    public void Shutdown()
    {
        // Registrations are removed by the host; nothing else to release.
        _host?.Logger.Info("Whitespace sample plugin shut down.");
        _host = null;
    }
}
