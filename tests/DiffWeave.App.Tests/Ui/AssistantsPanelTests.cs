using Avalonia.Headless.NUnit;
using DiffWeave.App.Documents;
using DiffWeave.App.Services;
using DiffWeave.App.ViewModels;
using DiffWeave.Core.Merge;
using DiffWeave.Extensibility;

namespace DiffWeave.App.Tests.Ui;

public class AssistantsPanelTests
{
    /// <summary>Copies the built sample plugin into a fresh plugin folder (loaded through the real plugin loader).</summary>
    public static string InstallSamplePlugin(string root)
    {
        string config = Path.GetFileName(TestContext.CurrentContext.TestDirectory.TrimEnd(Path.DirectorySeparatorChar));
        string built = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "DiffWeave.Samples.WhitespacePlugin", config));
        File.Exists(Path.Combine(built, "plugin.json")).Should().BeTrue($"sample plugin must be built at {built}");
        string pluginsDir = Path.Combine(root, "plugins");
        string target = Path.Combine(pluginsDir, "whitespace");
        Directory.CreateDirectory(target);
        foreach (string f in Directory.GetFiles(built))
        {
            File.Copy(f, Path.Combine(target, Path.GetFileName(f)));
        }

        return pluginsDir;
    }

    /// <summary>Inputs whose only conflict differs in whitespace (plus a real conflict).</summary>
    public static (string Base, string Left, string Right) WriteWhitespaceConflict(string dir)
    {
        string b = Path.Combine(dir, "ws.base.txt"), l = Path.Combine(dir, "ws.local.txt"), r = Path.Combine(dir, "ws.remote.txt");
        File.WriteAllText(b, "header\nint x = 1;\nmiddle\nkeep\nvalue = 1\nfooter\n");
        File.WriteAllText(l, "header\nint  x = 2;\nmiddle\nkeep\nvalue = 2\nfooter\n");
        File.WriteAllText(r, "header\nint x =   2;\nmiddle\nkeep\nvalue = 3\nfooter\n");
        return (b, l, r);
    }

    private static UiHarness WithSamplePlugin()
    {
        string root = TestUtil.CreateTempDirectory();
        PluginService plugins = new([InstallSamplePlugin(root)]);
        IReadOnlyList<PluginInfo> loaded = plugins.LoadAll();
        loaded.Should().ContainSingle(p => p.State == PluginState.Loaded, string.Join("; ", loaded.Select(p => $"{p.Directory}: {p.State} {p.Error}")));
        return new UiHarness(plugins);
    }

    [AvaloniaTest]
    public void Empty_state_without_assistants()
    {
        using UiHarness h = new();
        h.ViewModel.Assistants.ShowEmptyState.Should().BeTrue();
        h.ViewModel.Assistants.HasAssistants.Should().BeFalse();
        h.ViewModel.IsAssistantsPanelVisible.Should().BeFalse("the panel is hidden by default");
    }

    [AvaloniaTest]
    public async Task Sample_assistant_proposes_accept_applies_and_undo_reverts()
    {
        using UiHarness h = WithSamplePlugin();
        AssistantsViewModel a = h.ViewModel.Assistants;
        a.Assistants.Should().ContainSingle(x => x.Name == "Whitespace normalizer");
        (string b, string l, string r) = WriteWhitespaceConflict(h.Directory);
        MergeDocument? doc = await h.ViewModel.OpenMergeAsync([b, l, r], null, Path.Combine(h.Directory, "ws.txt"));
        doc!.UnresolvedCount.Should().Be(2);
        h.ViewModel.IsAssistantsPanelVisible = true;
        h.Window.Pump();

        await a.RunForAllCommand.ExecuteAsync(null);
        a.Proposals.Should().ContainSingle();
        ProposalItem p = a.Proposals[0];
        p.Rationale.Should().NotBeNullOrWhiteSpace();
        p.Confidence.Should().BeInRange(0, 1);
        p.PreviewLines.Should().Contain(x => x.StartsWith("+ ", StringComparison.Ordinal));

        a.AcceptCommand.Execute(p);
        p.State.Should().Be(ProposalState.Accepted);
        doc.UnresolvedCount.Should().Be(1);
        doc.Session.GetResolution(p.Proposal.RegionId).Choice.Should().Be(ResolutionChoice.Custom);

        h.ViewModel.UndoCommand.Execute(null);
        doc.UnresolvedCount.Should().Be(2, "accepted proposals are undoable");
    }

    [AvaloniaTest]
    public async Task Reject_leaves_the_merge_untouched_and_plugin_command_offers_proposals()
    {
        using UiHarness h = WithSamplePlugin();
        (string b, string l, string r) = WriteWhitespaceConflict(h.Directory);
        MergeDocument? doc = await h.ViewModel.OpenMergeAsync([b, l, r], null, null);
        AssistantsViewModel a = h.ViewModel.Assistants;
        await a.RunForAllCommand.ExecuteAsync(null);
        a.RejectCommand.Execute(a.Proposals[0]);
        doc!.UnresolvedCount.Should().Be(2);

        // The sample plugin also contributes a command that offers proposals through IEditorContext.
        h.Plugins.Manager.Commands.Commands.Should().NotBeEmpty();
        string id = h.Plugins.Manager.Commands.Commands[0].Id;
        Task<bool> run = h.ViewModel.ExecutePluginCommandAsync(id);
        for (int i = 0; i < 50 && a.Proposals.Count == 0; i++)
        {
            await Task.Delay(10);
            h.Window.Pump();
        }

        h.ViewModel.IsAssistantsPanelVisible.Should().BeTrue();
        a.Proposals.Should().ContainSingle();
        a.AcceptAllCommand.Execute(null);
        (await run).Should().BeTrue();
        doc.UnresolvedCount.Should().Be(1);
    }

    [AvaloniaTest]
    public async Task Decoration_provider_marks_trailing_whitespace()
    {
        using UiHarness h = WithSamplePlugin();
        string x = Path.Combine(h.Directory, "x.txt"), y = Path.Combine(h.Directory, "y.txt");
        File.WriteAllText(x, "alpha   \nbeta\n");
        File.WriteAllText(y, "alpha\nbeta\n");
        ComparisonDocument? doc = await h.ViewModel.OpenFilesAsync([x, y]);
        await h.ViewModel.RefreshPluginDecorationsAsync();
        DiffWeave.App.Editor.DecorationSink sink = new();
        doc!.Panes[0].PluginDecorations.GetDecorations(0, 1, sink);
        (sink.Inlines.Count + sink.Marks.Count + sink.Lines.Count).Should().BeGreaterThan(0);
    }

    [AvaloniaTest]
    public void Plugins_menu_lists_contributed_commands_and_reload_keeps_them()
    {
        using UiHarness h = WithSamplePlugin();
        int before = h.Plugins.Manager.Commands.Commands.Count;
        h.ViewModel.ReloadPluginsCommand.Execute(null);
        h.Plugins.Manager.Commands.Commands.Count.Should().Be(before);
        h.ViewModel.Assistants.Assistants.Should().ContainSingle();
        h.ViewModel.StatusMessage.Should().Contain("1 loaded");
    }
}
