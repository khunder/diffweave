using DiffWeave.Extensibility;

namespace DiffWeave.Core.Tests.TestPlugin;

public sealed class GoodPlugin : IDiffWeavePlugin
{
    public static int ShutdownCount;

    public void Initialize(IPluginHost host)
    {
        host.Commands.Register(new CommandContribution
        {
            Id = "test.good.hello",
            Title = "Hello",
            Execute = (ctx, ct) => Task.CompletedTask,
        });
        host.MergeAssistants.Register(new EchoAssistant());
        host.Settings.Set("initialized", true);
        host.Settings.Changed += (_, _) => { };
        host.Logger.Info("good plugin up");
    }

    public void Shutdown() => ShutdownCount++;
}

public sealed class EchoAssistant : IMergeAssistant
{
    public string Id => "test.echo";

    public string DisplayName => "Echo";

    public string? Description => null;

    public MergeAssistantCapabilities Capabilities => MergeAssistantCapabilities.ProposeResolutions | MergeAssistantCapabilities.Deterministic;

    public Task<MergeAssistantResult> RunAsync(MergeAssistantRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new MergeAssistantResult
        {
            Proposals = [.. request.Snapshot.Regions.Where(r => r.Kind == MergeRegionKind.Conflict)
                .Select(r => new ResolutionProposal(r.Id, ["echo"], "test", 2.0))],
        });
}

public sealed class ThrowingInitPlugin : IDiffWeavePlugin
{
    public void Initialize(IPluginHost host)
    {
        host.Commands.Register(new CommandContribution { Id = "test.throwing.partial", Title = "Partial", Execute = (_, _) => Task.CompletedTask });
        throw new InvalidOperationException("boom in Initialize");
    }

    public void Shutdown()
    {
    }
}

public sealed class ThrowingShutdownPlugin : IDiffWeavePlugin
{
    public void Initialize(IPluginHost host)
        => host.Commands.Register(new CommandContribution
        {
            Id = "test.throwing.command",
            Title = "Throws",
            Execute = (_, _) => throw new InvalidOperationException("boom in command"),
        });

    public void Shutdown() => throw new InvalidOperationException("boom in Shutdown");
}

public sealed class NotAPlugin
{
}
