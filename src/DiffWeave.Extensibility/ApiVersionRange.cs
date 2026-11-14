using System.Globalization;

namespace DiffWeave.Extensibility;

/// <summary>
/// A range of extension API versions, as declared by a plugin manifest.
/// </summary>
/// <remarks>
/// Accepted syntaxes:
/// <list type="bullet">
/// <item><c>"1.0"</c> - at least 1.0 and below the next major version (2.0), i.e. semantic-version compatible.</item>
/// <item><c>"[1.0,3.0)"</c>, <c>"[1.0,1.4]"</c>, <c>"(1.0,)"</c>, <c>"[1.2,]"</c> - interval notation with inclusive
/// (<c>[</c> <c>]</c>) or exclusive (<c>(</c> <c>)</c>) bounds; an empty bound is unbounded.</item>
/// <item><c>"*"</c> - any version (discouraged).</item>
/// </list>
/// </remarks>
public sealed record ApiVersionRange
{
    /// <summary>Creates a range.</summary>
    public ApiVersionRange(Version? minimum, bool minimumInclusive, Version? maximum, bool maximumInclusive)
    {
        Minimum = minimum;
        MinimumInclusive = minimumInclusive;
        Maximum = maximum;
        MaximumInclusive = maximumInclusive;
    }

    /// <summary>The lower bound, or <see langword="null"/> when unbounded.</summary>
    public Version? Minimum { get; }

    /// <summary>Whether <see cref="Minimum"/> itself is part of the range.</summary>
    public bool MinimumInclusive { get; }

    /// <summary>The upper bound, or <see langword="null"/> when unbounded.</summary>
    public Version? Maximum { get; }

    /// <summary>Whether <see cref="Maximum"/> itself is part of the range.</summary>
    public bool MaximumInclusive { get; }

    /// <summary>Returns whether <paramref name="version"/> lies within the range.</summary>
    public bool Contains(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        version = Normalize(version);
        if (Minimum is not null)
        {
            int c = version.CompareTo(Normalize(Minimum));
            if (c < 0 || (c == 0 && !MinimumInclusive))
            {
                return false;
            }
        }

        if (Maximum is not null)
        {
            int c = version.CompareTo(Normalize(Maximum));
            if (c > 0 || (c == 0 && !MaximumInclusive))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Parses a range; throws <see cref="FormatException"/> on invalid input.</summary>
    public static ApiVersionRange Parse(string text)
    {
        if (!TryParse(text, out ApiVersionRange? range))
        {
            throw new FormatException($"'{text}' is not a valid API version range.");
        }

        return range;
    }

    /// <summary>Tries to parse a range.</summary>
    public static bool TryParse(string? text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ApiVersionRange? range)
    {
        range = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        text = text.Trim();
        if (text == "*")
        {
            range = new ApiVersionRange(null, true, null, true);
            return true;
        }

        if (text[0] is '[' or '(')
        {
            char last = text[^1];
            if (last is not (']' or ')') || text.Length < 3)
            {
                return false;
            }

            string[] parts = text[1..^1].Split(',');
            if (parts.Length != 2)
            {
                return false;
            }

            Version? min = null, max = null;
            if (parts[0].Trim().Length > 0 && !TryParseVersion(parts[0], out min))
            {
                return false;
            }

            if (parts[1].Trim().Length > 0 && !TryParseVersion(parts[1], out max))
            {
                return false;
            }

            if (min is not null && max is not null && Normalize(min) > Normalize(max))
            {
                return false;
            }

            range = new ApiVersionRange(min, text[0] == '[', max, last == ']');
            return true;
        }

        if (!TryParseVersion(text, out Version? v))
        {
            return false;
        }

        range = new ApiVersionRange(v, true, new Version(v.Major + 1, 0), false);
        return true;
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        if (Minimum is null && Maximum is null)
        {
            return "*";
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"{(MinimumInclusive ? '[' : '(')}{Minimum},{Maximum}{(MaximumInclusive ? ']' : ')')}");
    }

    private static bool TryParseVersion(string s, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Version? v)
    {
        s = s.Trim();
        if (!s.Contains('.', StringComparison.Ordinal))
        {
            s += ".0";
        }

        return Version.TryParse(s, out v);
    }

    private static Version Normalize(Version v)
        => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
}
