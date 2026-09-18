using Microsoft.EntityFrameworkCore;
using YALA.Data;
using YALA.Data.Entities;

namespace YALA.Services;

public sealed record PriceSummary(decimal? Last, decimal? Average);

public sealed class ShoppingListRow
{
    public Guid Id { get; init; }
    public Guid CatalogItemId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? ImagePath { get; init; }
    public decimal Quantity { get; init; }
    public bool IsChecked { get; init; }
    public bool IsFavorite { get; init; }
    public Guid? AssignedStoreId { get; init; }
    public string? AssignedStoreName { get; init; }
    public string? CategoryName { get; init; }
    public IReadOnlyList<StorePrice> StorePrices { get; init; } = [];
}

public sealed record StorePrice(Guid StoreId, string StoreName, decimal? Price, bool IsAssigned, bool IsPreferred);
public sealed record QuickAddChoice(Guid Id, string Name, bool IsFavorite, DateTimeOffset? LastPurchased, int PurchaseCount);

public sealed class ShoppingListService(
    IDbContextFactory<ApplicationDbContext> dbContextFactory,
    HouseholdService householdService,
    ShoppingListChangeNotifier notifier)
{
    public async Task<IReadOnlyList<ShoppingListRow>> GetRowsAsync(CancellationToken cancellationToken = default)
    {
        var household = await RequireHouseholdAsync(cancellationToken);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.ShoppingListItems
            .AsNoTracking()
            .Where(x => x.HouseholdId == household.HouseholdId)
            .Include(x => x.CatalogItem).ThenInclude(x => x.Category)
            .Include(x => x.AssignedStore)
            .OrderBy(x => x.IsChecked)
            .ThenBy(x => x.CatalogItem.Category == null ? int.MaxValue : x.CatalogItem.Category.SortOrder)
            .ThenBy(x => x.CatalogItem.Name)
            .ToListAsync(cancellationToken);

        var itemIds = rows.Select(x => x.CatalogItemId).ToList();
        var offers = await db.StoreOffers.AsNoTracking()
            .Where(x => x.HouseholdId == household.HouseholdId && x.IsAvailable && itemIds.Contains(x.CatalogItemId) && x.Store.IsActive)
            .Include(x => x.Store)
            .Include(x => x.Prices)
            .ToListAsync(cancellationToken);

        return rows.Select(row => new ShoppingListRow
        {
            Id = row.Id,
            CatalogItemId = row.CatalogItemId,
            Name = row.CatalogItem.Name,
            Description = row.CatalogItem.Description,
            ImagePath = row.CatalogItem.ImagePath,
            Quantity = row.Quantity,
            IsChecked = row.IsChecked,
            IsFavorite = row.CatalogItem.IsFavorite,
            AssignedStoreId = row.AssignedStoreId,
            AssignedStoreName = row.AssignedStore?.Name,
            CategoryName = row.CatalogItem.Category?.Name,
            StorePrices = offers.Where(x => x.CatalogItemId == row.CatalogItemId)
                .Select(x => new StorePrice(x.StoreId, x.Store.Name, x.Prices.OrderByDescending(p => p.RecordedAt).Select(p => (decimal?)p.Price).FirstOrDefault(), row.AssignedStoreId == x.StoreId, x.IsPreferred))
                .OrderByDescending(x => x.IsPreferred)
                .ThenBy(x => x.StoreName)
                .ToArray()
        }).ToArray();
    }

    public async Task<IReadOnlyList<QuickAddChoice>> GetQuickAddChoicesAsync(string? query, CancellationToken cancellationToken = default)
    {
        var household = await RequireHouseholdAsync(cancellationToken);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var trimmed = query?.Trim();

        var catalog = db.CatalogItems.AsNoTracking()
            .Where(x => x.HouseholdId == household.HouseholdId && !x.IsArchived);
        if (!string.IsNullOrWhiteSpace(trimmed))
        {
            var term = trimmed.ToLower();
            catalog = catalog.Where(x => x.Name.ToLower().Contains(term)
                || (x.Description != null && x.Description.ToLower().Contains(term))
                || x.Aliases.Any(a => a.Alias.ToLower().Contains(term))
                || x.Variants.Any(v => v.Name.ToLower().Contains(term)
                    || (v.Brand != null && v.Brand.ToLower().Contains(term))
                    || (v.Size != null && v.Size.ToLower().Contains(term)))
                || x.Variants.Any(v => v.Barcodes.Any(b => b.Barcode == trimmed)));
        }

        var matches = await catalog
            .Select(x => new
            {
                x.Id,
                x.Name,
                x.IsFavorite,
                MatchRank = string.IsNullOrWhiteSpace(trimmed) ? 0
                    : x.Variants.Any(v => v.Barcodes.Any(b => b.Barcode == trimmed)) ? 0
                    : x.Name.ToLower() == trimmed.ToLower() ? 1
                    : x.Name.ToLower().StartsWith(trimmed.ToLower()) ? 2
                    : x.Aliases.Any(a => a.Alias.ToLower() == trimmed.ToLower()) ? 3
                    : x.Aliases.Any(a => a.Alias.ToLower().StartsWith(trimmed.ToLower())) ? 4
                    : x.Name.ToLower().Contains(trimmed.ToLower()) ? 5
                    : x.Variants.Any(v => v.Name.ToLower().Contains(trimmed.ToLower()) || (v.Brand != null && v.Brand.ToLower().Contains(trimmed.ToLower()))) ? 6
                    : 7,
                LastPurchased = db.PurchaseHistory.Where(p => p.HouseholdId == household.HouseholdId && p.CatalogItemId == x.Id)
                    .OrderByDescending(p => p.PurchasedAt).Select(p => (DateTimeOffset?)p.PurchasedAt).FirstOrDefault(),
                PurchaseCount = db.PurchaseHistory.Count(p => p.HouseholdId == household.HouseholdId && p.CatalogItemId == x.Id)
            })
            .OrderBy(x => x.MatchRank)
            .ThenByDescending(x => x.IsFavorite)
            .ThenByDescending(x => x.LastPurchased)
            .ThenByDescending(x => x.PurchaseCount)
            .ThenBy(x => x.Name)
            .Take(12)
            .ToListAsync(cancellationToken);

        return matches.Select(x => new QuickAddChoice(x.Id, x.Name, x.IsFavorite, x.LastPurchased, x.PurchaseCount)).ToArray();
    }

    public async Task AddAsync(Guid catalogItemId, CancellationToken cancellationToken = default)
    {
        var household = await RequireHouseholdAsync(cancellationToken);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var item = await db.CatalogItems.SingleOrDefaultAsync(
            x => x.Id == catalogItemId && x.HouseholdId == household.HouseholdId && !x.IsArchived,
            cancellationToken) ?? throw new InvalidOperationException("That item is not in this household's catalog.");

        var row = await db.ShoppingListItems.SingleOrDefaultAsync(
            x => x.HouseholdId == household.HouseholdId && x.CatalogItemId == item.Id,
            cancellationToken);
        if (row is null)
        {
            row = new ShoppingListItem
            {
                HouseholdId = household.HouseholdId,
                CatalogItemId = item.Id,
                Quantity = item.DefaultQuantity,
                AddedByUserId = household.UserId
            };
            db.ShoppingListItems.Add(row);
        }
        else
        {
            row.Quantity += item.DefaultQuantity;
            row.IsChecked = false;
            row.CheckedAt = null;
        }

        await db.SaveChangesAsync(cancellationToken);
        notifier.Notify(household.HouseholdId);
    }

    public async Task<Guid> QuickAddAsync(string name, CancellationToken cancellationToken = default)
    {
        var normalized = name.Trim();
        if (normalized.Length is < 1 or > 120)
        {
            throw new ArgumentException("Item names must be between 1 and 120 characters.", nameof(name));
        }

        var household = await RequireHouseholdAsync(cancellationToken);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var term = normalized.ToLower();
        var item = await db.CatalogItems.FirstOrDefaultAsync(
            x => x.HouseholdId == household.HouseholdId && !x.IsArchived
                && (x.Name.ToLower() == term
                    || x.Aliases.Any(a => a.Alias.ToLower() == term)
                    || x.Variants.Any(v => v.Name.ToLower() == term || v.Barcodes.Any(b => b.Barcode == normalized))),
            cancellationToken);
        if (item is null)
        {
            item = new CatalogItem { HouseholdId = household.HouseholdId, Name = normalized };
            db.CatalogItems.Add(item);
            await db.SaveChangesAsync(cancellationToken);
        }

        var row = await db.ShoppingListItems.SingleOrDefaultAsync(
            x => x.HouseholdId == household.HouseholdId && x.CatalogItemId == item.Id,
            cancellationToken);
        if (row is null)
        {
            db.ShoppingListItems.Add(new ShoppingListItem
            {
                HouseholdId = household.HouseholdId,
                CatalogItemId = item.Id,
                Quantity = item.DefaultQuantity,
                AddedByUserId = household.UserId
            });
        }
        else
        {
            row.Quantity += item.DefaultQuantity;
            row.IsChecked = false;
            row.CheckedAt = null;
        }

        await db.SaveChangesAsync(cancellationToken);
        notifier.Notify(household.HouseholdId);
        return item.Id;
    }

    public Task ToggleCheckedAsync(Guid shoppingListItemId, CancellationToken cancellationToken = default) =>
        MutateRowAsync(shoppingListItemId, row =>
        {
            row.IsChecked = !row.IsChecked;
            row.CheckedAt = row.IsChecked ? DateTimeOffset.UtcNow : null;
        }, cancellationToken);

    public Task ChangeQuantityAsync(Guid shoppingListItemId, decimal delta, CancellationToken cancellationToken = default) =>
        MutateRowAsync(shoppingListItemId, row => row.Quantity = Math.Max(0.25m, row.Quantity + delta), cancellationToken);

    public Task RemoveAsync(Guid shoppingListItemId, CancellationToken cancellationToken = default) =>
        MutateRowAsync(shoppingListItemId, row => row.IsChecked = true, cancellationToken, remove: true);

    public async Task AssignStoreAsync(Guid shoppingListItemId, Guid? storeId, CancellationToken cancellationToken = default)
    {
        var household = await RequireHouseholdAsync(cancellationToken);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        if (storeId is Guid id && !await db.Stores.AnyAsync(x => x.Id == id && x.HouseholdId == household.HouseholdId && x.IsActive, cancellationToken))
        {
            throw new InvalidOperationException("That store is not available in this household.");
        }

        var row = await db.ShoppingListItems.SingleOrDefaultAsync(
            x => x.Id == shoppingListItemId && x.HouseholdId == household.HouseholdId,
            cancellationToken) ?? throw new InvalidOperationException("That list item is not in this household.");
        row.AssignedStoreId = storeId;
        await db.SaveChangesAsync(cancellationToken);
        notifier.Notify(household.HouseholdId);
    }

    public async Task<int> ClearCheckedAsync(CancellationToken cancellationToken = default)
    {
        var household = await RequireHouseholdAsync(cancellationToken);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var checkedItems = await db.ShoppingListItems
            .Where(x => x.HouseholdId == household.HouseholdId && x.IsChecked)
            .ToListAsync(cancellationToken);

        foreach (var row in checkedItems)
        {
            db.PurchaseHistory.Add(new PurchaseHistory
            {
                HouseholdId = household.HouseholdId,
                CatalogItemId = row.CatalogItemId,
                StoreId = row.AssignedStoreId,
                Quantity = row.Quantity,
                PurchasedAt = row.CheckedAt ?? DateTimeOffset.UtcNow,
                PurchasedByUserId = household.UserId
            });
        }

        db.ShoppingListItems.RemoveRange(checkedItems);
        await db.SaveChangesAsync(cancellationToken);
        if (checkedItems.Count != 0)
        {
            notifier.Notify(household.HouseholdId);
        }

        return checkedItems.Count;
    }

    private async Task MutateRowAsync(Guid rowId, Action<ShoppingListItem> mutation, CancellationToken cancellationToken, bool remove = false)
    {
        var household = await RequireHouseholdAsync(cancellationToken);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.ShoppingListItems.SingleOrDefaultAsync(
            x => x.Id == rowId && x.HouseholdId == household.HouseholdId,
            cancellationToken) ?? throw new InvalidOperationException("That list item is not in this household.");
        if (remove)
        {
            db.ShoppingListItems.Remove(row);
        }
        else
        {
            mutation(row);
        }

        await db.SaveChangesAsync(cancellationToken);
        notifier.Notify(household.HouseholdId);
    }

    private async Task<CurrentHouseholdContext> RequireHouseholdAsync(CancellationToken cancellationToken) =>
        await householdService.GetCurrentAsync(cancellationToken)
        ?? throw new InvalidOperationException("Choose a household before editing the grocery list.");
}
