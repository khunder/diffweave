# Writing DiffWeave plugins

DiffWeave can be extended with plugins: .NET assemblies loaded at start-up from plugin folders. A plugin can

* add **commands** (menu items with optional keyboard shortcuts),
* add **merge assistants** that look at a merge and propose resolutions for regions,
* add **editor decorations** (gutter marks, line and inline highlights, tooltips),
* observe and veto merge operations with **lifecycle hooks** (before/after resolve, before save),
* register **file types** (extension → language id),
* store **settings** and write to the **plugin log**.

The complete, working example used throughout this page is the sample plugin in
[`samples/DiffWeave.Samples.WhitespacePlugin`](../samples/DiffWeave.Samples.WhitespacePlugin), with tests in
`samples/DiffWeave.Samples.WhitespacePlugin.Tests`.

## 1. Concepts

| Concept | Type (namespace `DiffWeave.Extensibility`) |
| --- | --- |
| API version | `ExtensibilityApi.ApiVersion` (currently `"1.0"`) |
| Entry point | `IDiffWeavePlugin { Initialize(IPluginHost); Shutdown(); }` |
| Manifest | `plugin.json` → `PluginManifest` |
| Host services | `IPluginHost`: `Commands`, `MergeAssistants`, `EditorExtensions`, `LifecycleHooks`, `FileTypes`, `Settings`, `Logger`, `Services` |
| Commands | `CommandContribution`, `CommandContext`, `IEditorContext` |
| Merge assistants | `IMergeAssistant`, `MergeAssistantRequest`, `MergeSnapshot`, `MergeAssistantResult`, `ResolutionProposal` |
| Decorations | `IEditorDecorationProvider`, `DecorationRequest`, `EditorDecoration` |
| Hooks | `IMergeLifecycleHook`, `MergeLifecycleContext`, `HookResult` |

Plugins reference **only** `DiffWeave.Extensibility`. It has no dependencies, and the host always supplies its own
copy at run time.

## 2. Create the project

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net11.0</TargetFramework>
    <Nullable>enable</Nullable>
    <!-- Produces a .deps.json so private dependencies of the plugin can be resolved. -->
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>
  <ItemGroup>
    <!-- Compile against the API, but do not copy it: the host provides it. -->
    <ProjectReference Include="path/to/DiffWeave.Extensibility.csproj">
      <Private>false</Private>
      <ExcludeAssets>runtime</ExcludeAssets>
    </ProjectReference>
  </ItemGroup>
  <ItemGroup>
    <None Include="plugin.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

If the plugin needs other NuGet packages, reference them normally: they are copied next to the plugin and resolved
from the plugin folder by the plugin's own load context, so two plugins can use different versions of a library.

## 3. Write the manifest

`plugin.json` (comments and trailing commas are allowed):

```json
{
  "id": "diffweave.samples.whitespace",
  "name": "Whitespace normalizer (sample)",
  "version": "1.0.0",
  "apiVersion": "[1.0,2.0)",
  "entryAssembly": "DiffWeave.Samples.WhitespacePlugin.dll",
  "entryType": "DiffWeave.Samples.WhitespacePlugin.WhitespacePlugin",
  "description": "Proposes resolutions for conflicts whose sides differ only in whitespace.",
  "authors": [ "DiffWeave contributors" ]
}
```

| Field | Rules |
| --- | --- |
| `id` | required; letters, digits, `.`, `-`, `_`; unique across installed plugins (duplicates are rejected) |
| `name` | required |
| `version` | required; `major.minor[.patch]`, an optional `-prerelease`/`+build` suffix is ignored for comparison |
| `apiVersion` | required; `"1.0"` means `[1.0,2.0)` (any 1.x host); interval notation `[1.0,1.4]`, `(1.0,)`, or `*` |
| `entryAssembly` | required; relative path to a `.dll` inside the plugin folder (no `..`) |
| `entryType` | required; full name of a public class implementing `IDiffWeavePlugin` with a parameterless constructor |
| `description`, `authors` | optional |

Versioning policy: additive changes to the API bump the minor version (old plugins keep working); breaking changes
bump the major version. Declare the lowest minor version whose members you use, up to the next major version.

## 4. Implement the entry point

```csharp
public sealed class WhitespacePlugin : IDiffWeavePlugin
{
    public void Initialize(IPluginHost host)
    {
        var assistant = new WhitespaceMergeAssistant();
        host.MergeAssistants.Register(assistant);
        host.EditorExtensions.RegisterDecorationProvider(new TrailingWhitespaceDecorations(host.Settings));
        host.Commands.Register(new CommandContribution
        {
            Id = "diffweave.samples.whitespace.resolve",
            Title = "Resolve whitespace-only conflicts",
            MenuPath = "Tools/Whitespace",
            Shortcut = "Ctrl+Alt+W",
            CanExecute = ctx => ctx.Editor?.Kind == EditorKind.Merge,
            Execute = async (ctx, ct) =>
            {
                MergeSnapshot? snapshot = ctx.Editor!.GetMergeSnapshot();
                if (snapshot is null) return;
                MergeAssistantResult result = await assistant.RunAsync(new MergeAssistantRequest { Snapshot = snapshot }, ct);
                await ctx.Editor.OfferProposalsAsync(result, assistant.DisplayName, ct);
            },
        });
    }

    public void Shutdown() { }
}
```

Everything registered through `host` is **removed automatically** when the plugin is unloaded (including settings
change handlers), so `Shutdown` only has to release resources the plugin created itself (timers, files, ...).
`Register` returns an `IDisposable` if you want to remove a contribution earlier.

### Commands

`MenuPath` is a `/`-separated path (`"Tools"`, `"Tools/Whitespace"`); `null` hides the command from menus.
`Shortcut` uses Avalonia `KeyGesture` syntax; gestures that clash with built-in ones are ignored by the host.
`CanExecute` must be cheap and side-effect free. `Execute` receives a `CommandContext` whose `Editor`
(`IEditorContext`) gives read access to the active document and two actions: `OfferProposalsAsync` (shows proposals
in the preview UI — nothing is applied without the user) and `ShowMessage`.

### Merge assistants

An assistant receives an immutable `MergeAssistantRequest`:

* `Snapshot`: base/left/right/output lines, file names, language id and all regions (`MergeRegionDescriptor`: id,
  kind, line spans in each input and in the output, resolved flag, whitespace-only flag),
* `TargetRegionIds` (empty = all unresolved conflicts), `UserInstruction`, `Options`.

It returns `ResolutionProposal`s (region id, replacement lines, rationale, confidence 0..1) and diagnostics. The
host runs assistants through `MergeAssistantRunner`: on the thread pool, with a timeout, exceptions turned into error
diagnostics, proposals for unknown regions or duplicates dropped, confidences clamped. Accepted proposals are applied
as **one undoable step** (`MergeSession.ApplyProposals`).

Declare `Capabilities` honestly: `Deterministic` for pure functions of the request, `RequiresUserInstruction` if the
UI must ask for text first, and `UsesNetwork` if any data leaves the process (the host then requires per-file consent;
see [ai-extension-points.md](ai-extension-points.md)).

### Editor decorations

`GetDecorationsAsync(DecorationRequest, CancellationToken)` is called on a background thread with a pane's lines and
returns `EditorDecoration`s. Use theme **style keys** (`Decoration.Info`, `Decoration.Warning`, `Decoration.Error`,
`Decoration.Accent`) instead of colours so light and dark themes work. Raise `Invalidated` to request recomputation.
A provider that throws or exceeds 2 seconds contributes nothing for that request.

### Lifecycle hooks

Implement only the methods you need (the interface has default implementations). `BeforeResolveAsync` and
`BeforeSaveAsync` can veto with `HookResult.Cancel("reason")`; the first veto wins. Hooks that throw or time out are
ignored and logged.

### Settings and logging

`host.Settings` is a JSON-backed store namespaced to the plugin id (`plugins/<id>/...` in the user's settings file).
Values can be any JSON-serializable type: `host.Settings.Set("threshold", 3)`, `Get("threshold", 1)`. Prefer
primitive and BCL types. `host.Logger` writes to the shared plugin log, shown in the Plugins dialog.

## 5. Install

Plugins are folders containing `plugin.json` plus the plugin's files. The App scans
(see [ui.md](ui.md) for the authoritative list):

* `<application directory>/plugins/<plugin folder>/`
* `$XDG_CONFIG_HOME/diffweave/plugins/<plugin folder>/` (Linux; `~/.config/...` by default) or the platform
  equivalent of the user configuration folder.

To install the sample after building it:

```bash
dotnet build samples/DiffWeave.Samples.WhitespacePlugin -c Release
mkdir -p ~/.config/diffweave/plugins/whitespace
cp artifacts/bin/DiffWeave.Samples.WhitespacePlugin/release/{DiffWeave.Samples.WhitespacePlugin.dll,DiffWeave.Samples.WhitespacePlugin.deps.json,plugin.json} \
   ~/.config/diffweave/plugins/whitespace/
```

## 6. Isolation and failure handling

* Each plugin is loaded into its own **collectible `AssemblyLoadContext`**. `DiffWeave.Extensibility` and
  `DiffWeave.Core` are always taken from the host (so the contract types are identical); every other dependency is
  resolved from the plugin folder.
* A plugin whose manifest is invalid, whose API range excludes the host, whose assembly or entry type is missing, or
  whose `Initialize` throws is shown in the Plugins dialog with its state (`InvalidManifest`, `Incompatible`,
  `Faulted`) and error text; the application keeps running. Contributions registered before a failing `Initialize`
  threw are rolled back.
* Commands, assistants, decoration providers and hooks are always invoked through guards that catch exceptions.
* On unload, registrations are removed, `Shutdown` is called (exceptions are logged), and the load context is
  unloaded. To be collectible, a plugin must not leave references to its objects in places the host does not track
  (static events of BCL types, threads that keep running, `GCHandle`s). The test suite verifies that the test plugin's
  load context is collected after unloading.

## 7. Testing a plugin

* Unit-test assistants directly: create a `MergeSession` with `DiffWeave.Core`, call `CreateSnapshot()`, run the
  assistant, and apply the proposals with `ApplyProposals` (see `WhitespacePluginTests`).
* End-to-end: create a `PluginManager` with a temporary plugin folder, `LoadAll()`, and use its registries
  (see `Loads_end_to_end_through_the_plugin_manager`).

## 8. Hosting plugins (for host authors)

```csharp
using var plugins = new PluginManager(new PluginManagerOptions
{
    PluginDirectories = [appPluginDir, userPluginDir],
    Settings = JsonSettingsStore.Load(settingsFile),
    DisabledPluginIds = userDisabledIds,
});
plugins.LoadAll();                                   // never throws for plugin failures
foreach (PluginInfo p in plugins.Plugins) { /* state, error, manifest for the Plugins dialog */ }
await plugins.Commands.ExecuteAsync(id, new CommandContext(editorContext));
MergeAssistantRunResult run = await plugins.RunAssistantAsync(assistant, request);
plugins.Unload(id);                                  // or Dispose() to unload all
```

`PluginManager.AddBuiltIn(manifest, plugin)` registers an in-process plugin (no separate load context) — useful for
built-in features implemented on the same API, and for tests.
