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
}
