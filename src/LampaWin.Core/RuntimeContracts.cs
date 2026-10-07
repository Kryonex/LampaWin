namespace LampaWin.Core;

public enum RuntimeState { Stopped, Starting, Ready, Degraded, Recovering, Failed }
public sealed record RuntimeStatus(RuntimeState State, string Message);
public sealed record RuntimeSnapshot(Uri TorrServerBaseUri, Uri JackettBaseUri, string JackettApiKey,
    string TorrServerUser, string TorrServerPassword, IReadOnlyList<string> Indexers, IReadOnlyList<string> Warnings);

public interface ILocalRuntime : IAsyncDisposable
{
    event Action<RuntimeStatus>? StatusChanged;
    Task<RuntimeSnapshot> StartAsync(CancellationToken cancellationToken = default);
    Task<RuntimeSnapshot> RecoverAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
