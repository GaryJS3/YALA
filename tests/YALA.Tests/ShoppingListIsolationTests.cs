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
    public async Task StoreDetailsStayInHouseholdAndRemovingAnItemPreservesItsPriceHistory()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var (household, item) = await fixture.SeedSingleHouseholdAsync();
        var service = fixture.CreateStoreService(fixture.CreateHouseholdService("gary"));
        await service.SaveStoreAsync(null, "Aldi");
        var store = Assert.Single(await service.GetStoresAsync());
        Assert.Equal(store, await service.GetStoreAsync(store.Id));
        await using (var imageDb = fixture.CreateDbContext())
        {
            var storedStore = await imageDb.Stores.SingleAsync();
            storedStore.ImagePath = $"households/{storedStore.HouseholdId:D}/stores/aldi.png";
            await imageDb.SaveChangesAsync();
        }
        Assert.Equal($"households/{household.Id:D}/stores/aldi.png", (await service.GetStoreAsync(store.Id))!.ImagePath);

        await service.SaveOfferAsync(store.Id, item.Id, "Dairy", 3.50m);
        var offer = Assert.Single(await service.GetOffersAsync(store.Id));
        await service.RemoveOfferAsync(offer.Id);

        Assert.Empty(await service.GetOffersAsync(store.Id));
        Assert.Equal(0, (await service.GetStoreAsync(store.Id))!.OfferCount);
        await using var db = fixture.CreateDbContext();
        var storedOffer = Assert.Single(await db.StoreOffers.Include(x => x.Prices).ToListAsync());
        Assert.False(storedOffer.IsAvailable);
        Assert.False(storedOffer.IsPreferred);
        Assert.Single(storedOffer.Prices);
        Assert.Null(await service.GetStoreAsync(Guid.NewGuid()));
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
        await using (var imageDb = fixture.CreateDbContext())
        {
            (await imageDb.Stores.SingleAsync()).ImagePath = "households/example/stores/aldi.png";
            await imageDb.SaveChangesAsync();
        }

        await catalogService.SetStoreAvailabilityAsync(item.Id, store.Id, true);
        var availableStore = Assert.Single((await catalogService.GetDetailsAsync(item.Id))!.Stores);
        Assert.True(availableStore.IsAvailable);
        Assert.Equal("households/example/stores/aldi.png", availableStore.ImagePath);

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

    [Fact]
    public async Task ItemAndShoppingListUsePreferredProductImageWhenItemHasNone()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var (_, item) = await fixture.SeedSingleHouseholdAsync();
        await using (var db = fixture.CreateDbContext())
        {
            db.ProductVariants.AddRange(
                new ProductVariant { HouseholdId = item.HouseholdId, CatalogItemId = item.Id, Name = "Other milk", ImagePath = "households/example/variants/other.jpg" },
                new ProductVariant { HouseholdId = item.HouseholdId, CatalogItemId = item.Id, Name = "Preferred milk", ImagePath = "households/example/variants/preferred.jpg", IsPreferred = true });
            await db.SaveChangesAsync();
        }

        var householdService = fixture.CreateHouseholdService("gary");
        var catalogService = fixture.CreateCatalogService(householdService);
        var listService = fixture.CreateShoppingListService(householdService);

        Assert.Equal("households/example/variants/preferred.jpg", (await catalogService.GetDetailsAsync(item.Id))!.ImagePath);
        await listService.AddAsync(item.Id);
        Assert.Equal("households/example/variants/preferred.jpg", Assert.Single(await listService.GetRowsAsync()).ImagePath);

        await using (var db = fixture.CreateDbContext())
        {
            var storedItem = await db.CatalogItems.SingleAsync(x => x.Id == item.Id);
            storedItem.ImagePath = "households/example/items/item.jpg";
            await db.SaveChangesAsync();
        }

        Assert.Equal("households/example/items/item.jpg", (await catalogService.GetDetailsAsync(item.Id))!.ImagePath);
        Assert.Equal("households/example/items/item.jpg", Assert.Single(await listService.GetRowsAsync()).ImagePath);
    }

    [Fact]
    public async Task ExactProductsCanBeAssignedToMultipleStoresAndAvailabilityPreservesOfferData()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var (_, item) = await fixture.SeedSingleHouseholdAsync();
        var householdService = fixture.CreateHouseholdService("gary");
        var catalogService = fixture.CreateCatalogService(householdService);
        var storeService = fixture.CreateStoreService(householdService);
        await storeService.SaveStoreAsync(null, "Aldi");
        await storeService.SaveStoreAsync(null, "Walmart");
        var stores = await storeService.GetStoresAsync();

        var variantId = await catalogService.SaveVariantAsync(item.Id, "Large bag", "Acme", "20 lb", false, "12345678", stores.Select(x => x.Id).ToArray());
        var variant = Assert.Single((await catalogService.GetDetailsAsync(item.Id))!.Variants);
        Assert.Equal(variantId, variant.Id);
        Assert.Equal(stores.Select(x => x.Id).ToHashSet(), variant.StoreIds);

        var aldi = Assert.Single(stores, x => x.Name == "Aldi");
        await using (var db = fixture.CreateDbContext())
        {
            var offer = await db.StoreOffers.SingleAsync(x => x.ProductVariantId == variantId && x.StoreId == aldi.Id);
            db.PriceHistory.Add(new PriceHistory { HouseholdId = offer.HouseholdId, StoreOfferId = offer.Id, Price = 24.99m });
            await db.SaveChangesAsync();
        }

        await catalogService.SetVariantStoreAvailabilityAsync(variantId, aldi.Id, false);
        variant = Assert.Single((await catalogService.GetDetailsAsync(item.Id))!.Variants);
        Assert.DoesNotContain(aldi.Id, variant.StoreIds);
        await using var verify = fixture.CreateDbContext();
        var unavailableOffer = await verify.StoreOffers.Include(x => x.Prices)
            .SingleAsync(x => x.ProductVariantId == variantId && x.StoreId == aldi.Id);
        Assert.False(unavailableOffer.IsAvailable);
        Assert.Single(unavailableOffer.Prices);
    }

    [Fact]
    public async Task ExactProductEditingUpdatesDetailsPreferredStateAndBarcodes()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var (_, item) = await fixture.SeedSingleHouseholdAsync();
        var householdService = fixture.CreateHouseholdService("gary");
        var catalogService = fixture.CreateCatalogService(householdService);

        var firstId = await catalogService.SaveVariantAsync(item.Id, "Small bag", "Acme", "5 lb", true, "12345678");
        var secondId = await catalogService.SaveVariantAsync(item.Id, "Large bag", "Acme", "20 lb", false, "87654321");

        await catalogService.UpdateVariantAsync(firstId, "Family bag", "New Acme", "10 lb", true, ["11112222", "33334444"]);

        var variants = (await catalogService.GetDetailsAsync(item.Id))!.Variants;
        var first = Assert.Single(variants, x => x.Id == firstId);
        var second = Assert.Single(variants, x => x.Id == secondId);
        Assert.Equal("Family bag", first.Name);
        Assert.Equal("New Acme", first.Brand);
        Assert.Equal("10 lb", first.Size);
        Assert.True(first.IsPreferred);
        Assert.Equal(["11112222", "33334444"], first.Barcodes.Select(x => x.Barcode).ToArray());
        Assert.False(second.IsPreferred);

        await Assert.ThrowsAsync<InvalidOperationException>(() => catalogService.UpdateVariantAsync(firstId, first.Name, first.Brand, first.Size, false, ["87654321"]));
    }

    [Fact]
    public async Task ShoppingListShowsMultipleExactProductsWithTheirStores()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var (_, item) = await fixture.SeedSingleHouseholdAsync();
        var householdService = fixture.CreateHouseholdService("gary");
        var catalogService = fixture.CreateCatalogService(householdService);
        var storeService = fixture.CreateStoreService(householdService);
        var listService = fixture.CreateShoppingListService(householdService);
        await storeService.SaveStoreAsync(null, "Aldi");
        await storeService.SaveStoreAsync(null, "Walmart");
        var stores = await storeService.GetStoresAsync();
        var aldi = Assert.Single(stores, x => x.Name == "Aldi");
        var walmart = Assert.Single(stores, x => x.Name == "Walmart");
        await using (var imageDb = fixture.CreateDbContext())
        {
            (await imageDb.Stores.SingleAsync(x => x.Id == walmart.Id)).ImagePath = "households/example/stores/walmart.png";
            await imageDb.SaveChangesAsync();
        }
        await catalogService.SaveVariantAsync(item.Id, "Small bag", "Acme", "5 lb", false, storeIds: [aldi.Id]);
        await catalogService.SaveVariantAsync(item.Id, "Large bag", "Acme", "20 lb", true, storeIds: [walmart.Id]);
        await listService.AddAsync(item.Id);

        var row = Assert.Single(await listService.GetRowsAsync());
        Assert.Equal(2, row.ExactProducts.Count);
        Assert.Equal(["Aldi"], Assert.Single(row.ExactProducts, x => x.Name == "Small bag").StoreNames);
        Assert.Equal(["Walmart"], Assert.Single(row.ExactProducts, x => x.Name == "Large bag").StoreNames);
        Assert.False(Assert.Single(row.ExactProducts, x => x.Name == "Small bag").IsPreferred);
        Assert.True(Assert.Single(row.ExactProducts, x => x.Name == "Large bag").IsPreferred);
        Assert.Equal(2, row.StorePrices.Count);
        Assert.Equal("households/example/stores/walmart.png", Assert.Single(row.StorePrices, x => x.StoreId == walmart.Id).ImagePath);
    }

    [Fact]
    public async Task RemovingExactProductHidesItAndPreservesOfferHistory()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var (_, item) = await fixture.SeedSingleHouseholdAsync();
        var householdService = fixture.CreateHouseholdService("gary");
        var catalogService = fixture.CreateCatalogService(householdService);
        var storeService = fixture.CreateStoreService(householdService);
        var listService = fixture.CreateShoppingListService(householdService);
        await storeService.SaveStoreAsync(null, "Aldi");
        var store = Assert.Single(await storeService.GetStoresAsync());
        var variantId = await catalogService.SaveVariantAsync(item.Id, "Large bag", "Acme", "20 lb", true, "12345678", [store.Id]);

        await using (var db = fixture.CreateDbContext())
        {
            var offer = await db.StoreOffers.SingleAsync(x => x.ProductVariantId == variantId);
            db.PriceHistory.Add(new PriceHistory { HouseholdId = offer.HouseholdId, StoreOfferId = offer.Id, Price = 24.99m });
            await db.SaveChangesAsync();
        }

        await catalogService.RemoveVariantAsync(variantId);

        Assert.Empty((await catalogService.GetDetailsAsync(item.Id))!.Variants);
        Assert.Empty(await storeService.GetVariantsAsync(item.Id));
        Assert.Empty(await catalogService.GetItemsAsync("Large bag"));
        Assert.Empty(await listService.GetQuickAddChoicesAsync("12345678"));
        await using var verify = fixture.CreateDbContext();
        var archivedVariant = await verify.ProductVariants.SingleAsync(x => x.Id == variantId);
        Assert.True(archivedVariant.IsArchived);
        Assert.False(archivedVariant.IsPreferred);
        var unavailableOffer = await verify.StoreOffers.Include(x => x.Prices).SingleAsync(x => x.ProductVariantId == variantId);
        Assert.False(unavailableOffer.IsAvailable);
        Assert.Single(unavailableOffer.Prices);
    }

    [Fact]
    public async Task TypedAdHocItemCanBeAssignedAndPromotedWithoutLosingListState()
    {
        await using var fixture = await TestFixture.CreateAsync();
        await fixture.SeedSingleHouseholdAsync();
        var householdService = fixture.CreateHouseholdService("gary");
        var listService = fixture.CreateShoppingListService(householdService);
        var catalogService = fixture.CreateCatalogService(householdService);
        var storeService = fixture.CreateStoreService(householdService);
        await storeService.SaveStoreAsync(null, "Aldi");
        var store = Assert.Single(await storeService.GetStoresAsync());

        await listService.QuickAddAsync("Birthday candles");
        var row = Assert.Single(await listService.GetRowsAsync());
        Assert.True(row.IsAdHoc);
        Assert.Empty(await catalogService.GetItemsAsync("Birthday candles"));
        await listService.ChangeQuantityAsync(row.Id, 2);
        await listService.AssignStoreAsync(row.Id, store.Id);

        var pending = Assert.Single(await catalogService.GetAdHocItemsAsync());
        Assert.Equal("Birthday candles", pending.Name);
        Assert.Equal(3, pending.Quantity);
        Assert.Equal("Aldi", pending.StoreName);
        var itemId = await catalogService.PromoteAdHocItemAsync(pending.ShoppingListItemId);

        var promotedRow = Assert.Single(await listService.GetRowsAsync());
        Assert.False(promotedRow.IsAdHoc);
        Assert.Equal(itemId, promotedRow.CatalogItemId);
        Assert.Equal(3, promotedRow.Quantity);
        Assert.Equal(store.Id, promotedRow.AssignedStoreId);
        Assert.Empty(await catalogService.GetAdHocItemsAsync());
        Assert.Equal("Birthday candles", (await catalogService.GetDetailsAsync(itemId))!.Name);
    }

    [Fact]
    public async Task AdHocItemCanBeDeletedWithoutDeletingCatalogItems()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var (_, savedItem) = await fixture.SeedSingleHouseholdAsync();
        var householdService = fixture.CreateHouseholdService("gary");
        var listService = fixture.CreateShoppingListService(householdService);
        var catalogService = fixture.CreateCatalogService(householdService);
        await listService.AddAsync(savedItem.Id);
        await listService.QuickAddAsync("One-off item");
        var adHoc = Assert.Single(await catalogService.GetAdHocItemsAsync());

        await catalogService.DeleteAdHocItemAsync(adHoc.ShoppingListItemId);

        Assert.Empty(await catalogService.GetAdHocItemsAsync());
        var remaining = Assert.Single(await listService.GetRowsAsync());
        Assert.Equal(savedItem.Id, remaining.CatalogItemId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalogService.DeleteAdHocItemAsync(remaining.Id));
        Assert.NotNull(await catalogService.GetDetailsAsync(savedItem.Id));
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
