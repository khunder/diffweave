using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;

namespace DiffWeave.App.Tests;

/// <summary>Helpers for headless UI tests.</summary>
public static class TestUtil
{
    /// <summary>Directory receiving screenshots.</summary>
    public static string ScreenshotDirectory
    {
        get
        {
            string dir = Path.Combine(TestContext.CurrentContext.TestDirectory, "screenshots");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>Runs jobs and renders a frame.</summary>
    public static void Pump(this Window window)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Saves a screenshot of the window and returns the path.</summary>
    public static string SaveScreenshot(this Window window, string name)
    {
        window.Pump();
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame rendered.");
        string path = Path.Combine(ScreenshotDirectory, name + ".png");
        frame.Save(path, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        return path;
    }

    /// <summary>Creates a fresh temp directory.</summary>
    public static string CreateTempDirectory()
    {
        string dir = Path.Combine(Path.GetTempPath(), "diffweave-test-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
