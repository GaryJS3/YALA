namespace YALA.Data.Entities;

public sealed class Category
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public List<CatalogItem> Items { get; set; } = [];
}

public sealed class CatalogItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Guid? CategoryId { get; set; }
    public string? ImagePath { get; set; }
    public decimal DefaultQuantity { get; set; } = 1;
    public bool IsFavorite { get; set; }
    public bool IsArchived { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Household Household { get; set; } = null!;
    public Category? Category { get; set; }
    public List<ItemAlias> Aliases { get; set; } = [];
    public List<ProductVariant> Variants { get; set; } = [];
    public List<StoreOffer> StoreOffers { get; set; } = [];
}

public sealed class ItemAlias
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }
    public Guid CatalogItemId { get; set; }
    public string Alias { get; set; } = string.Empty;
    public CatalogItem CatalogItem { get; set; } = null!;
}

public sealed class ProductVariant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }
    public Guid CatalogItemId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Brand { get; set; }
    public string? Size { get; set; }
    public string? ImagePath { get; set; }
    public string? Notes { get; set; }
    public bool IsPreferred { get; set; }
    public bool IsArchived { get; set; }
    public CatalogItem CatalogItem { get; set; } = null!;
    public List<ProductBarcode> Barcodes { get; set; } = [];
}

public sealed class ProductBarcode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }
    public Guid ProductVariantId { get; set; }
    public string Barcode { get; set; } = string.Empty;
    public ProductVariant ProductVariant { get; set; } = null!;
}

public sealed class Store
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public List<StoreOffer> Offers { get; set; } = [];
}

public sealed class StoreOffer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }
    public Guid CatalogItemId { get; set; }
    public Guid StoreId { get; set; }
    public Guid? ProductVariantId { get; set; }
    public string? Aisle { get; set; }
    public string? Notes { get; set; }
    public bool IsAvailable { get; set; } = true;
    public bool IsPreferred { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public CatalogItem CatalogItem { get; set; } = null!;
    public Store Store { get; set; } = null!;
    public ProductVariant? ProductVariant { get; set; }
    public List<PriceHistory> Prices { get; set; } = [];
}

public enum PriceSource
{
    Manual,
    Purchase,
    Import
}

public sealed class PriceHistory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }
    public Guid StoreOfferId { get; set; }
    public decimal Price { get; set; }
    public DateTimeOffset RecordedAt { get; set; } = DateTimeOffset.UtcNow;
    public PriceSource Source { get; set; } = PriceSource.Manual;
    public StoreOffer StoreOffer { get; set; } = null!;
}

public sealed class ShoppingListItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }
    public Guid? CatalogItemId { get; set; }
    public string? CustomName { get; set; }
    public decimal Quantity { get; set; } = 1;
    public Guid? AssignedStoreId { get; set; }
    public string? Notes { get; set; }
    public bool IsChecked { get; set; }
    public string? AddedByUserId { get; set; }
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CheckedAt { get; set; }
    public CatalogItem? CatalogItem { get; set; }
    public Store? AssignedStore { get; set; }
}

public sealed class PurchaseHistory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }
    public Guid CatalogItemId { get; set; }
    public Guid? ProductVariantId { get; set; }
    public Guid? StoreId { get; set; }
    public decimal Quantity { get; set; } = 1;
    public DateTimeOffset PurchasedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? PurchasedByUserId { get; set; }
    public decimal? Price { get; set; }
    public CatalogItem CatalogItem { get; set; } = null!;
    public ProductVariant? ProductVariant { get; set; }
    public Store? Store { get; set; }
}
