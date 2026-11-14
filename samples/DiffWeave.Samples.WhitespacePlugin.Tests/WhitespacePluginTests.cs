using AwesomeAssertions;
using DiffWeave.Core.Merge;
using DiffWeave.Core.Plugins;
using DiffWeave.Extensibility;
using NUnit.Framework;

namespace DiffWeave.Samples.WhitespacePlugin.Tests;

public class WhitespacePluginTests
{
    private static MergeSession CreateMerge()
    {
        string[] baseLines = ["a", "b", "c", "x", "y"];
        string[] left = ["a", "b  changed", "c", "x1", "y"];
        string[] right = ["a", "b\tchanged ", "c", "x2", "y"];
        MergeSession session = MergeSession.Create(baseLines, left, right);
        session.AutoResolve();
        return session;
    }

    [Test]
    public async Task Proposes_resolution_only_for_whitespace_conflicts()
    {
        MergeSession session = CreateMerge();
        session.ConflictCount.Should().Be(2);
        var assistant = new WhitespaceMergeAssistant();
        MergeAssistantResult result = await assistant.RunAsync(new MergeAssistantRequest { Snapshot = session.CreateSnapshot() }, CancellationToken.None);

        result.Proposals.Should().ContainSingle();
        ResolutionProposal p = result.Proposals[0];
        session.Regions[p.RegionId].IsWhitespaceOnlyConflict.Should().BeTrue();
        p.ReplacementLines.Should().Equal("b  changed");
        p.Confidence.Should().BeInRange(0, 1);
        result.Diagnostics.Should().ContainSingle(d => d.Severity == MessageSeverity.Information);
    }

    [Test]
    public async Task Is_deterministic_and_honours_prefer_option()
    {
        MergeSession session = CreateMerge();
        var assistant = new WhitespaceMergeAssistant();
        var request = new MergeAssistantRequest
        {
            Snapshot = session.CreateSnapshot(),
            Options = new Dictionary<string, string> { ["prefer"] = "right" },
        };
        MergeAssistantResult r1 = await assistant.RunAsync(request, CancellationToken.None);
        MergeAssistantResult r2 = await assistant.RunAsync(request, CancellationToken.None);
        r1.Proposals[0].ReplacementLines.Should().Equal("b\tchanged");
        r2.Proposals[0].ReplacementLines.Should().Equal(r1.Proposals[0].ReplacementLines);
    }

    [Test]
    public async Task Accepted_proposals_resolve_the_conflict_and_are_undoable()
    {
        MergeSession session = CreateMerge();
        MergeAssistantResult result = await new WhitespaceMergeAssistant().RunAsync(new MergeAssistantRequest { Snapshot = session.CreateSnapshot() }, CancellationToken.None);
        session.UnresolvedConflictCount.Should().Be(2);
        session.ApplyProposals(result.Proposals).Should().Be(1);
        session.UnresolvedConflictCount.Should().Be(1);
        session.Undo().Should().BeTrue();
        session.UnresolvedConflictCount.Should().Be(2);
    }

    [Test]
    public async Task Loads_end_to_end_through_the_plugin_manager()
    {
        string pluginDir = Path.GetDirectoryName(typeof(WhitespacePlugin).Assembly.Location)!;

        // Copy the plugin into its own folder, as an installation would.
        string root = Path.Combine(Path.GetTempPath(), "diffweave-sample-" + Guid.NewGuid().ToString("N"));
        string target = Path.Combine(root, "whitespace");
        Directory.CreateDirectory(target);
        try
        {
            foreach (string name in new[] { "DiffWeave.Samples.WhitespacePlugin.dll", "DiffWeave.Samples.WhitespacePlugin.deps.json", "plugin.json" })
            {
                string src = Path.Combine(pluginDir, name);
                if (File.Exists(src))
                {
                    File.Copy(src, Path.Combine(target, name));
                }
            }

            using var manager = new PluginManager(new PluginManagerOptions { PluginDirectories = [root] });
            manager.LoadAll();
            manager.Plugins.Should().ContainSingle(p => p.State == PluginState.Loaded && p.Id == "diffweave.samples.whitespace");
            manager.MergeAssistants.Assistants.Should().ContainSingle(a => a.Id == WhitespaceMergeAssistant.AssistantId);
            manager.Commands.Commands.Should().ContainSingle(c => c.Id == WhitespacePlugin.CommandId);
            manager.EditorExtensions.DecorationProviders.Should().HaveCount(1);

            // The command is disabled without an open merge.
            manager.Commands.CanExecute(WhitespacePlugin.CommandId, new CommandContext(null)).Should().BeFalse();

            IReadOnlyList<EditorDecoration> decorations = await manager.EditorExtensions.CollectDecorationsAsync(
                new DecorationRequest(PaneRole.Left, "x.txt", "plaintext", ["ok", "trailing  "]), CancellationToken.None);
            decorations.Should().ContainSingle(d => d.Line == 1 && d.Column == 8 && d.Length == 2);

            MergeSession session = CreateMerge();
            MergeAssistantRunResult run = await manager.RunAssistantAsync(manager.MergeAssistants.Assistants[0], new MergeAssistantRequest { Snapshot = session.CreateSnapshot() });
            run.Succeeded.Should().BeTrue();
            run.Result.Proposals.Should().ContainSingle();

            manager.Unload("diffweave.samples.whitespace").Should().BeTrue();
            manager.MergeAssistants.Assistants.Should().BeEmpty();
            manager.Commands.Commands.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
