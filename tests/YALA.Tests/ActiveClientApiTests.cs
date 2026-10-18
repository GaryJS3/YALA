using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using YALA.Client.Models;
using YALA.Client.Services;
using YALA.Data;
using YALA.Data.Entities;

namespace YALA.Tests;

public sealed class ActiveClientApiTests
{
    [Fact]
    public async Task Replay_rejects_a_stale_client_identity_even_in_a_shared_household()
    {
        await using var app = new TestApplication();
        using var http = app.CreateAuthenticatedClient("alice");
        var (household, _) = await app.SeedAsync();
        var state = new ClientState(); state.SetUser("bob");
        var api = new ApiClient(http, state, new BrowserStorage());
        var error = await Assert.ThrowsAsync<ApiException>(() => api.SendQueuedAsync(new QueuedMutation
        {
            UserId = "bob", HouseholdId = household, Uri = "api/list/quick-add", BodyJson = "{\"Name\":\"Wrong user\"}"
        }));
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        state.SetHousehold(new HouseholdChoice { Id = household });
        Assert.Empty((await api.GetListAsync()).Rows);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/catalog")]
    [InlineData("/households")]
    [InlineData("/stores")]
    [InlineData("/users")]
    public async Task Active_routes_render_the_WebAssembly_host(string path)
    {
        await using var app = new TestApplication();
        using var http = app.CreateAuthenticatedClient("alice");
        await app.SeedAsync();
        using var response = await http.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("YALA.Client.Pages.", html);
        Assert.Contains("blazor.web.js", html);
        Assert.Contains("__RequestVerificationToken", html);
    }

    [Fact]
    public async Task Catalog_flags_round_trip_through_the_real_client_and_endpoints()
    {
        await using var app = new TestApplication();
        using var http = app.CreateAuthenticatedClient("alice");
        var (household, item) = await app.SeedAsync();
        var state = new ClientState(); state.SetHousehold(new HouseholdChoice { Id = household });
        var api = new ApiClient(http, state, new BrowserStorage());
        await api.SetCatalogFavoriteAsync(item, true);
        await api.SetCatalogArchivedAsync(item, true);
        var details = await api.GetCatalogDetailsAsync(item);
        Assert.True(details!.IsFavorite);
        Assert.True(details.IsArchived);
        await api.SetCatalogFavoriteAsync(item, false);
        await api.SetCatalogArchivedAsync(item, false);
        details = await api.GetCatalogDetailsAsync(item);
        Assert.False(details!.IsFavorite);
        Assert.False(details.IsArchived);
    }

    [Fact]
    public async Task Household_headers_override_another_tabs_cookie_for_reads_and_writes()
    {
        await using var app = new TestApplication();
        using var http = app.CreateAuthenticatedClient("alice");
        var (first, _) = await app.SeedAsync();
        var (second, _) = await app.SeedHouseholdAsync("alice", "Second home");
        http.DefaultRequestHeaders.Add("Cookie", $"YalaHousehold={first}");
        var state = new ClientState(); state.SetHousehold(new HouseholdChoice { Id = second });
        var api = new ApiClient(http, state, new BrowserStorage());
        await api.QuickAddAsync("Second home groceries");
        Assert.Equal("Second home groceries", Assert.Single((await api.GetListAsync()).Rows).Name);
        state.SetHousehold(new HouseholdChoice { Id = first });
        Assert.Empty((await api.GetListAsync()).Rows);
        state.SetHousehold(new HouseholdChoice { Id = Guid.NewGuid() });
        var error = await Assert.ThrowsAsync<ApiException>(() => api.QuickAddAsync("Must not fall back"));
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        state.SetHousehold(new HouseholdChoice { Id = first });
        Assert.Empty((await api.GetListAsync()).Rows);
    }

    [Fact]
    public async Task Administrator_cannot_delete_a_households_only_member()
    {
        await using var app = new TestApplication();
        using var http = app.CreateAuthenticatedClient("alice");
        await app.SeedAsync();
        await app.SeedHouseholdAsync("bob", "Bobs home");
        var api = new ApiClient(http, new ClientState(), new BrowserStorage());
        var error = await Assert.ThrowsAsync<ApiException>(() => api.DeleteUserAsync("bob"));
        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Contains("only member", error.Message);
        Assert.Contains(await api.GetUsersAsync(), x => x.Id == "bob");
    }

    [Fact]
    public async Task Regular_users_cannot_manage_accounts_and_failed_creation_rolls_back()
    {
        await using var app = new TestApplication();
        using var adminHttp = app.CreateAuthenticatedClient("alice");
        await app.SeedAsync();
        await app.SeedHouseholdAsync("bob", "Bobs home");
        using var regularHttp = app.CreateAuthenticatedClient("bob");
        var regular = new ApiClient(regularHttp, new ClientState(), new BrowserStorage());
        Assert.Equal(HttpStatusCode.Forbidden, (await Assert.ThrowsAsync<ApiException>(() => regular.DeleteUserAsync("alice"))).StatusCode);
        var admin = new ApiClient(adminHttp, new ClientState(), new BrowserStorage());
        await Assert.ThrowsAsync<ApiException>(() => admin.CreateUserAsync(new UserCreateRequest
        {
            UserName = "invalid-household", DisplayName = "Test", Password = "password123", HouseholdId = Guid.NewGuid()
        }));
        Assert.DoesNotContain(await admin.GetUsersAsync(), x => x.UserName == "invalid-household");
    }

    [Fact]
    public async Task Active_household_controls_manage_categories_members_and_restore()
    {
        await using var app = new TestApplication();
        using var http = app.CreateAuthenticatedClient("alice");
        var (household, _) = await app.SeedAsync();
        await app.SeedHouseholdAsync("bob", "Bobs home");
        var state = new ClientState(); state.SetHousehold(new HouseholdChoice { Id = household });
        var api = new ApiClient(http, state, new BrowserStorage());
        await api.AddMemberAsync("bob");
        Assert.Contains((await api.GetHouseholdSettingsAsync()).Members, x => x.UserId == "bob");
        await api.RemoveMemberAsync("bob");
        Assert.DoesNotContain((await api.GetHouseholdSettingsAsync()).Members, x => x.UserId == "bob");
        await api.SaveCategoryAsync(null, "Frozen");
        var category = Assert.Single((await api.GetHouseholdsAsync()).Categories);
        await api.SaveCategoryAsync(category.Id, "Cold food");
        Assert.Equal("Cold food", Assert.Single((await api.GetHouseholdsAsync()).Categories).Name);
        await api.DeleteCategoryAsync(category.Id);
        Assert.Empty((await api.GetHouseholdsAsync()).Categories);
        await api.ArchiveHouseholdAsync();
        Assert.Contains(await api.GetArchivedHouseholdsAsync(), x => x.Id == household);
        await api.RestoreHouseholdAsync(household);
        Assert.Contains((await api.GetHouseholdsAsync()).Items, x => x.Id == household);
    }

    private sealed class TestApplication : WebApplicationFactory<global::Program>
    {
        private readonly string database = Path.Combine(Path.GetTempPath(), $"yala-tests-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
                services.RemoveAll<IDbContextFactory<ApplicationDbContext>>();
                services.AddDbContextFactory<ApplicationDbContext>(options => options.UseSqlite($"Data Source={database};Pooling=False"));
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "Test";
                    options.DefaultChallengeScheme = "Test";
                    options.DefaultForbidScheme = "Test";
                }).AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("Test", _ => { });
                services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
            });
        }

        public HttpClient CreateAuthenticatedClient(string userId)
        {
            var client = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = false });
            client.DefaultRequestHeaders.Add("X-Test-User", userId);
            return client;
        }

        public async Task<(Guid Household, Guid Item)> SeedAsync()
        {
            var result = await SeedHouseholdAsync("alice", "Home");
            await using var scope = Services.CreateAsyncScope();
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var user = await db.Users.SingleAsync(x => x.Id == "alice");
            user.IsAdministrator = true;
            await db.SaveChangesAsync();
            return result;
        }

        public async Task<(Guid Household, Guid Item)> SeedHouseholdAsync(string userId, string name)
        {
            await using var scope = Services.CreateAsyncScope();
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            if (!await db.Users.AnyAsync(x => x.Id == userId))
                db.Users.Add(new ApplicationUser { Id = userId, UserName = userId, NormalizedUserName = userId.ToUpperInvariant(), DisplayName = userId, SecurityStamp = Guid.NewGuid().ToString() });
            var household = new Household { Name = name };
            var item = new CatalogItem { Name = "Milk", HouseholdId = household.Id };
            db.Households.Add(household);
            db.CatalogItems.Add(item);
            db.HouseholdMembers.Add(new HouseholdMember { UserId = userId, HouseholdId = household.Id });
            await db.SaveChangesAsync();
            return (household.Id, item.Id);
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            foreach (var suffix in new[] { "", "-shm", "-wal" })
                if (File.Exists(database + suffix)) File.Delete(database + suffix);
        }
    }

    private sealed class TestAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-User", out var user)) return Task.FromResult(AuthenticateResult.NoResult());
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.ToString()), new Claim(ClaimTypes.Name, user.ToString())], Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
