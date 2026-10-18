using System.Text.Json;
using Microsoft.JSInterop;
using YALA.Client.Models;

namespace YALA.Client.Services;

public sealed class OfflineSnapshotCache(IJSRuntime js, ClientState state)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public async Task SaveSessionAsync(SessionSnapshot session) => await SaveAsync("yala.session", session);
    public async Task<SessionSnapshot?> GetSessionAsync() => await GetAsync<SessionSnapshot>("yala.session");
    private string ListKey => $"yala.list:{state.UserId}:{state.CurrentHouseholdId}";
    public async Task SaveListAsync(ListSnapshot snapshot) => await SaveAsync(ListKey, snapshot);
    public async Task<ListSnapshot?> GetListAsync() => await GetAsync<ListSnapshot>(ListKey);

    public async Task ClearAsync()
    {
        await js.InvokeVoidAsync("localStorage.removeItem", "yala.session");
        await js.InvokeVoidAsync("localStorage.removeItem", ListKey);
    }

    private async Task SaveAsync<T>(string key, T value) =>
        await js.InvokeVoidAsync("localStorage.setItem", key, JsonSerializer.Serialize(value, Options));

    private async Task<T?> GetAsync<T>(string key)
    {
        try
        {
            var value = await js.InvokeAsync<string?>("localStorage.getItem", key);
            return string.IsNullOrWhiteSpace(value) ? default : JsonSerializer.Deserialize<T>(value, Options);
        }
        catch (Exception ex) when (ex is JSException or JsonException) { return default; }
    }
}
