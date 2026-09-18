using Microsoft.EntityFrameworkCore;
using YALA.Data;
using YALA.Data.Entities;

namespace YALA.Services;

public sealed record StoreSummary(Guid Id, string Name, bool IsActive, int OfferCount, int SortOrder);
public sealed record PricePoint(decimal Price, DateTimeOffset RecordedAt);
public sealed record StoreOfferSummary(Guid Id, Guid CatalogItemId, string ItemName, string? ProductName, string? Aisle, bool IsPreferred, decimal? LastPrice, decimal? AveragePrice, IReadOnlyList<PricePoint> PriceHistory);

public sealed class StoreService(IDbContextFactory<ApplicationDbContext> dbContextFactory, HouseholdService householdService, ShoppingListChangeNotifier notifier)
{
    public async Task<IReadOnlyList<StoreSummary>> GetStoresAsync(CancellationToken cancellationToken = default)
    {
        var household = await RequireHouseholdAsync(cancellationToken);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Stores.AsNoTracking().Where(x => x.HouseholdId == household.HouseholdId)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Name)
            .Select(x => new StoreSummary(x.Id, x.Name, x.IsActive, x.Offers.Count(o => o.IsAvailable), x.SortOrder))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<StoreOfferSummary>> GetOffersAsync(Guid storeId, CancellationToken cancellationToken = default)
    {
        var household = await RequireHouseholdAsync(cancellationToken);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.Stores.AnyAsync(x => x.Id == storeId && x.HouseholdId == household.HouseholdId, cancellationToken))
        {
            throw new InvalidOperationException("That store is not in this household.");
        }

        var offers = await db.StoreOffers.AsNoTracking().Where(x => x.HouseholdId == household.HouseholdId && x.StoreId == storeId && x.IsAvailable)
            .Include(x => x.CatalogItem).Include(x => x.ProductVariant).Include(x => x.Prices)
            .OrderBy(x => x.CatalogItem.Name).ThenBy(x => x.ProductVariant == null ? string.Empty : x.ProductVariant.Name)
            .ToListAsync(cancellationToken);
        return offers.Select(x => new StoreOfferSummary(x.Id, x.CatalogItemId, x.CatalogItem.Name, x.ProductVariant?.Name, x.Aisle, x.IsPreferred,
                x.Prices.OrderByDescending(p => p.RecordedAt).Select(p => (decimal?)p.Price).FirstOrDefault(),
                x.Prices.Count == 0 ? null : x.Prices.Average(p => (decimal?)p.Price),
                x.Prices.OrderByDescending(p => p.RecordedAt).Select(p => new PricePoint(p.Price, p.RecordedAt)).ToArray()))
            .ToArray();
    }

    public async Task<IReadOnlyList<(Guid Id, string Name)>> GetCatalogItemsAsync(CancellationToken cancellationToken = default)
    {
        var household = await RequireHouseholdAsync(cancellationToken);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var items = await db.CatalogItems.AsNoTracking().Where(x => x.HouseholdId == household.HouseholdId && !x.IsArchived)
            .OrderBy(x => x.Name).Select(x => new { x.Id, x.Name }).ToListAsync(cancellationToken);
        return items.Select(x => (x.Id, x.Name)).ToArray();
    }

    public async Task<IReadOnlyList<(Guid Id, string Name)>> GetVariantsAsync(Guid catalogItemId, CancellationToken cancellationToken = default)
    {
        var household = await RequireHouseholdAsync(cancellationToken);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var variants = await db.ProductVariants.AsNoTracking().Where(x => x.HouseholdId == household.HouseholdId && x.CatalogItemId == catalogItemId && !x.IsArchived)
            .OrderByDescending(x => x.IsPreferred).ThenBy(x => x.Name).Select(x => new { x.Id, x.Name }).ToListAsync(cancellationToken);
        return variants.Select(x => (x.Id, x.Name)).ToArray();
    }

    public async Task SaveStoreAsync(Guid? id, string name, CancellationToken cancellationToken = default)
    {
        var household = await RequireHouseholdAsync(cancellationToken);
        var cleanName = name.Trim();
        if (cleanName.Length is < 1 or > 80) throw new ArgumentException("Store names must be between 1 and 80 characters.");
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        Store store;
        if (id is Guid storeId)
        {
            store = await db.Stores.SingleOrDefaultAsync(x => x.Id == storeId && x.HouseholdId == household.HouseholdId, cancellationToken)
                ?? throw new InvalidOperationException("That store is not in this household.");
        }
        else
        {
            store = new Store { HouseholdId = household.HouseholdId, SortOrder = await db.Stores.CountAsync(x => x.HouseholdId == household.HouseholdId, cancellationToken) };
            db.Stores.Add(store);
        }

        store.Name = cleanName;
        await db.SaveChangesAsync(cancellationToken);
        notifier.Notify(household.HouseholdId);
    }

    public async Task SetActiveAsync(Guid id, bool isActive, CancellationToken cancellationToken = default)
    {
        var household = await RequireHouseholdAsync(cancellationToken);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var store = await db.Stores.SingleOrDefaultAsync(x => x.Id == id && x.HouseholdId == household.HouseholdId, cancellationToken)
            ?? throw new InvalidOperationException("That store is not in this household.");
        store.IsActive = isActive;
        await db.SaveChangesAsync(cancellationToken);
        notifier.Notify(household.HouseholdId);
    }

    public async Task MoveStoreAsync(Guid id, int direction, CancellationToken cancellationToken = default)
    {
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        var household = await RequireHouseholdAsync(cancellationToken);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var stores = await db.Stores.Where(x => x.HouseholdId == household.HouseholdId)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Name).ToListAsync(cancellationToken);
        var index = stores.FindIndex(x => x.Id == id);
        var otherIndex = index + direction;
        if (index < 0 || otherIndex < 0 || otherIndex >= stores.Count) return;
        (stores[index], stores[otherIndex]) = (stores[otherIndex], stores[index]);
        for (var i = 0; i < stores.Count; i++) stores[i].SortOrder = i;
        await db.SaveChangesAsync(cancellationToken);
        notifier.Notify(household.HouseholdId);
    }

    public async Task SetPreferredOfferAsync(Guid offerId, bool isPreferred, CancellationToken cancellationToken = default)
    {
        var household = await RequireHouseholdAsync(cancellationToken);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var offer = await db.StoreOffers.SingleOrDefaultAsync(x => x.Id == offerId && x.HouseholdId == household.HouseholdId, cancellationToken)
            ?? throw new InvalidOperationException("That store offer is not in this household.");
        if (isPreferred)
        {
            var equivalentOffers = await db.StoreOffers.Where(x => x.HouseholdId == household.HouseholdId
                && x.CatalogItemId == offer.CatalogItemId && x.ProductVariantId == offer.ProductVariantId).ToListAsync(cancellationToken);
            foreach (var equivalent in equivalentOffers) equivalent.IsPreferred = equivalent.Id == offer.Id;
        }
        else
        {
            offer.IsPreferred = false;
        }
        await db.SaveChangesAsync(cancellationToken);
        notifier.Notify(household.HouseholdId);
    }

    public async Task SaveOfferAsync(Guid storeId, Guid catalogItemId, string? aisle, decimal? price, Guid? productVariantId = null, CancellationToken cancellationToken = default)
    {
        var household = await RequireHouseholdAsync(cancellationToken);
        if (price < 0) throw new ArgumentOutOfRangeException(nameof(price), "Price cannot be negative.");
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var store = await db.Stores.SingleOrDefaultAsync(x => x.Id == storeId && x.HouseholdId == household.HouseholdId && x.IsActive, cancellationToken)
            ?? throw new InvalidOperationException("That store is not active in this household.");
        var item = await db.CatalogItems.SingleOrDefaultAsync(x => x.Id == catalogItemId && x.HouseholdId == household.HouseholdId && !x.IsArchived, cancellationToken)
            ?? throw new InvalidOperationException("That item is not in this household.");
        if (productVariantId is Guid variantId && !await db.ProductVariants.AnyAsync(x => x.Id == variantId && x.CatalogItemId == item.Id && x.HouseholdId == household.HouseholdId && !x.IsArchived, cancellationToken))
            throw new InvalidOperationException("That product does not belong to this catalog item.");
        var offer = await db.StoreOffers.SingleOrDefaultAsync(
            x => x.HouseholdId == household.HouseholdId && x.StoreId == storeId && x.CatalogItemId == catalogItemId && x.ProductVariantId == productVariantId,
            cancellationToken);
        if (offer is null)
        {
            offer = new StoreOffer { HouseholdId = household.HouseholdId, StoreId = store.Id, CatalogItemId = item.Id, ProductVariantId = productVariantId };
            db.StoreOffers.Add(offer);
        }

        offer.Aisle = string.IsNullOrWhiteSpace(aisle) ? null : aisle.Trim();
        offer.IsAvailable = true;
        offer.UpdatedAt = DateTimeOffset.UtcNow;
        if (price is decimal recordedPrice)
        {
            db.PriceHistory.Add(new PriceHistory { HouseholdId = household.HouseholdId, StoreOfferId = offer.Id, Price = recordedPrice, Source = PriceSource.Manual });
        }

        await db.SaveChangesAsync(cancellationToken);
        notifier.Notify(household.HouseholdId);
    }

    private async Task<CurrentHouseholdContext> RequireHouseholdAsync(CancellationToken cancellationToken) =>
        await householdService.GetCurrentAsync(cancellationToken)
        ?? throw new InvalidOperationException("Choose a household first.");
}
