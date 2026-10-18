using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.HtmlRendering.Infrastructure;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using YALA.Client.Models;
using YALA.Client.Pages;
using YALA.Client.Services;

namespace YALA.Tests;

public sealed class ActiveClientComponentTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Quick_add_events_queue_only_network_failures_and_do_not_throw_during_refresh(bool offline)
    {
        var failing = false;
        var household = Guid.NewGuid();
        using var fixture = new ClientFixture(request =>
        {
            if (failing && offline) throw new HttpRequestException("Offline");
            if (request.Method == HttpMethod.Post)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("Permission denied") });
            return Task.FromResult(request.RequestUri!.AbsolutePath switch
            {
                "/api/session" => StubHttpHandler.Json(new SessionSnapshot
                {
                    UserId = "alice", IsAuthenticated = true,
                    Households = new() { Current = new() { Id = household, Name = "Home" } }
                }),
                "/api/list" => StubHttpHandler.Json(new ListSnapshot()),
                _ => StubHttpHandler.Json(Array.Empty<QuickAddChoice>())
            });
        });
        var html = await RenderHomeAsync(fixture, interact: async renderer =>
        {
            failing = true;
            await renderer.DispatchAsync("oninput", new ChangeEventArgs { Value = "Milk" });
            await renderer.DispatchAsync("onkeydown", new KeyboardEventArgs { Key = "Enter" });
        });
        if (offline)
        {
            Assert.Contains("Offline", html);
            Assert.Contains("Milk", Assert.Single(await fixture.Queue.GetAsync()).BodyJson);
        }
        else
        {
            Assert.Contains("Permission denied", html);
            Assert.Empty(await fixture.Queue.GetAsync());
        }
    }

    [Fact]
    public async Task Home_renders_assigned_ad_hoc_items_in_the_correct_store_group()
    {
        var household = Guid.NewGuid();
        var store = Guid.NewGuid();
        using var fixture = new ClientFixture(request => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/api/session" => StubHttpHandler.Json(new SessionSnapshot
            {
                UserId = "alice", IsAuthenticated = true,
                Households = new() { Current = new() { Id = household, Name = "Home" } }
            }),
            "/api/list" => StubHttpHandler.Json(new ListSnapshot
            {
                Rows = [new() { Id = Guid.NewGuid(), Name = "Special groceries", AssignedStoreId = store, AssignedStoreName = "My Store", Quantity = 1 }],
                Stores = [new() { Id = store, Name = "My Store" }]
            }),
            _ => StubHttpHandler.Json(Array.Empty<QuickAddChoice>())
        }));
        var html = await RenderHomeAsync(fixture, "?view=stores");
        Assert.Contains("store-group", html);
        Assert.Contains("My Store", html);
        Assert.Contains("Special groceries", html);
        Assert.DoesNotContain("Any store", html);
    }

    [Fact]
    public async Task Home_renders_a_scoped_snapshot_when_offline()
    {
        using var fixture = new ClientFixture(_ => throw new HttpRequestException("Offline"));
        await fixture.Cache.SaveSessionAsync(new SessionSnapshot
        {
            UserId = "alice", IsAuthenticated = true,
            Households = new() { Current = new() { Id = fixture.HouseholdId, Name = "Home" } }
        });
        await fixture.Cache.SaveListAsync(new ListSnapshot { Rows = [new() { Name = "Cached milk", Quantity = 1 }] });
        await fixture.Queue.EnqueueAsync(fixture.Mutation());
        var html = await RenderHomeAsync(fixture);
        Assert.Contains("Cached milk", html);
        Assert.Contains("Offline", html);
        Assert.Contains("1 changes waiting", html);
    }

    [Fact]
    public async Task Api_gets_and_uploads_carry_the_tab_household_header()
    {
        var requests = new List<(string Path, string Household)>();
        using var fixture = new ClientFixture(request =>
        {
            requests.Add((request.RequestUri!.AbsolutePath, request.Headers.GetValues("X-Yala-Household").Single()));
            return Task.FromResult(request.RequestUri.AbsolutePath switch
            {
                "/api/list" => StubHttpHandler.Json(new ListSnapshot()),
                "/api/antiforgery" => StubHttpHandler.Json(new AntiforgeryResponse { Token = "token" }),
                _ => new HttpResponseMessage(HttpStatusCode.NoContent)
            });
        });
        fixture.State.SetHousehold(null);
        fixture.Storage.Session["yala.household"] = fixture.HouseholdId.ToString();
        await fixture.Api.GetListAsync();
        await fixture.Api.UploadItemImageAsync(Guid.NewGuid(), new TestBrowserFile());
        await fixture.Api.UploadStoreImageAsync(Guid.NewGuid(), new TestBrowserFile());
        Assert.Equal(5, requests.Count);
        Assert.All(requests, request => Assert.Equal(fixture.HouseholdId.ToString(), request.Household));
    }

    private static async Task<string> RenderHomeAsync(ClientFixture fixture, string query = "", Func<TestHtmlRenderer, Task>? interact = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSupplyValueFromQueryProvider();
        services.AddSingleton(fixture.Api);
        services.AddSingleton(fixture.State);
        services.AddSingleton(fixture.Queue);
        services.AddSingleton(fixture.Sync);
        services.AddSingleton(fixture.Cache);
        services.AddSingleton<IJSRuntime>(fixture.Storage);
        services.AddSingleton<NavigationManager>(new TestNavigationManager(query));
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new TestHtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = renderer.BeginRenderingComponent(typeof(Home), ParameterView.Empty);
            await component.QuiescenceTask;
            if (interact is not null) await interact(renderer);
            return component.ToHtmlString();
        });
    }

    private sealed class TestHtmlRenderer(IServiceProvider services, ILoggerFactory logger) : StaticHtmlRenderer(services, logger)
    {
        protected override IComponent ResolveComponentForRenderMode(Type componentType, int? parentComponentId, IComponentActivator componentActivator, IComponentRenderMode renderMode) =>
            componentActivator.CreateInstance(componentType);

        public Task DispatchAsync(string attribute, EventArgs args)
        {
            // Framework render-tree access is confined to this event-dispatch test adapter.
#pragma warning disable BL0006
            var frames = GetCurrentRenderTreeFrames(0);
            var frame = frames.Array.Take(frames.Count).First(x => x.FrameType == RenderTreeFrameType.Attribute && x.AttributeName == attribute);
            return DispatchEventAsync(frame.AttributeEventHandlerId, null, args);
#pragma warning restore BL0006
        }
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager(string query) => Initialize("https://localhost/", "https://localhost/" + query);
        protected override void NavigateToCore(string uri, bool forceLoad) => Uri = ToAbsoluteUri(uri).ToString();
    }

    private sealed class TestBrowserFile : IBrowserFile
    {
        public string Name => "image.png";
        public DateTimeOffset LastModified => DateTimeOffset.UtcNow;
        public long Size => 4;
        public string ContentType => "image/png";
        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default) => new MemoryStream([1, 2, 3, 4]);
    }
}
