using System.Net;
using System.Net.Http.Json;
using Microsoft.JSInterop;
using YALA.Client.Models;
using YALA.Client.Services;

namespace YALA.Tests;

internal sealed class BrowserStorage : IJSRuntime
{
    public Dictionary<string, string> Local { get; } = [];
    public Dictionary<string, string> Session { get; } = [];

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
        InvokeAsync<TValue>(identifier, default, args);

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        var storage = identifier.StartsWith("sessionStorage") ? Session : Local;
        object? result = null;
        if (identifier.EndsWith("getItem")) result = storage.GetValueOrDefault((string)args![0]!);
        if (identifier.EndsWith("setItem")) storage[(string)args![0]!] = (string)args[1]!;
        if (identifier.EndsWith("removeItem")) storage.Remove((string)args![0]!);
        return ValueTask.FromResult(result is null ? default! : (TValue)result);
    }
}

internal sealed class StubHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    public static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
}

internal sealed class ClientFixture : IDisposable
{
    public BrowserStorage Storage { get; } = new();
    public ClientState State { get; } = new();
    public OfflineQueue Queue { get; }
    public OfflineSyncService Sync { get; }
    public OfflineSnapshotCache Cache { get; }
    public ApiClient Api { get; }
    public HttpClient Http { get; }
    public Guid HouseholdId { get; } = Guid.NewGuid();

    public ClientFixture(Func<HttpRequestMessage, Task<HttpResponseMessage>> send)
    {
        State.SetUser("alice");
        State.SetHousehold(new HouseholdChoice { Id = HouseholdId, Name = "Home" });
        Http = new HttpClient(new StubHttpHandler(send)) { BaseAddress = new Uri("https://localhost/") };
        Api = new ApiClient(Http, State, Storage);
        Queue = new OfflineQueue(Storage);
        Cache = new OfflineSnapshotCache(Storage, State);
        Sync = new OfflineSyncService(Api, Queue, State);
    }

    public QueuedMutation Mutation(string uri = "api/list/clear-checked") =>
        new() { Uri = uri, UserId = "alice", HouseholdId = HouseholdId };

    public void Dispose() => Http.Dispose();
}
