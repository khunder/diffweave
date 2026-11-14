using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace DiffWeave.App.Services;

/// <summary>Modal questions and file pickers (replaceable in tests).</summary>
public interface IDialogService
{
    /// <summary>Asks a question; returns the index of the chosen button, or -1 when dismissed.</summary>
    Task<int> AskAsync(string title, string message, params string[] buttons);

    /// <summary>Picks files to open; null when cancelled.</summary>
    Task<IReadOnlyList<string>?> PickFilesAsync(string title, bool allowMultiple);

    /// <summary>Picks a folder; null when cancelled.</summary>
    Task<string?> PickFolderAsync(string title);

    /// <summary>Picks a file to save; null when cancelled.</summary>
    Task<string?> PickSaveFileAsync(string title, string? suggestedPath);
}

/// <summary>The real dialogs, shown over a window.</summary>
public sealed class DialogService(Window owner) : IDialogService
{
    /// <inheritdoc />
    public async Task<int> AskAsync(string title, string message, params string[] buttons)
    {
        int result = -1;
        Window dialog = new()
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            MaxWidth = 560,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        StackPanel buttonsPanel = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        for (int i = 0; i < buttons.Length; i++)
        {
            int index = i;
            Button b = new() { Content = buttons[i], MinWidth = 88, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = i == 0, IsCancel = i == buttons.Length - 1 && buttons.Length > 1 };
            if (i == 0)
            {
                b.Classes.Add("accent");
            }

            b.Click += (_, _) =>
            {
                result = index;
                dialog.Close();
            };
            buttonsPanel.Children.Add(b);
        }

        dialog.Content = new Border
        {
            Padding = new Thickness(20, 18),
            Child = new StackPanel
            {
                Spacing = 18,
                Children =
                {
                    new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 500 },
                    buttonsPanel,
                },
            },
        };
        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                dialog.Close();
            }
        };
        await dialog.ShowDialog(owner);
        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>?> PickFilesAsync(string title, bool allowMultiple)
    {
        IReadOnlyList<IStorageFile> files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = allowMultiple });
        List<string> paths = [.. files.Select(f => f.TryGetLocalPath()).OfType<string>()];
        return paths.Count == 0 ? null : paths;
    }

    /// <inheritdoc />
    public async Task<string?> PickFolderAsync(string title)
    {
        IReadOnlyList<IStorageFolder> folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    /// <inheritdoc />
    public async Task<string?> PickSaveFileAsync(string title, string? suggestedPath)
    {
        IStorageFile? file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedPath is null ? null : Path.GetFileName(suggestedPath),
        });
        return file?.TryGetLocalPath();
    }
}
