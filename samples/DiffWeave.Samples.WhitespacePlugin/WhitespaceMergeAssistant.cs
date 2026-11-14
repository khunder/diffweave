using System.Globalization;
using System.Text;
using DiffWeave.Extensibility;

namespace DiffWeave.Samples.WhitespacePlugin;

/// <summary>
/// A deterministic merge assistant: for every targeted conflict whose left and right sides are identical once all
/// whitespace is removed, it proposes the preferred side (option "prefer": "left" (default) or "right") with trailing
/// whitespace removed from every line. Other conflicts get an informational diagnostic.
/// </summary>
public sealed class WhitespaceMergeAssistant : IMergeAssistant
{
    /// <summary>The assistant id.</summary>
    public const string AssistantId = "diffweave.samples.whitespace.assistant";

    /// <inheritdoc/>
    public string Id => AssistantId;

    /// <inheritdoc/>
    public string DisplayName => "Whitespace normalizer";

    /// <inheritdoc/>
    public string? Description => "Resolves conflicts whose sides differ only in whitespace (sample plugin).";

    /// <inheritdoc/>
    public MergeAssistantCapabilities Capabilities => MergeAssistantCapabilities.ProposeResolutions | MergeAssistantCapabilities.Deterministic;

    /// <inheritdoc/>
    public Task<MergeAssistantResult> RunAsync(MergeAssistantRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        MergeSnapshot s = request.Snapshot;
        bool preferRight = request.Options.TryGetValue("prefer", out string? prefer) && string.Equals(prefer, "right", StringComparison.OrdinalIgnoreCase);

        IEnumerable<MergeRegionDescriptor> targets = request.TargetRegionIds.Count > 0
            ? s.Regions.Where(r => request.TargetRegionIds.Contains(r.Id))
            : s.Regions.Where(r => r.Kind == MergeRegionKind.Conflict && !r.IsResolved);

        var proposals = new List<ResolutionProposal>();
        var diagnostics = new List<AssistantDiagnostic>();
        foreach (MergeRegionDescriptor region in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (region.Kind != MergeRegionKind.Conflict)
            {
                continue;
            }

            IReadOnlyList<string> left = Slice(s.LeftLines, region.Left);
            IReadOnlyList<string> right = Slice(s.RightLines, region.Right);
            if (Squash(left) != Squash(right))
            {
                diagnostics.Add(new AssistantDiagnostic(MessageSeverity.Information,
                    string.Create(CultureInfo.InvariantCulture, $"Conflict {region.Id} changes more than whitespace; no proposal."), region.Id));
                continue;
            }

            IReadOnlyList<string> chosen = preferRight ? right : left;
            string[] normalized = [.. chosen.Select(l => l.TrimEnd())];
            string side = preferRight ? "right" : "left";
            proposals.Add(new ResolutionProposal(
                region.Id,
                normalized,
                $"Both sides contain the same text apart from whitespace; taking the {side} side without trailing whitespace.",
                Confidence: left.Count == right.Count ? 0.95 : 0.8));
        }

        return Task.FromResult(new MergeAssistantResult { Proposals = proposals, Diagnostics = diagnostics });
    }

    private static string[] Slice(IReadOnlyList<string> lines, LineSpan span)
    {
        var result = new string[span.Count];
        for (int k = 0; k < span.Count; k++)
        {
            result[k] = lines[span.Start + k];
        }

        return result;
    }

    private static string Squash(IReadOnlyList<string> lines)
    {
        var sb = new StringBuilder();
        foreach (string line in lines)
        {
            foreach (char c in line)
            {
                if (!char.IsWhiteSpace(c))
                {
                    sb.Append(c);
                }
            }
        }

        return sb.ToString();
    }
}
