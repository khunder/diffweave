using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Headless;
using DiffWeave.App.Controls;
using DiffWeave.App.Editor;

namespace DiffWeave.App.Tests.Editor;

public class DiffTextViewTests
{
    private static (Window Window, DiffTextView View) Show(IEditorTextSource source, double width = 600, double height = 400)
    {
        DiffTextView view = new() { Source = source };
        Window window = new() { Width = width, Height = height, Content = view };
        window.Show();
        window.Pump();
        return (window, view);
    }

    private static string[] Lines(int n) => [.. Enumerable.Range(1, n).Select(i => $"line {i} " + new string('x', i % 50))];

    [AvaloniaTest]
    public void Renders_only_visible_rows_for_large_documents()
    {
        (Window window, DiffTextView view) = Show(new ReadOnlyTextSource(Lines(200_000)));
        view.LineCount.Should().Be(200_000);
        view.GetVisibleRows().Count.Should().BeLessThan(40);
        view.CachedLayoutCount.Should().BeLessThan(40);

        view.ScrollToLine(150_000);
        window.Pump();
        view.GetVisibleRows()[0].Line.Should().Be(150_000);
        view.CachedLayoutCount.Should().BeLessThan(80);
        window.Close();
    }

    [AvaloniaTest]
    public void Typing_and_undo_in_editable_source()
    {
        EditableTextSource source = new("hello\nworld");
        (Window window, DiffTextView view) = Show(source);
        view.IsReadOnly = false;
        view.Focus();
        view.SetCaret(new TextPos(0, 5));
        window.KeyTextInput(" there");
        source.Text.Should().Be("hello there\nworld");
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        source.Text.Should().Be("hello there\n\nworld");
        window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        source.Text.Should().Be("hello there\nworld");
        window.KeyPress(Key.Back, RawInputModifiers.None, PhysicalKey.Backspace, null);
        source.Text.Should().Be("hello ther\nworld");
        window.Close();
    }

    [AvaloniaTest]
    public void Screenshot_basic_rendering()
    {
        string[] lines = ["using System;", "", "namespace Demo;", "", "public static class Program", "{", "\tpublic static void Main()", "\t{", "\t\tConsole.WriteLine(\"Hello\");", "\t}", "}"];
        StaticDecorationSource deco = new();
        deco.Add(new LineDecoration(2, HighlightKind.Changed));
        deco.Add(new InlineDecoration(2, 10, 4, HighlightKind.Changed));
        deco.Add(new LineDecoration(8, HighlightKind.Inserted));
        deco.Add(new GapMarker(5, HighlightKind.Deleted));
        deco.Add(new GutterMark(4, HighlightKind.Warning, "test"));
        (Window window, DiffTextView view) = Show(new ReadOnlyTextSource(lines));
        view.DecorationSources.Add(deco);
        view.ShowWhitespace = true;
        view.Select(new TextRange(new TextPos(4, 7), new TextPos(4, 13)));
        window.SaveScreenshot("editor-basic");
        window.Close();
    }
}
