using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using DiffWeave.App.Documents;
using DiffWeave.App.Views;
using DiffWeave.Core.Directories;

namespace DiffWeave.App.Tests.Ui;

public class DirectoryViewTests
{
    public static (string A, string B) CreateTrees(string root)
    {
        string a = Path.Combine(root, "left"), b = Path.Combine(root, "right");
        void W(string dir, string rel, string text)
        {
            string p = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text);
        }

        W(a, "README.md", "# Project\n");
        W(b, "README.md", "# Project\n");
        W(a, "src/Stock.cs", TestData.Base);
        W(b, "src/Stock.cs", TestData.Left);
        W(a, "src/Order.cs", "class Order {}\n");
        W(b, "src/Order.cs", "class Order {}\n");
        W(a, "src/Legacy.cs", "// old\n");
        W(b, "src/Invoice.cs", "class Invoice {}\n");
        W(b, "docs/guide.md", "Guide\n");
        W(a, "build.sh", "#!/bin/sh\necho build\n");
        W(b, "build.sh", "#!/bin/sh\necho build --release\n");
        return (a, b);
    }

    [AvaloniaTest]
    public async Task Shows_status_per_entry_and_filters()
    {
        using UiHarness h = new();
        (string a, string b) = CreateTrees(h.Directory);
        DirectoryView? view = await h.ViewModel.OpenDirectoriesAsync([a, b]);
        h.Window.Pump();
        DirectoryDocument doc = view!.Document;
        DirectoryRow stock = doc.Rows.Single(r => r.Name == "Stock.cs");
        stock.Node.Status.Should().Be(EntryStatus.Different);
        doc.Rows.Single(r => r.Name == "Legacy.cs").StatusText.Should().Be("Only in A");
        doc.Rows.Single(r => r.Name == "Invoice.cs").StatusText.Should().Be("Only in B");
        doc.Rows.Single(r => r.Name == "README.md").StatusText.Should().Be("Identical");
        stock.SizeA.Should().Be($"{new FileInfo(Path.Combine(a, "src", "Stock.cs")).Length} B");
        h.ViewModel.DifferenceStatus.Should().Contain("different");

        doc.Filter = new DirectoryFilter(ShowIdentical: false);
        doc.Rows.Should().NotContain(r => r.Name == "README.md" || r.Name == "Order.cs");
        doc.Rows.Should().Contain(r => r.Name == "Stock.cs");
        doc.Filter = new DirectoryFilter(ShowIdentical: false, ShowDifferent: false);
        doc.Rows.Select(r => r.Name).Should().Contain(["Legacy.cs", "Invoice.cs"]).And.NotContain("Stock.cs");
    }

    [AvaloniaTest]
    public async Task Enter_opens_the_selected_file_pair()
    {
        using UiHarness h = new();
        (string a, string b) = CreateTrees(h.Directory);
        DirectoryView? view = await h.ViewModel.OpenDirectoriesAsync([a, b]);
        h.Window.Pump();
        DirectoryRow row = view!.Document.Rows.Single(r => r.Name == "Stock.cs");
        view.RowList.SelectedItem = row;
        h.Window.Pump();
        view.RowList.ContainerFromItem(row)!.Focus().Should().BeTrue();
        h.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        for (int i = 0; i < 20 && h.ViewModel.ActiveDiff is null; i++)
        {
            await Task.Delay(20);
            h.Window.Pump();
        }

        h.ViewModel.Tabs.Should().HaveCount(2);
        h.ViewModel.ActiveDiff.Should().BeOfType<ComparisonDocument>();
        h.ViewModel.ActiveDiff!.Inputs[1].Path.Should().Be(Path.Combine(b, "src", "Stock.cs"));
    }

    [AvaloniaTest]
    public async Task Copy_requires_confirmation_and_refreshes()
    {
        using UiHarness h = new();
        (string a, string b) = CreateTrees(h.Directory);
        DirectoryView? view = await h.ViewModel.OpenDirectoriesAsync([a, b]);
        view!.RowList.SelectedItem = view.Document.Rows.Single(r => r.Name == "Legacy.cs");

        h.Dialogs.Answers.Enqueue(1); // cancel
        (await view.CopySelectedAsync(0, 1)).Should().BeFalse();
        File.Exists(Path.Combine(b, "src", "Legacy.cs")).Should().BeFalse();

        h.Dialogs.Answers.Enqueue(0); // confirm
        (await view.CopySelectedAsync(0, 1)).Should().BeTrue();
        File.ReadAllText(Path.Combine(b, "src", "Legacy.cs")).Should().Be("// old\n");
        view.Document.Rows.Single(r => r.Name == "Legacy.cs").Node.Status.Should().Be(EntryStatus.Identical);
        h.Dialogs.Questions.Should().HaveCount(2);
    }

    [AvaloniaTest]
    public async Task Three_directories()
    {
        using UiHarness h = new();
        (string a, string b) = CreateTrees(h.Directory);
        string c = Path.Combine(h.Directory, "third");
        Directory.CreateDirectory(c);
        File.WriteAllText(Path.Combine(c, "README.md"), "# Project\n");
        DirectoryView? view = await h.ViewModel.OpenDirectoriesAsync([a, b, c]);
        view!.Document.Rows.Should().OnlyContain(r => r.HasC);
        view.Document.Rows.Single(r => r.Name == "README.md").Node.Status.Should().Be(EntryStatus.Identical);
    }
}
