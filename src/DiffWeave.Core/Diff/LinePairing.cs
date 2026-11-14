namespace DiffWeave.Core.Diff;

/// <summary>Pairs the lines of two changed blocks by similarity, for drawing them side by side.</summary>
internal static class LinePairing
{
    /// <summary>Blocks larger than this (m × n) are paired top-down instead of by similarity.</summary>
    private const long MaxDpCells = 250_000;

    private const double MinSimilarity = 0.35;

    /// <summary>
    /// Returns rows (a, b) with -1 for filler, covering lines aStart..aStart+aCount and bStart..bStart+bCount in order.
    /// Lines are paired when similar enough; the pairing maximizes the total similarity (monotonic alignment).
    /// </summary>
    public static List<(int A, int B)> Pair(IReadOnlyList<string> a, int aStart, int aCount, IReadOnlyList<string> b, int bStart, int bCount)
    {
        var rows = new List<(int, int)>(Math.Max(aCount, bCount));
        if (aCount == 0 || bCount == 0 || (long)aCount * bCount > MaxDpCells)
        {
            int n = Math.Max(aCount, bCount);
            for (int k = 0; k < n; k++)
            {
                rows.Add((k < aCount ? aStart + k : -1, k < bCount ? bStart + k : -1));
            }

            return rows;
        }

        if (aCount == bCount && aCount <= 1)
        {
            rows.Add((aStart, bStart));
            return rows;
        }

        var sigA = new HashSet<int>[aCount];
        var sigB = new HashSet<int>[bCount];
        for (int i = 0; i < aCount; i++)
        {
            sigA[i] = Bigrams(a[aStart + i]);
        }

        for (int j = 0; j < bCount; j++)
        {
            sigB[j] = Bigrams(b[bStart + j]);
        }

        // score[i, j] = best total similarity for the first i lines of A and j lines of B.
        double[,] score = new double[aCount + 1, bCount + 1];
        byte[,] move = new byte[aCount + 1, bCount + 1]; // 0 = pair, 1 = skip A, 2 = skip B
        for (int i = 0; i <= aCount; i++)
        {
            for (int j = 0; j <= bCount; j++)
            {
                if (i == 0 && j == 0)
                {
                    continue;
                }

                double best = double.NegativeInfinity;
                byte m = 0;
                if (i > 0 && j > 0)
                {
                    double s = Similarity(sigA[i - 1], sigB[j - 1], a[aStart + i - 1], b[bStart + j - 1]);
                    if (s >= MinSimilarity)
                    {
                        best = score[i - 1, j - 1] + s;
                        m = 0;
                    }
                }

                if (i > 0 && score[i - 1, j] > best)
                {
                    best = score[i - 1, j];
                    m = 1;
                }

                if (j > 0 && score[i, j - 1] > best)
                {
                    best = score[i, j - 1];
                    m = 2;
                }

                score[i, j] = best;
                move[i, j] = m;
            }
        }

        var reversed = new List<(int, int)>();
        int x = aCount, y = bCount;
        while (x > 0 || y > 0)
        {
            byte m = move[x, y];
            if (x > 0 && y > 0 && m == 0)
            {
                reversed.Add((aStart + x - 1, bStart + y - 1));
                x--;
                y--;
            }
            else if (x > 0 && (m == 1 || y == 0))
            {
                reversed.Add((aStart + x - 1, -1));
                x--;
            }
            else
            {
                reversed.Add((-1, bStart + y - 1));
                y--;
            }
        }

        reversed.Reverse();

        // Between two paired rows, unpaired A and B lines are zipped top-down so that no filler is wasted.
        var gapA = new List<int>();
        var gapB = new List<int>();
        void Flush()
        {
            int n = Math.Max(gapA.Count, gapB.Count);
            for (int k = 0; k < n; k++)
            {
                rows.Add((k < gapA.Count ? gapA[k] : -1, k < gapB.Count ? gapB[k] : -1));
            }

            gapA.Clear();
            gapB.Clear();
        }

        foreach ((int ra, int rb) in reversed)
        {
            if (ra >= 0 && rb >= 0)
            {
                Flush();
                rows.Add((ra, rb));
            }
            else if (ra >= 0)
            {
                gapA.Add(ra);
            }
            else
            {
                gapB.Add(rb);
            }
        }

        Flush();
        return rows;
    }

    private static HashSet<int> Bigrams(string s)
    {
        var set = new HashSet<int>();
        string t = s.Trim();
        if (t.Length == 1)
        {
            set.Add(t[0]);
        }

        for (int i = 0; i + 1 < t.Length; i++)
        {
            set.Add((t[i] << 16) | t[i + 1]);
        }

        return set;
    }

    private static double Similarity(HashSet<int> x, HashSet<int> y, string a, string b)
    {
        if (x.Count == 0 && y.Count == 0)
        {
            return LineDiffer.IsBlank(a) && LineDiffer.IsBlank(b) ? 1.0 : 0.0;
        }

        if (x.Count == 0 || y.Count == 0)
        {
            return 0;
        }

        int common = 0;
        HashSet<int> small = x.Count <= y.Count ? x : y, large = ReferenceEquals(small, x) ? y : x;
        foreach (int g in small)
        {
            if (large.Contains(g))
            {
                common++;
            }
        }

        return 2.0 * common / (x.Count + y.Count);
    }
}
