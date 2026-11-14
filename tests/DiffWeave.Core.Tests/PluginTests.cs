using System.Runtime.CompilerServices;
using AwesomeAssertions;
using DiffWeave.Core.Merge;
using DiffWeave.Core.Plugins;
using DiffWeave.Extensibility;
using NUnit.Framework;

namespace DiffWeave.Core.Tests;

public class PluginTests
{
    private const string TestAssembly = "DiffWeave.Core.Tests.TestPlugin.dll";

    private static string TestPluginBuildDir()
    {
        // artifacts/bin/DiffWeave.Core.Tests/<config>/ -> artifacts/bin/DiffWeave.Core.Tests.TestPlugin/<config>/
        var here = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        string dir = Path.Combine(here.Parent!.Parent!.FullName, "DiffWeave.Core.Tests.TestPlugin", here.Name);
        File.Exists(Path.Combine(dir, TestAssembly)).Should().BeTrue($"the test plugin must be built at {dir}");
        return dir;
    }

    private static string InstallPlugin(TempDir tmp, string folder, string id, string entryType, string apiVersion = "1.0", bool copyAssembly = true)
    {
        string dir = tmp.Dir(Path.Combine("plugins", folder));
        if (copyAssembly)
        {
            string build = TestPluginBuildDir();
            foreach (string file in Directory.GetFiles(build, "DiffWeave.Core.Tests.TestPlugin.*"))
            {
                File.Copy(file, Path.Combine(dir, Path.GetFileName(file)));
            }
        }

        File.WriteAllText(Path.Combine(dir, "plugin.json"), $$"""
            {
              // comments are allowed
              "id": "{{id}}",
              "name": "Test {{id}}",
              "version": "1.2.3-beta",
              "apiVersion": "{{apiVersion}}",
              "entryAssembly": "{{TestAssembly}}",
              "entryType": "DiffWeave.Core.Tests.TestPlugin.{{entryType}}",
            }
            """);
        return dir;
    }

    [Test]
    public void Manifest_validation()
    {
        PluginManifestReader.Parse("{ not json", out IReadOnlyList<string> errors).Should().BeNull();
        errors.Should().ContainSingle(e => e.StartsWith("Invalid JSON", StringComparison.Ordinal));

        PluginManifestReader.Parse("""{ "id": "x" }""", out errors).Should().BeNull();
        errors.Should().Contain(e => e.Contains("'name'", StringComparison.Ordinal));

        PluginManifestReader.Parse("""
            { "id": "bad id!", "name": "n", "version": "one", "apiVersion": "[3,1]", "entryAssembly": "../evil.dll", "entryType": "T" }
            """, out errors).Should().BeNull();
        errors.Should().HaveCount(4);

        PluginManifest? ok = PluginManifestReader.Parse("""
            { "id": "a.b-c_d", "name": "n", "version": "2.0", "apiVersion": "1.0", "entryAssembly": "lib/P.dll", "entryType": "P.Q", "authors": ["me"] }
            """, out errors);
        errors.Should().BeEmpty();
        ok!.Authors.Should().Equal("me");
    }

    [TestCase("1.0", "1.0", true)]
    [TestCase("1.0", "1.9", true)]
    [TestCase("1.0", "2.0", false)]
    [TestCase("1.2", "1.1", false)]
    [TestCase("[1.0,3.0)", "2.5", true)]
    [TestCase("[1.0,3.0)", "3.0", false)]
    [TestCase("(1.0,]", "1.0", false)]
    [TestCase("[1.0,]", "99.0", true)]
    [TestCase("*", "7.3", true)]
    public void Api_version_ranges(string range, string version, bool expected)
        => ApiVersionRange.Parse(range).Contains(new Version(version)).Should().Be(expected);

    [Test]
    public void Loads_valid_plugin_and_unloads_it_completely()
    {
        using var tmp = new TempDir();
        InstallPlugin(tmp, "good", "test.good", "GoodPlugin");
        var log = new PluginLogBuffer();
        var settings = new JsonSettingsStore();
        using var manager = new PluginManager(new PluginManagerOptions { PluginDirectories = [Path.Combine(tmp.Path, "plugins")], Log = log, Settings = settings });
        int changes = 0;
        manager.PluginsChanged += (_, _) => changes++;

        LoadAndCheck(manager);
        changes.Should().BeGreaterThan(0);
        settings.GetSection("plugins").GetSection("test.good").Get("initialized", false).Should().BeTrue();
        log.Entries.Should().Contain(e => e.Source == "test.good" && e.Message == "good plugin up");

        AssistantIsFromCollectibleContext(manager).Should().BeTrue("plugin types come from their own load context");

        WeakReference? context = UnloadAndForget(manager, "test.good");
        manager.Commands.Commands.Should().BeEmpty();
        manager.MergeAssistants.Assistants.Should().BeEmpty();
        manager.Plugins.Single().State.Should().Be(PluginState.Unloaded);

        for (int i = 0; i < 20 && context!.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        context!.IsAlive.Should().BeFalse("the plugin's load context must be collectible after unloading");

        // It can be loaded again.
        manager.Load("test.good").State.Should().Be(PluginState.Loaded);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LoadAndCheck(PluginManager manager)
    {
        manager.LoadAll().Should().ContainSingle(p => p.Id == "test.good" && p.State == PluginState.Loaded);
        manager.Commands.Commands.Should().ContainSingle(c => c.Id == "test.good.hello");
        manager.MergeAssistants.Find("test.echo").Should().NotBeNull();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool AssistantIsFromCollectibleContext(PluginManager manager)
        => System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(manager.MergeAssistants.Assistants[0].GetType().Assembly)!.IsCollectible;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference? UnloadAndForget(PluginManager manager, string id)
    {
        manager.Unload(id, out WeakReference? context).Should().BeTrue();
        context.Should().NotBeNull();
        return context;
    }

    [Test]
    public void Invalid_incompatible_and_failing_plugins_are_isolated()
    {
        using var tmp = new TempDir();
        string bad = tmp.Dir("plugins/bad");
        File.WriteAllText(Path.Combine(bad, "plugin.json"), "{ \"id\": 5");
        InstallPlugin(tmp, "future", "test.future", "GoodPlugin", apiVersion: "[2.0,3.0)");
        InstallPlugin(tmp, "throwinit", "test.throwinit", "ThrowingInitPlugin");
        InstallPlugin(tmp, "missing", "test.missing", "GoodPlugin", copyAssembly: false);
        InstallPlugin(tmp, "wrongtype", "test.wrongtype", "NotAPlugin");
        InstallPlugin(tmp, "notype", "test.notype", "DoesNotExist");
        InstallPlugin(tmp, "zdup", "test.throwinit", "GoodPlugin");
        InstallPlugin(tmp, "good", "test.good", "GoodPlugin");

        using var manager = new PluginManager(new PluginManagerOptions { PluginDirectories = [Path.Combine(tmp.Path, "plugins"), Path.Combine(tmp.Path, "nope")] });
        IReadOnlyList<PluginInfo> plugins = manager.LoadAll();

        PluginState StateOf(string id) => plugins.First(p => p.Id == id).State;
        plugins.Should().Contain(p => p.Manifest == null && p.State == PluginState.InvalidManifest && p.Id == "bad");
        StateOf("test.future").Should().Be(PluginState.Incompatible);
        plugins.First(p => p.Id == "test.future").Error.Should().Contain("1.0");
        StateOf("test.throwinit").Should().Be(PluginState.Faulted);
        plugins.First(p => p.Id == "test.throwinit" && p.State == PluginState.Faulted).Error.Should().Contain("boom in Initialize");
        plugins.Should().Contain(p => p.Id == "test.throwinit" && p.State == PluginState.InvalidManifest, "duplicate ids are rejected");
        StateOf("test.missing").Should().Be(PluginState.Faulted);
        StateOf("test.wrongtype").Should().Be(PluginState.Faulted);
        StateOf("test.notype").Should().Be(PluginState.Faulted);
        StateOf("test.good").Should().Be(PluginState.Loaded);

        // Registrations made before the failure were rolled back.
        manager.Commands.Commands.Select(c => c.Id).Should().Equal("test.good.hello");
        manager.Log.Entries.Should().Contain(e => e.Level == PluginLogLevel.Error);
    }

    [Test]
    public void Disabled_plugins_are_not_loaded()
    {
        using var tmp = new TempDir();
        InstallPlugin(tmp, "good", "test.good", "GoodPlugin");
        using var manager = new PluginManager(new PluginManagerOptions { PluginDirectories = [Path.Combine(tmp.Path, "plugins")], DisabledPluginIds = ["test.good"] });
        manager.LoadAll().Single().State.Should().Be(PluginState.Disabled);
        manager.Commands.Commands.Should().BeEmpty();
    }

    [Test]
    public async Task Throwing_commands_and_shutdown_do_not_crash_the_host()
    {
        using var tmp = new TempDir();
        string dir = InstallPlugin(tmp, "throwing", "test.throwing", "ThrowingShutdownPlugin");
        using var manager = new PluginManager();
        manager.LoadFrom(dir).State.Should().Be(PluginState.Loaded);
        (await manager.Commands.ExecuteAsync("test.throwing.command", new CommandContext(null))).Should().BeFalse();
        (await manager.Commands.ExecuteAsync("unknown", new CommandContext(null))).Should().BeFalse();
        manager.Log.Entries.Should().Contain(e => e.Message.Contains("test.throwing.command", StringComparison.Ordinal));
        manager.Unload("test.throwing").Should().BeTrue();
        manager.Log.Entries.Should().Contain(e => e.Message.Contains("shutdown", StringComparison.Ordinal));
    }

    [Test]
    public async Task Assistant_runner_validates_and_isolates()
    {
        MergeSession session = MergeSession.Create(["1", "2", "3"], ["1", "L", "3"], ["1", "R", "3"]);
        var request = new MergeAssistantRequest { Snapshot = session.CreateSnapshot() };

        MergeAssistantRunResult ok = await MergeAssistantRunner.RunAsync(new FakeAssistant((_, _) => Task.FromResult(new MergeAssistantResult
        {
            Proposals =
            [
                new ResolutionProposal(1, ["x"], "r", 7),
                new ResolutionProposal(1, ["dup"], "r", 0.5),
                new ResolutionProposal(42, ["?"], "r", 0.5),
            ],
        })), request);
        ok.Succeeded.Should().BeTrue();
        ok.Result.Proposals.Should().ContainSingle().Which.Confidence.Should().Be(1);
        ok.Result.Diagnostics.Should().HaveCount(2);

        MergeAssistantRunResult thrown = await MergeAssistantRunner.RunAsync(new FakeAssistant((_, _) => throw new InvalidOperationException("nope")), request);
        thrown.Succeeded.Should().BeFalse();
        thrown.Result.Diagnostics.Single().Severity.Should().Be(MessageSeverity.Error);

        MergeAssistantRunResult slow = await MergeAssistantRunner.RunAsync(
            new FakeAssistant(async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return MergeAssistantResult.Empty;
            }),
            request,
            TimeSpan.FromMilliseconds(100));
        slow.Succeeded.Should().BeFalse();
        slow.Result.Diagnostics.Single().Message.Should().Contain("timed out");

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Func<Task> cancelled = () => MergeAssistantRunner.RunAsync(new FakeAssistant((_, _) => Task.FromResult(MergeAssistantResult.Empty)), request, null, null, cts.Token);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task Built_in_plugins_registries_hooks_and_decorations()
    {
        using var manager = new PluginManager();
        var plugin = new InlinePlugin();
        manager.AddBuiltIn(new PluginManifest { Id = "inline", Name = "Inline", Version = "1.0", ApiVersion = "1.0", EntryAssembly = "x.dll", EntryType = "x" }, plugin)
            .State.Should().Be(PluginState.Loaded);
        plugin.Host!.Services.GetService(typeof(IFileTypeService)).Should().BeSameAs(plugin.Host.FileTypes);

        Action dup = () => plugin.Host!.Commands.Register(new CommandContribution { Id = "inline.cmd", Title = "again", Execute = (_, _) => Task.CompletedTask });
        dup.Should().Throw<InvalidOperationException>();

        MergeSnapshot snap = MergeSession.Create(["a"], ["a"], ["a"]).CreateSnapshot();
        (await manager.LifecycleHooks.RunBeforeSaveAsync(new MergeLifecycleContext(snap))).Proceed.Should().BeFalse();
        (await manager.LifecycleHooks.RunBeforeResolveAsync(new MergeLifecycleContext(snap, 0, "Left"))).Proceed.Should().BeTrue("a throwing hook is ignored");
        await manager.LifecycleHooks.RunAfterResolveAsync(new MergeLifecycleContext(snap, 0, "Left"));

        IReadOnlyList<EditorDecoration> decorations = await manager.EditorExtensions.CollectDecorationsAsync(
            new DecorationRequest(PaneRole.Output, "f.cs", "csharp", ["a", "b"]), CancellationToken.None);
        decorations.Should().ContainSingle().Which.Kind.Should().Be(DecorationKind.GutterMark);

        manager.FileTypes.DetectLanguage("x.foo").Should().Be("foolang");
        manager.Unload("inline");
        manager.FileTypes.DetectLanguage("x.foo").Should().Be(FileTypeService.PlainText);
        manager.LifecycleHooks.Hooks.Should().BeEmpty();
        manager.EditorExtensions.DecorationProviders.Should().BeEmpty();
        plugin.ShutdownCalled.Should().BeTrue();
    }

    [Test]
    public void File_type_detection()
    {
        var ft = new FileTypeService();
        ft.DetectLanguage("/x/Program.CS").Should().Be("csharp");
        ft.DetectLanguage("Makefile").Should().Be("makefile");
        ft.DetectLanguage("script", "#!/usr/bin/env python3").Should().Be("python");
        ft.DetectLanguage("noext", "<?xml version=\"1.0\"?>").Should().Be("xml");
        ft.DetectLanguage(null).Should().Be("plaintext");
    }

    [Test]
    public void Settings_store_is_typed_namespaced_and_persistent()
    {
        using var tmp = new TempDir();
        string file = Path.Combine(tmp.Path, "sub", "settings.json");
        JsonSettingsStore store = JsonSettingsStore.Load(file);
        ISettingsStore section = store.GetSection("plugins").GetSection("p");
        var changed = new List<string>();
        section.Changed += (_, k) => changed.Add(k);
        section.Set("count", 3);
        section.Set("list", new List<string> { "a", "b" });
        section.Set("point", new Point(1, 2));
        store.Set("top", "v");
        section.Get("count", 0).Should().Be(3);
        section.Get("missing", 9).Should().Be(9);
        section.Get("count", "fallback").Should().Be("fallback", "a value of another type yields the default");
        section.Keys.Should().BeEquivalentTo(["count", "list", "point"]);
        store.Keys.Should().BeEquivalentTo(["top"]);
        changed.Should().Equal("count", "list", "point");
        store.Save();

        JsonSettingsStore reloaded = JsonSettingsStore.Load(file);
        ISettingsStore again = reloaded.GetSection("plugins").GetSection("p");
        again.Get<List<string>>("list", []).Should().Equal("a", "b");
        again.Get("point", new Point(0, 0)).Should().Be(new Point(1, 2));
        again.Remove("count").Should().BeTrue();
        again.TryGet("count", out int _).Should().BeFalse();
        Action bad = () => again.Set("a/b", 1);
        bad.Should().Throw<ArgumentException>();
    }

    public sealed record Point(int X, int Y);

    private sealed class FakeAssistant(Func<MergeAssistantRequest, CancellationToken, Task<MergeAssistantResult>> run) : IMergeAssistant
    {
        public string Id => "fake";

        public string DisplayName => "Fake";

        public string? Description => null;

        public MergeAssistantCapabilities Capabilities => MergeAssistantCapabilities.None;

        public Task<MergeAssistantResult> RunAsync(MergeAssistantRequest request, CancellationToken cancellationToken) => run(request, cancellationToken);
    }

    private sealed class InlinePlugin : IDiffWeavePlugin
    {
        public IPluginHost? Host { get; private set; }

        public bool ShutdownCalled { get; private set; }

        public void Initialize(IPluginHost host)
        {
            Host = host;
            host.Commands.Register(new CommandContribution { Id = "inline.cmd", Title = "Cmd", Execute = (_, _) => Task.CompletedTask });
            host.LifecycleHooks.Register(new VetoSaveHook());
            host.LifecycleHooks.Register(new ThrowingHook());
            host.EditorExtensions.RegisterDecorationProvider(new GutterProvider());
            host.EditorExtensions.RegisterDecorationProvider(new ThrowingProvider());
            host.FileTypes.RegisterExtension(".foo", "foolang");
        }

        public void Shutdown() => ShutdownCalled = true;
    }

    private sealed class VetoSaveHook : IMergeLifecycleHook
    {
        public string Id => "veto";

        public Task<HookResult> BeforeSaveAsync(MergeLifecycleContext context, CancellationToken cancellationToken) => Task.FromResult(HookResult.Cancel("no"));
    }

    private sealed class ThrowingHook : IMergeLifecycleHook
    {
        public string Id => "throws";

        public Task<HookResult> BeforeResolveAsync(MergeLifecycleContext context, CancellationToken cancellationToken) => throw new InvalidOperationException();

        public Task AfterResolveAsync(MergeLifecycleContext context, CancellationToken cancellationToken) => throw new InvalidOperationException();
    }

    private sealed class GutterProvider : IEditorDecorationProvider
    {
        public string Id => "gutter";

        public event EventHandler? Invalidated
        {
            add { }
            remove { }
        }

        public Task<IReadOnlyList<EditorDecoration>> GetDecorationsAsync(DecorationRequest request, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<EditorDecoration>>([new EditorDecoration { Kind = DecorationKind.GutterMark, Line = 0, Glyph = "*" }]);
    }

    private sealed class ThrowingProvider : IEditorDecorationProvider
    {
        public string Id => "throwing";

        public event EventHandler? Invalidated
        {
            add { }
            remove { }
        }

        public Task<IReadOnlyList<EditorDecoration>> GetDecorationsAsync(DecorationRequest request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("provider failure");
    }
}
