using DiffWeave.Extensibility;

namespace DiffWeave.Core.Plugins;

/// <summary>The outcome of <see cref="MergeAssistantRunner.RunAsync"/>.</summary>
/// <param name="Result">The validated result (empty proposals on failure).</param>
/// <param name="Succeeded">False when the assistant threw, timed out or returned nothing.</param>
/// <param name="Elapsed">How long the assistant ran.</param>
public sealed record MergeAssistantRunResult(MergeAssistantResult Result, bool Succeeded, TimeSpan Elapsed);

/// <summary>
/// Runs merge assistants with failure isolation and result validation: the assistant runs on the thread pool with a
/// timeout; exceptions become error diagnostics; proposals for unknown regions, duplicate proposals for a region and
/// proposals with null lines are dropped (with a warning diagnostic); confidences are clamped to [0, 1].
/// </summary>
public static class MergeAssistantRunner
{
    /// <summary>The default timeout.</summary>
    public static TimeSpan DefaultTimeout { get; } = TimeSpan.FromSeconds(60);

    /// <summary>Runs <paramref name="assistant"/>. Throws only <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled.</summary>
    public static async Task<MergeAssistantRunResult> RunAsync(IMergeAssistant assistant, MergeAssistantRequest request, TimeSpan? timeout = null, IPluginLogger? logger = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assistant);
        ArgumentNullException.ThrowIfNull(request);
        logger ??= NullPluginLogger.Instance;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout ?? DefaultTimeout);
        MergeAssistantResult? raw;
        try
        {
            raw = await Task.Run(() => assistant.RunAsync(request, cts.Token), cts.Token).WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.Warn($"Merge assistant '{assistant.Id}' timed out.");
            return Failure("The assistant timed out.", watch.Elapsed);
        }
        catch (Exception ex)
        {
            logger.Error($"Merge assistant '{assistant.Id}' failed.", ex);
            return Failure($"The assistant failed: {ex.Message}", watch.Elapsed);
        }

        if (raw is null)
        {
            return Failure("The assistant returned no result.", watch.Elapsed);
        }

        return new MergeAssistantRunResult(Validate(raw, request.Snapshot), true, watch.Elapsed);
    }

    /// <summary>Validates and normalizes a result against a snapshot (see type remarks).</summary>
    public static MergeAssistantResult Validate(MergeAssistantResult result, MergeSnapshot snapshot)
    {
        var known = snapshot.Regions.Select(r => r.Id).ToHashSet();
        var seen = new HashSet<int>();
        var proposals = new List<ResolutionProposal>();
        var diagnostics = new List<AssistantDiagnostic>(result.Diagnostics?.Where(d => d is not null) ?? []);
        foreach (ResolutionProposal? p in result.Proposals ?? [])
        {
            if (p is null || p.ReplacementLines is null)
            {
                diagnostics.Add(new AssistantDiagnostic(MessageSeverity.Warning, "Dropped an empty proposal."));
                continue;
            }

            if (!known.Contains(p.RegionId))
            {
                diagnostics.Add(new AssistantDiagnostic(MessageSeverity.Warning, $"Dropped a proposal for unknown region {p.RegionId}."));
                continue;
            }

            if (!seen.Add(p.RegionId))
            {
                diagnostics.Add(new AssistantDiagnostic(MessageSeverity.Warning, $"Dropped a duplicate proposal for region {p.RegionId}.", p.RegionId));
                continue;
            }

            double c = double.IsNaN(p.Confidence) ? 0 : Math.Clamp(p.Confidence, 0, 1);
            string[] lines = [.. p.ReplacementLines.Select(l => l ?? string.Empty)];
            proposals.Add(p with { Confidence = c, ReplacementLines = lines, Rationale = p.Rationale ?? string.Empty });
        }

        return new MergeAssistantResult { Proposals = proposals, Diagnostics = diagnostics };
    }

    private static MergeAssistantRunResult Failure(string message, TimeSpan elapsed)
        => new(new MergeAssistantResult { Diagnostics = [new AssistantDiagnostic(MessageSeverity.Error, message)] }, false, elapsed);
}
