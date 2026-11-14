using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using DiffWeave.App.Controls;
using DiffWeave.App.Documents;
using DiffWeave.App.Editor;
using DiffWeave.App.Services;
using DiffWeave.Core.Merge;

namespace DiffWeave.App.Tests.Ui;

public class MainWindowTests
{
    private static async Task<(UiHarness H, MergeDocument Doc, string Output)> OpenMergeAsync()
    {
        UiHarness h = new();
        (string b, string l, string r) = TestData.WriteMergeInputs(h.Directory);
        string output = Path.Combine(h.Directory, "Stock.cs");
        MergeDocument? doc = await h.ViewModel.OpenMergeAsync([b, l, r], null, output);
        doc.Should().NotBeNull();
        h.Window.Pump();
        return (h, doc!, output);
    }

    [AvaloniaTest]
    public async Task Two_way_diff_navigation_with_keyboard()
    {
        using UiHarness h = new();
        (string b, string l, _) = TestData.WriteMergeInputs(h.Directory);
        ComparisonDocument? doc = await h.ViewModel.OpenFilesAsync([b, l]);
        doc!.DifferenceCount.Should().BeGreaterThan(1);
        h.Window.Pump();
        h.Window.KeyPress(Key.Down, RawInputModifiers.Control, PhysicalKey.ArrowDown, null);
        doc.CurrentBlock.Should().BeGreaterThanOrEqualTo(0);
        int first = doc.CurrentBlock;
        h.Window.KeyPress(Key.Down, RawInputModifiers.Control, PhysicalKey.ArrowDown, null);
        doc.CurrentBlock.Should().BeGreaterThan(first);
        h.ViewModel.DifferenceStatus.Should().StartWith("Difference 2 of");
        h.Window.KeyPress(Key.Up, RawInputModifiers.Control, PhysicalKey.ArrowUp, null);
        doc.CurrentBlock.Should().Be(first);

        // The current block is framed in both panes.
        DiffWeave.App.Views.DiffDocumentView view = h.ViewModel.SelectedTab!.DiffView!;
        view.Panes[0].View.CurrentRegion.Should().NotBeNull();
        view.Panes[1].View.CurrentRegion.Should().Be(doc.Current!.Spans[1]);
        view.Bands.Should().HaveCount(1);
        view.Bands[0].CurrentBlock.Should().Be(doc.CurrentBlock);
    }

    [AvaloniaTest]
    public async Task Pane_headers_show_encoding_eol_and_line_count()
    {
        using UiHarness h = new();
        (string b, string l, _) = TestData.WriteMergeInputs(h.Directory);
        await h.ViewModel.OpenFilesAsync([b, l], ["Base label", null]);
        EditorPane pane = h.ViewModel.SelectedTab!.DiffView!.Panes[0];
        pane.TitleText.Should().Be("Base label");
        pane.InfoText.Should().Be($"UTF-8 · LF · {TestData.Base.Split('\n').Length - 1} lines");
    }

    [AvaloniaTest]
    public async Task Single_file_is_compared_with_an_empty_pane()
    {
        using UiHarness h = new();
        (string b, _, _) = TestData.WriteMergeInputs(h.Directory);
        ComparisonDocument? doc = await h.ViewModel.OpenFilesAsync([b]);
        doc!.Inputs.Should().HaveCount(2);
        doc.Inputs[1].Lines.Should().BeEmpty();
        doc.DifferenceCount.Should().Be(1);
    }

    [AvaloniaTest]
    public async Task Ignore_whitespace_rediffs_live()
    {
        using UiHarness h = new();
        string a = Path.Combine(h.Directory, "a.txt"), b = Path.Combine(h.Directory, "b.txt");
        File.WriteAllText(a, "one\ntwo  three\nfour\n");
        File.WriteAllText(b, "one\ntwo three\nFOUR\n");
        ComparisonDocument? doc = await h.ViewModel.OpenFilesAsync([a, b]);
        doc!.DifferenceCount.Should().Be(1);
        await h.ViewModel.SetWhitespaceCommand.ExecuteAsync("IgnoreChanges");
        doc.DifferenceCount.Should().Be(1);
        await h.ViewModel.ToggleIgnoreCaseCommand.ExecuteAsync(null);
        doc.DifferenceCount.Should().Be(0);
        h.ViewModel.DifferenceStatus.Should().Be("No differences");
        await h.ViewModel.SetWhitespaceCommand.ExecuteAsync("Compare");
        await h.ViewModel.ToggleIgnoreCaseCommand.ExecuteAsync(null);
        doc.DifferenceCount.Should().Be(1);
    }

    [AvaloniaTest]
    public async Task Merge_status_and_choosing_with_ctrl_digits()
    {
        (UiHarness h, MergeDocument doc, string _) = await OpenMergeAsync();
        using UiHarness owner = h;
        doc.ConflictCount.Should().Be(2);
        doc.UnresolvedCount.Should().Be(2);
        h.ViewModel.ConflictStatus.Should().Be("2 conflicts, 2 unresolved");
        doc.Session.Regions[doc.CurrentRegion].IsConflict.Should().BeTrue("the first unresolved conflict is selected");

        h.Window.KeyPress(Key.D2, RawInputModifiers.Control, PhysicalKey.Digit2, "2");
        doc.UnresolvedCount.Should().Be(1);
        h.ViewModel.ConflictStatus.Should().Be("2 conflicts, 1 unresolved");

        h.Window.KeyPress(Key.PageDown, RawInputModifiers.Control, PhysicalKey.PageDown, null);
        doc.Session.GetResolution(doc.CurrentRegion).IsResolved.Should().BeFalse();
        h.Window.KeyPress(Key.D3, RawInputModifiers.Control, PhysicalKey.Digit3, "3");
        doc.UnresolvedCount.Should().Be(0);
        doc.OutputSource.Output.Lines.Should().Contain(l => l.Contains("Stock with {Count} items", StringComparison.Ordinal));

        // Undo through the window (focus is not in the output editor).
        h.Window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        doc.UnresolvedCount.Should().Be(1);
    }

    [AvaloniaTest]
    public async Task Plain_letter_keys_choose_when_focus_is_in_an_input_pane()
    {
        (UiHarness h, MergeDocument doc, string _) = await OpenMergeAsync();
        using UiHarness owner = h;
        DiffTextView left = h.ViewModel.SelectedTab!.DiffView!.GetPane(1)!.View;
        left.Focus();
        doc.GoToNextUnresolvedConflict();
        int region = doc.CurrentRegion;
        h.Window.KeyPress(Key.A, RawInputModifiers.None, PhysicalKey.A, "a");
        doc.Session.GetResolution(region).Choice.Should().Be(ResolutionChoice.Base);
    }

    [AvaloniaTest]
    public async Task Typing_in_the_output_pane_edits_the_merge_and_undo_restores_it()
    {
        (UiHarness h, MergeDocument doc, string _) = await OpenMergeAsync();
        using UiHarness owner = h;
        DiffTextView output = h.ViewModel.SelectedTab!.DiffView!.OutputPane!.View;
        output.CanEdit.Should().BeTrue();
        output.Focus();
        output.SetCaret(new TextPos(0, 0));
        string before = output.Source!.GetLine(0);
        h.Window.KeyTextInput("// edited");
        h.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        h.Window.Pump();
        output.Source.GetLine(0).Should().Be("// edited");
        output.Source.GetLine(1).Should().Be(before);
        doc.IsDirty.Should().BeTrue();

        // Each edit is one undo step of the merge session (typing, then Enter).
        h.Window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        output.Source.GetLine(0).Should().Be("// edited" + before);
        h.Window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        output.Source.GetLine(0).Should().Be(before);
        doc.Session.CanRedo.Should().BeTrue();
        h.Window.KeyPress(Key.Y, RawInputModifiers.Control, PhysicalKey.Y, "y");
        h.Window.KeyPress(Key.Y, RawInputModifiers.Control, PhysicalKey.Y, "y");
        output.Source.GetLine(0).Should().Be("// edited");
    }

    [AvaloniaTest]
    public async Task Save_writes_the_output_and_exit_code_reflects_resolution()
    {
        (UiHarness h, MergeDocument doc, string output) = await OpenMergeAsync();
        using UiHarness owner = h;
        h.ViewModel.TrackExitCodeOf(doc);
        h.ViewModel.ExitCode.Should().Be(1);

        // Saving with unresolved conflicts asks; cancel keeps the file unsaved.
        h.Dialogs.Answers.Enqueue(1);
        (await h.ViewModel.SaveAsync()).Should().BeFalse();
        File.Exists(output).Should().BeFalse();

        doc.ResolveAll(ResolutionChoice.Left);
        h.Window.KeyPress(Key.S, RawInputModifiers.Control, PhysicalKey.S, "s");
        h.Window.Pump();
        File.Exists(output).Should().BeTrue();
        File.ReadAllText(output).Should().Contain("Total =>").And.NotContain("<<<<<<<");
        doc.IsDirty.Should().BeFalse();
        h.ViewModel.ExitCode.Should().Be(0);
    }

    [AvaloniaTest]
    public async Task Unsaved_changes_guard_asks_before_closing_a_tab()
    {
        (UiHarness h, MergeDocument doc, string _) = await OpenMergeAsync();
        using UiHarness owner = h;
        h.Dialogs.Answers.Enqueue(2); // Cancel
        await h.ViewModel.CloseTabCommand.ExecuteAsync(null);
        h.ViewModel.Tabs.Should().ContainSingle(t => t.Diff == doc);
        h.Dialogs.Questions.Should().ContainSingle(q => q.StartsWith("Save merge result?", StringComparison.Ordinal));

        h.Dialogs.Answers.Enqueue(1); // Don't save
        await h.ViewModel.CloseTabCommand.ExecuteAsync(null);
        h.ViewModel.Tabs.Should().NotContain(t => t.Diff == doc);
    }

    [AvaloniaTest]
    public async Task Find_and_go_to_line()
    {
        using UiHarness h = new();
        (string b, string l, _) = TestData.WriteMergeInputs(h.Directory);
        await h.ViewModel.OpenFilesAsync([b, l]);
        h.Window.Pump();
        DiffTextView view = h.ViewModel.SelectedTab!.DiffView!.Panes[1].View;
        view.Focus();
        h.Window.Pump();
        h.Window.ActiveTextView.Should().BeSameAs(view);
        h.Window.KeyPress(Key.F, RawInputModifiers.Control, PhysicalKey.F, "f");
        h.Window.IsFindBarVisible.Should().BeTrue();
        h.Window.FindTextBox.Text = "remove";
        h.Window.Pump();
        h.Window.FindNext(false).Should().BeTrue();
        view.SelectedText.Should().Be("Remove");
        view.FindHighlight.Should().NotBeNull();
        h.Window.FindNext(false).Should().BeTrue();
        view.CaretPosition.Line.Should().BeGreaterThan(20);

        h.Window.KeyPress(Key.G, RawInputModifiers.Control, PhysicalKey.G, "g");
        h.Window.KeyTextInput("\b\b\b");
        view.GoToLine(4);
        view.CaretPosition.Should().Be(new TextPos(4, 0));
    }

    [AvaloniaTest]
    public async Task Theme_and_palette_switch_without_errors()
    {
        using UiHarness h = new();
        (string b, string l, _) = TestData.WriteMergeInputs(h.Directory);
        await h.ViewModel.OpenFilesAsync([b, l]);
        h.ViewModel.SetThemeCommand.Execute("Dark");
        h.ViewModel.TogglePaletteCommand.Execute(null);
        h.Window.Pump();
        h.ViewModel.Settings.Palette.Should().Be(PaletteMode.ColorBlind);
        h.Window.SaveScreenshot("diff-2way-dark-colorblind");
        h.ViewModel.TogglePaletteCommand.Execute(null);
        h.ViewModel.SetThemeCommand.Execute("Light");
        new SettingsStore(Path.Combine(h.Directory, "config", "settings.json")).Load().Theme.Should().Be(ThemeMode.Light);
    }

    [AvaloniaTest]
    public async Task Files_changed_on_disk_show_a_reload_bar()
    {
        using UiHarness h = new();
        (string b, string l, _) = TestData.WriteMergeInputs(h.Directory);
        ComparisonDocument? doc = await h.ViewModel.OpenFilesAsync([b, l]);
        File.WriteAllText(l, TestData.Left + "// appended\n");
        File.SetLastWriteTimeUtc(l, DateTime.UtcNow.AddMinutes(1));
        h.ViewModel.CheckFilesChangedOnDisk();
        h.ViewModel.HasChangedOnDisk.Should().BeTrue();
        await h.ViewModel.ReloadCommand.ExecuteAsync(null);
        h.ViewModel.HasChangedOnDisk.Should().BeFalse();
        h.ViewModel.ActiveDiff.Should().NotBeSameAs(doc);
        h.ViewModel.ActiveDiff!.Inputs[1].Lines[^1].Should().Be("// appended");
        h.ViewModel.Tabs.Should().ContainSingle();
    }

    [AvaloniaTest]
    public async Task Settings_persist_recent_files_and_layout()
    {
        using UiHarness h = new();
        (string b, string l, string r) = TestData.WriteMergeInputs(h.Directory);
        await h.ViewModel.OpenFilesAsync([b, l, r]);
        AppSettings s = new SettingsStore(Path.Combine(h.Directory, "config", "settings.json")).Load();
        s.LastLayout.Should().Be("ThreeWay");
        s.RecentFiles[0].Should().Equal(b, l, r);
    }
}
