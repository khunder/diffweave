using System.Diagnostics;
using System.Text;
using DiffWeave.Core.Diff;

namespace DiffWeave.Core.Tests;

internal static class TestUtil
{
    private static readonly Lazy<bool> s_gitAvailable = new(() =>
    {
        try
        {
            (int code, _, _) = Run("git", ["--version"], null);
            return code == 0;
        }
        catch (Exception)
        {
            return false;
        }
    });

    public static bool GitAvailable => s_gitAvailable.Value;

    public static (int ExitCode, string StdOut, string StdErr) Run(string file, IEnumerable<string> args, string? workingDirectory)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        // Isolate from user/system git configuration (diff.algorithm, merge.conflictStyle, ...).
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        psi.Environment["HOME"] = Path.GetTempPath();
        using Process p = Process.Start(psi)!;
        Task<string> stdout = p.StandardOutput.ReadToEndAsync();
        Task<string> stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, stdout.Result, stderr.Result);
    }

    public static string[] Lines(string text) => text.Length == 0 ? [] : text.Split('\n');

    /// <summary>Applies hunks to A and returns the result, taking inserted lines from B.</summary>
    public static List<string> Apply(IReadOnlyList<string> a, IReadOnlyList<string> b, DiffResult diff)
    {
        var result = new List<string>();
        int ia = 0;
        foreach (DiffHunk h in diff.Hunks)
        {
            while (ia < h.StartA)
            {
                result.Add(a[ia++]);
            }

            for (int k = 0; k < h.CountB; k++)
            {
                result.Add(b[h.StartB + k]);
            }

            ia = h.EndA;
        }

        while (ia < a.Count)
        {
            result.Add(a[ia++]);
        }

        return result;
    }

    /// <summary>Edit distance (insertions + deletions) via LCS dynamic programming; reference for small inputs.</summary>
    public static int ReferenceEditDistance(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        int n = a.Count, m = b.Count;
        int[,] lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = m - 1; j >= 0; j--)
            {
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        return n + m - 2 * lcs[0, 0];
    }

    /// <summary>Random lines over a small alphabet (so lines repeat and diffs are ambiguous).</summary>
    public static string[] RandomLines(Random rng, int count, int alphabet)
        => [.. Enumerable.Range(0, count).Select(_ => "l" + rng.Next(alphabet))];

    /// <summary>Applies random edits to a copy of <paramref name="source"/>.</summary>
    public static string[] Mutate(Random rng, IReadOnlyList<string> source, int edits, int alphabet, string tag = "x")
    {
        var list = source.ToList();
        for (int e = 0; e < edits; e++)
        {
            int op = rng.Next(3);
            int pos = rng.Next(list.Count + 1);
            if (op == 0 || list.Count == 0)
            {
                list.Insert(pos, tag + rng.Next(alphabet));
            }
            else if (op == 1 && pos < list.Count)
            {
                list.RemoveAt(pos);
            }
            else if (pos < list.Count)
            {
                list[pos] = tag + rng.Next(alphabet);
            }
        }

        return [.. list];
    }

    public static string WriteLines(string dir, string name, IReadOnlyList<string> lines)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, lines.Count == 0 ? string.Empty : string.Join("\n", lines) + "\n");
        return path;
    }
}

/// <summary>A temporary directory deleted on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "diffweave-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string relative, string content)
    {
        string full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
        return full;
    }

    public string Dir(string relative)
    {
        string full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(full);
        return full;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
