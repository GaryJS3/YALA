using System.Text.Json;
using Microsoft.JSInterop;

namespace YALA.Client.Services;

public sealed class OfflineQueue(IJSRuntime js)
{
    private const string StorageKey = "yala.pending-mutations.v2";
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public async Task<bool> HasLegacyChangesAsync() =>
        !string.IsNullOrWhiteSpace(await js.InvokeAsync<string?>("localStorage.getItem", "yala.pending-mutations"));

    public Task DiscardLegacyAsync() => js.InvokeVoidAsync("localStorage.removeItem", "yala.pending-mutations").AsTask();

    public async Task<IReadOnlyList<QueuedMutation>> GetAsync()
    {
        try
        {
            var json = await js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            if (string.IsNullOrWhiteSpace(json)) return [];
            return JsonSerializer.Deserialize<List<QueuedMutation>>(json, Options)
                ?? throw new JsonException("The saved queue is not a list.");
        }
        catch (Exception ex) when (ex is JSException or JsonException)
        {
            throw new InvalidOperationException("Saved changes could not be read from this browser. They have been kept. Do not clear browser data; try reopening YALA.", ex);
        }
    }

    public Task EnqueueAsync(QueuedMutation mutation) =>
        UpdateAsync(mutation.OperationId, JsonSerializer.Serialize(mutation, Options));

    public Task RemoveAsync(Guid operationId) => UpdateAsync(operationId, null);

    private Task UpdateAsync(Guid operationId, string? replacement) =>
        js.InvokeVoidAsync("yalaOfflineQueue.update", StorageKey, operationId.ToString(), replacement).AsTask();
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
