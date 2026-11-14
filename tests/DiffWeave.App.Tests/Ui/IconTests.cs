using Avalonia.Headless.NUnit;
using Avalonia.Media.Imaging;
using DiffWeave.App.Services;

namespace DiffWeave.App.Tests.Ui;

public class IconTests
{
    [AvaloniaTest]
    [TestCase(256)]
    [TestCase(64)]
    public void Icon_renders(int size)
    {
        using RenderTargetBitmap bmp = AppIcon.Render(size);
        bmp.PixelSize.Width.Should().Be(size);
        string path = Path.Combine(TestUtil.ScreenshotDirectory, $"icon-{size}.png");
        bmp.Save(path, PngBitmapEncoderOptions.Default);
        new FileInfo(path).Length.Should().BeGreaterThan(500);
    }
}
