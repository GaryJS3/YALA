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
    public async Task Concurrent_replay_applies_a_list_operation_once_and_returns_the_original_result()
    {
        await using var app = new TestApplication();
        using var http = app.CreateAuthenticatedClient("alice");
        var (household, _) = await app.SeedAsync();
        var operationId = Guid.NewGuid();
        Task<HttpResponseMessage> SendAsync()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/list/quick-add")
            {
                Content = JsonContent.Create(new QuickAddCommand("Bread"))
            };
            request.Headers.Add("X-Yala-Household", household.ToString());
            request.Headers.Add("X-Yala-User", "alice");
            request.Headers.Add("X-Yala-Operation", operationId.ToString());
            return http.SendAsync(request);
        }

        var responses = await Task.WhenAll(SendAsync(), SendAsync());
        using var first = responses[0];
        using var second = responses[1];
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var id = await first.Content.ReadFromJsonAsync<Guid>();
        Assert.Equal(id, await second.Content.ReadFromJsonAsync<Guid>());
        var list = await http.GetFromJsonAsync<ListSnapshot>("/api/list");
        var row = Assert.Single(list!.Rows);
        Assert.Equal(id, row.Id);
        Assert.Equal(1m, row.Quantity);
    }

    [Fact]
    public async Task Clear_checked_keeps_rows_checked_after_the_original_snapshot()
    {
        await using var app = new TestApplication();
        using var http = app.CreateAuthenticatedClient("alice");
        var (household, _) = await app.SeedAsync();
        var api = new ApiClient(http, new ClientState(), new BrowserStorage());
        await api.GetSessionAsync();
        var selected = (await api.QuickAddAsync("First"))!.Value;
        var rechecked = (await api.QuickAddAsync("Second"))!.Value;
        var addedLater = (await api.QuickAddAsync("Third"))!.Value;
        await api.ToggleAsync(selected);
        await api.ToggleAsync(rechecked);
        var checkedRows = (await api.GetListAsync()).Rows.Where(x => x.IsChecked)
            .Select(x => new CheckedListRow(x.Id, x.CheckedAt)).ToArray();
        await api.ToggleAsync(rechecked);
        await api.ToggleAsync(rechecked);
        await api.ToggleAsync(addedLater);
        var queued = new QueuedMutation
        {
            HouseholdId = household, UserId = "alice", Uri = "api/list/clear-checked",
            BodyJson = System.Text.Json.JsonSerializer.Serialize(new ClearCheckedCommand(checkedRows))
        };
        await api.SendQueuedAsync(queued);
        await api.SendQueuedAsync(queued);
        var rows = (await api.GetListAsync()).Rows;
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.True(row.IsChecked));
        Assert.DoesNotContain(rows, row => row.Id == selected);
    }

    [Fact]
    public async Task Product_edits_round_trip_with_multiple_barcodes_and_keep_inactive_offer_prices()
    {
        await using var app = new TestApplication();
        using var http = app.CreateAuthenticatedClient("alice");
        var (_, item) = await app.SeedAsync();
        var api = new ApiClient(http, new ClientState(), new BrowserStorage());
        await api.SaveStoreAsync(null, "First store");
        await api.SaveStoreAsync(null, "Second store");
        var stores = await api.GetStoresAsync();
        var firstStore = stores[0].Id;
        var secondStore = stores[1].Id;
        var variantId = (await api.SaveVariantAsync(new VariantSaveRequest
        {
            ItemId = item, Name = "Whole milk", Barcodes = ["11112222", "33334444"], StoreIds = [firstStore]
        }))!.Value;
        await api.SaveOfferAsync(new StoreOfferSaveRequest { StoreId = firstStore, CatalogItemId = item, ProductVariantId = variantId, Price = 3.5m });
        await api.SaveVariantAsync(new VariantSaveRequest
        {
            Id = variantId, ItemId = item, Name = "Gallon milk", Preferred = true, Barcodes = ["33334444", "55556666"], StoreIds = [secondStore]
        });
        var variant = Assert.Single((await api.GetCatalogDetailsAsync(item))!.Variants);
        Assert.Equal("Gallon milk", variant.Name);
        Assert.True(variant.IsPreferred);
        Assert.Equal(new[] { "33334444", "55556666" }, variant.Barcodes.Select(x => x.Barcode).Order());
        Assert.Equal(secondStore, Assert.Single(variant.StoreIds));
        Assert.Empty((await api.GetStoreDetailsAsync(firstStore))!.Offers);
        await api.SetVariantStoreAvailabilityAsync(variantId, firstStore, true);
        Assert.Equal(3.5m, Assert.Single((await api.GetStoreDetailsAsync(firstStore))!.Offers).LastPrice);
        Assert.Null(await api.GetCatalogDetailsAsync(Guid.NewGuid()));
        Assert.Null(await api.GetStoreDetailsAsync(Guid.NewGuid()));
    }

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

    [Theory]
    [InlineData("/api/households/create", "{\"Name\":null}")]
    [InlineData("/api/households/rename", "{\"Name\":null}")]
    [InlineData("/api/households/categories", "{\"Name\":null}")]
    [InlineData("/api/households/members", "{\"UserName\":null}")]
    [InlineData("/api/catalog/aliases", "{\"ItemId\":\"00000000-0000-0000-0000-000000000000\",\"Alias\":null}")]
    [InlineData("/api/users", "{\"DisplayName\":null,\"UserName\":null,\"Password\":null}")]
    [InlineData("/api/users/reset-password", "{\"UserId\":\"alice\",\"Password\":null}")]
    public async Task Null_required_values_return_bad_request(string path, string json)
    {
        await using var app = new TestApplication();
        using var http = app.CreateAuthenticatedClient("alice");
        await app.SeedAsync();
        using var response = await http.PostAsync(path, new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Duplicate_category_returns_a_useful_validation_error()
    {
        await using var app = new TestApplication();
        using var http = app.CreateAuthenticatedClient("alice");
        await app.SeedAsync();
        var api = new ApiClient(http, new ClientState(), new BrowserStorage());
        await api.SaveCategoryAsync(null, "Frozen");
        var error = await Assert.ThrowsAsync<ApiException>(() => api.SaveCategoryAsync(null, "frozen"));
        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Equal("A category with that name already exists in this household.", error.Message);
        Assert.Single((await api.GetHouseholdsAsync()).Categories);
    }

    [Fact]
    public async Task Quantity_overflow_does_not_change_the_existing_list()
    {
        await using var app = new TestApplication();
        using var http = app.CreateAuthenticatedClient("alice");
        var (_, item) = await app.SeedAsync();
        var api = new ApiClient(http, new ClientState(), new BrowserStorage());
        await api.SaveCatalogItemAsync(new CatalogSaveRequest { Id = item, Name = "Milk", DefaultQuantity = decimal.MaxValue });
        await api.AddItemAsync(item);
        Assert.Equal(HttpStatusCode.BadRequest, (await Assert.ThrowsAsync<ApiException>(() => api.AddItemAsync(item))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Assert.ThrowsAsync<ApiException>(() => api.QuickAddAsync("Milk"))).StatusCode);
        Assert.Equal(decimal.MaxValue, Assert.Single((await api.GetListAsync()).Rows).Quantity);
        Assert.Equal(HttpStatusCode.BadRequest, (await Assert.ThrowsAsync<ApiException>(() => api.SaveCatalogItemAsync(
            new CatalogSaveRequest { Id = item, Name = "Milk", DefaultQuantity = 0.1m }))).StatusCode);
    }

    [Fact]
    public async Task Concurrent_generic_offer_creation_does_not_create_duplicate_offers()
    {
        await using var app = new TestApplication();
        using var http = app.CreateAuthenticatedClient("alice");
        var (_, item) = await app.SeedAsync();
        var api = new ApiClient(http, new ClientState(), new BrowserStorage());
        await api.SaveStoreAsync(null, "Market");
        var store = Assert.Single(await api.GetStoresAsync()).Id;
        var offer = new StoreOfferSaveRequest { StoreId = store, CatalogItemId = item, Price = 2m };
        await Task.WhenAll(api.SaveOfferAsync(offer), api.SaveOfferAsync(offer));
        Assert.Single((await api.GetStoreDetailsAsync(store))!.Offers);
        await api.SetStoreActiveAsync(store, false);
        Assert.Equal(0, Assert.Single((await api.GetCatalogAsync(null, false)).Items).StoreCount);
    }

    [Fact]
    public async Task Membership_lookup_ignores_case_and_concurrent_removal_keeps_one_member()
    {
        await using var app = new TestApplication();
        using var http = app.CreateAuthenticatedClient("alice");
        var (home, _) = await app.SeedAsync();
        await app.SeedHouseholdAsync("bob", "Bobs home");
        var api = new ApiClient(http, new ClientState(), new BrowserStorage());
        await api.AddMemberAsync("BOB");
        using var bob = app.CreateAuthenticatedClient("bob");
        http.DefaultRequestHeaders.Add("X-Yala-Household", home.ToString());
        bob.DefaultRequestHeaders.Add("X-Yala-Household", home.ToString());
        var results = await Task.WhenAll(http.DeleteAsync("/api/households/members/alice"), bob.DeleteAsync("/api/households/members/bob"));
        Assert.Single(results, x => x.IsSuccessStatusCode);
        foreach (var response in results) response.Dispose();
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(1, await db.HouseholdMembers.CountAsync(x => x.HouseholdId == home));
    }

    [Fact]
    public async Task Self_service_deletion_keeps_the_last_admin_and_sole_household_members()
    {
        await using var app = new TestApplication();
        await app.SeedAsync();
        await app.SeedHouseholdAsync("bob", "Bobs home");
        await using var scope = app.Services.CreateAsyncScope();
        var deletion = scope.ServiceProvider.GetRequiredService<YALA.Services.AccountDeletionService>();
        Assert.Contains("last administrator", (await Assert.ThrowsAsync<InvalidOperationException>(() => deletion.DeleteAsync("alice"))).Message);
        Assert.Contains("only member", (await Assert.ThrowsAsync<InvalidOperationException>(() => deletion.DeleteAsync("bob"))).Message);
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var bobsHome = await db.HouseholdMembers.Where(x => x.UserId == "bob").Select(x => x.HouseholdId).SingleAsync();
        db.HouseholdMembers.Add(new HouseholdMember { HouseholdId = bobsHome, UserId = "alice" });
        await db.SaveChangesAsync();
        await deletion.DeleteAsync("bob");
        Assert.False(await db.Users.AnyAsync(x => x.Id == "bob"));
        Assert.True(await db.HouseholdMembers.AnyAsync(x => x.HouseholdId == bobsHome && x.UserId == "alice"));
    }

    [Fact]
    public async Task Session_rejects_deleted_users_and_private_responses_are_not_cacheable()
    {
        await using var app = new TestApplication();
        using var http = app.CreateAuthenticatedClient("alice");
        await app.SeedAsync();
        using var session = await http.GetAsync("/api/session");
        Assert.True(session.Headers.CacheControl?.NoStore);
        using var deleted = app.CreateAuthenticatedClient("deleted-user");
        using var response = await deleted.GetAsync("/api/session");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("alice", "last administrator")]
    [InlineData("bob", "only member")]
    public async Task Account_settings_form_cannot_bypass_deletion_guards(string userId, string expected)
    {
        await using var app = new TestApplication();
        await app.SeedAsync();
        await app.SeedHouseholdAsync("bob", "Bobs home");
        using var browser = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        browser.DefaultRequestHeaders.Add("X-Test-User", userId);
        var html = await browser.GetStringAsync("/Account/Manage/DeletePersonalData");
        var token = System.Text.RegularExpressions.Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(token);
        using var response = await browser.PostAsync("/Account/Manage/DeletePersonalData", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "delete-user", ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token), ["Input.Password"] = ""
        }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(expected, await response.Content.ReadAsStringAsync());
        await using var scope = app.Services.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Users.AnyAsync(x => x.Id == userId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Login_honors_lockout_and_two_factor_authentication(bool twoFactor)
    {
        await using var app = new TestApplication();
        await app.SeedAsync();
        await using var scope = app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>>();
        var user = (await users.FindByIdAsync("alice"))!;
        Assert.True((await users.AddPasswordAsync(user, "password123")).Succeeded);
        Assert.True((await users.SetLockoutEnabledAsync(user, true)).Succeeded);
        if (twoFactor)
        {
            Assert.True((await users.ResetAuthenticatorKeyAsync(user)).Succeeded);
            Assert.True((await users.SetTwoFactorEnabledAsync(user, true)).Succeeded);
        }
        using var browser = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        for (var attempt = 0; attempt < (twoFactor ? 1 : 5); attempt++)
        {
            var html = await browser.GetStringAsync("/Account/Login?ReturnUrl=%2Fcatalog");
            var token = System.Text.RegularExpressions.Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
            Assert.NotEmpty(token);
            using var response = await browser.PostAsync("/Account/Login?ReturnUrl=%2Fcatalog", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["_handler"] = "login", ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token),
                ["Input.UserName"] = "alice", ["Input.Password"] = twoFactor ? "password123" : "incorrect123", ["Input.RememberMe"] = "true"
            }));
            if (twoFactor)
            {
                Assert.Equal(HttpStatusCode.Found, response.StatusCode);
                Assert.Contains("Account/LoginWith2fa", response.Headers.Location!.ToString());
                Assert.Contains("RememberMe=True", response.Headers.Location.ToString(), StringComparison.OrdinalIgnoreCase);
            }
            else Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        if (!twoFactor)
        {
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Entry(user).ReloadAsync();
            Assert.True(await users.IsLockedOutAsync(user));
        }
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
