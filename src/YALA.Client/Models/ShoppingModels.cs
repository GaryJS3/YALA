namespace YALA.Client.Models;

public sealed class ShoppingListRow
{
    public Guid Id { get; set; }
    public Guid? CatalogItemId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? ImagePath { get; set; }
    public decimal Quantity { get; set; }
    public bool IsChecked { get; set; }
    public bool IsFavorite { get; set; }
    public Guid? AssignedStoreId { get; set; }
    public string? AssignedStoreName { get; set; }
    public string? CategoryName { get; set; }
    public IReadOnlyList<StorePrice> StorePrices { get; set; } = [];
    public IReadOnlyList<ExactProductSummary> ExactProducts { get; set; } = [];
    public bool IsAdHoc => CatalogItemId is null;

    public IReadOnlyList<ExactProductSummary> GetExactProductsForStore(Guid storeId)
    {
        var storeName = StorePrices.FirstOrDefault(x => x.StoreId == storeId)?.StoreName;
        return storeName is null
            ? []
            : ExactProducts.Where(product => product.StoreNames.Contains(storeName, StringComparer.OrdinalIgnoreCase)).ToArray();
    }

    public bool StoreCarriesPreferredExactProduct(string storeName) =>
        ExactProducts.Any(product => product.IsPreferred && product.StoreNames.Contains(storeName, StringComparer.OrdinalIgnoreCase));
}

public sealed record StorePrice(Guid StoreId, string StoreName, decimal? Price, bool IsAssigned, bool IsPreferred, string? ImagePath = null);
public sealed record ExactProductSummary(string Name, string? Brand, string? Size, bool IsPreferred, IReadOnlyList<string> StoreNames);
public sealed record QuickAddChoice(Guid Id, string Name, bool IsFavorite, DateTimeOffset? LastPurchased, int PurchaseCount);
public sealed record StoreListEntry(ShoppingListRow Item, Guid? StoreId);
public sealed record StoreListGroup(Guid? StoreId, string Name, IReadOnlyList<StoreListEntry> Items);

public static class ShoppingListGrouping
{
    public static IReadOnlyList<StoreListGroup> GroupRowsByStore(IEnumerable<ShoppingListRow> rows)
    {
        var entries = rows.Where(x => !x.IsChecked).SelectMany(row =>
        {
            if (!row.IsAdHoc && row.StorePrices.Count > 0)
            {
                return row.StorePrices.Select(store => new
                {
                    store.StoreId,
                    StoreName = store.StoreName,
                    Entry = new StoreListEntry(row, (Guid?)store.StoreId)
                });
            }

            return
            [
                new
                {
                    StoreId = row.AssignedStoreId ?? Guid.Empty,
                    StoreName = row.AssignedStoreName ?? "Any store",
                    Entry = new StoreListEntry(row, row.AssignedStoreId)
                }
            ];
        });

        return entries
            .GroupBy(x => new { x.StoreId, x.StoreName })
            .OrderBy(x => x.Key.StoreName == "Any store" ? "~~~" : x.Key.StoreName, StringComparer.OrdinalIgnoreCase)
            .Select(x => new StoreListGroup(x.Key.StoreId == Guid.Empty ? null : x.Key.StoreId, x.Key.StoreName, x.Select(entry => entry.Entry).ToArray()))
            .ToArray();
    }

}
