using System.Text.Json;

namespace YALA.Client.Services;

public sealed class OfflineSyncService(ApiClient api, OfflineQueue queue, ClientState state)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<IReadOnlyList<QueuedMutation>> GetPendingAsync() =>
        (await queue.GetAsync()).Where(x => x.UserId == state.UserId && x.HouseholdId == state.CurrentHouseholdId).ToArray();

    public async Task ExecuteAsync(string uri, object? body, Func<Task> send)
    {
        if (state.UserId is null || state.CurrentHouseholdId is not Guid householdId)
            throw new InvalidOperationException("Choose a household before changing the list.");

        var mutation = new QueuedMutation
        {
            UserId = state.UserId,
            HouseholdId = householdId,
            Uri = uri,
            BodyJson = body is null ? null : JsonSerializer.Serialize(body)
        };
        await gate.WaitAsync();
        try
        {
            // Keep later edits behind earlier offline edits, preserving their order.
            if ((await GetPendingAsync()).Any(x => x.Error is null))
            {
                await queue.EnqueueAsync(mutation);
                await FlushCoreAsync();
                return;
            }
            try { await send(); state.SetConnectivity(false, (await GetPendingAsync()).Count); }
            catch (Exception ex) when (IsConnectionFailure(ex))
            {
                await queue.EnqueueAsync(mutation);
                state.SetConnectivity(true, (await GetPendingAsync()).Count);
            }
        }
        finally { gate.Release(); }
    }

    public async Task FlushAsync()
    {
        await gate.WaitAsync();
        try { await FlushCoreAsync(); }
        finally { gate.Release(); }
    }

    private async Task FlushCoreAsync()
    {
        foreach (var mutation in await GetPendingAsync())
        {
            if (mutation.Error is not null) continue;
            try { await api.SendQueuedAsync(mutation); await queue.RemoveAsync(mutation.OperationId); }
            catch (Exception ex) when (IsConnectionFailure(ex))
            {
                state.SetConnectivity(true, (await GetPendingAsync()).Count);
                return;
            }
            catch (ApiException ex)
            {
                mutation.Error = ex.Message;
                await queue.EnqueueAsync(mutation);
            }
        }
        state.SetConnectivity(false, (await GetPendingAsync()).Count);
    }

    public async Task RetryAsync(Guid operationId)
    {
        var mutation = (await GetPendingAsync()).Single(x => x.OperationId == operationId);
        mutation.Error = null;
        await queue.EnqueueAsync(mutation);
        await FlushAsync();
    }

    public async Task DiscardAsync(Guid operationId)
    {
        if ((await GetPendingAsync()).Any(x => x.OperationId == operationId)) await queue.RemoveAsync(operationId);
        state.SetConnectivity(state.IsOffline, (await GetPendingAsync()).Count);
    }

    public static bool IsConnectionFailure(Exception ex) =>
        ex is HttpRequestException { StatusCode: null } or TaskCanceledException;
}
