using Relliga.Domain;

namespace Relliga.Application;

public sealed record ConnectionStatus(string State, string Description, string? InputKind = null);
public sealed record SyncPage(string ConnectionId, string Scope, string? Cursor, IReadOnlyList<ConversationMessage> Messages,
    IReadOnlyList<(string Account, long Chat, long Message)>? Deleted = null);
public interface ICredentialVault
{
    Task<byte[]?> ReadAsync(string key, CancellationToken ct = default);
    Task WriteAsync(string key, byte[] secret, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
}
public interface IConnectionStore
{
    Task<ConnectionRecord?> GetConnectionAsync(string provider, CancellationToken ct = default);
    Task SaveConnectionAsync(ConnectionRecord connection, CancellationToken ct = default);
    Task<string?> GetCheckpointAsync(string connectionId, string scope, CancellationToken ct = default);
    Task ApplySyncPageAsync(SyncPage page, CancellationToken ct = default);
    Task<IReadOnlyList<long>> GetMessageIdsAsync(string accountId, long chatId, long after, CancellationToken ct = default);
    Task<IReadOnlyList<long>> GetLiveChatIdsAsync(string accountId, CancellationToken ct = default);
    Task<IReadOnlyList<ConversationSummary>> GetConversationsAsync(string? text = null, CancellationToken ct = default);
}
public interface IConnectedProvider : IAsyncDisposable
{
    string Name { get; }
    event Action<ConnectionStatus>? StatusChanged;
    Task RunAsync(ConnectionRecord connection, IConnectionStore store, CancellationToken ct, bool once = false);
    Task AuthorizeAsync(string input, CancellationToken ct);
    Task<string> DisconnectAsync(ConnectionRecord? connection, CancellationToken ct);
    void Refresh();
}
public sealed class ProviderFailure(string message, bool reauthorize = false, TimeSpan? retryAfter = null) : IOException(message)
{
    public bool Reauthorize { get; } = reauthorize;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

// Owns one provider worker. Database transactions belong to the store; networking never holds LibraryActivity.
public sealed class SynchronizationService(IConnectionStore store, IConnectedProvider provider) : IAsyncDisposable, IDisposable
{
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private CancellationTokenSource? stopping;
    private Task? worker;
    private bool disposed;
    public event Action<ConnectionStatus>? StatusChanged;
    public ConnectionStatus Status { get; private set; } = new("Disconnected", "No connected account.");
    private void Publish(ConnectionStatus status) { Status = status; StatusChanged?.Invoke(status); }
    public async Task ResumeSavedAsync(CancellationToken ct = default)
    {
        var connection = await store.GetConnectionAsync(provider.Name, ct);
        if (connection is { Automatic: true } && connection.State != "Disconnected") await StartAsync(ct);
        else if (connection is not null) Publish(new(connection.State, connection.State == "Disconnected" ? "Account disconnected. Local history remains available." : "Automatic synchronization is paused."));
    }
    public async Task StartAsync(CancellationToken ct = default)
    {
        await lifecycle.WaitAsync(ct);
        try
        {
            var connection = await store.GetConnectionAsync(provider.Name, ct);
            if (worker is { IsCompleted: false })
            {
                if (connection is { Automatic: false } || Status.State is "Paused" or "Error" or "Reauthorization required") await StopAsync();
                else { provider.Refresh(); return; }
            }
            if (connection is null || connection.State == "Disconnected") connection = new(Guid.NewGuid().ToString("N"), provider.Name, true, "Connecting");
            connection = connection with { Automatic = true, State = "Connecting" };
            await store.SaveConnectionAsync(connection, ct);
            stopping?.Dispose(); stopping = new();
            provider.StatusChanged -= Publish; provider.StatusChanged += Publish;
            worker = Task.Run(() => WorkAsync(connection, stopping.Token));
        }
        finally { lifecycle.Release(); }
    }
    private async Task WorkAsync(ConnectionRecord connection, CancellationToken ct, bool once = false)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try { await provider.RunAsync(connection, store, ct, once); return; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (ProviderFailure ex)
            {
                if (ex.Reauthorize) { Publish(new("Reauthorization required", ex.Message)); await SaveTerminalStateAsync(connection,"Reauthorization required",ct); return; }
                var delay = ex.RetryAfter ?? TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Min(++attempt, 8))));
                Publish(new("Retrying", $"{ex.Message} Retrying in {Math.Ceiling(delay.TotalSeconds):N0} seconds."));
                try { await Task.Delay(delay, ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            }
            catch (Exception ex) { Publish(new("Error", ex is DllNotFoundException or PlatformNotSupportedException or InvalidDataException ? ex.Message : "Synchronization stopped safely. " + ex.GetType().Name + ". Review local configuration and reconnect.")); await SaveTerminalStateAsync(connection,"Error",ct); return; }
        }
    }
    private async Task SaveTerminalStateAsync(ConnectionRecord connection,string state,CancellationToken ct)
    {
        try { var current = await store.GetConnectionAsync(provider.Name,ct); if (current?.Id == connection.Id) await store.SaveConnectionAsync(current with { State=state, Automatic=false },ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { Publish(new("Error","Synchronization failed, and connection status could not be persisted ("+ex.GetType().Name+"). Check the library before reconnecting.")); }
    }
    public Task AuthorizeAsync(string input, CancellationToken ct = default) => provider.AuthorizeAsync(input, ct);
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await lifecycle.WaitAsync(ct);
        try
        {
            if (worker is { IsCompleted: false }) { provider.Refresh(); return; }
            var connection = await store.GetConnectionAsync(provider.Name,ct);
            if (connection is null || connection.State == "Disconnected") throw new InvalidOperationException("Connect an account before refreshing.");
            var once = !connection.Automatic;
            connection = connection with { State = "Synchronizing" }; await store.SaveConnectionAsync(connection,ct);
            stopping?.Dispose(); stopping = new(); provider.StatusChanged -= Publish; provider.StatusChanged += Publish;
            worker = Task.Run(() => WorkAsync(connection,stopping.Token,once));
        }
        finally { lifecycle.Release(); }
    }
    public async Task PauseAsync(CancellationToken ct = default)
    {
        await lifecycle.WaitAsync(ct);
        try { await StopAsync(); var connection = await store.GetConnectionAsync(provider.Name, ct); if (connection is not null) await store.SaveConnectionAsync(connection with { Automatic = false, State = "Paused" }, ct); Publish(new("Paused", "Synchronization paused. Local browsing is available.")); }
        finally { lifecycle.Release(); }
    }
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        await lifecycle.WaitAsync(ct);
        try
        {
            // Logout must complete while the session is active. Failure leaves it connected for a safe retry.
            var connection = await store.GetConnectionAsync(provider.Name, ct);
            var outcome = await provider.DisconnectAsync(connection, ct); await StopAsync();
            if (connection is not null) await store.SaveConnectionAsync(connection with { Automatic = false, State = "Disconnected" }, ct);
            Publish(new("Disconnected", outcome + " Local history was retained."));
        }
        finally { lifecycle.Release(); }
    }
    private async Task StopAsync() { stopping?.Cancel(); if (worker is not null) await worker; worker = null; }
    public async ValueTask DisposeAsync() { if (disposed) return; disposed = true; await StopAsync(); provider.StatusChanged -= Publish; await provider.DisposeAsync(); stopping?.Dispose(); lifecycle.Dispose(); }
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
