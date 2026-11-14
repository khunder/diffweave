using Avalonia.Controls;
using Avalonia.Layout;
using DiffWeave.App.Services;
using DiffWeave.App.ViewModels;

namespace DiffWeave.App.Views;

/// <summary>The start page: open actions and recent comparisons.</summary>
public partial class StartPage : UserControl
{
    /// <summary>Required by the XAML loader.</summary>
    public StartPage()
    {
        InitializeComponent();
    }

    /// <summary>Creates the page for a view model.</summary>
    public StartPage(MainWindowViewModel vm)
        : this()
    {
        DataContext = vm;
        Logo.Source = AppIcon.Render(128);
        foreach (List<string> entry in vm.Settings.RecentFiles.Take(8))
        {
            List<string> paths = entry;
            Button b = new()
            {
                Content = new TextBlock { Text = string.Join("  ↔  ", paths.Select(System.IO.Path.GetFileName)), TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Classes = { "link" },
                Command = vm.OpenRecentCommand,
                CommandParameter = paths,
            };
            ToolTip.SetTip(b, string.Join("\n", paths));
            RecentList.Items.Add(b);
        }

        RecentSection.IsVisible = RecentList.Items.Count > 0;
    }
}
