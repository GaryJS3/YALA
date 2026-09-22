using System.Text.Json;
using Microsoft.JSInterop;

namespace YALA.Client.Services;

public sealed class OfflineQueue(IJSRuntime js)
{
    private const string StorageKey = "yala.pending-mutations";
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<QueuedMutation>> GetAsync()
    {
        try
        {
            var json = await js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            return string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<QueuedMutation>>(json, Options) ?? [];
        }
        catch (JSException) { return []; }
    }

    public async Task EnqueueAsync(QueuedMutation mutation)
    {
        var pending = (await GetAsync()).ToList();
        pending.Add(mutation);
        await SaveAsync(pending);
    }

    public async Task RemoveAsync(Guid operationId)
    {
        var pending = (await GetAsync()).Where(x => x.OperationId != operationId).ToList();
        await SaveAsync(pending);
    }

    private Task SaveAsync(IReadOnlyList<QueuedMutation> pending) =>
        js.InvokeVoidAsync("localStorage.setItem", StorageKey, JsonSerializer.Serialize(pending, Options)).AsTask();
}

public sealed class QueuedMutation
{
    public Guid OperationId { get; set; } = Guid.NewGuid();
    public string Method { get; set; } = "POST";
    public string Uri { get; set; } = "";
    public string? BodyJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
