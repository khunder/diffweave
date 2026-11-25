using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffWeave.App.Cli;
using DiffWeave.App.Controls;
using DiffWeave.App.Documents;
using DiffWeave.App.Editor;
using DiffWeave.App.Services;
using DiffWeave.App.Views;
using DiffWeave.Core.Diff;
using DiffWeave.Core.Merge;
using DiffWeave.Extensibility;

namespace DiffWeave.App.ViewModels;

/// <summary>What the view model needs from the window.</summary>
public interface IMainView
{
    /// <summary>The text view that had focus last (for find, clipboard, go to line).</summary>
    DiffTextView? ActiveTextView { get; }

    /// <summary>Dialogs.</summary>
    IDialogService Dialogs { get; }

    /// <summary>Shows the find bar.</summary>
    void ShowFindBar();

    /// <summary>Shows the go-to-line bar.</summary>
    void ShowGoToLineBar();

    /// <summary>Applies appearance settings (theme, palette, fonts).</summary>
    void ApplyAppearance();

    /// <summary>Rebuilds the plugin menu.</summary>
    void RebuildPluginMenu();

    /// <summary>Closes the window (after the unsaved-changes guard).</summary>
    void CloseWindow();

    /// <summary>Opens a file or URL with the desktop.</summary>
    void OpenExternal(string pathOrUrl);
}

/// <summary>State and commands of the main window.</summary>
public sealed partial class MainWindowViewModel : ObservableObject, IDocumentHost
{
    private readonly SettingsStore _settingsStore;
    private IMainView? _view;
    private CancellationTokenSource? _decorationCts;

    /// <summary>Creates the view model.</summary>
    public MainWindowViewModel(SettingsStore settingsStore, PluginService plugins)
    {
        _settingsStore = settingsStore;
        Settings = settingsStore.Load();
        Plugins = plugins;
        Assistants = new AssistantsViewModel(plugins, () => ActiveDiff);
        Assistants.Message += (_, m) => ShowMessage(m, MessageSeverity.Information);
        plugins.Changed += (_, _) =>
        {
            _view?.RebuildPluginMenu();
            _ = RefreshPluginDecorationsAsync();
        };
        IsAssistantsPanelVisible = Settings.ShowAssistantsPanel;
    }

    /// <summary>Persisted settings.</summary>
    public AppSettings Settings { get; }

    /// <summary>The plugin host.</summary>
    public PluginService Plugins { get; }

    /// <summary>The assistants panel model.</summary>
    public AssistantsViewModel Assistants { get; }

    /// <summary>Open tabs.</summary>
    public ObservableCollection<DocumentTab> Tabs { get; } = [];

    /// <summary>The selected tab.</summary>
    [ObservableProperty]
    public partial DocumentTab? SelectedTab { get; set; }

    /// <summary>Status bar message.</summary>
    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "Ready";

    /// <summary>"Difference 2 of 7".</summary>
    [ObservableProperty]
    public partial string DifferenceStatus { get; set; } = string.Empty;

    /// <summary>"3 conflicts, 1 unresolved".</summary>
    [ObservableProperty]
    public partial string ConflictStatus { get; set; } = string.Empty;

    /// <summary>"Ln 4, Col 7".</summary>
    [ObservableProperty]
    public partial string CaretStatus { get; set; } = string.Empty;

    /// <summary>Message of the "changed on disk" bar, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChangedOnDisk))]
    public partial string? ChangedOnDiskMessage { get; set; }

    /// <summary>True when the "changed on disk" bar is visible.</summary>
    public bool HasChangedOnDisk => ChangedOnDiskMessage is not null;

    /// <summary>Assistants panel visibility.</summary>
    [ObservableProperty]
    public partial bool IsAssistantsPanelVisible { get; set; }

    /// <summary>True when scrolling is synchronized.</summary>
    [ObservableProperty]
    public partial bool IsSyncScrolling { get; set; } = true;

    /// <summary>The active diff or merge document.</summary>
    public DiffDocument? ActiveDiff => SelectedTab?.Diff;

    /// <summary>The active merge document.</summary>
    public MergeDocument? ActiveMerge => ActiveDiff as MergeDocument;

    /// <summary>True when a diff/merge is active.</summary>
    public bool HasDiff => ActiveDiff is not null;

    /// <summary>True when a merge is active.</summary>
    public bool IsMerge => ActiveMerge is not null;

    /// <summary>True when a directory comparison is active.</summary>
    public bool IsDirectory => SelectedTab?.DirectoryView is not null;

    /// <summary>Exit code decided at shutdown.</summary>
    public int ExitCode
    {
        get
        {
            if (StartedAsMerge is { } merge)
            {
                return merge.WasSaved && merge.SavedResolved && !merge.IsDirty ? ExitCodes.Success : ExitCodes.Cancelled;
            }

            return ExitCodes.Success;
        }
    }

    /// <summary>The merge opened from the command line (decides the exit code).</summary>
    public MergeDocument? StartedAsMerge { get; private set; }

    /// <summary>Makes <paramref name="merge"/> decide the process exit code (as if opened from the command line).</summary>
    public void TrackExitCodeOf(MergeDocument merge) => StartedAsMerge = merge;

    /// <summary>Connects the window.</summary>
    public void Attach(IMainView view) => _view = view;

    /// <summary>The current diff options from settings.</summary>
    public DiffOptions CurrentDiffOptions => NonInteractive.OptionsFrom(Settings);

    // ---------------------------------------------------------------- opening

    /// <summary>Opens what the command line asked for.</summary>
    public async Task OpenFromCommandLineAsync(CommandLineOptions options)
    {
        string? L(int i) => i < options.Labels.Count ? options.Labels[i] : null;
        switch (options.Mode)
        {
            case LaunchMode.FileDiff:
                await OpenFilesAsync(options.Inputs, [L(0), L(1), L(2)]);
                break;
            case LaunchMode.FileMerge:
                MergeDocument? merge = await OpenMergeAsync(options.Inputs, [L(0), L(1), L(2)], options.Output);
                StartedAsMerge = merge;
                break;
            case LaunchMode.DirectoryDiff:
                await OpenDirectoriesAsync(options.Inputs, [L(0), L(1), L(2)]);
                break;
            default:
                ShowStartPage();
                break;
        }
    }

    /// <summary>Shows the start page tab.</summary>
    public void ShowStartPage()
    {
        if (Tabs.FirstOrDefault(t => t.IsStartPage) is { } existing)
        {
            SelectedTab = existing;
            return;
        }

        StartPage page = new(this);
        AddTab(new DocumentTab("Start", page));
    }

    private static async Task<InputFile> LoadInputAsync(string path, string? label)
    {
        if (InputFile.IsNullDevice(path))
        {
            // git passes /dev/null for the missing side of an added or deleted file.
            return InputFile.FromText(label ?? "(no file)", string.Empty);
        }

        if (!File.Exists(path) && !Path.Exists(path))
        {
            return InputFile.FromText(label ?? Path.GetFileName(path) + " (missing)", string.Empty, Path.GetFullPath(path));
        }

        return await InputFile.LoadAsync(path, label);
    }

    /// <summary>Opens a comparison of 1–3 files (one file is compared with an empty pane).</summary>
    public async Task<ComparisonDocument?> OpenFilesAsync(IReadOnlyList<string> paths, IReadOnlyList<string?>? labels = null)
    {
        try
        {
            List<InputFile> inputs = [];
            for (int i = 0; i < paths.Count; i++)
            {
                inputs.Add(await LoadInputAsync(paths[i], labels is not null && i < labels.Count ? labels[i] : null));
            }

            if (inputs.Count == 1)
            {
                inputs.Add(InputFile.Empty("(empty)"));
            }

            ComparisonDocument doc = new(inputs) { Options = CurrentDiffOptions, Host = this };
            doc.LanguageId = Plugins.Manager.FileTypes.DetectLanguage(inputs[0].Path, inputs[0].Lines.FirstOrDefault());
            await doc.RecomputeAsync();
            AddDocument(doc);
            Settings.AddRecent([.. paths]);
            Settings.LastLayout = inputs.Count == 2 ? "TwoWay" : "ThreeWay";
            SaveSettings();
            ShowMessage(doc.DifferenceCount == 0 ? "The files are identical." : $"{doc.DifferenceCount} difference(s).", MessageSeverity.Information);
            return doc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await ReportErrorAsync("Cannot open files", ex.Message);
            return null;
        }
    }

    /// <summary>Opens a merge: three inputs = base, left, right; two inputs = left, right without base.</summary>
    public async Task<MergeDocument?> OpenMergeAsync(IReadOnlyList<string> paths, IReadOnlyList<string?>? labels, string? output)
    {
        try
        {
            List<InputFile> inputs = [];
            for (int i = 0; i < paths.Count; i++)
            {
                inputs.Add(await LoadInputAsync(paths[i], labels is not null && i < labels.Count ? labels[i] : null));
            }

            InputFile? baseFile = inputs.Count == 3 ? inputs[0] : null;
            InputFile left = inputs[^2], right = inputs[^1];
            MergeDocument doc = await MergeDocument.CreateAsync(baseFile, left, right, output, CurrentDiffOptions);
            doc.Host = this;
            doc.LifecycleHooks = Plugins.Manager.LifecycleHooks;
            doc.LanguageId = Plugins.Manager.FileTypes.DetectLanguage(output ?? left.Path, left.Lines.FirstOrDefault());
            AddDocument(doc);
            if (!doc.GoToNextUnresolvedConflict())
            {
                doc.GoToFirstDifference();
            }

            Settings.AddRecent([.. paths]);
            Settings.LastLayout = "Merge";
            SaveSettings();
            ShowMessage(doc.UnresolvedCount == 0
                ? "All changes were merged automatically. Review and save (Ctrl+S)."
                : $"{doc.UnresolvedCount} unresolved — use Ctrl+1/2/3 to choose A, B or C.", MessageSeverity.Information);
            return doc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await ReportErrorAsync("Cannot open merge", ex.Message);
            return null;
        }
    }

    /// <summary>Opens a directory comparison.</summary>
    public async Task<DirectoryView?> OpenDirectoriesAsync(IReadOnlyList<string> paths, IReadOnlyList<string?>? labels = null)
    {
        DirectoryDocument doc = new(paths, labels);
        DirectoryView view = new(doc, _view?.Dialogs);
        view.OpenRequested += async (_, row) => await OpenFromDirectoryRowAsync(doc, row);
        view.StatusMessage += (_, m) => ShowMessage(m, MessageSeverity.Information);
        AddTab(new DocumentTab(string.Join(" ↔ ", doc.Roots.Select(r => Path.GetFileName(r.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } n ? n : r)), view));
        Settings.AddRecent([.. paths]);
        Settings.LastLayout = "Directory";
        SaveSettings();
        try
        {
            await view.RefreshAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await ReportErrorAsync("Cannot compare directories", ex.Message);
        }

        UpdateStatus();

        return view;
    }

    private async Task OpenFromDirectoryRowAsync(DirectoryDocument doc, DirectoryRow row)
    {
        IReadOnlyList<string?> paths = doc.PathsOf(row.Node);
        List<string> existing = [];
        List<string?> labels = [];
        string[] letters = ["A", "B", "C"];
        for (int i = 0; i < paths.Count; i++)
        {
            existing.Add(paths[i] ?? "/dev/null");
            labels.Add(paths[i] is null ? $"{letters[i]}: (missing)" : $"{letters[i]}: {row.Node.RelativePath}");
        }

        await OpenFilesAsync(existing, labels);
    }

    private void AddDocument(DiffDocument doc)
    {
        DiffDocumentView view = new(doc);
        view.ApplySettings(Settings);
        view.Synchronizer.IsEnabled = IsSyncScrolling;
        view.OpenPaneRequested += async (_, pane) => await ReplacePaneAsync(doc, pane);
        foreach (EditorPane p in view.Panes)
        {
            p.View.CaretMoved += (_, _) => UpdateCaretStatus(p.View);
            p.View.StatusMessage += (_, m) => ShowMessage(m, MessageSeverity.Information);
        }

        doc.CurrentBlockChanged += (_, _) => UpdateStatus();
        doc.StateChanged += (_, _) =>
        {
            UpdateStatus();
            if (Tabs.FirstOrDefault(t => t.Diff == doc) is { } tab)
            {
                tab.IsDirty = doc is MergeDocument m && m.IsDirty && m.WasSaved;
                tab.Title = TitleOf(doc);
            }

            if (doc is MergeDocument)
            {
                ScheduleOutputDecorations();
            }
        };
        AddTab(new DocumentTab(TitleOf(doc), view));
        _ = RefreshPluginDecorationsAsync();
    }

    private static string TitleOf(DiffDocument doc) => doc is MergeDocument m
        ? (m.OutputPath is null ? "Merge" : Path.GetFileName(m.OutputPath))
        : string.Join(" ↔ ", doc.Inputs.Select(i => i.Label));

    private void AddTab(DocumentTab tab)
    {
        if (Tabs.Count == 1 && Tabs[0].IsStartPage && !tab.IsStartPage)
        {
            Tabs.Clear();
        }

        Tabs.Add(tab);
        SelectedTab = tab;
    }

    private async Task ReplacePaneAsync(DiffDocument doc, int pane)
    {
        if (_view is null || doc is MergeDocument)
        {
            ShowMessage("Inputs of a merge cannot be replaced; start a new merge instead.", MessageSeverity.Warning);
            return;
        }

        IReadOnlyList<string>? files = await _view.Dialogs.PickFilesAsync($"Open file for pane {(char)('A' + pane)}", false);
        if (files is null)
        {
            return;
        }

        List<string> paths = [.. doc.Inputs.Select(i => i.Path ?? "/dev/null")];
        paths[pane] = files[0];
        int index = Tabs.IndexOf(SelectedTab!);
        await OpenFilesAsync(paths);
        if (index >= 0 && index < Tabs.Count - 1)
        {
            Tabs.RemoveAt(index);
        }
    }

    partial void OnSelectedTabChanged(DocumentTab? value)
    {
        OnPropertyChanged(nameof(ActiveDiff));
        OnPropertyChanged(nameof(ActiveMerge));
        OnPropertyChanged(nameof(HasDiff));
        OnPropertyChanged(nameof(IsMerge));
        OnPropertyChanged(nameof(IsDirectory));
        Assistants.OnDocumentChanged();
        UpdateStatus();
        NotifyCommands();
    }

    private void NotifyCommands()
    {
        foreach (IRelayCommand c in new IRelayCommand[]
        {
            SaveCommand, SaveAsCommand, NextDifferenceCommand, PreviousDifferenceCommand, NextConflictCommand, PreviousConflictCommand,
            ChooseACommand, ChooseBCommand, ChooseCCommand, ChooseBThenCCommand, ChooseCThenBCommand, AutoMergeAllCommand, UnresolveCommand,
            UndoCommand, RedoCommand, ReloadCommand,
        })
        {
            c.NotifyCanExecuteChanged();
        }
    }

    // ---------------------------------------------------------------- status

    /// <summary>Recomputes the status bar texts.</summary>
    public void UpdateStatus()
    {
        DiffDocument? doc = ActiveDiff;
        if (doc is null)
        {
            DifferenceStatus = SelectedTab?.DirectoryView?.Document.Summary ?? string.Empty;
            ConflictStatus = string.Empty;
            CaretStatus = string.Empty;
            NotifyCommands();
            return;
        }

        int count = doc.DifferenceCount;
        DifferenceStatus = count == 0 ? "No differences" : doc.CurrentOrdinal > 0 ? $"Difference {doc.CurrentOrdinal} of {count}" : $"{count} difference{(count == 1 ? string.Empty : "s")}";
        ConflictStatus = doc is MergeDocument m ? $"{m.ConflictCount} conflict{(m.ConflictCount == 1 ? string.Empty : "s")}, {m.UnresolvedCount} unresolved" : string.Empty;
        NotifyCommands();
    }

    private void UpdateCaretStatus(DiffTextView view)
    {
        TextPos p = view.CaretPosition;
        CaretStatus = $"Ln {p.Line + 1}, Col {p.Column + 1}";
    }

    /// <inheritdoc />
    public void ShowMessage(string message, MessageSeverity severity) => StatusMessage = severity == MessageSeverity.Information ? message : $"{severity}: {message}";

    /// <inheritdoc />
    public Task<int> OfferProposalsAsync(MergeAssistantResult result, string sourceDisplayName, CancellationToken cancellationToken)
    {
        IsAssistantsPanelVisible = true;
        return Assistants.OfferAsync(result, sourceDisplayName, cancellationToken);
    }

    private async Task ReportErrorAsync(string title, string message)
    {
        ShowMessage($"{title}: {message}", MessageSeverity.Error);
        if (_view is not null)
        {
            await _view.Dialogs.AskAsync(title, message, "OK");
        }
    }

    /// <summary>Saves settings.</summary>
    public void SaveSettings()
    {
        Settings.ShowAssistantsPanel = IsAssistantsPanelVisible;
        _settingsStore.Save(Settings);
    }

    partial void OnIsAssistantsPanelVisibleChanged(bool value) => SaveSettings();

    partial void OnIsSyncScrollingChanged(bool value)
    {
        foreach (DocumentTab t in Tabs)
        {
            if (t.DiffView is { } v)
            {
                v.Synchronizer.IsEnabled = value;
            }
        }
    }

    // ---------------------------------------------------------------- file commands

    /// <summary>Opens 2 or 3 files chosen in a dialog.</summary>
    [RelayCommand]
    public async Task OpenFilesDialogAsync()
    {
        if (_view is null)
        {
            return;
        }

        IReadOnlyList<string>? files = await _view.Dialogs.PickFilesAsync("Open 1–3 files to compare (base first for three files)", true);
        if (files is { Count: > 0 })
        {
            await OpenFilesAsync([.. files.Take(3)]);
        }
    }

    /// <summary>Opens a three-way merge chosen in dialogs.</summary>
    [RelayCommand]
    public async Task OpenMergeDialogAsync()
    {
        if (_view is null)
        {
            return;
        }

        IReadOnlyList<string>? files = await _view.Dialogs.PickFilesAsync("Select base, local and remote files (2 or 3)", true);
        if (files is not { Count: >= 2 })
        {
            return;
        }

        string? output = await _view.Dialogs.PickSaveFileAsync("Save merge result as", files[^2]);
        if (output is not null)
        {
            await OpenMergeAsync([.. files.Take(3)], null, output);
        }
    }

    /// <summary>Opens two directories chosen in dialogs.</summary>
    [RelayCommand]
    public async Task OpenDirectoriesDialogAsync()
    {
        if (_view is null)
        {
            return;
        }

        string? a = await _view.Dialogs.PickFolderAsync("First directory (A)");
        if (a is null)
        {
            return;
        }

        string? b = await _view.Dialogs.PickFolderAsync("Second directory (B)");
        if (b is not null)
        {
            await OpenDirectoriesAsync([a, b]);
        }
    }

    /// <summary>Re-opens a recent entry.</summary>
    [RelayCommand]
    public async Task OpenRecentAsync(IReadOnlyList<string>? paths)
    {
        if (paths is null || paths.Count == 0)
        {
            return;
        }

        if (paths.All(Directory.Exists))
        {
            await OpenDirectoriesAsync(paths);
        }
        else
        {
            await OpenFilesAsync(paths);
        }
    }

    private bool CanSave() => IsMerge;

    /// <summary>Saves the merge result.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    public async Task<bool> SaveAsync()
    {
        if (ActiveMerge is not { } merge)
        {
            return false;
        }

        if (merge.OutputPath is null)
        {
            return await SaveAsAsync();
        }

        return await SaveMergeAsync(merge, merge.OutputPath);
    }

    /// <summary>Saves the merge result to a new file.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    public async Task<bool> SaveAsAsync()
    {
        if (ActiveMerge is not { } merge || _view is null)
        {
            return false;
        }

        string? path = await _view.Dialogs.PickSaveFileAsync("Save merge result as", merge.OutputPath ?? merge.Inputs[1].Path);
        return path is not null && await SaveMergeAsync(merge, path);
    }

    private async Task<bool> SaveMergeAsync(MergeDocument merge, string path)
    {
        if (merge.UnresolvedCount > 0 && _view is not null)
        {
            int answer = await _view.Dialogs.AskAsync(
                "Unresolved conflicts",
                $"{merge.UnresolvedCount} region(s) are still unresolved. Save anyway with conflict markers?",
                "Save with markers",
                "Cancel");
            if (answer != 0)
            {
                return false;
            }
        }

        try
        {
            if (!await merge.SaveAsync(path))
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ReportErrorAsync("Cannot save", ex.Message);
            return false;
        }

        ShowMessage($"Saved {path}", MessageSeverity.Information);
        UpdateStatus();
        return true;
    }

    private bool CanReload() => HasDiff || IsDirectory;

    /// <summary>Reloads the active comparison from disk.</summary>
    [RelayCommand(CanExecute = nameof(CanReload))]
    public async Task ReloadAsync()
    {
        ChangedOnDiskMessage = null;
        if (SelectedTab?.DirectoryView is { } dv)
        {
            await dv.RefreshAsync();
            return;
        }

        if (ActiveDiff is not { } doc || SelectedTab is not { } tab)
        {
            return;
        }

        if (doc is MergeDocument m && m.IsDirty && m.WasSaved && _view is not null)
        {
            int answer = await _view.Dialogs.AskAsync("Reload merge?", "Reloading discards your merge decisions.", "Reload", "Cancel");
            if (answer != 0)
            {
                return;
            }
        }

        int index = Tabs.IndexOf(tab);
        List<string> paths = [.. doc.Inputs.Where(i => i.Path is not null).Select(i => i.Path!)];
        List<string?> labels = [.. doc.Inputs.Where(i => i.Path is not null).Select(i => (string?)i.Label)];
        if (doc is MergeDocument merge)
        {
            if (!merge.HasBase)
            {
                paths = [.. paths.Skip(paths.Count - 2)];
                labels = [.. labels.Skip(labels.Count - 2)];
            }

            MergeDocument? newMerge = await OpenMergeAsync(paths, labels, merge.OutputPath);
            if (StartedAsMerge == merge)
            {
                StartedAsMerge = newMerge;
            }
        }
        else
        {
            await OpenFilesAsync(paths, labels);
        }

        Tabs.RemoveAt(index);
    }

    /// <summary>Checks whether inputs changed on disk and shows the reload bar.</summary>
    public void CheckFilesChangedOnDisk()
    {
        if (ChangedOnDiskMessage is not null || ActiveDiff is not { } doc || _dismissedChanges.Contains(doc))
        {
            return;
        }

        InputFile? changed = doc.Inputs.FirstOrDefault(i => i.IsModifiedOnDisk());
        if (changed is not null)
        {
            ChangedOnDiskMessage = $"'{changed.Label}' was changed by another program.";
        }
    }

    /// <summary>Dismisses the reload bar (no further prompts for this document).</summary>
    [RelayCommand]
    public void DismissChangedOnDisk()
    {
        if (ActiveDiff is { } doc)
        {
            _dismissedChanges.Add(doc);
        }

        ChangedOnDiskMessage = null;
    }

    private readonly HashSet<DiffDocument> _dismissedChanges = [];

    /// <summary>Closes the selected tab (asking for unsaved merges).</summary>
    [RelayCommand]
    public async Task CloseTabAsync(DocumentTab? tab)
    {
        tab ??= SelectedTab;
        if (tab is null)
        {
            return;
        }

        if (!await ConfirmDiscardAsync(tab))
        {
            return;
        }

        Tabs.Remove(tab);
        if (Tabs.Count == 0)
        {
            ShowStartPage();
        }
    }

    /// <summary>Asks to save an unsaved merge; returns false to cancel the close.</summary>
    public async Task<bool> ConfirmDiscardAsync(DocumentTab tab)
    {
        if (tab.Diff is not MergeDocument merge || !merge.IsDirty || _view is null)
        {
            return true;
        }

        SelectedTab = tab;
        int answer = await _view.Dialogs.AskAsync(
            "Save merge result?",
            $"The merge result{(merge.OutputPath is null ? string.Empty : " for " + Path.GetFileName(merge.OutputPath))} has not been saved. {merge.UnresolvedCount} region(s) are unresolved.",
            "Save",
            "Don't save",
            "Cancel");
        return answer switch
        {
            0 => await SaveAsync(),
            1 => true,
            _ => false,
        };
    }

    /// <summary>Runs the unsaved-changes guard for every tab; returns true when the window may close.</summary>
    public async Task<bool> ConfirmCloseAllAsync()
    {
        foreach (DocumentTab tab in Tabs.ToList())
        {
            if (!await ConfirmDiscardAsync(tab))
            {
                return false;
            }
        }

        SaveSettings();
        return true;
    }

    /// <summary>Exits the application.</summary>
    [RelayCommand]
    public void Exit() => _view?.CloseWindow();

    // ---------------------------------------------------------------- edit commands

    private bool CanUndo() => ActiveMerge?.Session.CanUndo == true;

    private bool CanRedo() => ActiveMerge?.Session.CanRedo == true;

    /// <summary>Undo in the merge.</summary>
    [RelayCommand(CanExecute = nameof(CanUndo))]
    public void Undo()
    {
        if (ActiveMerge is { } m && m.Session.Undo())
        {
            ShowMessage("Undone.", MessageSeverity.Information);
        }
    }

    /// <summary>Redo in the merge.</summary>
    [RelayCommand(CanExecute = nameof(CanRedo))]
    public void Redo()
    {
        if (ActiveMerge is { } m && m.Session.Redo())
        {
            ShowMessage("Redone.", MessageSeverity.Information);
        }
    }

    /// <summary>Copies the selection of the active pane.</summary>
    [RelayCommand]
    public Task CopyAsync() => _view?.ActiveTextView?.CopyAsync() ?? Task.CompletedTask;

    /// <summary>Cuts the selection of the output pane.</summary>
    [RelayCommand]
    public Task CutAsync() => _view?.ActiveTextView?.CutAsync() ?? Task.CompletedTask;

    /// <summary>Pastes into the output pane.</summary>
    [RelayCommand]
    public Task PasteAsync() => _view?.ActiveTextView?.PasteAsync() ?? Task.CompletedTask;

    /// <summary>Selects all text of the active pane.</summary>
    [RelayCommand]
    public void SelectAll() => _view?.ActiveTextView?.SelectAll();

    /// <summary>Shows the find bar.</summary>
    [RelayCommand]
    public void Find() => _view?.ShowFindBar();

    /// <summary>Shows the go-to-line bar.</summary>
    [RelayCommand]
    public void GoToLine() => _view?.ShowGoToLineBar();

    // ---------------------------------------------------------------- navigation

    private bool CanNavigate() => ActiveDiff?.DifferenceCount > 0;

    /// <summary>Next difference (Ctrl+Down).</summary>
    [RelayCommand(CanExecute = nameof(CanNavigate))]
    public void NextDifference()
    {
        if (ActiveDiff is { } d && !d.GoToNextDifference())
        {
            ShowMessage("No more differences below.", MessageSeverity.Information);
        }
    }

    /// <summary>Previous difference (Ctrl+Up).</summary>
    [RelayCommand(CanExecute = nameof(CanNavigate))]
    public void PreviousDifference()
    {
        if (ActiveDiff is { } d && !d.GoToPreviousDifference())
        {
            ShowMessage("No more differences above.", MessageSeverity.Information);
        }
    }

    /// <summary>First difference (Ctrl+Home).</summary>
    [RelayCommand]
    public void FirstDifference() => ActiveDiff?.GoToFirstDifference();

    /// <summary>Last difference (Ctrl+End).</summary>
    [RelayCommand]
    public void LastDifference() => ActiveDiff?.GoToLastDifference();

    private bool CanMergeNavigate() => ActiveMerge?.UnresolvedCount > 0;

    /// <summary>Next unresolved conflict (Ctrl+PgDn).</summary>
    [RelayCommand(CanExecute = nameof(CanMergeNavigate))]
    public void NextConflict()
    {
        if (ActiveMerge is { } m && !m.GoToNextUnresolvedConflict())
        {
            ShowMessage("No unresolved conflicts.", MessageSeverity.Information);
        }
    }

    /// <summary>Previous unresolved conflict (Ctrl+PgUp).</summary>
    [RelayCommand(CanExecute = nameof(CanMergeNavigate))]
    public void PreviousConflict()
    {
        if (ActiveMerge is { } m && !m.GoToPreviousUnresolvedConflict())
        {
            ShowMessage("No unresolved conflicts.", MessageSeverity.Information);
        }
    }

    // ---------------------------------------------------------------- merge commands

    private bool CanChoose() => IsMerge;

    private bool CanChooseA() => ActiveMerge?.HasBase == true;

    /// <summary>Choose A (base) for the current region (Ctrl+1).</summary>
    [RelayCommand(CanExecute = nameof(CanChooseA))]
    public Task ChooseA() => ChooseAsync(ResolutionChoice.Base, "A");

    /// <summary>Choose B (local) for the current region (Ctrl+2).</summary>
    [RelayCommand(CanExecute = nameof(CanChoose))]
    public Task ChooseB() => ChooseAsync(ResolutionChoice.Left, "B");

    /// <summary>Choose C (remote) for the current region (Ctrl+3).</summary>
    [RelayCommand(CanExecute = nameof(CanChoose))]
    public Task ChooseC() => ChooseAsync(ResolutionChoice.Right, "C");

    /// <summary>Choose B then C.</summary>
    [RelayCommand(CanExecute = nameof(CanChoose))]
    public Task ChooseBThenC() => ChooseAsync(ResolutionChoice.LeftThenRight, "B+C");

    /// <summary>Choose C then B.</summary>
    [RelayCommand(CanExecute = nameof(CanChoose))]
    public Task ChooseCThenB() => ChooseAsync(ResolutionChoice.RightThenLeft, "C+B");

    /// <summary>Marks the current region unresolved again.</summary>
    [RelayCommand(CanExecute = nameof(CanChoose))]
    public Task Unresolve() => ChooseAsync(ResolutionChoice.Unresolved, "unresolved");

    private async Task ChooseAsync(ResolutionChoice choice, string label)
    {
        if (ActiveMerge is not { } m)
        {
            return;
        }

        if (await m.ChooseAsync(choice))
        {
            ShowMessage(choice == ResolutionChoice.Unresolved ? "Region marked unresolved." : $"Chose {label}. {m.UnresolvedCount} unresolved.", MessageSeverity.Information);
        }
        else
        {
            ShowMessage("Nothing to resolve.", MessageSeverity.Information);
        }
    }

    /// <summary>Automatically merges all non-conflicting changes.</summary>
    [RelayCommand(CanExecute = nameof(CanChoose))]
    public void AutoMergeAll()
    {
        if (ActiveMerge is { } m)
        {
            int n = m.AutoMergeAll();
            ShowMessage(n == 0 ? "Nothing left to merge automatically." : $"Automatically resolved {n} region(s).", MessageSeverity.Information);
        }
    }

    /// <summary>Resolves all remaining regions with one side.</summary>
    [RelayCommand]
    public void ResolveAll(string? side)
    {
        if (ActiveMerge is not { } m)
        {
            return;
        }

        ResolutionChoice choice = side switch { "A" => ResolutionChoice.Base, "C" => ResolutionChoice.Right, _ => ResolutionChoice.Left };
        int n = m.ResolveAll(choice);
        ShowMessage($"Resolved {n} region(s) with {side}.", MessageSeverity.Information);
    }

    // ---------------------------------------------------------------- diff options

    /// <summary>Sets the whitespace mode and re-diffs.</summary>
    [RelayCommand]
    public Task SetWhitespace(string? mode)
    {
        Settings.Whitespace = Enum.TryParse(mode, out WhitespaceSetting w) ? w : WhitespaceSetting.Compare;
        return ApplyDiffOptionsAsync();
    }

    /// <summary>Toggles case-insensitive comparison.</summary>
    [RelayCommand]
    public Task ToggleIgnoreCase()
    {
        Settings.IgnoreCase = !Settings.IgnoreCase;
        return ApplyDiffOptionsAsync();
    }

    /// <summary>Toggles ignoring blank lines.</summary>
    [RelayCommand]
    public Task ToggleIgnoreBlankLines()
    {
        Settings.IgnoreBlankLines = !Settings.IgnoreBlankLines;
        return ApplyDiffOptionsAsync();
    }

    /// <summary>Re-diffs all open comparisons with the current options.</summary>
    public async Task ApplyDiffOptionsAsync()
    {
        SaveSettings();
        OnPropertyChanged(nameof(Settings));
        foreach (DocumentTab t in Tabs.ToList())
        {
            if (t.Diff is { } d)
            {
                d.Options = CurrentDiffOptions;
                await d.RecomputeAsync();
            }
        }

        UpdateStatus();
        ShowMessage($"Compared again: whitespace {Settings.Whitespace}, ignore case {(Settings.IgnoreCase ? "on" : "off")}, ignore blank lines {(Settings.IgnoreBlankLines ? "on" : "off")}.", MessageSeverity.Information);
    }

    // ---------------------------------------------------------------- view commands

    /// <summary>Sets the theme.</summary>
    [RelayCommand]
    public void SetTheme(string? theme)
    {
        Settings.Theme = Enum.TryParse(theme, out ThemeMode t) ? t : ThemeMode.System;
        ApplyAppearance();
    }

    /// <summary>Toggles the colour-blind safe palette.</summary>
    [RelayCommand]
    public void TogglePalette()
    {
        Settings.Palette = Settings.Palette == PaletteMode.ColorBlind ? PaletteMode.Standard : PaletteMode.ColorBlind;
        ApplyAppearance();
    }

    /// <summary>Changes the font size.</summary>
    [RelayCommand]
    public void ZoomFont(string? delta)
    {
        Settings.FontSize = delta == "0" ? 13 : Math.Clamp(Settings.FontSize + (delta == "-" ? -1 : 1), 7, 40);
        ApplyAppearance();
    }

    /// <summary>Sets the font family.</summary>
    public void SetFontFamily(string family)
    {
        Settings.FontFamily = family;
        ApplyAppearance();
    }

    /// <summary>Sets the tab size.</summary>
    [RelayCommand]
    public void SetTabSize(string? size)
    {
        Settings.TabSize = int.TryParse(size, out int n) ? n : 4;
        ApplyAppearance();
    }

    /// <summary>Toggles word wrap.</summary>
    [RelayCommand]
    public void ToggleWordWrap()
    {
        Settings.WordWrap = !Settings.WordWrap;
        ApplyAppearance();
    }

    /// <summary>Toggles visible whitespace.</summary>
    [RelayCommand]
    public void ToggleShowWhitespace()
    {
        Settings.ShowWhitespace = !Settings.ShowWhitespace;
        ApplyAppearance();
    }

    /// <summary>Toggles line numbers.</summary>
    [RelayCommand]
    public void ToggleLineNumbers()
    {
        Settings.ShowLineNumbers = !Settings.ShowLineNumbers;
        ApplyAppearance();
    }

    /// <summary>Toggles the assistants panel.</summary>
    [RelayCommand]
    public void ToggleAssistantsPanel() => IsAssistantsPanelVisible = !IsAssistantsPanelVisible;

    /// <summary>Toggles synchronized scrolling.</summary>
    [RelayCommand]
    public void ToggleSyncScrolling() => IsSyncScrolling = !IsSyncScrolling;

    private void ApplyAppearance()
    {
        SaveSettings();
        OnPropertyChanged(nameof(Settings));
        _view?.ApplyAppearance();
        foreach (DocumentTab t in Tabs)
        {
            t.DiffView?.ApplySettings(Settings);
        }
    }

    // ---------------------------------------------------------------- plugins

    /// <summary>Reloads plugins.</summary>
    [RelayCommand]
    public void ReloadPlugins()
    {
        IReadOnlyList<PluginInfo> plugins = Plugins.Reload();
        foreach (DocumentTab t in Tabs)
        {
            if (t.Diff is MergeDocument m)
            {
                m.LifecycleHooks = Plugins.Manager.LifecycleHooks;
            }
        }

        int loaded = plugins.Count(p => p.State == PluginState.Loaded);
        int failed = plugins.Count(p => p.State is PluginState.Faulted or PluginState.InvalidManifest or PluginState.Incompatible);
        ShowMessage($"Plugins reloaded: {loaded} loaded{(failed > 0 ? $", {failed} failed" : string.Empty)}.", MessageSeverity.Information);
    }

    /// <summary>Executes a plugin command with the active document as context.</summary>
    public async Task<bool> ExecutePluginCommandAsync(string id)
    {
        bool ok = await Plugins.Manager.Commands.ExecuteAsync(id, new CommandContext(ActiveDiff));
        if (!ok)
        {
            ShowMessage($"Command '{id}' failed — see Plugins › Manage for the log.", MessageSeverity.Warning);
        }

        return ok;
    }

    private DispatcherTimerHolder? _outputTimer;

    private void ScheduleOutputDecorations()
    {
        if (Plugins.Manager.EditorExtensions.DecorationProviders.Count == 0)
        {
            return;
        }

        _outputTimer ??= new DispatcherTimerHolder(TimeSpan.FromMilliseconds(400), () => _ = RefreshPluginDecorationsAsync());
        _outputTimer.Restart();
    }

    /// <summary>Collects plugin decorations for all panes of the active document.</summary>
    public async Task RefreshPluginDecorationsAsync()
    {
        if (ActiveDiff is not { } doc)
        {
            return;
        }

        _decorationCts?.Cancel();
        CancellationTokenSource cts = new();
        _decorationCts = cts;
        IEditorExtensionRegistry registry = Plugins.Manager.EditorExtensions;
        foreach (PaneModel pane in doc.Panes)
        {
            if (registry.DecorationProviders.Count == 0)
            {
                if (pane.PluginDecorations is { } empty)
                {
                    empty.Clear();
                }

                continue;
            }

            DecorationRequest request = new(pane.Role, pane.Input?.Path ?? pane.Path, doc.LanguageId ?? "plaintext", doc.GetPaneLines(pane.Index));
            IReadOnlyList<EditorDecoration> decorations;
            try
            {
                decorations = await Task.Run(() => registry.CollectDecorationsAsync(request, cts.Token), cts.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (cts.IsCancellationRequested)
            {
                return;
            }

            PluginDecorationAdapter.Fill(pane.PluginDecorations, decorations);
        }
    }
}

/// <summary>Converts plugin <see cref="EditorDecoration"/>s into editor decorations.</summary>
public static class PluginDecorationAdapter
{
    /// <summary>Maps a theme style key to a highlight kind.</summary>
    public static HighlightKind KindOf(string styleKey) => styleKey switch
    {
        "Decoration.Warning" => HighlightKind.Warning,
        "Decoration.Error" => HighlightKind.Error,
        "Decoration.Accent" => HighlightKind.Proposal,
        _ => HighlightKind.Info,
    };

    /// <summary>Replaces the content of <paramref name="target"/>.</summary>
    public static void Fill(StaticDecorationSource target, IReadOnlyList<EditorDecoration> decorations)
    {
        target.Clear();
        foreach (EditorDecoration d in decorations)
        {
            HighlightKind kind = KindOf(d.StyleKey);
            switch (d.Kind)
            {
                case DecorationKind.GutterMark:
                    target.Add(new GutterMark(d.Line, kind, d.Tooltip));
                    break;
                case DecorationKind.LineHighlight:
                    for (int i = 0; i < Math.Max(1, d.LineCount); i++)
                    {
                        target.Add(new LineDecoration(d.Line + i, kind));
                    }

                    break;
                case DecorationKind.InlineHighlight:
                    target.Add(new InlineDecoration(d.Line, d.Column, d.Length, kind, InlineStyle.Underline));
                    break;
            }
        }

        target.NotifyChanged();
    }
}

/// <summary>A restartable one-shot UI timer.</summary>
public sealed class DispatcherTimerHolder
{
    private readonly Avalonia.Threading.DispatcherTimer _timer;

    /// <summary>Creates the timer.</summary>
    public DispatcherTimerHolder(TimeSpan interval, Action action)
    {
        _timer = new Avalonia.Threading.DispatcherTimer { Interval = interval };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            action();
        };
    }

    /// <summary>Restarts the countdown.</summary>
    public void Restart()
    {
        _timer.Stop();
        _timer.Start();
    }
}
