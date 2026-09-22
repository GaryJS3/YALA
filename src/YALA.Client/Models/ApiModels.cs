namespace YALA.Client.Models;

public sealed class SessionSnapshot
{
    public bool IsAuthenticated { get; set; }
    public bool RequiresSetup { get; set; }
    public string? UserName { get; set; }
    public bool IsAdministrator { get; set; }
    public HouseholdSnapshot Households { get; set; } = new();
}
public sealed class ManagedUser { public string Id { get; set; } = ""; public string UserName { get; set; } = ""; public string DisplayName { get; set; } = ""; public bool IsAdministrator { get; set; } public List<string> Households { get; set; } = []; }
public sealed class UserCreateRequest { public string DisplayName { get; set; } = ""; public string UserName { get; set; } = ""; public string Password { get; set; } = ""; public Guid? HouseholdId { get; set; } }

public sealed class HouseholdSnapshot
{
    public List<HouseholdChoice> Items { get; set; } = [];
    public HouseholdChoice? Current { get; set; }
    public List<CategoryChoice> Categories { get; set; } = [];
}

public sealed class HouseholdChoice { public Guid Id { get; set; } public string Name { get; set; } = ""; public bool IsDefault { get; set; } }
public sealed class CategoryChoice { public Guid Id { get; set; } public string Name { get; set; } = ""; public int SortOrder { get; set; } }

public sealed class ListSnapshot
{
    public List<ShoppingListRow> Rows { get; set; } = [];
    public List<StoreSummary> Stores { get; set; } = [];
}

public sealed class ShoppingListRow
{
    public Guid Id { get; set; }
    public Guid? CatalogItemId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? ImagePath { get; set; }
    public decimal Quantity { get; set; }
    public bool IsChecked { get; set; }
    public bool IsFavorite { get; set; }
    public Guid? AssignedStoreId { get; set; }
    public string? AssignedStoreName { get; set; }
    public string? CategoryName { get; set; }
    public List<StorePrice> StorePrices { get; set; } = [];
    public List<ExactProductSummary> ExactProducts { get; set; } = [];
    public bool IsAdHoc => CatalogItemId is null;
}

public sealed class StorePrice { public Guid StoreId { get; set; } public string StoreName { get; set; } = ""; public decimal? Price { get; set; } public bool IsAssigned { get; set; } public bool IsPreferred { get; set; } public string? ImagePath { get; set; } }
public sealed class ExactProductSummary { public string Name { get; set; } = ""; public string? Brand { get; set; } public string? Size { get; set; } public bool IsPreferred { get; set; } public List<string> StoreNames { get; set; } = []; }
public sealed class QuickAddChoice { public Guid Id { get; set; } public string Name { get; set; } = ""; public bool IsFavorite { get; set; } public DateTimeOffset? LastPurchased { get; set; } public int PurchaseCount { get; set; } }

public sealed class CatalogSnapshot { public List<CatalogEntry> Items { get; set; } = []; public List<AdHocCatalogEntry> AdHocItems { get; set; } = []; public List<CategoryChoice> Categories { get; set; } = []; }
public sealed class CatalogEntry { public Guid Id { get; set; } public string Name { get; set; } = ""; public string? Description { get; set; } public string? CategoryName { get; set; } public decimal DefaultQuantity { get; set; } public bool IsFavorite { get; set; } public bool IsArchived { get; set; } public int StoreCount { get; set; } public DateTimeOffset? LastPurchased { get; set; } public List<ExactProductSummary> ExactProducts { get; set; } = []; public List<StorePrice> StorePrices { get; set; } = []; public string? ImagePath { get; set; } }
public sealed class AdHocCatalogEntry { public Guid ShoppingListItemId { get; set; } public string Name { get; set; } = ""; public decimal Quantity { get; set; } public string? StoreName { get; set; } }
public sealed class CatalogDetails { public Guid Id { get; set; } public string Name { get; set; } = ""; public string? Description { get; set; } public Guid? CategoryId { get; set; } public string? CategoryName { get; set; } public decimal DefaultQuantity { get; set; } public bool IsFavorite { get; set; } public bool IsArchived { get; set; } public string? ImagePath { get; set; } public List<AliasEntry> Aliases { get; set; } = []; public List<VariantEntry> Variants { get; set; } = []; public List<ItemStoreAvailability> Stores { get; set; } = []; }
public sealed class AliasEntry { public Guid Id { get; set; } public string Alias { get; set; } = ""; }
public sealed class VariantEntry { public Guid Id { get; set; } public string Name { get; set; } = ""; public string? Brand { get; set; } public string? Size { get; set; } public string? ImagePath { get; set; } public bool IsPreferred { get; set; } public List<BarcodeEntry> Barcodes { get; set; } = []; public List<Guid> StoreIds { get; set; } = []; }
public sealed class BarcodeEntry { public Guid Id { get; set; } public string Barcode { get; set; } = ""; }
public sealed class ItemStoreAvailability { public Guid Id { get; set; } public string Name { get; set; } = ""; public string? ImagePath { get; set; } public bool IsActive { get; set; } public bool IsAvailable { get; set; } }
public sealed class CatalogSaveRequest { public Guid? Id { get; set; } public string Name { get; set; } = ""; public string? Description { get; set; } public Guid? CategoryId { get; set; } public decimal DefaultQuantity { get; set; } = 1; }
public sealed class VariantSaveRequest { public Guid? Id { get; set; } public Guid ItemId { get; set; } public string Name { get; set; } = ""; public string? Brand { get; set; } public string? Size { get; set; } public bool Preferred { get; set; } public string? Barcode { get; set; } public List<string> Barcodes { get; set; } = []; public List<Guid> StoreIds { get; set; } = []; }
public sealed class ProductLookupResult { public string Barcode { get; set; } = ""; public string? Name { get; set; } public string? Brand { get; set; } public string? Size { get; set; } public string? Description { get; set; } public string? ImageUrl { get; set; } public List<string> Sources { get; set; } = []; }

public sealed class StoreSummary { public Guid Id { get; set; } public string Name { get; set; } = ""; public string? ImagePath { get; set; } public bool IsActive { get; set; } public int OfferCount { get; set; } public int SortOrder { get; set; } }
public sealed class StoreDetails { public StoreSummary Store { get; set; } = new(); public List<StoreOfferSummary> Offers { get; set; } = []; public List<CatalogChoice> CatalogItems { get; set; } = []; }
public sealed class CatalogChoice { public Guid Id { get; set; } public string Name { get; set; } = ""; }
public sealed class StoreOfferSummary { public Guid Id { get; set; } public Guid CatalogItemId { get; set; } public string ItemName { get; set; } = ""; public string? ProductName { get; set; } public string? Aisle { get; set; } public bool IsPreferred { get; set; } public decimal? LastPrice { get; set; } public decimal? AveragePrice { get; set; } public List<PricePoint> PriceHistory { get; set; } = []; }
public sealed class PricePoint { public decimal Price { get; set; } public DateTimeOffset RecordedAt { get; set; } }
public sealed class StoreOfferSaveRequest { public Guid StoreId { get; set; } public Guid CatalogItemId { get; set; } public string? Aisle { get; set; } public decimal? Price { get; set; } public Guid? ProductVariantId { get; set; } }
