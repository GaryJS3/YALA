using YALA.Services;

namespace YALA.Tests;

public sealed class StoreListGroupingTests
{
    [Fact]
    public void AvailableAtMultipleStores_AppearsInEveryStoreGroup()
    {
        var bjsId = Guid.NewGuid();
        var walmartId = Guid.NewGuid();
        var row = new ShoppingListRow
        {
            Id = Guid.NewGuid(),
            CatalogItemId = Guid.NewGuid(),
            Name = "Cat Food",
            StorePrices =
            [
                new StorePrice(bjsId, "BJ's", 20m, false, false),
                new StorePrice(walmartId, "Walmart", 18m, false, true)
            ]
        };

        var groups = ShoppingListService.GroupRowsByStore([row]);

        Assert.Collection(groups,
            group =>
            {
                Assert.Equal("BJ's", group.Name);
                Assert.Equal(bjsId, group.StoreId);
                var entry = Assert.Single(group.Items);
                Assert.Same(row, entry.Item);
                Assert.Equal(bjsId, entry.StoreId);
            },
            group =>
            {
                Assert.Equal("Walmart", group.Name);
                Assert.Equal(walmartId, group.StoreId);
                var entry = Assert.Single(group.Items);
                Assert.Same(row, entry.Item);
                Assert.Equal(walmartId, entry.StoreId);
            });
    }

    [Fact]
    public void AdHocItem_RemainsInItsAssignedStoreOnly()
    {
        var storeId = Guid.NewGuid();
        var row = new ShoppingListRow
        {
            Id = Guid.NewGuid(),
            Name = "Milk",
            AssignedStoreId = storeId,
            AssignedStoreName = "Walmart"
        };

        var group = Assert.Single(ShoppingListService.GroupRowsByStore([row]));

        Assert.Equal("Walmart", group.Name);
        Assert.Equal(storeId, group.StoreId);
        Assert.Equal(storeId, Assert.Single(group.Items).StoreId);
    }

    [Fact]
    public void ExactProductsForStore_IncludeSingleMatchingProductAndExcludeOtherStores()
    {
        var bjsId = Guid.NewGuid();
        var walmartId = Guid.NewGuid();
        var row = new ShoppingListRow
        {
            CatalogItemId = Guid.NewGuid(),
            StorePrices =
            [
                new StorePrice(bjsId, "BJ's", null, false, false),
                new StorePrice(walmartId, "Walmart", null, false, true)
            ],
            ExactProducts =
            [
                new ExactProductSummary("Club size", "Brand", "25 lb", false, ["BJ's"]),
                new ExactProductSummary("Regular size", "Brand", "20 lb", true, ["Walmart"])
            ]
        };

        var bjsProduct = Assert.Single(row.GetExactProductsForStore(bjsId));
        var walmartProduct = Assert.Single(row.GetExactProductsForStore(walmartId));

        Assert.Equal("Club size", bjsProduct.Name);
        Assert.Equal("Regular size", walmartProduct.Name);
        Assert.False(bjsProduct.IsPreferred);
        Assert.True(walmartProduct.IsPreferred);
        Assert.False(row.StoreCarriesPreferredExactProduct("BJ's"));
        Assert.True(row.StoreCarriesPreferredExactProduct("walmart"));
    }
}
