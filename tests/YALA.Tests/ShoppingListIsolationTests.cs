using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using YALA.Data;
using YALA.Data.Entities;
using YALA.Services;

namespace YALA.Tests;

public sealed class ShoppingListIsolationTests
{
    [Fact]
    public async Task SearchAndAddStayInsideTheSelectedHousehold()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var (firstHouse, secondHouse, firstItem, secondItem) = await fixture.SeedTwoHouseholdsAsync();
        var householdService = fixture.CreateHouseholdService("gary");
        var service = fixture.CreateShoppingListService(householdService);
        var catalogService = fixture.CreateCatalogService(householdService);

        Assert.Empty(await service.GetQuickAddChoicesAsync("222222"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddAsync(secondItem.Id));
        foreach (var term in new[] { "kitty food", "purina", "indoor advantage", "111111" })
        {
            var match = Assert.Single(await service.GetQuickAddChoicesAsync(term));
            Assert.Equal(firstItem.Id, match.Id);
            Assert.Equal(firstItem.Id, Assert.Single(await catalogService.GetItemsAsync(term)).Id);
        }
        Assert.Equal(firstItem.Id, await service.QuickAddAsync("Kitty Food"));
        Assert.Single(await service.GetRowsAsync());

        Assert.True(await householdService.SwitchAsync(secondHouse.Id));
        var parentHouseholdList = await service.GetQuickAddChoicesAsync("222222");
        Assert.Single(parentHouseholdList);
        Assert.Equal("Cat Food", parentHouseholdList[0].Name);
        Assert.Equal(secondItem.Id, parentHouseholdList[0].Id);
        Assert.NotEqual(firstItem.Id, parentHouseholdList[0].Id);
    }

    [Fact]
    public async Task AddingAgainIncreasesQuantityAndClearingCheckedCreatesPurchaseHistory()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var (household, item) = await fixture.SeedSingleHouseholdAsync();
        var service = fixture.CreateShoppingListService(fixture.CreateHouseholdService("gary"));

        await service.AddAsync(item.Id);
        await service.AddAsync(item.Id);
        var row = Assert.Single(await service.GetRowsAsync());
        Assert.Equal(2m, row.Quantity);

        await service.ToggleCheckedAsync(row.Id);
        Assert.Equal(1, await service.ClearCheckedAsync());
        Assert.Empty(await service.GetRowsAsync());

        await using var db = fixture.CreateDbContext();
        var purchase = Assert.Single(await db.PurchaseHistory.ToListAsync());
        Assert.Equal(household.Id, purchase.HouseholdId);
        Assert.Equal(item.Id, purchase.CatalogItemId);
        Assert.Equal(2m, purchase.Quantity);
        Assert.Equal("gary", purchase.PurchasedByUserId);
        Assert.Contains(await service.GetQuickAddChoicesAsync(null), x => x.Id == item.Id && x.LastPurchased is not null);
    }

    [Fact]
    public async Task DatabaseRejectsAListRowPointingAcrossHouseholds()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var (firstHouse, secondHouse, _, secondItem) = await fixture.SeedTwoHouseholdsAsync();
        await using var db = fixture.CreateDbContext();
        db.ShoppingListItems.Add(new ShoppingListItem { HouseholdId = firstHouse.Id, CatalogItemId = secondItem.Id });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task HouseholdSettingsManageCategoriesAndArchiveWithoutLosingData()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var (firstHouse, secondHouse, firstItem, _) = await fixture.SeedTwoHouseholdsAsync();
        var service = fixture.CreateHouseholdService("gary");

        await service.RenameCurrentAsync("Home");
        await service.SaveCategoryAsync(null, "Everyday");
        var category = Assert.Single(await service.GetCategoriesAsync());
        await service.SaveCategoryAsync(category.Id, "Regulars");

        await using (var db = fixture.CreateDbContext())
        {
            var item = await db.CatalogItems.SingleAsync(x => x.Id == firstItem.Id);
            item.CategoryId = category.Id;
            await db.SaveChangesAsync();
        }

        await service.DeleteCategoryAsync(category.Id);
        await using (var db = fixture.CreateDbContext())
        {
            Assert.Null((await db.CatalogItems.SingleAsync(x => x.Id == firstItem.Id)).CategoryId);
        }

        await service.ArchiveCurrentAsync();
        Assert.Equal(secondHouse.Id, (await service.GetCurrentAsync())?.HouseholdId);
        Assert.Contains((await service.GetArchivedHouseholdsAsync()), x => x.Id == firstHouse.Id && x.Name == "Home");

        Assert.True(await service.RestoreHouseholdAsync(firstHouse.Id));
        Assert.True(await service.SwitchAsync(firstHouse.Id));
        Assert.Equal("Home", (await service.GetCurrentAsync())?.HouseholdName);
    }

    [Fact]
    public async Task StoreOffersKeepAvailabilityAndTripAssignmentSeparateAndTrackPrices()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var (household, item) = await fixture.SeedSingleHouseholdAsync();
        var householdService = fixture.CreateHouseholdService("gary");
        var storesService = fixture.CreateStoreService(householdService);
        var listService = fixture.CreateShoppingListService(householdService);

        await storesService.SaveStoreAsync(null, "Aldi");
        await storesService.SaveStoreAsync(null, "Walmart");
        var stores = await storesService.GetStoresAsync();
        var aldi = Assert.Single(stores, x => x.Name == "Aldi");
        var walmart = Assert.Single(stores, x => x.Name == "Walmart");
        await storesService.SaveOfferAsync(aldi.Id, item.Id, "Dairy", 3.50m);
        await storesService.SaveOfferAsync(aldi.Id, item.Id, "Dairy", 4.00m);
        await storesService.SaveOfferAsync(aldi.Id, item.Id, "Dairy", 4.50m);
        await storesService.SaveOfferAsync(walmart.Id, item.Id, "Dairy", 4.25m);

        var aldiOffer = Assert.Single(await storesService.GetOffersAsync(aldi.Id));
        Assert.Equal(4.50m, aldiOffer.LastPrice);
        Assert.Equal(4.00m, aldiOffer.AveragePrice);
        Assert.Equal(3, aldiOffer.PriceHistory.Count);
        Assert.Single(await storesService.GetOffersAsync(walmart.Id));
        var walmartOffer = Assert.Single(await storesService.GetOffersAsync(walmart.Id));
        await storesService.SetPreferredOfferAsync(walmartOffer.Id, true);

        await listService.AddAsync(item.Id);
        var row = Assert.Single(await listService.GetRowsAsync());
        await listService.AssignStoreAsync(row.Id, walmart.Id);
        row = Assert.Single(await listService.GetRowsAsync());
        Assert.Equal(walmart.Id, row.AssignedStoreId);
        Assert.Equal(walmart.Id, Assert.Single(row.StorePrices, x => x.IsPreferred).StoreId);
        Assert.Single(await storesService.GetOffersAsync(aldi.Id));
        Assert.Single(await storesService.GetOffersAsync(walmart.Id));

        await storesService.MoveStoreAsync(walmart.Id, -1);
        Assert.Equal("Walmart", (await storesService.GetStoresAsync())[0].Name);
        Assert.Equal(household.Id, (await householdService.GetCurrentAsync())?.HouseholdId);
    }

    [Fact]
    public async Task AnotherHouseholdsStoreCannotBeAttachedToCurrentHouseholdsItem()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var (firstHouse, secondHouse, firstItem, _) = await fixture.SeedTwoHouseholdsAsync();
        var householdService = fixture.CreateHouseholdService("gary");
        var storesService = fixture.CreateStoreService(householdService);
        Guid otherStoreId;
        await using (var db = fixture.CreateDbContext())
        {
            var otherStore = new Store { HouseholdId = secondHouse.Id, Name = "Other market" };
            db.Stores.Add(otherStore);
            await db.SaveChangesAsync();
            otherStoreId = otherStore.Id;
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => storesService.SaveOfferAsync(otherStoreId, firstItem.Id, null, null));
        await using var verify = fixture.CreateDbContext();
        Assert.Empty(await verify.StoreOffers.ToListAsync());
    }

    [Fact]
    public async Task ItemPageStoreChecksToggleGenericAvailabilityWithoutLosingOfferData()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var (_, item) = await fixture.SeedSingleHouseholdAsync();
        var householdService = fixture.CreateHouseholdService("gary");
        var catalogService = fixture.CreateCatalogService(householdService);
        var storeService = fixture.CreateStoreService(householdService);
        await storeService.SaveStoreAsync(null, "Aldi");
        var store = Assert.Single(await storeService.GetStoresAsync());

        await catalogService.SetStoreAvailabilityAsync(item.Id, store.Id, true);
        Assert.True(Assert.Single((await catalogService.GetDetailsAsync(item.Id))!.Stores).IsAvailable);

        await catalogService.SetStoreAvailabilityAsync(item.Id, store.Id, false);
        Assert.False(Assert.Single((await catalogService.GetDetailsAsync(item.Id))!.Stores).IsAvailable);
        await using var db = fixture.CreateDbContext();
        Assert.False(Assert.Single(await db.StoreOffers.ToListAsync()).IsAvailable);
    }

    [Fact]
    public async Task ItemPageCannotToggleAnotherHouseholdsStore()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var (firstHouse, secondHouse, firstItem, _) = await fixture.SeedTwoHouseholdsAsync();
        Guid otherStoreId;
        await using (var db = fixture.CreateDbContext())
        {
            var store = new Store { HouseholdId = secondHouse.Id, Name = "Other market" };
            db.Stores.Add(store);
            await db.SaveChangesAsync();
            otherStoreId = store.Id;
        }

        var service = fixture.CreateCatalogService(fixture.CreateHouseholdService("gary"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetStoreAvailabilityAsync(firstItem.Id, otherStoreId, true));
        await using var verify = fixture.CreateDbContext();
        Assert.Empty(await verify.StoreOffers.Where(x => x.HouseholdId == firstHouse.Id).ToListAsync());
    }

    private sealed class TestFixture(SqliteConnection connection, DbContextOptions<ApplicationDbContext> options) : IAsyncDisposable
    {
        public static async Task<TestFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
            var fixture = new TestFixture(connection, options);
            await using var db = fixture.CreateDbContext();
            await db.Database.EnsureCreatedAsync();

            db.Users.Add(new ApplicationUser { Id = "gary", UserName = "gary", NormalizedUserName = "GARY", DisplayName = "Gary" });
            await db.SaveChangesAsync();
            return fixture;
        }

        public ApplicationDbContext CreateDbContext() => new(options);

        public HouseholdService CreateHouseholdService(string userId) =>
            new(new TestAuthenticationStateProvider(userId), new TestDbContextFactory(options));

        public ShoppingListService CreateShoppingListService(HouseholdService householdService)
        {
            return new ShoppingListService(new TestDbContextFactory(options), householdService, new ShoppingListChangeNotifier());
        }

        public StoreService CreateStoreService(HouseholdService householdService)
        {
            return new StoreService(new TestDbContextFactory(options), householdService, new ShoppingListChangeNotifier());
        }

        public CatalogService CreateCatalogService(HouseholdService householdService)
        {
            return new CatalogService(new TestDbContextFactory(options), householdService, new ShoppingListChangeNotifier());
        }

        public async Task<(Household Household, CatalogItem Item)> SeedSingleHouseholdAsync()
        {
            var household = new Household { Name = "GJ's House" };
            var item = new CatalogItem { HouseholdId = household.Id, Name = "Milk" };
            await using var db = CreateDbContext();
            db.Households.Add(household);
            db.HouseholdMembers.Add(new HouseholdMember { HouseholdId = household.Id, UserId = "gary" });
            db.CatalogItems.Add(item);
            await db.SaveChangesAsync();
            var user = await db.Users.SingleAsync(x => x.Id == "gary");
            user.DefaultHouseholdId = household.Id;
            await db.SaveChangesAsync();
            return (household, item);
        }

        public async Task<(Household FirstHouse, Household SecondHouse, CatalogItem FirstItem, CatalogItem SecondItem)> SeedTwoHouseholdsAsync()
        {
            var first = new Household { Name = "GJ's House" };
            var second = new Household { Name = "Parents' House" };
            var firstItem = new CatalogItem { HouseholdId = first.Id, Name = "Cat Food" };
            var secondItem = new CatalogItem { HouseholdId = second.Id, Name = "Cat Food" };
            var firstVariant = new ProductVariant { HouseholdId = first.Id, CatalogItemId = firstItem.Id, Name = "Purina ONE Indoor Advantage Premium Dry Cat Food", Brand = "Purina", Size = "Turkey, 16 lb" };
            var secondVariant = new ProductVariant { HouseholdId = second.Id, CatalogItemId = secondItem.Id, Name = "IAMS ProActive Health", Brand = "IAMS", Size = "16 lb" };

            await using var db = CreateDbContext();
            db.Households.AddRange(first, second);
            db.HouseholdMembers.AddRange(
                new HouseholdMember { HouseholdId = first.Id, UserId = "gary" },
                new HouseholdMember { HouseholdId = second.Id, UserId = "gary" });
            db.CatalogItems.AddRange(firstItem, secondItem);
            db.ProductVariants.AddRange(firstVariant, secondVariant);
            db.ProductBarcodes.AddRange(
                new ProductBarcode { HouseholdId = first.Id, ProductVariantId = firstVariant.Id, Barcode = "111111" },
                new ProductBarcode { HouseholdId = second.Id, ProductVariantId = secondVariant.Id, Barcode = "222222" });
            db.ItemAliases.AddRange(
                new ItemAlias { HouseholdId = first.Id, CatalogItemId = firstItem.Id, Alias = "Kitty Food" },
                new ItemAlias { HouseholdId = first.Id, CatalogItemId = firstItem.Id, Alias = "Dry Cat Food" },
                new ItemAlias { HouseholdId = first.Id, CatalogItemId = firstItem.Id, Alias = "Purina" });
            await db.SaveChangesAsync();
            var user = await db.Users.SingleAsync(x => x.Id == "gary");
            user.DefaultHouseholdId = first.Id;
            await db.SaveChangesAsync();
            return (first, second, firstItem, secondItem);
        }

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }

    private sealed class TestDbContextFactory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => new(options);
        public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ApplicationDbContext(options));
    }

    private sealed class TestAuthenticationStateProvider(string userId) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim(ClaimTypes.Name, userId)
            ], "Test");
            return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
        }
    }
}
