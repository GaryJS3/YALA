using Microsoft.EntityFrameworkCore;
using YALA.Data;
using YALA.Data.Entities;

namespace YALA.Services;

public sealed record CatalogEntry(Guid Id, string Name, string? Description, string? CategoryName, decimal DefaultQuantity, bool IsFavorite, bool IsArchived, int StoreCount, DateTimeOffset? LastPurchased);
public sealed record ProductVariantEntry(Guid Id, string Name, string? Brand, string? Size, string? ImagePath, bool IsPreferred, IReadOnlyList<(Guid Id, string Barcode)> Barcodes);
public sealed record CatalogAliasEntry(Guid Id, string Alias);
public sealed record ItemStoreAvailability(Guid Id, string Name, bool IsActive, bool IsAvailable);
public sealed record CatalogDetails(Guid Id, string Name, string? Description, Guid? CategoryId, string? CategoryName,
    decimal DefaultQuantity, bool IsFavorite, bool IsArchived, string? ImagePath,
    IReadOnlyList<CatalogAliasEntry> Aliases, IReadOnlyList<ProductVariantEntry> Variants,
    IReadOnlyList<ItemStoreAvailability> Stores);

public sealed class CatalogService(IDbContextFactory<ApplicationDbContext> dbContextFactory, HouseholdService householdService, ShoppingListChangeNotifier notifier)
{
    public async Task<IReadOnlyList<CatalogEntry>> GetItemsAsync(string? query = null, bool includeArchived = false, CancellationToken cancellationToken = default)
    {
        var household = await householdService.GetCurrentAsync(cancellationToken)
            ?? throw new InvalidOperationException("Choose a household before viewing the catalog.");
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var items = db.CatalogItems.AsNoTracking().Where(x => x.HouseholdId == household.HouseholdId && (includeArchived || !x.IsArchived));
        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim().ToLower();
            items = items.Where(x => x.Name.ToLower().Contains(term)
                || (x.Description != null && x.Description.ToLower().Contains(term))
                || x.Aliases.Any(a => a.Alias.ToLower().Contains(term))
                || x.Variants.Any(v => v.Name.ToLower().Contains(term)
                    || (v.Brand != null && v.Brand.ToLower().Contains(term))
                    || (v.Size != null && v.Size.ToLower().Contains(term))
                    || v.Barcodes.Any(b => b.Barcode == query.Trim())));
        }

        return await items.OrderBy(x => x.IsArchived).ThenBy(x => x.Category == null ? int.MaxValue : x.Category.SortOrder).ThenBy(x => x.Name)
            .Select(x => new CatalogEntry(x.Id, x.Name, x.Description, x.Category == null ? null : x.Category.Name, x.DefaultQuantity, x.IsFavorite, x.IsArchived,
                x.StoreOffers.Count(o => o.IsAvailable), db.PurchaseHistory.Where(p => p.HouseholdId == household.HouseholdId && p.CatalogItemId == x.Id)
                    .OrderByDescending(p => p.PurchasedAt).Select(p => (DateTimeOffset?)p.PurchasedAt).FirstOrDefault()))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken cancellationToken = default)
    {
        var household = await householdService.GetCurrentAsync(cancellationToken)
            ?? throw new InvalidOperationException("Choose a household before viewing categories.");
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Categories.AsNoTracking().Where(x => x.HouseholdId == household.HouseholdId).OrderBy(x => x.SortOrder).ToListAsync(cancellationToken);
    }

    public async Task<CatalogDetails?> GetDetailsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var household = await householdService.GetCurrentAsync(cancellationToken)
            ?? throw new InvalidOperationException("Choose a household before viewing the catalog.");
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var item = await db.CatalogItems.AsNoTracking().Where(x => x.Id == id && x.HouseholdId == household.HouseholdId)
            .Include(x => x.Aliases).Include(x => x.Variants).ThenInclude(x => x.Barcodes).SingleOrDefaultAsync(cancellationToken);
        if (item is null) return null;
        var categoryName = item.CategoryId is Guid categoryId
            ? await db.Categories.Where(x => x.HouseholdId == household.HouseholdId && x.Id == categoryId).Select(x => x.Name).SingleAsync(cancellationToken)
            : null;
        var stores = await db.Stores.AsNoTracking().Where(x => x.HouseholdId == household.HouseholdId)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Name)
            .Select(x => new ItemStoreAvailability(x.Id, x.Name, x.IsActive,
                db.StoreOffers.Any(o => o.HouseholdId == household.HouseholdId && o.CatalogItemId == item.Id && o.StoreId == x.Id && o.IsAvailable)))
            .ToListAsync(cancellationToken);
        return new CatalogDetails(item.Id, item.Name, item.Description, item.CategoryId, categoryName, item.DefaultQuantity,
            item.IsFavorite, item.IsArchived, item.ImagePath,
            item.Aliases.OrderBy(x => x.Alias).Select(x => new CatalogAliasEntry(x.Id, x.Alias)).ToArray(),
            item.Variants.Where(x => !x.IsArchived).OrderByDescending(x => x.IsPreferred).ThenBy(x => x.Name)
                .Select(x => new ProductVariantEntry(x.Id, x.Name, x.Brand, x.Size, x.ImagePath, x.IsPreferred,
                    x.Barcodes.OrderBy(b => b.Barcode).Select(b => (b.Id, b.Barcode)).ToArray())).ToArray(), stores);
    }

    public async Task AddAliasAsync(Guid itemId, string alias, CancellationToken cancellationToken = default)
    {
        var household = await RequireHouseholdAsync(cancellationToken);
        var cleanAlias = alias.Trim();
        if (cleanAlias.Length is < 1 or > 100) throw new ArgumentException("Alias must be between 1 and 100 characters.");
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.CatalogItems.AnyAsync(x => x.Id == itemId && x.HouseholdId == household.HouseholdId, cancellationToken))
            throw new InvalidOperationException("That item is not in this household.");
        if (await db.ItemAliases.AnyAsync(x => x.HouseholdId == household.HouseholdId && x.CatalogItemId == itemId && x.Alias.ToLower() == cleanAlias.ToLower(), cancellationToken)) return;
        db.ItemAliases.Add(new ItemAlias { HouseholdId = household.HouseholdId, CatalogItemId = itemId, Alias = cleanAlias });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveAliasAsync(Guid aliasId, CancellationToken cancellationToken = default)
    {
        var household = await RequireHouseholdAsync(cancellationToken);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var alias = await db.ItemAliases.SingleOrDefaultAsync(x => x.Id == aliasId && x.HouseholdId == household.HouseholdId, cancellationToken);
        if (alias is null) throw new InvalidOperationException("That alias is not in this household.");
        db.ItemAliases.Remove(alias);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<Guid> SaveVariantAsync(Guid itemId, string name, string? brand, string? size, bool preferred, string? barcode = null, CancellationToken cancellationToken = default)
    {
        var household = await RequireHouseholdAsync(cancellationToken);
        var cleanName = name.Trim();
        if (cleanName.Length is < 1 or > 200) throw new ArgumentException("Product names must be between 1 and 200 characters.");
        var cleanBarcode = string.IsNullOrWhiteSpace(barcode) ? null : new string(barcode.Where(char.IsLetterOrDigit).ToArray());
        if (cleanBarcode is not null && (cleanBarcode.Length is < 4 or > 64)) throw new ArgumentException("Enter a barcode with 4 to 64 letters or numbers.");
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.CatalogItems.AnyAsync(x => x.Id == itemId && x.HouseholdId == household.HouseholdId, cancellationToken))
            throw new InvalidOperationException("That item is not in this household.");
        if (preferred)
        {
            var otherVariants = await db.ProductVariants.Where(x => x.HouseholdId == household.HouseholdId && x.CatalogItemId == itemId).ToListAsync(cancellationToken);
            foreach (var variant in otherVariants) variant.IsPreferred = false;
        }

        if (cleanBarcode is not null && await db.ProductBarcodes.AnyAsync(x => x.HouseholdId == household.HouseholdId && x.Barcode == cleanBarcode, cancellationToken))
            throw new InvalidOperationException("That barcode is already recorded in this household.");

        var product = new ProductVariant
        {
            HouseholdId = household.HouseholdId,
            CatalogItemId = itemId,
            Name = cleanName,
            Brand = string.IsNullOrWhiteSpace(brand) ? null : brand.Trim(),
            Size = string.IsNullOrWhiteSpace(size) ? null : size.Trim(),
            IsPreferred = preferred
        };
        db.ProductVariants.Add(product);
        if (cleanBarcode is not null)
        {
            db.ProductBarcodes.Add(new ProductBarcode { HouseholdId = household.HouseholdId, ProductVariantId = product.Id, Barcode = cleanBarcode });
        }
        await db.SaveChangesAsync(cancellationToken);
        return product.Id;
    }

    public async Task AddBarcodeAsync(Guid variantId, string barcode, CancellationToken cancellationToken = default)
    {
        var household = await RequireHouseholdAsync(cancellationToken);
        var cleanBarcode = new string(barcode.Where(char.IsLetterOrDigit).ToArray());
        if (cleanBarcode.Length is < 4 or > 64) throw new ArgumentException("Enter a barcode with 4 to 64 letters or numbers.");
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.ProductVariants.AnyAsync(x => x.Id == variantId && x.HouseholdId == household.HouseholdId, cancellationToken))
            throw new InvalidOperationException("That product is not in this household.");
        if (await db.ProductBarcodes.AnyAsync(x => x.HouseholdId == household.HouseholdId && x.Barcode == cleanBarcode, cancellationToken))
            throw new InvalidOperationException("That barcode is already recorded in this household.");
        db.ProductBarcodes.Add(new ProductBarcode { HouseholdId = household.HouseholdId, ProductVariantId = variantId, Barcode = cleanBarcode });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveBarcodeAsync(Guid barcodeId, CancellationToken cancellationToken = default)
    {
        var household = await RequireHouseholdAsync(cancellationToken);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var barcode = await db.ProductBarcodes.SingleOrDefaultAsync(x => x.Id == barcodeId && x.HouseholdId == household.HouseholdId, cancellationToken);
        if (barcode is null) throw new InvalidOperationException("That barcode is not in this household.");
        db.ProductBarcodes.Remove(barcode);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<Guid> SaveAsync(Guid? id, string name, string? description, Guid? categoryId, decimal defaultQuantity, CancellationToken cancellationToken = default)
    {
        var household = await householdService.GetCurrentAsync(cancellationToken)
            ?? throw new InvalidOperationException("Choose a household before editing catalog items.");
        var cleanName = name.Trim();
        if (cleanName.Length is < 1 or > 120 || defaultQuantity <= 0)
        {
            throw new ArgumentException("Enter an item name and a quantity greater than zero.");
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        if (categoryId is Guid chosenCategory && !await db.Categories.AnyAsync(x => x.Id == chosenCategory && x.HouseholdId == household.HouseholdId, cancellationToken))
        {
            throw new InvalidOperationException("That category is not in this household.");
        }

        CatalogItem item;
        if (id is Guid itemId)
        {
            item = await db.CatalogItems.SingleOrDefaultAsync(x => x.Id == itemId && x.HouseholdId == household.HouseholdId, cancellationToken)
                ?? throw new InvalidOperationException("That item is not in this household.");
        }
        else
        {
            item = new CatalogItem { HouseholdId = household.HouseholdId };
            db.CatalogItems.Add(item);
        }

        item.Name = cleanName;
        item.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        item.CategoryId = categoryId;
        item.DefaultQuantity = defaultQuantity;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        notifier.Notify(household.HouseholdId);
        return item.Id;
    }

    public async Task SetStoreAvailabilityAsync(Guid itemId, Guid storeId, bool isAvailable, CancellationToken cancellationToken = default)
    {
        var household = await RequireHouseholdAsync(cancellationToken);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.CatalogItems.AnyAsync(x => x.Id == itemId && x.HouseholdId == household.HouseholdId, cancellationToken))
            throw new InvalidOperationException("That item is not in this household.");
        if (!await db.Stores.AnyAsync(x => x.Id == storeId && x.HouseholdId == household.HouseholdId, cancellationToken))
            throw new InvalidOperationException("That store is not in this household.");

        var offers = await db.StoreOffers.Where(x => x.HouseholdId == household.HouseholdId
            && x.CatalogItemId == itemId && x.StoreId == storeId).ToListAsync(cancellationToken);
        var offer = offers.SingleOrDefault(x => x.ProductVariantId == null);
        if (!isAvailable)
        {
            foreach (var existingOffer in offers)
            {
                existingOffer.IsAvailable = false;
                existingOffer.UpdatedAt = DateTimeOffset.UtcNow;
            }
            if (offers.Count == 0) return;
            await db.SaveChangesAsync(cancellationToken);
            notifier.Notify(household.HouseholdId);
            return;
        }
        if (offer is null)
        {
            offer = new StoreOffer { HouseholdId = household.HouseholdId, CatalogItemId = itemId, StoreId = storeId };
            db.StoreOffers.Add(offer);
        }
        offer.IsAvailable = true;
        offer.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        notifier.Notify(household.HouseholdId);
    }

    public async Task SetFavoriteAsync(Guid id, bool favorite, CancellationToken cancellationToken = default)
    {
        var household = await householdService.GetCurrentAsync(cancellationToken)
            ?? throw new InvalidOperationException("Choose a household first.");
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var item = await db.CatalogItems.SingleOrDefaultAsync(x => x.Id == id && x.HouseholdId == household.HouseholdId, cancellationToken)
            ?? throw new InvalidOperationException("That item is not in this household.");
        item.IsFavorite = favorite;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        notifier.Notify(household.HouseholdId);
    }

    public async Task SetArchivedAsync(Guid id, bool archived, CancellationToken cancellationToken = default)
    {
        var household = await householdService.GetCurrentAsync(cancellationToken)
            ?? throw new InvalidOperationException("Choose a household first.");
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var item = await db.CatalogItems.SingleOrDefaultAsync(x => x.Id == id && x.HouseholdId == household.HouseholdId, cancellationToken)
            ?? throw new InvalidOperationException("That item is not in this household.");
        item.IsArchived = archived;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        if (archived)
        {
            db.ShoppingListItems.RemoveRange(db.ShoppingListItems.Where(x => x.HouseholdId == household.HouseholdId && x.CatalogItemId == id));
        }

        await db.SaveChangesAsync(cancellationToken);
        notifier.Notify(household.HouseholdId);
    }

    private async Task<CurrentHouseholdContext> RequireHouseholdAsync(CancellationToken cancellationToken) =>
        await householdService.GetCurrentAsync(cancellationToken)
        ?? throw new InvalidOperationException("Choose a household first.");
}
