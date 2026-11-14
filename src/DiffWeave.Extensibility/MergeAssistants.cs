namespace DiffWeave.Extensibility;

/// <summary>A 0-based line range: <see cref="Start"/> and <see cref="Count"/> lines.</summary>
/// <param name="Start">First line (0-based).</param>
/// <param name="Count">Number of lines; 0 denotes an empty range positioned before line <paramref name="Start"/>.</param>
public readonly record struct LineSpan(int Start, int Count)
{
    /// <summary>One past the last line.</summary>
    public int End => Start + Count;
}

/// <summary>Classification of a merge region as exposed to plugins (mirrors the core merge engine).</summary>
public enum MergeRegionKind
{
    /// <summary>All three inputs agree.</summary>
    Stable,

    /// <summary>Only the left input differs from the base.</summary>
    LeftChanged,

    /// <summary>Only the right input differs from the base.</summary>
    RightChanged,

    /// <summary>Left and right made the same change.</summary>
    BothChangedSame,

    /// <summary>Left and right made different changes.</summary>
    Conflict,
}

/// <summary>A region of the merge as exposed to plugins.</summary>
/// <param name="Id">The region id, stable for the lifetime of a merge session (it is the region index).</param>
/// <param name="Kind">The classification.</param>
/// <param name="Base">Lines of the base input belonging to the region.</param>
/// <param name="Left">Lines of the left input belonging to the region.</param>
/// <param name="Right">Lines of the right input belonging to the region.</param>
/// <param name="Output">Lines of the current output produced by the region (empty for unresolved conflicts).</param>
/// <param name="IsResolved">Whether the user (or auto-merge) has chosen a resolution.</param>
/// <param name="IsWhitespaceOnly">For conflicts: left and right differ only in whitespace.</param>
public sealed record MergeRegionDescriptor(
    int Id,
    MergeRegionKind Kind,
    LineSpan Base,
    LineSpan Left,
    LineSpan Right,
    LineSpan Output,
    bool IsResolved,
    bool IsWhitespaceOnly);

/// <summary>
/// Immutable snapshot of a merge: the three inputs, the current output, file names, language and regions.
/// Lines never contain line terminators.
/// </summary>
public sealed record MergeSnapshot
{
    /// <summary>Base (common ancestor) lines; empty for two-way merges.</summary>
    public required IReadOnlyList<string> BaseLines { get; init; }

    /// <summary>Left ("local", "ours") lines.</summary>
    public required IReadOnlyList<string> LeftLines { get; init; }

    /// <summary>Right ("remote", "theirs") lines.</summary>
    public required IReadOnlyList<string> RightLines { get; init; }

    /// <summary>Current output lines (unresolved conflicts contribute no lines).</summary>
    public required IReadOnlyList<string> OutputLines { get; init; }

    /// <summary>Display name or path of the base file.</summary>
    public string? BaseName { get; init; }

    /// <summary>Display name or path of the left file.</summary>
    public string? LeftName { get; init; }

    /// <summary>Display name or path of the right file.</summary>
    public string? RightName { get; init; }

    /// <summary>Path the output will be saved to.</summary>
    public string? OutputName { get; init; }

    /// <summary>Language id (see <see cref="IFileTypeService"/>), e.g. "csharp"; "plaintext" when unknown.</summary>
    public string LanguageId { get; init; } = "plaintext";

    /// <summary>All regions in document order (including stable ones).</summary>
    public required IReadOnlyList<MergeRegionDescriptor> Regions { get; init; }
}

/// <summary>What a merge assistant can do. The host uses it to decide where to offer the assistant.</summary>
[Flags]
public enum MergeAssistantCapabilities
{
    /// <summary>Nothing declared.</summary>
    None = 0,

    /// <summary>Returns <see cref="ResolutionProposal"/>s for conflict regions.</summary>
    ProposeResolutions = 1,

    /// <summary>Returns explanations (as <see cref="ResolutionProposal.Rationale"/> or diagnostics) without replacement text.</summary>
    ExplainConflicts = 2,

    /// <summary>Needs <see cref="MergeAssistantRequest.UserInstruction"/>; the UI asks the user for it.</summary>
    RequiresUserInstruction = 4,

    /// <summary>Produces the same result for the same request.</summary>
    Deterministic = 8,

    /// <summary>
    /// Sends request data outside the process (e.g. to a remote service). The host requires explicit per-file consent
    /// before running such assistants; see docs/ai-extension-points.md.
    /// </summary>
    UsesNetwork = 16,
}

/// <summary>An immutable request handed to a merge assistant.</summary>
public sealed record MergeAssistantRequest
{
    /// <summary>The merge to work on.</summary>
    public required MergeSnapshot Snapshot { get; init; }

    /// <summary>The regions the user asked about (ids from <see cref="MergeSnapshot.Regions"/>). Empty means "all unresolved conflicts".</summary>
    public IReadOnlyList<int> TargetRegionIds { get; init; } = [];

    /// <summary>Optional free-text instruction from the user.</summary>
    public string? UserInstruction { get; init; }

    /// <summary>Free-form options (assistant-specific), e.g. from the assistant's settings page.</summary>
    public IReadOnlyDictionary<string, string> Options { get; init; } = new Dictionary<string, string>();
}

/// <summary>A proposed resolution for one region.</summary>
/// <param name="RegionId">Id of the region (see <see cref="MergeRegionDescriptor.Id"/>).</param>
/// <param name="ReplacementLines">The proposed output lines for the region (without line terminators).</param>
/// <param name="Rationale">Human-readable explanation shown in the preview.</param>
/// <param name="Confidence">Confidence between 0 and 1 (the host clamps out-of-range values).</param>
public sealed record ResolutionProposal(int RegionId, IReadOnlyList<string> ReplacementLines, string Rationale, double Confidence);

/// <summary>A diagnostic message produced by an assistant.</summary>
/// <param name="Severity">Severity.</param>
/// <param name="Message">Text.</param>
/// <param name="RegionId">Optional region the message is about.</param>
public sealed record AssistantDiagnostic(MessageSeverity Severity, string Message, int? RegionId = null);

/// <summary>The result of running a merge assistant.</summary>
public sealed record MergeAssistantResult
{
    /// <summary>Proposed resolutions, at most one per region.</summary>
    public IReadOnlyList<ResolutionProposal> Proposals { get; init; } = [];

    /// <summary>Messages for the user.</summary>
    public IReadOnlyList<AssistantDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>An empty result.</summary>
    public static MergeAssistantResult Empty { get; } = new();
}

/// <summary>
/// A component that looks at a merge and proposes resolutions. Assistants never modify the merge themselves:
/// they return proposals that the user previews, accepts or rejects.
/// </summary>
public interface IMergeAssistant
{
    /// <summary>Unique id.</summary>
    string Id { get; }

    /// <summary>Name shown in the UI.</summary>
    string DisplayName { get; }

    /// <summary>Optional description shown in the UI.</summary>
    string? Description { get; }

    /// <summary>Declared capabilities.</summary>
    MergeAssistantCapabilities Capabilities { get; }

    /// <summary>
    /// Runs the assistant. Called on a background thread; must honour <paramref name="cancellationToken"/>.
    /// The host validates the result (unknown regions and duplicate proposals are dropped) and catches exceptions.
    /// </summary>
    Task<MergeAssistantResult> RunAsync(MergeAssistantRequest request, CancellationToken cancellationToken);
}

/// <summary>Registry of merge assistants.</summary>
public interface IMergeAssistantRegistry
{
    /// <summary>All registered assistants.</summary>
    IReadOnlyList<IMergeAssistant> Assistants { get; }

    /// <summary>Raised (on any thread) when assistants are added or removed.</summary>
    event EventHandler? Changed;

    /// <summary>Registers an assistant; dispose the result to remove it. Throws when the id is already taken.</summary>
    IDisposable Register(IMergeAssistant assistant);

    /// <summary>Finds an assistant by id.</summary>
    IMergeAssistant? Find(string id);
}
