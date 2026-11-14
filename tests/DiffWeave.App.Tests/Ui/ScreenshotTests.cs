using Avalonia.Headless.NUnit;
using DiffWeave.App.Services;

namespace DiffWeave.App.Tests.Ui;

/// <summary>Renders the main screens in light and dark themes and saves PNG screenshots.</summary>
public class ScreenshotTests
{
    [AvaloniaTest]
    [TestCase(ThemeMode.Light)]
    [TestCase(ThemeMode.Dark)]
    public async Task Two_way_diff(ThemeMode theme)
    {
        using UiHarness h = new(theme: theme);
        (string b, string l, _) = TestData.WriteMergeInputs(h.Directory);
        await h.ViewModel.OpenFilesAsync([b, l]);
        h.ViewModel.NextDifference();
        h.ViewModel.NextDifference();
        h.Window.SaveScreenshot($"diff-2way-{theme.ToString().ToLowerInvariant()}");
    }

    [AvaloniaTest]
    [TestCase(ThemeMode.Light)]
    [TestCase(ThemeMode.Dark)]
    public async Task Three_way_merge(ThemeMode theme)
    {
        using UiHarness h = new(theme: theme);
        (string b, string l, string r) = TestData.WriteMergeInputs(h.Directory);
        await h.ViewModel.OpenMergeAsync([b, l, r], ["Stock.cs (base)", "Stock.cs (local)", "Stock.cs (remote)"], Path.Combine(h.Directory, "Stock.cs"));
        h.Window.SaveScreenshot($"merge-3way-{theme.ToString().ToLowerInvariant()}");
    }

    [AvaloniaTest]
    [TestCase(ThemeMode.Light)]
    [TestCase(ThemeMode.Dark)]
    public async Task Directory_comparison(ThemeMode theme)
    {
        using UiHarness h = new(theme: theme);
        (string a, string b) = DirectoryViewTests.CreateTrees(h.Directory);
        DiffWeave.App.Views.DirectoryView? view = await h.ViewModel.OpenDirectoriesAsync([a, b]);
        view!.Document.ExpandAll();
        view.RowList.SelectedItem = view.Document.Rows.First(r => r.Name == "Stock.cs");
        h.Window.SaveScreenshot($"directory-{theme.ToString().ToLowerInvariant()}");
    }

    [AvaloniaTest]
    [TestCase(ThemeMode.Light)]
    [TestCase(ThemeMode.Dark)]
    public async Task Assistants_panel(ThemeMode theme)
    {
        string root = TestUtil.CreateTempDirectory();
        DiffWeave.App.Services.PluginService plugins = new([AssistantsPanelTests.InstallSamplePlugin(root)]);
        plugins.LoadAll();
        using UiHarness h = new(plugins, theme);
        (string b, string l, string r) = AssistantsPanelTests.WriteWhitespaceConflict(h.Directory);
        await h.ViewModel.OpenMergeAsync([b, l, r], ["ws.txt (base)", "ws.txt (local)", "ws.txt (remote)"], Path.Combine(h.Directory, "ws.txt"));
        h.ViewModel.IsAssistantsPanelVisible = true;
        await h.ViewModel.Assistants.RunForAllCommand.ExecuteAsync(null);
        h.Window.SaveScreenshot($"assistants-{theme.ToString().ToLowerInvariant()}");
    }

    [AvaloniaTest]
    [TestCase(ThemeMode.Light)]
    [TestCase(ThemeMode.Dark)]
    public void Assistants_panel_empty(ThemeMode theme)
    {
        using UiHarness h = new(theme: theme);
        h.ViewModel.IsAssistantsPanelVisible = true;
        h.ViewModel.ShowStartPage();
        h.Window.SaveScreenshot($"start-and-empty-assistants-{theme.ToString().ToLowerInvariant()}");
    }
}
