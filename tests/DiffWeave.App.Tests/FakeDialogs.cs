using DiffWeave.App.Services;

namespace DiffWeave.App.Tests;

/// <summary>Scripted dialogs for UI tests.</summary>
public sealed class FakeDialogs : IDialogService
{
    public Queue<int> Answers { get; } = new();

    public List<string> Questions { get; } = [];

    public Queue<string?> SavePaths { get; } = new();

    public Queue<IReadOnlyList<string>?> OpenPaths { get; } = new();

    public Task<int> AskAsync(string title, string message, params string[] buttons)
    {
        Questions.Add(title + ": " + message);
        return Task.FromResult(Answers.Count > 0 ? Answers.Dequeue() : -1);
    }

    public Task<IReadOnlyList<string>?> PickFilesAsync(string title, bool allowMultiple) => Task.FromResult(OpenPaths.Count > 0 ? OpenPaths.Dequeue() : null);

    public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

    public Task<string?> PickSaveFileAsync(string title, string? suggestedPath) => Task.FromResult(SavePaths.Count > 0 ? SavePaths.Dequeue() : null);
}
