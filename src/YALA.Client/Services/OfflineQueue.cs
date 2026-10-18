using System.Text.Json;
using Microsoft.JSInterop;

namespace YALA.Client.Services;

public sealed class OfflineQueue(IJSRuntime js)
{
    private const string StorageKey = "yala.pending-mutations.v2";
    private readonly SemaphoreSlim gate = new(1, 1);
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public async Task<bool> HasLegacyChangesAsync() =>
        !string.IsNullOrWhiteSpace(await js.InvokeAsync<string?>("localStorage.getItem", "yala.pending-mutations"));

    public Task DiscardLegacyAsync() => js.InvokeVoidAsync("localStorage.removeItem", "yala.pending-mutations").AsTask();

    public async Task<IReadOnlyList<QueuedMutation>> GetAsync()
    {
        try
        {
            var json = await js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            return string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<QueuedMutation>>(json, Options) ?? [];
        }
        catch (Exception ex) when (ex is JSException or JsonException) { return []; }
    }

    public async Task EnqueueAsync(QueuedMutation mutation)
    {
        await gate.WaitAsync();
        try
        {
            var pending = (await GetAsync()).ToList();
            pending.RemoveAll(x => x.OperationId == mutation.OperationId);
            pending.Add(mutation);
            await SaveAsync(pending);
        }
        finally { gate.Release(); }
    }

    public async Task RemoveAsync(Guid operationId)
    {
        await gate.WaitAsync();
        try { await SaveAsync((await GetAsync()).Where(x => x.OperationId != operationId).ToList()); }
        finally { gate.Release(); }
    }

    private Task SaveAsync(IReadOnlyList<QueuedMutation> pending) =>
        js.InvokeVoidAsync("localStorage.setItem", StorageKey, JsonSerializer.Serialize(pending, Options)).AsTask();
}

public sealed class QueuedMutation
{
    public string UserId { get; set; } = "";
    public Guid HouseholdId { get; set; }
    public string? Error { get; set; }
    public Guid OperationId { get; set; } = Guid.NewGuid();
    public string Method { get; set; } = "POST";
    public string Uri { get; set; } = "";
    public string? BodyJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
