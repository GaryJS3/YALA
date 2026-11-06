using System.Text.Json;
using YALA.Client.Models;
using Microsoft.EntityFrameworkCore;
using YALA.Data;
using YALA.Data.Entities;

namespace YALA.Services;

public sealed record PriceSummary(decimal? Last, decimal? Average);

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
            .Include(x => x.CatalogItem!).ThenInclude(x => x.Category)
            .Include(x => x.AssignedStore)
            .OrderBy(x => x.IsChecked)
            .ThenBy(x => x.CatalogItem == null || x.CatalogItem.Category == null ? int.MaxValue : x.CatalogItem.Category.SortOrder)
            .ThenBy(x => x.CatalogItem == null ? x.CustomName : x.CatalogItem.Name)
            .ToListAsync(cancellationToken);

        var itemIds = rows.Where(x => x.CatalogItemId != null).Select(x => x.CatalogItemId!.Value).ToList();
        var offers = await db.StoreOffers.AsNoTracking()
            .Where(x => x.HouseholdId == household.HouseholdId && x.IsAvailable && itemIds.Contains(x.CatalogItemId) && x.Store.IsActive)
            .Include(x => x.Store)
            .Include(x => x.Prices)
            .ToListAsync(cancellationToken);
        var variants = await db.ProductVariants.AsNoTracking()
            .Where(x => x.HouseholdId == household.HouseholdId && !x.IsArchived && itemIds.Contains(x.CatalogItemId))
            .OrderByDescending(x => x.IsPreferred).ThenBy(x => x.Name)
            .Select(x => new
            {
                x.CatalogItemId,
                x.Name,
                x.Brand,
                x.Size,
                x.ImagePath,
                x.IsPreferred,
                StoreNames = db.StoreOffers.Where(o => o.HouseholdId == household.HouseholdId && o.ProductVariantId == x.Id && o.IsAvailable && o.Store.IsActive)
                    .OrderBy(o => o.Store.SortOrder).ThenBy(o => o.Store.Name).Select(o => o.Store.Name).ToArray()
            })
            .ToListAsync(cancellationToken);

        return rows.Select(row => new ShoppingListRow
        {
            Id = row.Id,
            CatalogItemId = row.CatalogItemId,
            Name = row.CatalogItem?.Name ?? row.CustomName ?? "Unnamed item",
            Description = row.CatalogItem?.Description,
            ImagePath = row.CatalogItem?.ImagePath ?? (row.CatalogItemId is Guid imageItemId
                ? variants.Where(x => x.CatalogItemId == imageItemId && !string.IsNullOrWhiteSpace(x.ImagePath))
                    .OrderByDescending(x => x.IsPreferred).ThenBy(x => x.Name)
                    .Select(x => x.ImagePath).FirstOrDefault()
                : null),
            Quantity = row.Quantity,
            IsChecked = row.IsChecked,
            CheckedAt = row.CheckedAt,
            IsFavorite = row.CatalogItem?.IsFavorite ?? false,
            AssignedStoreId = row.AssignedStoreId,
            AssignedStoreName = row.AssignedStore?.Name,
            CategoryName = row.CatalogItem?.Category?.Name,
            StorePrices = offers.Where(x => x.CatalogItemId == row.CatalogItemId)
                .GroupBy(x => new { x.StoreId, x.Store.Name, x.Store.ImagePath })
                .Select(x => new StorePrice(x.Key.StoreId, x.Key.Name,
                    x.SelectMany(o => o.Prices).OrderByDescending(p => p.RecordedAt).Select(p => (decimal?)p.Price).FirstOrDefault(),
                    row.AssignedStoreId == x.Key.StoreId, x.Any(o => o.IsPreferred), x.Key.ImagePath))
                .OrderByDescending(x => x.IsPreferred)
                .ThenBy(x => x.StoreName)
                .ToArray(),
            ExactProducts = row.CatalogItemId is Guid catalogItemId
                ? variants.Where(x => x.CatalogItemId == catalogItemId)
                    .Select(x => new ExactProductSummary(x.Name, x.Brand, x.Size, x.IsPreferred, x.StoreNames)).ToArray()
                : []
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
                || x.Variants.Any(v => !v.IsArchived && (v.Name.ToLower().Contains(term)
                    || (v.Brand != null && v.Brand.ToLower().Contains(term))
                    || (v.Size != null && v.Size.ToLower().Contains(term))))
                || x.Variants.Any(v => !v.IsArchived && v.Barcodes.Any(b => b.Barcode == trimmed)));
        }

        var matches = await catalog
            .Select(x => new
            {
                x.Id,
                x.Name,
                x.IsFavorite,
                MatchRank = string.IsNullOrWhiteSpace(trimmed) ? 0
                    : x.Variants.Any(v => !v.IsArchived && v.Barcodes.Any(b => b.Barcode == trimmed)) ? 0
                    : x.Name.ToLower() == trimmed.ToLower() ? 1
                    : x.Name.ToLower().StartsWith(trimmed.ToLower()) ? 2
                    : x.Aliases.Any(a => a.Alias.ToLower() == trimmed.ToLower()) ? 3
                    : x.Aliases.Any(a => a.Alias.ToLower().StartsWith(trimmed.ToLower())) ? 4
                    : x.Name.ToLower().Contains(trimmed.ToLower()) ? 5
                    : x.Variants.Any(v => !v.IsArchived && (v.Name.ToLower().Contains(trimmed.ToLower()) || (v.Brand != null && v.Brand.ToLower().Contains(trimmed.ToLower())))) ? 6
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

    public Task AddAsync(Guid catalogItemId, CancellationToken cancellationToken = default, Guid? operationId = null) =>
        ApplyAsync(nameof(AddAsync), operationId, async (db, household) =>
        {
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

            return true;
        }, cancellationToken);

    public Task<Guid> QuickAddAsync(string name, CancellationToken cancellationToken = default, Guid? operationId = null) =>
        ApplyAsync(nameof(QuickAddAsync), operationId, async (db, household) =>
        {
            var normalized = name?.Trim() ?? "";
            if (normalized.Length is < 1 or > 120)
            {
                throw new ArgumentException("Item names must be between 1 and 120 characters.", nameof(name));
            }

            var term = normalized.ToLower();
            var item = await db.CatalogItems.FirstOrDefaultAsync(
                x => x.HouseholdId == household.HouseholdId && !x.IsArchived
                    && (x.Name.ToLower() == term
                        || x.Aliases.Any(a => a.Alias.ToLower() == term)
                        || x.Variants.Any(v => !v.IsArchived && (v.Name.ToLower() == term || v.Barcodes.Any(b => b.Barcode == normalized)))),
                cancellationToken);
            var row = item is not null
                ? await db.ShoppingListItems.SingleOrDefaultAsync(x => x.HouseholdId == household.HouseholdId && x.CatalogItemId == item.Id, cancellationToken)
                : await db.ShoppingListItems.SingleOrDefaultAsync(x => x.HouseholdId == household.HouseholdId && x.CatalogItemId == null && x.CustomName != null && x.CustomName.ToLower() == term, cancellationToken);
            if (row is null)
            {
                row = new ShoppingListItem
                {
                    HouseholdId = household.HouseholdId,
                    CatalogItemId = item?.Id,
                    CustomName = item is null ? normalized : null,
                    Quantity = item?.DefaultQuantity ?? 1,
                    AddedByUserId = household.UserId
                };
                db.ShoppingListItems.Add(row);
            }
            else
            {
                row.Quantity += item?.DefaultQuantity ?? 1;
                row.IsChecked = false;
                row.CheckedAt = null;
            }

            return item?.Id ?? row.Id;
        }, cancellationToken);

    public Task ToggleCheckedAsync(Guid shoppingListItemId, CancellationToken cancellationToken = default, Guid? operationId = null) =>
        MutateRowAsync(nameof(ToggleCheckedAsync), operationId, shoppingListItemId, (db, row) =>
        {
            row.IsChecked = !row.IsChecked;
            row.CheckedAt = row.IsChecked ? DateTimeOffset.UtcNow : null;
        }, cancellationToken);

    public Task ChangeQuantityAsync(Guid shoppingListItemId, decimal delta, CancellationToken cancellationToken = default, Guid? operationId = null) =>
        MutateRowAsync(nameof(ChangeQuantityAsync), operationId, shoppingListItemId, (db, row) =>
        {
            try { row.Quantity = Math.Max(0.25m, row.Quantity + delta); }
            catch (OverflowException) { throw new ArgumentException("That quantity is too large."); }
        }, cancellationToken);

    public Task RemoveAsync(Guid shoppingListItemId, CancellationToken cancellationToken = default, Guid? operationId = null) =>
        MutateRowAsync(nameof(RemoveAsync), operationId, shoppingListItemId, (db, row) => db.ShoppingListItems.Remove(row), cancellationToken);

    public Task AssignStoreAsync(Guid shoppingListItemId, Guid? storeId, CancellationToken cancellationToken = default, Guid? operationId = null) =>
        ApplyAsync(nameof(AssignStoreAsync), operationId, async (db, household) =>
        {
            if (storeId is Guid id && !await db.Stores.AnyAsync(x => x.Id == id && x.HouseholdId == household.HouseholdId && x.IsActive, cancellationToken))
            {
                throw new InvalidOperationException("That store is not available in this household.");
            }

            var row = await FindRowAsync(db, household.HouseholdId, shoppingListItemId, cancellationToken);
            row.AssignedStoreId = storeId;
            return true;
        }, cancellationToken);

    public Task<int> ClearCheckedAsync(CancellationToken cancellationToken = default, Guid? operationId = null, IReadOnlyList<CheckedListRow>? rows = null) =>
        ApplyAsync(nameof(ClearCheckedAsync), operationId, async (db, household) =>
        {
            var query = db.ShoppingListItems.Where(x => x.HouseholdId == household.HouseholdId && x.IsChecked);
            if (rows is not null)
            {
                var ids = rows.Select(x => x.Id).Distinct().ToArray();
                query = query.Where(x => ids.Contains(x.Id));
            }

            var checkedItems = await query.ToListAsync(cancellationToken);
            if (rows is not null)
            {
                var selected = rows.ToHashSet();
                checkedItems.RemoveAll(row => !selected.Contains(new CheckedListRow(row.Id, row.CheckedAt)));
            }

            foreach (var row in checkedItems)
            {
                if (row.CatalogItemId is not Guid catalogItemId) continue;
                db.PurchaseHistory.Add(new PurchaseHistory
                {
                    HouseholdId = household.HouseholdId,
                    CatalogItemId = catalogItemId,
                    StoreId = row.AssignedStoreId,
                    Quantity = row.Quantity,
                    PurchasedAt = row.CheckedAt ?? DateTimeOffset.UtcNow,
                    PurchasedByUserId = household.UserId
                });
            }

            db.ShoppingListItems.RemoveRange(checkedItems);
            return checkedItems.Count;
        }, cancellationToken);

    private Task MutateRowAsync(string action, Guid? operationId, Guid rowId, Action<ApplicationDbContext, ShoppingListItem> mutation, CancellationToken cancellationToken) =>
        ApplyAsync(action, operationId, async (db, household) =>
        {
            mutation(db, await FindRowAsync(db, household.HouseholdId, rowId, cancellationToken));
            return true;
        }, cancellationToken);

    private static async Task<ShoppingListItem> FindRowAsync(ApplicationDbContext db, Guid householdId, Guid rowId, CancellationToken cancellationToken) =>
        await db.ShoppingListItems.SingleOrDefaultAsync(x => x.Id == rowId && x.HouseholdId == householdId, cancellationToken)
        ?? throw new InvalidOperationException("That list item is not in this household.");

    private async Task<T> ApplyAsync<T>(string action, Guid? operationId,
        Func<ApplicationDbContext, CurrentHouseholdContext, Task<T>> apply, CancellationToken cancellationToken)
    {
        var household = await RequireHouseholdAsync(cancellationToken);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        // SQLite serializes writers before reading the list or checking the receipt.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (operationId is Guid id)
        {
            var receipt = await db.ShoppingListOperations.FindAsync([household.HouseholdId, household.UserId, id], cancellationToken);
            if (receipt is not null)
            {
                if (receipt.Action != action) throw new ArgumentException("That operation ID was used for a different list action.");
                return JsonSerializer.Deserialize<T>(receipt.Result)!;
            }
        }

        var result = await apply(db, household);
        var listChanged = db.ChangeTracker.HasChanges();
        if (operationId is Guid newId)
            db.ShoppingListOperations.Add(new ShoppingListOperation
            {
                HouseholdId = household.HouseholdId, UserId = household.UserId, Id = newId,
                Action = action, Result = JsonSerializer.Serialize(result)
            });

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (listChanged) notifier.Notify(household.HouseholdId);
        return result;
    }

    private async Task<CurrentHouseholdContext> RequireHouseholdAsync(CancellationToken cancellationToken) =>
        await householdService.GetCurrentAsync(cancellationToken)
        ?? throw new InvalidOperationException("Choose a household before editing the grocery list.");
}
