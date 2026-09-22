using System.Text.Json;
using Microsoft.JSInterop;
using YALA.Client.Models;

namespace YALA.Client.Services;

public sealed class OfflineSnapshotCache(IJSRuntime js)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public async Task SaveSessionAsync(SessionSnapshot session) => await SaveAsync("yala.session", session);
    public async Task<SessionSnapshot?> GetSessionAsync() => await GetAsync<SessionSnapshot>("yala.session");
    public async Task SaveListAsync(ListSnapshot snapshot) => await SaveAsync("yala.list", snapshot);
    public async Task<ListSnapshot?> GetListAsync() => await GetAsync<ListSnapshot>("yala.list");

    public async Task ClearAsync()
    {
        await js.InvokeVoidAsync("localStorage.removeItem", "yala.session");
        await js.InvokeVoidAsync("localStorage.removeItem", "yala.list");
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
        catch (JSException) { return default; }
    }
}
