using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(DiffWeave.App.Tests.TestAppBuilder))]
[assembly: NUnit.Framework.Parallelizable(NUnit.Framework.ParallelScope.None)]

namespace DiffWeave.App.Tests;

/// <summary>Builds the headless Avalonia application used by UI tests (real Skia rendering for screenshots).</summary>
public static class TestAppBuilder
{
    /// <summary>Entry point used by Avalonia.Headless.</summary>
    public static AppBuilder BuildAvaloniaApp()
    {
        DiffWeave.App.Services.AppPaths.ConfigDirectoryOverride = Path.Combine(Path.GetTempPath(), "diffweave-tests-config-" + Environment.ProcessId);
        return AppBuilder.Configure<App>()
            .UseSkia()
            .UseHarfBuzz()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }
}
