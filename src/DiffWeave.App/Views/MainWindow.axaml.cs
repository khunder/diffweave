using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiffWeave.App.Cli;
using DiffWeave.App.Controls;
using DiffWeave.App.Editor;
using DiffWeave.App.Services;
using DiffWeave.App.ViewModels;
using DiffWeave.Extensibility;

namespace DiffWeave.App.Views;

/// <summary>The main window: menus, toolbar, tabs, find bar, assistants panel and status bar.</summary>
public partial class MainWindow : Window, IMainView
{
    private static readonly string[] FontChoices =
        [DiffTextView.DefaultFontFamily, "DejaVu Sans Mono", "Liberation Mono", "Cascadia Mono", "JetBrains Mono", "Fira Code", "Source Code Pro", "Noto Sans Mono", "Consolas", "Menlo"];

    private readonly DispatcherTimer _diskTimer;
    private DiffTextView? _activeTextView;
    private bool _closeConfirmed;

    /// <summary>Required by the XAML loader; creates a window with default services.</summary>
    public MainWindow()
        : this(new MainWindowViewModel(SettingsStore.Default, new PluginService()))
    {
    }

    /// <summary>Creates the window for a view model.</summary>
    public MainWindow(MainWindowViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        ViewModel = vm;
        Dialogs = new DialogService(this);
        vm.Attach(this);
        Width = vm.Settings.WindowWidth;
        Height = vm.Settings.WindowHeight;
        try
        {
            Icon = new WindowIcon(AppIcon.Render(64));
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            // Icons are cosmetic.
        }

        AddHandler(GotFocusEvent, OnAnyGotFocus, RoutingStrategies.Bubble, true);
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Bubble, false);
        WireFindBar();
        HelpCommandLine.Click += async (_, _) => await Dialogs.AskAsync("Command line", CommandLineParser.Usage, "Close");
        HelpGit.Click += async (_, _) => await Dialogs.AskAsync("Git integration", GitHelpText, "Close");
        HelpAbout.Click += async (_, _) => await Dialogs.AskAsync(
            "About DiffWeave",
            $"{CommandLineParser.VersionText}\nA cross-platform file and directory diff and three-way merge tool.\nExtension API {ExtensibilityApi.ApiVersion}.",
            "Close");
        AssistantsPanelView.CloseRequested += (_, _) => vm.IsAssistantsPanelVisible = false;
        AssistantsPanelView.DocsRequested += (_, _) => OpenExternal(DocsPath("extensibility.md"));
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.SelectedTab))
            {
                OnTabChanged();
            }
        };
        BuildFontMenu();
        RebuildPluginMenu();
        RebuildRecentMenu();
        _diskTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _diskTimer.Tick += (_, _) => vm.CheckFilesChangedOnDisk();
        _diskTimer.Start();
        Activated += (_, _) => vm.CheckFilesChangedOnDisk();
        ApplyAppearance();
    }

    /// <summary>The view model.</summary>
    public MainWindowViewModel ViewModel { get; }

    /// <inheritdoc />
    public IDialogService Dialogs { get; set; }

    /// <inheritdoc />
    public DiffTextView? ActiveTextView => _activeTextView is { } atv && atv.IsAttachedToVisualTree() ? atv : ViewModel.SelectedTab?.DiffView?.ActivePane?.View;

    /// <summary>The find bar text box (tests).</summary>
    public TextBox FindTextBox => FindText;

    /// <summary>True when the find bar is open.</summary>
    public bool IsFindBarVisible => FindBar.IsVisible;

    /// <summary>Creates the window for the command line and loads plugins.</summary>
    public static MainWindow CreateForStartup(CommandLineOptions options)
    {
        PluginService plugins = new();
        plugins.LoadAll();
        MainWindowViewModel vm = new(SettingsStore.Default, plugins);
        MainWindow window = new(vm);
        window.Opened += async (_, _) => await vm.OpenFromCommandLineAsync(options);
        return window;
    }

    private const string GitHelpText = """
        git config --global merge.tool diffweave
        git config --global mergetool.diffweave.cmd 'diffweave --auto --L1 "$MERGED (Base)" --L2 "$MERGED (Local)" --L3 "$MERGED (Remote)" -o "$MERGED" "$BASE" "$LOCAL" "$REMOTE"'
        git config --global mergetool.diffweave.trustExitCode true

        git config --global diff.tool diffweave
        git config --global difftool.diffweave.cmd 'diffweave --L1 "$LOCAL" --L2 "$REMOTE" "$LOCAL" "$REMOTE"'

        Then run: git mergetool   or   git difftool
        See docs/git-integration.md for details.
        """;

    private static string DocsPath(string file)
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 6 && dir is not null; i++)
        {
            string candidate = Path.Combine(dir, "docs", file);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        return Path.Combine(AppContext.BaseDirectory, "docs", file);
    }

    /// <inheritdoc />
    public void OpenExternal(string pathOrUrl)
    {
        try
        {
            if (File.Exists(pathOrUrl))
            {
                _ = Launcher.LaunchFileInfoAsync(new FileInfo(pathOrUrl));
            }
            else
            {
                ViewModel.ShowMessage($"Documentation: {pathOrUrl}", MessageSeverity.Information);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            ViewModel.ShowMessage(ex.Message, MessageSeverity.Warning);
        }
    }

    /// <inheritdoc />
    public void ApplyAppearance()
    {
        if (Application.Current is App app)
        {
            app.ApplyAppearance(ViewModel.Settings.Theme, ViewModel.Settings.Palette);
        }
    }

    /// <inheritdoc />
    public void CloseWindow() => Close();

    /// <summary>Closes without the unsaved-changes guard.</summary>
    public void ForceClose()
    {
        _closeConfirmed = true;
        Close();
    }

    /// <inheritdoc />
    public void ShowFindBar()
    {
        GoToBar.IsVisible = false;
        FindBar.IsVisible = true;
        if (ActiveTextView is { } v && !v.SelectionRange.IsEmpty && v.SelectionRange.Start.Line == v.SelectionRange.End.Line)
        {
            FindText.Text = v.SelectedText;
        }

        FindText.Focus();
        FindText.SelectAll();
        UpdateFindHighlight();
    }

    /// <inheritdoc />
    public void ShowGoToLineBar()
    {
        FindBar.IsVisible = false;
        GoToBar.IsVisible = true;
        GoToText.Text = ActiveTextView is { } v ? (v.CaretPosition.Line + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
        GoToText.Focus();
        GoToText.SelectAll();
    }

    /// <summary>Runs "find next/previous" with the find bar options.</summary>
    public bool FindNext(bool backwards)
    {
        if (string.IsNullOrEmpty(FindText.Text) || ActiveTextView is not { } view)
        {
            return false;
        }

        return view.Find(CurrentFindOptions(), backwards);
    }

    /// <summary>Goes to the line typed in the go-to bar.</summary>
    public bool GoToTypedLine()
    {
        if (int.TryParse(GoToText.Text, out int line) && ActiveTextView is { } view)
        {
            view.GoToLine(line - 1);
            GoToBar.IsVisible = false;
            view.Focus();
            return true;
        }

        return false;
    }

    private FindOptions CurrentFindOptions() => new(FindText.Text ?? string.Empty, FindCase.IsChecked == true, FindRegex.IsChecked == true, FindWord.IsChecked == true);

    private void WireFindBar()
    {
        FindText.TextChanged += (_, _) => UpdateFindHighlight();
        FindCase.IsCheckedChanged += (_, _) => UpdateFindHighlight();
        FindRegex.IsCheckedChanged += (_, _) => UpdateFindHighlight();
        FindWord.IsCheckedChanged += (_, _) => UpdateFindHighlight();
        FindNextButton.Click += (_, _) => FindNext(false);
        FindPrevButton.Click += (_, _) => FindNext(true);
        FindClose.Click += (_, _) => CloseFindBar();
        FindText.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                FindNext(e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CloseFindBar();
                e.Handled = true;
            }
        };
        GoToButton.Click += (_, _) => GoToTypedLine();
        GoToClose.Click += (_, _) => GoToBar.IsVisible = false;
        GoToText.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                GoToTypedLine();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                GoToBar.IsVisible = false;
                ActiveTextView?.Focus();
                e.Handled = true;
            }
        };
    }

    private void CloseFindBar()
    {
        FindBar.IsVisible = false;
        UpdateFindHighlight();
        ActiveTextView?.Focus();
    }

    private void UpdateFindHighlight()
    {
        FindOptions? options = FindBar.IsVisible && !string.IsNullOrEmpty(FindText.Text) ? CurrentFindOptions() : null;
        if (ViewModel.SelectedTab?.DiffView is { } dv)
        {
            foreach (EditorPane p in dv.Panes)
            {
                p.View.FindHighlight = options;
            }
        }
    }

    private void OnTabChanged()
    {
        _activeTextView = null;
        UpdateFindHighlight();
        Title = ViewModel.SelectedTab is { IsStartPage: false } tab ? $"{tab.Title} — DiffWeave" : "DiffWeave";
        RebuildRecentMenu();
    }

    private void OnAnyGotFocus(object? sender, RoutedEventArgs e)
    {
        if (e.Source is DiffTextView v)
        {
            _activeTextView = v;
        }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        if (e.Key == Key.F3)
        {
            if (!FindBar.IsVisible && string.IsNullOrEmpty(FindText.Text))
            {
                ShowFindBar();
            }
            else
            {
                FindNext(e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            }

            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && (FindBar.IsVisible || GoToBar.IsVisible))
        {
            FindBar.IsVisible = false;
            GoToBar.IsVisible = false;
            UpdateFindHighlight();
            e.Handled = true;
            return;
        }

        // KDiff3-style single-key choices when the focus is not in an editable control.
        if (e.KeyModifiers == KeyModifiers.None && e.Key is Key.A or Key.B or Key.C && ViewModel.IsMerge)
        {
            object? focused = FocusManager?.GetFocusedElement();
            bool editing = focused is TextBox || (focused is DiffTextView dtv && dtv.CanEdit);
            if (!editing)
            {
                _ = e.Key switch
                {
                    Key.A => ViewModel.ChooseACommand.ExecuteAsync(null),
                    Key.B => ViewModel.ChooseBCommand.ExecuteAsync(null),
                    _ => ViewModel.ChooseCCommand.ExecuteAsync(null),
                };
                e.Handled = true;
            }
        }
    }

    private void BuildFontMenu()
    {
        List<MenuItem> items = [];
        foreach (string f in FontChoices)
        {
            string family = f;
            MenuItem item = new() { Header = f == DiffTextView.DefaultFontFamily ? "Default (system monospace)" : f, ToggleType = MenuItemToggleType.Radio };
            item.IsChecked = ViewModel.Settings.FontFamily == f;
            item.Click += (_, _) =>
            {
                ViewModel.SetFontFamily(family);
                foreach (MenuItem m in items)
                {
                    m.IsChecked = ReferenceEquals(m, item);
                }
            };
            items.Add(item);
        }

        FontFamilyMenu.ItemsSource = items;
    }

    private void RebuildRecentMenu()
    {
        List<Control> items = [];
        foreach (List<string> entry in ViewModel.Settings.RecentFiles)
        {
            List<string> paths = entry;
            MenuItem item = new() { Header = string.Join("  ↔  ", paths.Select(Path.GetFileName)), Command = ViewModel.OpenRecentCommand, CommandParameter = paths };
            ToolTip.SetTip(item, string.Join("\n", paths));
            items.Add(item);
        }

        if (items.Count == 0)
        {
            items.Add(new MenuItem { Header = "(none)", IsEnabled = false });
        }

        RecentMenu.ItemsSource = items;
    }

    /// <inheritdoc />
    public void RebuildPluginMenu()
    {
        List<Control> items = [];
        MenuItem manage = new() { Header = "_Manage Plugins…" };
        manage.Click += async (_, _) => await ShowPluginManagerAsync();
        MenuItem reload = new() { Header = "_Reload Plugins", Command = ViewModel.ReloadPluginsCommand };
        items.Add(manage);
        items.Add(reload);
        IReadOnlyList<CommandContribution> commands = ViewModel.Plugins.Manager.Commands.Commands;
        if (commands.Count > 0)
        {
            items.Add(new Separator());
        }

        Dictionary<string, MenuItem> submenus = new(StringComparer.Ordinal);
        foreach (CommandContribution c in commands)
        {
            if (c.MenuPath is null)
            {
                continue;
            }

            string id = c.Id;
            MenuItem item = new() { Header = c.Title };
            if (c.Description is not null)
            {
                ToolTip.SetTip(item, c.Description);
            }

            if (c.Shortcut is not null)
            {
                try
                {
                    item.InputGesture = KeyGesture.Parse(c.Shortcut);
                }
                catch (ArgumentException)
                {
                }
            }

            item.Click += async (_, _) => await ViewModel.ExecutePluginCommandAsync(id);
            string[] path = c.MenuPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (path.Length <= 1)
            {
                items.Add(item);
                continue;
            }

            string key = string.Join('/', path.Skip(1));
            if (!submenus.TryGetValue(key, out MenuItem? sub))
            {
                sub = new MenuItem { Header = path[^1], ItemsSource = new List<Control>() };
                submenus[key] = sub;
                items.Add(sub);
            }

            ((List<Control>)sub.ItemsSource!).Add(item);
        }

        PluginsMenu.ItemsSource = items;
        RegisterPluginShortcuts(commands);
    }

    private readonly List<KeyBinding> _pluginBindings = [];

    private void RegisterPluginShortcuts(IReadOnlyList<CommandContribution> commands)
    {
        foreach (KeyBinding b in _pluginBindings)
        {
            KeyBindings.Remove(b);
        }

        _pluginBindings.Clear();
        HashSet<KeyGesture> taken = [.. KeyBindings.Select(k => k.Gesture).OfType<KeyGesture>()];
        foreach (CommandContribution c in commands)
        {
            if (c.Shortcut is null)
            {
                continue;
            }

            KeyGesture gesture;
            try
            {
                gesture = KeyGesture.Parse(c.Shortcut);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (!taken.Add(gesture))
            {
                continue;
            }

            string id = c.Id;
            KeyBinding binding = new() { Gesture = gesture, Command = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(() => ViewModel.ExecutePluginCommandAsync(id)) };
            _pluginBindings.Add(binding);
            KeyBindings.Add(binding);
        }
    }

    private async Task ShowPluginManagerAsync()
    {
        IReadOnlyList<PluginInfo> plugins = ViewModel.Plugins.Manager.Plugins;
        string text = plugins.Count == 0
            ? "No plugins found."
            : string.Join("\n", plugins.Select(p => $"• {p.Manifest?.Name ?? Path.GetFileName(p.Directory)} {p.Manifest?.Version} — {p.State}{(p.Error is null ? string.Empty : ": " + p.Error)}"));
        text += "\n\nPlugin folders:\n" + string.Join("\n", ViewModel.Plugins.Directories);
        IReadOnlyList<Core.Plugins.PluginLogEntry> log = ViewModel.Plugins.Manager.Log.Entries;
        if (log.Count > 0)
        {
            text += "\n\nRecent log:\n" + string.Join("\n", log.TakeLast(8).Select(l => $"[{l.Level}] {l.Source}: {l.Message}"));
        }

        int answer = await Dialogs.AskAsync("Plugins", text, "Close", "Reload plugins");
        if (answer == 1)
        {
            ViewModel.ReloadPlugins();
        }
    }

    /// <inheritdoc />
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed || e.Cancel)
        {
            return;
        }

        if (ViewModel.Tabs.Any(t => t.Diff is Documents.MergeDocument { IsDirty: true }))
        {
            e.Cancel = true;
            Dispatcher.UIThread.Post(async () =>
            {
                if (await ViewModel.ConfirmCloseAllAsync())
                {
                    _closeConfirmed = true;
                    Close();
                }
            });
        }
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        _diskTimer.Stop();
        if (WindowState == WindowState.Normal)
        {
            ViewModel.Settings.WindowWidth = Width;
            ViewModel.Settings.WindowHeight = Height;
        }

        ViewModel.SaveSettings();
        App.ExitCode = ViewModel.ExitCode;
        base.OnClosed(e);
    }
}
