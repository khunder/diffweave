using Avalonia.Headless.NUnit;
using DiffWeave.App.Documents;
using DiffWeave.App.Services;
using DiffWeave.Core.Merge;
using DiffWeave.Extensibility;

namespace DiffWeave.App.Tests.Ui;

/// <summary>Merge lifecycle hooks registered by an in-process test plugin are invoked around resolve and save.</summary>
public class LifecycleHookTests
{
    private sealed class RecordingHook : IMergeLifecycleHook
    {
        public List<string> Calls { get; } = [];

        public bool VetoSave { get; set; }

        public string Id => "tests.recording-hook";

        public Task<HookResult> BeforeResolveAsync(MergeLifecycleContext context, CancellationToken cancellationToken)
        {
            Calls.Add($"before-resolve {context.RegionId} {context.Choice}");
            return Task.FromResult(HookResult.Continue);
        }

        public Task AfterResolveAsync(MergeLifecycleContext context, CancellationToken cancellationToken)
        {
            Calls.Add($"after-resolve {context.RegionId}");
            return Task.CompletedTask;
        }

        public Task<HookResult> BeforeSaveAsync(MergeLifecycleContext context, CancellationToken cancellationToken)
        {
            Calls.Add("before-save");
            return Task.FromResult(VetoSave ? HookResult.Cancel("Policy check failed") : HookResult.Continue);
        }
    }

    private sealed class HookPlugin(RecordingHook hook) : IDiffWeavePlugin
    {
        public void Initialize(IPluginHost host) => host.LifecycleHooks.Register(hook);

        public void Shutdown()
        {
        }
    }

    [AvaloniaTest]
    public async Task Hooks_run_around_resolve_and_can_veto_save()
    {
        RecordingHook hook = new() { VetoSave = true };
        PluginService plugins = new([Path.Combine(Path.GetTempPath(), "diffweave-no-plugins")]);
        plugins.AddBuiltIn(
            new PluginManifest { Id = "tests.hooks", Name = "Hooks", Version = "1.0.0", ApiVersion = "1.0", EntryAssembly = "-", EntryType = "-" },
            new HookPlugin(hook));
        plugins.LoadAll();
        using UiHarness h = new(plugins);
        (string b, string l, string r) = TestData.WriteMergeInputs(h.Directory);
        string output = Path.Combine(h.Directory, "out.cs");
        MergeDocument? doc = await h.ViewModel.OpenMergeAsync([b, l, r], null, output);
        int region = doc!.CurrentRegion;
        await h.ViewModel.ChooseBCommand.ExecuteAsync(null);
        for (int i = 0; i < 50 && hook.Calls.Count < 2; i++)
        {
            await Task.Delay(10);
        }

        hook.Calls.Should().Equal($"before-resolve {region} Left", $"after-resolve {region}");
        doc.ResolveAll(ResolutionChoice.Right);
        (await h.ViewModel.SaveAsync()).Should().BeFalse();
        File.Exists(output).Should().BeFalse();
        h.ViewModel.StatusMessage.Should().Contain("Policy check failed");

        hook.VetoSave = false;
        (await h.ViewModel.SaveAsync()).Should().BeTrue();
        File.Exists(output).Should().BeTrue();
    }
}
