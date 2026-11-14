using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using DiffWeave.App.Controls;
using DiffWeave.App.Documents;
using DiffWeave.App.Editor;
using DiffWeave.App.Views;

namespace DiffWeave.App.Tests.Editor;

public class EditorBehaviourTests
{
    private static string[] Numbered(int n, string prefix = "line") => [.. Enumerable.Range(1, n).Select(i => $"{prefix} {i}")];

    [AvaloniaTest]
    public void Word_wrap_and_phantom_rows_change_the_row_model()
    {
        DiffTextView view = new() { Source = new ReadOnlyTextSource(["short", new string('x', 500), "end"]) };
        Window w = new() { Width = 400, Height = 300, Content = view };
        w.Show();
        w.Pump();
        view.RowCount.Should().Be(3);
        view.WordWrap = true;
        w.Pump();
        view.RowCount.Should().BeGreaterThan(5);
        view.GetVisibleRows().Where(r => r.Line == 1).Should().HaveCountGreaterThan(1);
        int wrapped = view.RowCount;
        view.SetPhantomRows([new PhantomRow(1, "placeholder", HighlightKind.Conflict), new PhantomRow(3, "at end", HighlightKind.Conflict)]);
        view.RowCount.Should().Be(wrapped + 2);
        (double top, double bottom) = view.GetSpanBounds(new LineSpan(1, 0));
        (bottom - top).Should().BeApproximately(view.RowHeight, 0.01);
        view.LineToRow(2).Should().Be(view.RowCount - 2);
        view.GetVisibleRows().Count(r => r.IsPhantom).Should().Be(2);
        w.SaveScreenshot("editor-wrap-phantoms");
        w.Close();
    }

    [AvaloniaTest]
    public void Selection_copy_and_paste_through_the_clipboard()
    {
        EditableTextSource source = new("alpha beta\ngamma");
        DiffTextView view = new() { Source = source, IsReadOnly = false };
        Window w = new() { Width = 400, Height = 300, Content = view };
        w.Show();
        w.Pump();
        view.Focus();
        view.Select(new TextRange(new TextPos(0, 6), new TextPos(0, 10)));
        view.SelectedText.Should().Be("beta");
        w.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
        w.Pump();
        view.SetCaret(new TextPos(1, 5));
        w.KeyPress(Key.V, RawInputModifiers.Control, PhysicalKey.V, "v");
        for (int i = 0; i < 10 && source.Text.EndsWith("gamma", StringComparison.Ordinal); i++)
        {
            w.Pump();
        }

        source.Text.Should().Be("alpha beta\ngammabeta");
        w.KeyPress(Key.Left, RawInputModifiers.Shift | RawInputModifiers.Control, PhysicalKey.ArrowLeft, null);
        view.SelectedText.Should().Be("gammabeta");
        w.KeyPress(Key.Back, RawInputModifiers.None, PhysicalKey.Backspace, null);
        source.Text.Should().Be("alpha beta\n");
        w.Close();
    }

    [AvaloniaTest]
    public void Mouse_selects_and_double_click_selects_a_word()
    {
        DiffTextView view = new() { Source = new ReadOnlyTextSource(["hello wonderful world"]) };
        Window w = new() { Width = 500, Height = 200, Content = view };
        w.Show();
        w.Pump();
        double y = view.RowHeight / 2;
        double x = view.TextAreaLeft + (view.CharWidth * 8.2);
        w.MouseDown(new Point(x, y), MouseButton.Left);
        w.MouseUp(new Point(x, y), MouseButton.Left);
        w.MouseDown(new Point(x, y), MouseButton.Left);
        w.MouseUp(new Point(x, y), MouseButton.Left);
        view.SelectedText.Should().Be("wonderful");
        w.Close();
    }

    [AvaloniaTest]
    public async Task Synchronized_scrolling_keeps_corresponding_lines_together()
    {
        using UiHarness h = new();
        string a = Path.Combine(h.Directory, "a.txt"), b = Path.Combine(h.Directory, "b.txt");
        List<string> left = [.. Numbered(2000)];
        List<string> right = [.. left];
        right.InsertRange(100, Numbered(300, "inserted"));
        File.WriteAllLines(a, left);
        File.WriteAllLines(b, right);
        await h.ViewModel.OpenFilesAsync([a, b]);
        h.Window.Pump();
        DiffDocumentView dv = h.ViewModel.SelectedTab!.DiffView!;
        DiffTextView va = dv.Panes[0].View, vb = dv.Panes[1].View;

        va.ScrollToLine(1000);
        h.Window.Pump();
        double midA = va.GetLineAtY(va.ViewportHeight / 2);
        double midB = vb.GetLineAtY(vb.ViewportHeight / 2);
        (midB - midA).Should().BeApproximately(300, 1.5, "line N of A sits next to line N+300 of B");
        va.HorizontalOffset = 0;
        vb.HorizontalOffset.Should().Be(va.HorizontalOffset);

        // Scrolling B through the inserted block keeps A on the insertion point.
        vb.ScrollToLine(200);
        h.Window.Pump();
        double a1 = va.GetLineAtY(va.ViewportHeight / 2);
        vb.ScrollToLine(250);
        h.Window.Pump();
        va.GetLineAtY(va.ViewportHeight / 2).Should().BeApproximately(a1, 0.5);
    }

    [AvaloniaTest]
    public async Task Ruler_click_and_band_click_navigate()
    {
        using UiHarness h = new();
        string a = Path.Combine(h.Directory, "a.txt"), b = Path.Combine(h.Directory, "b.txt");
        List<string> left = [.. Numbered(3000)];
        List<string> right = [.. left];
        right[2500] = "changed";
        right[10] = "changed early";
        File.WriteAllLines(a, left);
        File.WriteAllLines(b, right);
        ComparisonDocument? doc = await h.ViewModel.OpenFilesAsync([a, b]);
        h.Window.Pump();
        DiffDocumentView dv = h.ViewModel.SelectedTab!.DiffView!;
        OverviewRuler ruler = dv.Ruler;
        Point p = ruler.TranslatePoint(new Point(ruler.Bounds.Width / 2, ruler.Bounds.Height * 2500 / 3000), h.Window)!.Value;
        h.Window.MouseDown(p, MouseButton.Left);
        h.Window.MouseUp(p, MouseButton.Left);
        h.Window.Pump();
        DiffTextView vb = dv.Panes[1].View;
        vb.TopLine.Should().BeInRange(2300, 2500);

        // Click the band ribbon of the block at line 2500 to make it current.
        DiffBand band = dv.Bands[0];
        DiffBlock block = doc!.Mapping!.Blocks.Single(x => x.Spans[1].Start == 2500);
        (double top, double bottom) = vb.GetSpanBounds(block.Spans[1]);
        Point bp = band.TranslatePoint(new Point(band.Bounds.Width / 2, ((top + bottom) / 2) + (vb.TranslatePoint(default, band)!.Value.Y)), h.Window)!.Value;
        h.Window.MouseDown(bp, MouseButton.Left);
        h.Window.MouseUp(bp, MouseButton.Left);
        doc.CurrentBlock.Should().Be(block.Index);
    }

    [AvaloniaTest]
    public async Task Large_files_open_and_scroll_with_virtualized_rendering()
    {
        using UiHarness h = new();
        string a = Path.Combine(h.Directory, "big-a.txt"), b = Path.Combine(h.Directory, "big-b.txt");
        string[] left = [.. Enumerable.Range(0, 200_000).Select(i => $"{i:D6} The quick brown fox jumps over the lazy dog {i % 97}")];
        string[] right = [.. left];
        for (int i = 50; i < right.Length; i += 1000)
        {
            right[i] = right[i].Replace("lazy", "sleepy", StringComparison.Ordinal);
        }

        File.WriteAllLines(a, left);
        File.WriteAllLines(b, right);
        Stopwatch sw = Stopwatch.StartNew();
        ComparisonDocument? doc = await h.ViewModel.OpenFilesAsync([a, b]);
        h.Window.Pump();
        TimeSpan open = sw.Elapsed;
        doc!.DifferenceCount.Should().Be(200);
        DiffTextView view = h.ViewModel.SelectedTab!.DiffView!.Panes[0].View;
        sw.Restart();
        for (int i = 0; i < 50; i++)
        {
            view.VerticalOffset += view.RowHeight * 3000;
            h.Window.Pump();
        }

        TimeSpan scroll = sw.Elapsed;
        view.CachedLayoutCount.Should().BeLessThan(4100);
        view.GetVisibleRows().Count.Should().BeLessThan(80);
        TestContext.Out.WriteLine($"open {open.TotalMilliseconds:0} ms, 50 scroll frames {scroll.TotalMilliseconds:0} ms");
        open.Should().BeLessThan(TimeSpan.FromSeconds(15));
        scroll.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }
}
