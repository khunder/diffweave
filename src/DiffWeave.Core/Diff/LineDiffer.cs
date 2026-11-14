using System.Text;

namespace DiffWeave.Core.Diff;

/// <summary>Computes line-level differences between two sequences of lines.</summary>
/// <remarks>
/// Pipeline: lines are normalized according to <see cref="DiffOptions"/> and interned to integers; the common prefix
/// and suffix are stripped; lines occurring only on one side are set aside (they can never match, so this does not
/// affect minimality); the remaining sequences are diffed with the selected algorithm; finally pure insertion/deletion
/// hunks are slid to a canonical position (as far down as possible, or onto an adjacent hunk).
/// </remarks>
public static class LineDiffer
{
    /// <summary>
    /// Diffs <paramref name="a"/> against <paramref name="b"/>. Thread-safe; the inputs are not modified and must
    /// not change during the call.
    /// </summary>
    public static DiffResult Diff(IReadOnlyList<string> a, IReadOnlyList<string> b, DiffOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        options ??= DiffOptions.Default;

        int[] ida = new int[a.Count];
        int[] idb = new int[b.Count];
        Intern(a, b, ida, idb, options);
        return DiffIds(ida, idb, options, cancellationToken, a, b);
    }

    /// <summary>Diffs two sequences of integer tokens (equal integers are equal elements).</summary>
    internal static DiffResult DiffIds(int[] ida, int[] idb, DiffOptions options, CancellationToken cancellationToken, IReadOnlyList<string>? a = null, IReadOnlyList<string>? b = null)
    {
        bool[] changedA = new bool[ida.Length];
        bool[] changedB = new bool[idb.Length];
        var engine = new Engine(ida, idb, changedA, changedB, options, cancellationToken);
        engine.Run();
        List<DiffHunk> hunks = BuildHunks(changedA, changedB);
        hunks = SlideHunks(hunks, ida, idb);
        if (options.IgnoreBlankLines && a is not null && b is not null)
        {
            for (int i = 0; i < hunks.Count; i++)
            {
                DiffHunk h = hunks[i];
                if (AllBlank(a, h.StartA, h.CountA) && AllBlank(b, h.StartB, h.CountB))
                {
                    hunks[i] = h with { IsIgnorable = true };
                }
            }
        }

        return new DiffResult(hunks, ida.Length, idb.Length);
    }

    /// <summary>Returns the comparison key of a line under the given options (the line itself when nothing is ignored).</summary>
    public static string Normalize(string line, DiffOptions options)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(options);
        return Normalize(line, options.Whitespace, options.IgnoreCase);
    }

    internal static string Normalize(string line, WhitespaceMode whitespace, bool ignoreCase)
    {
        string s = whitespace switch
        {
            WhitespaceMode.IgnoreTrailing => line.TrimEnd(),
            WhitespaceMode.IgnoreAmount => CollapseWhitespace(line),
            WhitespaceMode.IgnoreAll => RemoveWhitespace(line),
            _ => line,
        };
        return ignoreCase ? s.ToUpperInvariant() : s;
    }

    /// <summary>Whether two lines compare equal under the given options.</summary>
    public static bool LinesEqual(string a, string b, DiffOptions options)
        => options.NormalizesLines ? Normalize(a, options) == Normalize(b, options) : a == b;

    /// <summary>Whether two lines are equal when all whitespace is ignored (used to flag whitespace-only changes).</summary>
    public static bool EqualIgnoringWhitespace(string a, string b)
    {
        int i = 0, j = 0;
        while (true)
        {
            while (i < a.Length && char.IsWhiteSpace(a[i]))
            {
                i++;
            }

            while (j < b.Length && char.IsWhiteSpace(b[j]))
            {
                j++;
            }

            if (i == a.Length || j == b.Length)
            {
                return i == a.Length && j == b.Length;
            }

            if (a[i] != b[j])
            {
                return false;
            }

            i++;
            j++;
        }
    }

    /// <summary>Whether a line consists only of whitespace.</summary>
    public static bool IsBlank(string line) => string.IsNullOrWhiteSpace(line);

    private static bool AllBlank(IReadOnlyList<string> lines, int start, int count)
    {
        for (int i = start; i < start + count; i++)
        {
            if (!IsBlank(lines[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static string CollapseWhitespace(string line)
    {
        int n = line.Length;
        while (n > 0 && char.IsWhiteSpace(line[n - 1]))
        {
            n--;
        }

        bool needs = n != line.Length;
        for (int i = 0; i < n && !needs; i++)
        {
            if (char.IsWhiteSpace(line[i]) && (line[i] != ' ' || (i + 1 < n && char.IsWhiteSpace(line[i + 1]))))
            {
                needs = true;
            }
        }

        if (!needs)
        {
            return line;
        }

        var sb = new StringBuilder(n);
        bool inWs = false;
        for (int i = 0; i < n; i++)
        {
            char c = line[i];
            if (char.IsWhiteSpace(c))
            {
                if (!inWs)
                {
                    sb.Append(' ');
                    inWs = true;
                }
            }
            else
            {
                sb.Append(c);
                inWs = false;
            }
        }

        return sb.ToString();
    }

    private static string RemoveWhitespace(string line)
    {
        int k = 0;
        while (k < line.Length && !char.IsWhiteSpace(line[k]))
        {
            k++;
        }

        if (k == line.Length)
        {
            return line;
        }

        var sb = new StringBuilder(line.Length);
        foreach (char c in line)
        {
            if (!char.IsWhiteSpace(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static void Intern(IReadOnlyList<string> a, IReadOnlyList<string> b, int[] ida, int[] idb, DiffOptions options)
    {
        var map = new Dictionary<string, int>(Math.Min(a.Count + b.Count, 1 << 20), StringComparer.Ordinal);
        bool normalize = options.NormalizesLines;
        for (int i = 0; i < a.Count; i++)
        {
            string key = normalize ? Normalize(a[i], options) : a[i];
            if (!map.TryGetValue(key, out int id))
            {
                id = map.Count;
                map.Add(key, id);
            }

            ida[i] = id;
        }

        for (int i = 0; i < b.Count; i++)
        {
            string key = normalize ? Normalize(b[i], options) : b[i];
            if (!map.TryGetValue(key, out int id))
            {
                id = map.Count;
                map.Add(key, id);
            }

            idb[i] = id;
        }
    }

    private static List<DiffHunk> BuildHunks(bool[] changedA, bool[] changedB)
    {
        var hunks = new List<DiffHunk>();
        int i = 0, j = 0;
        int n = changedA.Length, m = changedB.Length;
        while (i < n || j < m)
        {
            if (i < n && j < m && !changedA[i] && !changedB[j])
            {
                i++;
                j++;
                continue;
            }

            int si = i, sj = j;
            while (i < n && changedA[i])
            {
                i++;
            }

            while (j < m && changedB[j])
            {
                j++;
            }

            if (i == si && j == sj)
            {
                // Inconsistent marking would loop forever; cannot happen with a valid edit script.
                throw new InvalidOperationException("Internal diff error: unmatched unchanged lines.");
            }

            hunks.Add(new DiffHunk(si, i - si, sj, j - sj));
        }

        return hunks;
    }

    /// <summary>
    /// Moves pure insertions/deletions to a canonical position: onto an adjacent hunk if possible (merging them),
    /// otherwise as far down as possible.
    /// </summary>
    private static List<DiffHunk> SlideHunks(List<DiffHunk> hunks, int[] ida, int[] idb)
    {
        if (hunks.Count == 0)
        {
            return hunks;
        }

        var result = new List<DiffHunk>(hunks.Count);
        for (int k = 0; k < hunks.Count; k++)
        {
            DiffHunk h = hunks[k];
            bool pureDelete = h.CountB == 0 && h.CountA > 0;
            bool pureInsert = h.CountA == 0 && h.CountB > 0;
            if (!pureDelete && !pureInsert)
            {
                AddMerging(result, h);
                continue;
            }

            int[] seq = pureDelete ? ida : idb;
            int start = pureDelete ? h.StartA : h.StartB;
            int len = pureDelete ? h.CountA : h.CountB;

            // Bounds in the sliding sequence: previous hunk's end and next hunk's start.
            DiffHunk? prev = result.Count > 0 ? result[^1] : null;
            int lowLimit = prev is null ? 0 : (pureDelete ? prev.Value.EndA : prev.Value.EndB);
            int highLimit = k + 1 < hunks.Count ? (pureDelete ? hunks[k + 1].StartA : hunks[k + 1].StartB) : seq.Length;

            int up = start;
            while (up > lowLimit && seq[up - 1] == seq[up + len - 1])
            {
                up--;
            }

            int down = start;
            while (down + len < highLimit && seq[down] == seq[down + len])
            {
                down++;
            }

            int chosen;
            if (down + len == highLimit && k + 1 < hunks.Count)
            {
                chosen = down; // touches the next hunk
            }
            else if (up == lowLimit && prev is not null)
            {
                chosen = up; // touches the previous hunk
            }
            else
            {
                chosen = down;
            }

            int shift = chosen - start;
            DiffHunk moved = new(h.StartA + shift, h.CountA, h.StartB + shift, h.CountB);
            AddMerging(result, moved);

            if (k + 1 < hunks.Count && moved.EndA == hunks[k + 1].StartA && moved.EndB == hunks[k + 1].StartB)
            {
                DiffHunk next = hunks[k + 1];
                result[^1] = new DiffHunk(moved.StartA, next.EndA - moved.StartA, moved.StartB, next.EndB - moved.StartB);
                k++;
            }
        }

        return result;
    }

    private static void AddMerging(List<DiffHunk> result, DiffHunk h)
    {
        if (result.Count > 0)
        {
            DiffHunk last = result[^1];
            if (last.EndA == h.StartA && last.EndB == h.StartB)
            {
                result[^1] = new DiffHunk(last.StartA, h.EndA - last.StartA, last.StartB, h.EndB - last.StartB);
                return;
            }
        }

        result.Add(h);
    }

    /// <summary>The actual diff computation over interned ids, marking changed elements.</summary>
    private sealed class Engine
    {
        private const int HistogramMaxChain = 64;

        private readonly int[] _a;
        private readonly int[] _b;
        private readonly bool[] _ca;
        private readonly bool[] _cb;
        private readonly DiffOptions _options;
        private readonly CancellationToken _ct;
        private int _checkCounter;

        public Engine(int[] a, int[] b, bool[] ca, bool[] cb, DiffOptions options, CancellationToken ct)
        {
            _a = a;
            _b = b;
            _ca = ca;
            _cb = cb;
            _options = options;
            _ct = ct;
        }

        public void Run()
        {
            switch (_options.Algorithm)
            {
                case DiffAlgorithm.Patience:
                    Patience(0, _a.Length, 0, _b.Length);
                    break;
                case DiffAlgorithm.Histogram:
                    Histogram(0, _a.Length, 0, _b.Length);
                    break;
                default:
                    MyersWithDiscard(0, _a.Length, 0, _b.Length);
                    break;
            }
        }

        private void CheckCancel()
        {
            if ((++_checkCounter & 0x3FF) == 0)
            {
                _ct.ThrowIfCancellationRequested();
            }
        }

        // ---------- Myers with preprocessing ----------

        /// <summary>
        /// Strips common prefix/suffix, marks lines that have no match on the other side as changed and runs Myers on
        /// the remaining lines (re-indexed), which is equivalent but much faster on very different inputs.
        /// </summary>
        private void MyersWithDiscard(int aLo, int aHi, int bLo, int bHi)
        {
            while (aLo < aHi && bLo < bHi && _a[aLo] == _b[bLo])
            {
                aLo++;
                bLo++;
            }

            while (aLo < aHi && bLo < bHi && _a[aHi - 1] == _b[bHi - 1])
            {
                aHi--;
                bHi--;
            }

            if (aLo == aHi || bLo == bHi)
            {
                Mark(aLo, aHi, bLo, bHi);
                return;
            }

            var inB = new HashSet<int>();
            for (int j = bLo; j < bHi; j++)
            {
                inB.Add(_b[j]);
            }

            var inA = new HashSet<int>();
            for (int i = aLo; i < aHi; i++)
            {
                inA.Add(_a[i]);
            }

            var keptA = new List<int>(aHi - aLo);
            for (int i = aLo; i < aHi; i++)
            {
                if (inB.Contains(_a[i]))
                {
                    keptA.Add(i);
                }
                else
                {
                    _ca[i] = true;
                }
            }

            var keptB = new List<int>(bHi - bLo);
            for (int j = bLo; j < bHi; j++)
            {
                if (inA.Contains(_b[j]))
                {
                    keptB.Add(j);
                }
                else
                {
                    _cb[j] = true;
                }
            }

            if (keptA.Count == 0 || keptB.Count == 0)
            {
                foreach (int i in keptA)
                {
                    _ca[i] = true;
                }

                foreach (int j in keptB)
                {
                    _cb[j] = true;
                }

                return;
            }

            int[] sa = new int[keptA.Count];
            int[] sb = new int[keptB.Count];
            for (int i = 0; i < sa.Length; i++)
            {
                sa[i] = _a[keptA[i]];
            }

            for (int j = 0; j < sb.Length; j++)
            {
                sb[j] = _b[keptB[j]];
            }

            bool[] ra = new bool[sa.Length];
            bool[] rb = new bool[sb.Length];
            new Myers(sa, sb, ra, rb, _options.Minimal, _ct).Compare(0, sa.Length, 0, sb.Length);
            for (int i = 0; i < ra.Length; i++)
            {
                if (ra[i])
                {
                    _ca[keptA[i]] = true;
                }
            }

            for (int j = 0; j < rb.Length; j++)
            {
                if (rb[j])
                {
                    _cb[keptB[j]] = true;
                }
            }
        }

        private void Mark(int aLo, int aHi, int bLo, int bHi)
        {
            for (int i = aLo; i < aHi; i++)
            {
                _ca[i] = true;
            }

            for (int j = bLo; j < bHi; j++)
            {
                _cb[j] = true;
            }
        }

        // ---------- Patience ----------

        private void Patience(int aLo, int aHi, int bLo, int bHi)
        {
            CheckCancel();
            while (aLo < aHi && bLo < bHi && _a[aLo] == _b[bLo])
            {
                aLo++;
                bLo++;
            }

            while (aLo < aHi && bLo < bHi && _a[aHi - 1] == _b[bHi - 1])
            {
                aHi--;
                bHi--;
            }

            if (aLo == aHi || bLo == bHi)
            {
                Mark(aLo, aHi, bLo, bHi);
                return;
            }

            // Lines unique in both ranges: id -> (index in a, index in b); -2 marks "not unique".
            var uniq = new Dictionary<int, (int A, int B)>();
            for (int i = aLo; i < aHi; i++)
            {
                int id = _a[i];
                uniq[id] = uniq.TryGetValue(id, out var e) ? (-2, e.B) : (i, -1);
            }

            for (int j = bLo; j < bHi; j++)
            {
                int id = _b[j];
                if (uniq.TryGetValue(id, out var e) && e.A >= 0)
                {
                    uniq[id] = e.B == -1 ? (e.A, j) : (-2, -2);
                }
            }

            var pairs = new List<(int A, int B)>();
            for (int i = aLo; i < aHi; i++)
            {
                if (uniq.TryGetValue(_a[i], out var e) && e.A == i && e.B >= 0)
                {
                    pairs.Add(e);
                }
            }

            if (pairs.Count == 0)
            {
                MyersWithDiscard(aLo, aHi, bLo, bHi);
                return;
            }

            List<(int A, int B)> lis = LongestIncreasing(pairs);
            int pa = aLo, pb = bLo;
            foreach ((int ai, int bi) in lis)
            {
                Patience(pa, ai, pb, bi);
                pa = ai + 1;
                pb = bi + 1;
            }

            Patience(pa, aHi, pb, bHi);
        }

        /// <summary>Longest subsequence of pairs (ordered by A) with increasing B, by patience sorting.</summary>
        private static List<(int A, int B)> LongestIncreasing(List<(int A, int B)> pairs)
        {
            int n = pairs.Count;
            int[] tails = new int[n];   // index into pairs of the smallest tail of each pile
            int[] prev = new int[n];
            int piles = 0;
            for (int k = 0; k < n; k++)
            {
                int lo = 0, hi = piles;
                while (lo < hi)
                {
                    int mid = (lo + hi) >>> 1;
                    if (pairs[tails[mid]].B < pairs[k].B)
                    {
                        lo = mid + 1;
                    }
                    else
                    {
                        hi = mid;
                    }
                }

                prev[k] = lo > 0 ? tails[lo - 1] : -1;
                tails[lo] = k;
                if (lo == piles)
                {
                    piles++;
                }
            }

            var result = new List<(int A, int B)>(piles);
            for (int k = tails[piles - 1]; k >= 0; k = prev[k])
            {
                result.Add(pairs[k]);
            }

            result.Reverse();
            return result;
        }

        // ---------- Histogram ----------

        private void Histogram(int aLo, int aHi, int bLo, int bHi)
        {
            while (true)
            {
                CheckCancel();
                while (aLo < aHi && bLo < bHi && _a[aLo] == _b[bLo])
                {
                    aLo++;
                    bLo++;
                }

                while (aLo < aHi && bLo < bHi && _a[aHi - 1] == _b[bHi - 1])
                {
                    aHi--;
                    bHi--;
                }

                if (aLo == aHi || bLo == bHi)
                {
                    Mark(aLo, aHi, bLo, bHi);
                    return;
                }

                // Occurrences of each id in the A range.
                var occ = new Dictionary<int, List<int>>();
                for (int i = aLo; i < aHi; i++)
                {
                    if (!occ.TryGetValue(_a[i], out List<int>? list))
                    {
                        list = [];
                        occ.Add(_a[i], list);
                    }

                    list.Add(i);
                }

                int bestA = -1, bestB = -1, bestLen = 0, bestCount = int.MaxValue;
                int j = bLo;
                while (j < bHi)
                {
                    if (!occ.TryGetValue(_b[j], out List<int>? list) || list.Count > HistogramMaxChain || list.Count > bestCount)
                    {
                        j++;
                        continue;
                    }

                    int nextJ = j + 1;
                    foreach (int i0 in list)
                    {
                        int s1 = i0, s2 = j;
                        while (s1 > aLo && s2 > bLo && _a[s1 - 1] == _b[s2 - 1])
                        {
                            s1--;
                            s2--;
                        }

                        int e1 = i0 + 1, e2 = j + 1;
                        int minCount = list.Count;
                        while (e1 < aHi && e2 < bHi && _a[e1] == _b[e2])
                        {
                            minCount = Math.Min(minCount, occ[_a[e1]].Count);
                            e1++;
                            e2++;
                        }

                        for (int k = s1; k < i0; k++)
                        {
                            minCount = Math.Min(minCount, occ[_a[k]].Count);
                        }

                        int len = e1 - s1;
                        if (minCount < bestCount || (minCount == bestCount && len > bestLen))
                        {
                            bestCount = minCount;
                            bestLen = len;
                            bestA = s1;
                            bestB = s2;
                        }

                        nextJ = Math.Max(nextJ, e2);
                    }

                    j = nextJ;
                }

                if (bestA < 0)
                {
                    if (bestCount == int.MaxValue && occ.Values.All(l => l.Count <= HistogramMaxChain))
                    {
                        // No common element at all.
                        Mark(aLo, aHi, bLo, bHi);
                    }
                    else
                    {
                        MyersWithDiscard(aLo, aHi, bLo, bHi);
                    }

                    return;
                }

                Histogram(aLo, bestA, bLo, bestB);

                // Tail-iterate on the right part.
                aLo = bestA + bestLen;
                bLo = bestB + bestLen;
            }
        }
    }

    /// <summary>Myers' linear-space divide and conquer (after xdiff), with an optional cost cut-off.</summary>
    private sealed class Myers
    {
        private readonly int[] _a;
        private readonly int[] _b;
        private readonly bool[] _ca;
        private readonly bool[] _cb;
        private readonly int[] _kvdf;
        private readonly int[] _kvdb;
        private readonly int _offset;
        private readonly int _maxCost;
        private readonly CancellationToken _ct;

        public Myers(int[] a, int[] b, bool[] ca, bool[] cb, bool minimal, CancellationToken ct)
        {
            _a = a;
            _b = b;
            _ca = ca;
            _cb = cb;
            _ct = ct;
            int ndiags = a.Length + b.Length + 3;
            _kvdf = new int[ndiags];
            _kvdb = new int[ndiags];
            _offset = b.Length + 1;
            if (minimal)
            {
                _maxCost = int.MaxValue;
            }
            else
            {
                int bits = 1;
                for (long n = ndiags; n != 0; n >>= 2)
                {
                    bits <<= 1;
                }

                _maxCost = Math.Max(bits, 256);
            }
        }

        public void Compare(int aLo, int aHi, int bLo, int bHi)
        {
            // Explicit stack to avoid deep recursion on large inputs.
            var stack = new Stack<(int, int, int, int)>();
            stack.Push((aLo, aHi, bLo, bHi));
            while (stack.Count > 0)
            {
                (aLo, aHi, bLo, bHi) = stack.Pop();
                _ct.ThrowIfCancellationRequested();
                while (aLo < aHi && bLo < bHi && _a[aLo] == _b[bLo])
                {
                    aLo++;
                    bLo++;
                }

                while (aLo < aHi && bLo < bHi && _a[aHi - 1] == _b[bHi - 1])
                {
                    aHi--;
                    bHi--;
                }

                if (aLo == aHi)
                {
                    for (int j = bLo; j < bHi; j++)
                    {
                        _cb[j] = true;
                    }

                    continue;
                }

                if (bLo == bHi)
                {
                    for (int i = aLo; i < aHi; i++)
                    {
                        _ca[i] = true;
                    }

                    continue;
                }

                (int x, int y) = Split(aLo, aHi, bLo, bHi);
                stack.Push((x, aHi, y, bHi));
                stack.Push((aLo, x, bLo, y));
            }
        }

        /// <summary>Finds a split point (x, y) on an optimal (or, beyond the cost limit, good) edit path.</summary>
        private (int X, int Y) Split(int off1, int lim1, int off2, int lim2)
        {
            int[] kvdf = _kvdf, kvdb = _kvdb;
            int o = _offset;
            int dmin = off1 - lim2, dmax = lim1 - off2;
            int fmid = off1 - off2, bmid = lim1 - lim2;
            bool odd = ((fmid - bmid) & 1) != 0;
            int fmin = fmid, fmax = fmid;
            int bmin = bmid, bmax = bmid;

            kvdf[fmid + o] = off1;
            kvdb[bmid + o] = lim1;

            for (int ec = 1; ; ec++)
            {
                if ((ec & 0xFF) == 0)
                {
                    _ct.ThrowIfCancellationRequested();
                }

                // Forward pass.
                if (fmin > dmin)
                {
                    kvdf[--fmin - 1 + o] = -1;
                }
                else
                {
                    ++fmin;
                }

                if (fmax < dmax)
                {
                    kvdf[++fmax + 1 + o] = -1;
                }
                else
                {
                    --fmax;
                }

                for (int d = fmax; d >= fmin; d -= 2)
                {
                    int i1 = kvdf[d - 1 + o] >= kvdf[d + 1 + o] ? kvdf[d - 1 + o] + 1 : kvdf[d + 1 + o];
                    int i2 = i1 - d;
                    while (i1 < lim1 && i2 < lim2 && _a[i1] == _b[i2])
                    {
                        i1++;
                        i2++;
                    }

                    kvdf[d + o] = i1;
                    if (odd && bmin <= d && d <= bmax && kvdb[d + o] <= i1)
                    {
                        return (i1, i2);
                    }
                }

                // Backward pass.
                if (bmin > dmin)
                {
                    kvdb[--bmin - 1 + o] = int.MaxValue;
                }
                else
                {
                    ++bmin;
                }

                if (bmax < dmax)
                {
                    kvdb[++bmax + 1 + o] = int.MaxValue;
                }
                else
                {
                    --bmax;
                }

                for (int d = bmax; d >= bmin; d -= 2)
                {
                    int i1 = kvdb[d - 1 + o] < kvdb[d + 1 + o] ? kvdb[d - 1 + o] : kvdb[d + 1 + o] - 1;
                    int i2 = i1 - d;
                    while (i1 > off1 && i2 > off2 && _a[i1 - 1] == _b[i2 - 1])
                    {
                        i1--;
                        i2--;
                    }

                    kvdb[d + o] = i1;
                    if (!odd && fmin <= d && d <= fmax && i1 <= kvdf[d + o])
                    {
                        return (i1, i2);
                    }
                }

                if (ec >= _maxCost)
                {
                    // Give up on minimality: split at the furthest-reaching forward or backward point.
                    int fbest = -1, fbestX = 0;
                    for (int d = fmax; d >= fmin; d -= 2)
                    {
                        int i1 = Math.Min(kvdf[d + o], lim1);
                        int i2 = i1 - d;
                        if (lim2 < i2)
                        {
                            i1 = lim2 + d;
                            i2 = lim2;
                        }

                        if (fbest < i1 + i2)
                        {
                            fbest = i1 + i2;
                            fbestX = i1;
                        }
                    }

                    int bbest = int.MaxValue, bbestX = 0;
                    for (int d = bmax; d >= bmin; d -= 2)
                    {
                        int i1 = Math.Max(off1, kvdb[d + o]);
                        int i2 = i1 - d;
                        if (i2 < off2)
                        {
                            i1 = off2 + d;
                            i2 = off2;
                        }

                        if (i1 + i2 < bbest)
                        {
                            bbest = i1 + i2;
                            bbestX = i1;
                        }
                    }

                    int x, y;
                    if ((lim1 + lim2) - bbest < fbest - (off1 + off2))
                    {
                        x = fbestX;
                        y = fbest - fbestX;
                    }
                    else
                    {
                        x = bbestX;
                        y = bbest - bbestX;
                    }

                    bool degenerate = (x == off1 && y == off2) || (x == lim1 && y == lim2);
                    if (!degenerate && x >= off1 && x <= lim1 && y >= off2 && y <= lim2)
                    {
                        return (x, y);
                    }
                }
            }
        }
    }
}
