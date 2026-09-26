using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using YALA.Data;
using YALA.Data.Entities;
using YALA.Services;

namespace YALA.Api;

public static class YalaApiEndpoints
{
    public static IEndpointRouteBuilder MapYalaApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api");

        api.MapGet("/session", GetSessionAsync).AllowAnonymous();
        api.MapGet("/antiforgery", (HttpContext http, IAntiforgery antiforgery) => Results.Ok(new { Token = antiforgery.GetAndStoreTokens(http).RequestToken })).AllowAnonymous();

        var secured = api.MapGroup("/").RequireAuthorization();
        secured.MapGet("/households", GetHouseholdsAsync);
        secured.MapPost("/households/switch", SwitchHouseholdAsync);
        secured.MapPost("/households/default", MakeDefaultHouseholdAsync);
        secured.MapPost("/households/create", CreateHouseholdAsync);
        secured.MapPost("/households/rename", RenameHouseholdAsync);
        secured.MapPost("/households/archive", ArchiveHouseholdAsync);
        secured.MapGet("/households/archived", GetArchivedHouseholdsAsync);
        secured.MapPost("/households/restore", RestoreHouseholdAsync);

        secured.MapGet("/list", GetListAsync);
        secured.MapGet("/list/suggestions", GetSuggestionsAsync);
        secured.MapPost("/list/add", AddListItemAsync);
        secured.MapPost("/list/quick-add", QuickAddAsync);
        secured.MapPost("/list/toggle", ToggleAsync);
        secured.MapPost("/list/quantity", ChangeQuantityAsync);
        secured.MapPost("/list/remove", RemoveAsync);
        secured.MapPost("/list/assign-store", AssignStoreAsync);
        secured.MapPost("/list/clear-checked", ClearCheckedAsync);

        secured.MapGet("/catalog", GetCatalogAsync);
        secured.MapGet("/catalog/details", GetNewCatalogDetailsAsync);
        secured.MapGet("/catalog/details/{id:guid}", GetCatalogDetailsAsync);
        secured.MapPost("/catalog/promote", PromoteAdHocAsync);
        secured.MapDelete("/catalog/ad-hoc/{id:guid}", DeleteAdHocAsync);
        secured.MapPost("/catalog/save", SaveCatalogAsync);
        secured.MapPost("/catalog/favorite", SetFavoriteAsync);
        secured.MapPost("/catalog/archive", SetArchivedAsync);
        secured.MapPost("/catalog/aliases", AddAliasAsync);
        secured.MapDelete("/catalog/aliases/{id:guid}", RemoveAliasAsync);
        secured.MapPost("/catalog/variants", SaveVariantAsync);
        secured.MapDelete("/catalog/variants/{id:guid}", RemoveVariantAsync);
        secured.MapPost("/catalog/variants/store", SetVariantStoreAsync);
        secured.MapPost("/catalog/store", SetItemStoreAsync);
        secured.MapGet("/products/lookup/{barcode}", LookupProductAsync);
        secured.MapPost("/images/items/{id:guid}", UploadItemImageAsync);
        secured.MapPost("/images/stores/{id:guid}", UploadStoreImageAsync);

        secured.MapGet("/stores", GetStoresAsync);
        secured.MapGet("/stores/{id:guid}", GetStoreDetailsAsync);
        secured.MapPost("/stores/save", SaveStoreAsync);
        secured.MapPost("/stores/active", SetStoreActiveAsync);
        secured.MapPost("/stores/move", MoveStoreAsync);
        secured.MapPost("/stores/preferred", SetPreferredOfferAsync);
        secured.MapDelete("/stores/offers/{id:guid}", RemoveOfferAsync);
        secured.MapPost("/stores/offers", SaveOfferAsync);
        secured.MapGet("/users", GetUsersAsync);
        secured.MapPost("/users", CreateUserAsync);
        secured.MapPost("/users/reset-password", ResetPasswordAsync);
        secured.MapDelete("/users/{id}", DeleteUserAsync);

        return endpoints;
    }

    private static async Task<IResult> GetSessionAsync(
        HttpContext http,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        HouseholdService households,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var requiresSetup = !await db.Users.AnyAsync(cancellationToken);
        if (http.User.Identity?.IsAuthenticated != true)
        {
            return Results.Ok(new { IsAuthenticated = false, RequiresSetup = requiresSetup, UserName = (string?)null, IsAdministrator = false, Households = new { Items = Array.Empty<object>(), Current = (object?)null, Categories = Array.Empty<object>() } });
        }

        var userId = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var user = userId is null ? null : await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId, cancellationToken);
        var snapshot = await GetHouseholdSnapshotAsync(households, cancellationToken);
        return Results.Ok(new { IsAuthenticated = true, RequiresSetup = requiresSetup, UserName = user?.UserName, IsAdministrator = user?.IsAdministrator == true, Households = snapshot });
    }

    private static async Task<IResult> GetHouseholdsAsync(HouseholdService households, CancellationToken cancellationToken) => Results.Ok(await GetHouseholdSnapshotAsync(households, cancellationToken));

    private static async Task<object> GetHouseholdSnapshotAsync(HouseholdService households, CancellationToken cancellationToken)
    {
        var items = (await households.GetHouseholdsAsync(cancellationToken)).Select(x => new { x.Id, x.Name, x.IsDefault }).ToList();
        var current = await households.GetCurrentAsync(cancellationToken);
        var categories = current is null ? [] : (await households.GetCategoriesAsync(cancellationToken)).Select(x => new { x.Id, x.Name, x.SortOrder }).ToList();
        var selected = current is null ? null : items.FirstOrDefault(x => x.Id == current.HouseholdId);
        return new { Items = items, Current = selected, Categories = categories };
    }

    private static async Task<IResult> SwitchHouseholdAsync(HouseholdCommand command, HouseholdService households, CancellationToken cancellationToken)
    {
        var allowed = await households.SwitchAsync(command.HouseholdId, cancellationToken);
        return allowed ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> MakeDefaultHouseholdAsync(HouseholdCommand command, HouseholdService households, CancellationToken cancellationToken)
    {
        var allowed = await households.MakeDefaultAsync(command.HouseholdId, cancellationToken);
        return allowed ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> CreateHouseholdAsync(HouseholdCreateCommand command, HouseholdService households, CancellationToken cancellationToken) => Results.Ok(await households.CreateAsync(command.Name, command.MakeDefault, cancellationToken));
    private static async Task<IResult> RenameHouseholdAsync(HouseholdNameCommand command, HouseholdService households, HttpContext http, CancellationToken cancellationToken) { await SelectHouseholdAsync(http, households, cancellationToken); await households.RenameCurrentAsync(command.Name, cancellationToken); return Results.NoContent(); }
    private static async Task<IResult> ArchiveHouseholdAsync(HouseholdService households, HttpContext http, CancellationToken cancellationToken) { await SelectHouseholdAsync(http, households, cancellationToken); await households.ArchiveCurrentAsync(cancellationToken); return Results.NoContent(); }
    private static async Task<IResult> GetArchivedHouseholdsAsync(HouseholdService households, CancellationToken cancellationToken) => Results.Ok(await households.GetArchivedHouseholdsAsync(cancellationToken));
    private static async Task<IResult> RestoreHouseholdAsync(HouseholdCommand command, HouseholdService households, CancellationToken cancellationToken) => (await households.RestoreHouseholdAsync(command.HouseholdId, cancellationToken)) ? Results.NoContent() : Results.NotFound();

    private static async Task<IResult> GetListAsync(HouseholdService households, ShoppingListService lists, StoreService stores, HttpContext http, CancellationToken cancellationToken)
    {
        await SelectHouseholdAsync(http, households, cancellationToken);
        return Results.Ok(new { Rows = await lists.GetRowsAsync(cancellationToken), Stores = await stores.GetStoresAsync(cancellationToken) });
    }

    private static async Task<IResult> GetSuggestionsAsync(string? query, HouseholdService households, ShoppingListService lists, HttpContext http, CancellationToken cancellationToken)
    {
        await SelectHouseholdAsync(http, households, cancellationToken);
        return Results.Ok(await lists.GetQuickAddChoicesAsync(query, cancellationToken));
    }

    private static async Task<IResult> AddListItemAsync(ListItemCommand command, HouseholdService households, ShoppingListService lists, HttpContext http, CancellationToken cancellationToken)
    {
        await SelectHouseholdAsync(http, households, cancellationToken);
        await lists.AddAsync(command.CatalogItemId, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> QuickAddAsync(QuickAddCommand command, HouseholdService households, ShoppingListService lists, HttpContext http, CancellationToken cancellationToken)
    {
        await SelectHouseholdAsync(http, households, cancellationToken);
        return Results.Ok(await lists.QuickAddAsync(command.Name, cancellationToken));
    }

    private static async Task<IResult> ToggleAsync(ListRowCommand command, HouseholdService households, ShoppingListService lists, HttpContext http, CancellationToken cancellationToken)
    {
        await SelectHouseholdAsync(http, households, cancellationToken);
        await lists.ToggleCheckedAsync(command.Id, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> ChangeQuantityAsync(QuantityCommand command, HouseholdService households, ShoppingListService lists, HttpContext http, CancellationToken cancellationToken)
    {
        await SelectHouseholdAsync(http, households, cancellationToken);
        await lists.ChangeQuantityAsync(command.Id, command.Delta, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> RemoveAsync(ListRowCommand command, HouseholdService households, ShoppingListService lists, HttpContext http, CancellationToken cancellationToken)
    {
        await SelectHouseholdAsync(http, households, cancellationToken);
        await lists.RemoveAsync(command.Id, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> AssignStoreAsync(AssignStoreCommand command, HouseholdService households, ShoppingListService lists, HttpContext http, CancellationToken cancellationToken)
    {
        await SelectHouseholdAsync(http, households, cancellationToken);
        await lists.AssignStoreAsync(command.Id, command.StoreId, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> ClearCheckedAsync(HouseholdService households, ShoppingListService lists, HttpContext http, CancellationToken cancellationToken)
    {
        await SelectHouseholdAsync(http, households, cancellationToken);
        return Results.Ok(await lists.ClearCheckedAsync(cancellationToken));
    }

    private static async Task<IResult> GetCatalogAsync(string? query, bool includeArchived, HouseholdService households, CatalogService catalog, HttpContext http, CancellationToken cancellationToken)
    {
        await SelectHouseholdAsync(http, households, cancellationToken);
        var items = await catalog.GetItemsAsync(query, includeArchived, cancellationToken);
        var adHoc = await catalog.GetAdHocItemsAsync(cancellationToken);
        var categories = (await catalog.GetCategoriesAsync(cancellationToken)).Select(x => new { x.Id, x.Name, x.SortOrder }).ToList();
        return Results.Ok(new { Items = items, AdHocItems = adHoc, Categories = categories });
    }

    private static async Task<IResult> GetNewCatalogDetailsAsync(HouseholdService households, CatalogService catalog, HttpContext http, CancellationToken cancellationToken)
    {
        await SelectHouseholdAsync(http, households, cancellationToken);
        return Results.Ok(new { Categories = (await catalog.GetCategoriesAsync(cancellationToken)).Select(x => new { x.Id, x.Name, x.SortOrder }) });
    }

    private static async Task<IResult> GetCatalogDetailsAsync(Guid id, HouseholdService households, CatalogService catalog, HttpContext http, CancellationToken cancellationToken)
    {
        await SelectHouseholdAsync(http, households, cancellationToken);
        var details = await catalog.GetDetailsAsync(id, cancellationToken);
        if (details is null) return Results.NotFound();
        return Results.Ok(new
        {
            details.Id, details.Name, details.Description, details.CategoryId, details.CategoryName, details.DefaultQuantity, details.IsFavorite, details.IsArchived, details.ImagePath,
            Aliases = details.Aliases.Select(x => new { x.Id, x.Alias }),
            Variants = details.Variants.Select(x => new { x.Id, x.Name, x.Brand, x.Size, x.ImagePath, x.IsPreferred, Barcodes = x.Barcodes.Select(b => new { Id = b.Id, Barcode = b.Barcode }), x.StoreIds }),
            details.Stores
        });
    }

    private static async Task<IResult> PromoteAdHocAsync(PromoteCommand command, HouseholdService households, CatalogService catalog, HttpContext http, CancellationToken cancellationToken)
    {
        await SelectHouseholdAsync(http, households, cancellationToken); return Results.Ok(await catalog.PromoteAdHocItemAsync(command.ShoppingListItemId, cancellationToken));
    }

    private static async Task<IResult> DeleteAdHocAsync(Guid id, HouseholdService households, CatalogService catalog, HttpContext http, CancellationToken cancellationToken)
    {
        await SelectHouseholdAsync(http, households, cancellationToken); await catalog.DeleteAdHocItemAsync(id, cancellationToken); return Results.NoContent();
    }

    private static async Task<IResult> SaveCatalogAsync(CatalogSaveCommand command, HouseholdService households, CatalogService catalog, HttpContext http, CancellationToken cancellationToken)
    {
        await SelectHouseholdAsync(http, households, cancellationToken);
        return Results.Ok(await catalog.SaveAsync(command.Id, command.Name, command.Description, command.CategoryId, command.DefaultQuantity, cancellationToken));
    }

    private static async Task<IResult> SetFavoriteAsync(CatalogFlagCommand command, HouseholdService households, CatalogService catalog, HttpContext http, CancellationToken cancellationToken)
    {
        await SelectHouseholdAsync(http, households, cancellationToken); await catalog.SetFavoriteAsync(command.Id, command.Value, cancellationToken); return Results.NoContent();
    }

    private static async Task<IResult> SetArchivedAsync(CatalogFlagCommand command, HouseholdService households, CatalogService catalog, HttpContext http, CancellationToken cancellationToken)
    {
        await SelectHouseholdAsync(http, households, cancellationToken); await catalog.SetArchivedAsync(command.Id, command.Value, cancellationToken); return Results.NoContent();
    }

    private static async Task<IResult> AddAliasAsync(AliasCommand command, HouseholdService households, CatalogService catalog, HttpContext http, CancellationToken cancellationToken)
    {
        await SelectHouseholdAsync(http, households, cancellationToken); await catalog.AddAliasAsync(command.ItemId, command.Alias, cancellationToken); return Results.NoContent();
    }

    private static async Task<IResult> RemoveAliasAsync(Guid id, HouseholdService households, CatalogService catalog, HttpContext http, CancellationToken cancellationToken)
    {
        await SelectHouseholdAsync(http, households, cancellationToken); await catalog.RemoveAliasAsync(id, cancellationToken); return Results.NoContent();
    }

    private static async Task<IResult> SaveVariantAsync(VariantCommand command, HouseholdService households, CatalogService catalog, HttpContext http, CancellationToken cancellationToken)
    {
        await SelectHouseholdAsync(http, households, cancellationToken);
        var id = command.Id is Guid variantId
            ? await UpdateVariantAndReturnIdAsync(catalog, variantId, command, cancellationToken)
            : await catalog.SaveVariantAsync(command.ItemId, command.Name, command.Brand, command.Size, command.Preferred, command.Barcode, command.StoreIdsValue, cancellationToken);
        return Results.Ok(id);
    }

    private static async Task<Guid> UpdateVariantAndReturnIdAsync(CatalogService catalog, Guid variantId, VariantCommand command, CancellationToken cancellationToken)
    {
        await catalog.UpdateVariantAsync(variantId, command.Name, command.Brand, command.Size, command.Preferred, command.BarcodesValue, cancellationToken);
        return variantId;
    }

    private static async Task<IResult> RemoveVariantAsync(Guid id, HouseholdService households, CatalogService catalog, HttpContext http, CancellationToken cancellationToken)
    {
        await SelectHouseholdAsync(http, households, cancellationToken); await catalog.RemoveVariantAsync(id, cancellationToken); return Results.NoContent();
    }

    private static async Task<IResult> SetVariantStoreAsync(VariantStoreCommand command, HouseholdService households, CatalogService catalog, HttpContext http, CancellationToken cancellationToken)
    {
        await SelectHouseholdAsync(http, households, cancellationToken); await catalog.SetVariantStoreAvailabilityAsync(command.VariantId, command.StoreId, command.Available, cancellationToken); return Results.NoContent();
    }

    private static async Task<IResult> SetItemStoreAsync(ItemStoreCommand command, HouseholdService households, CatalogService catalog, HttpContext http, CancellationToken cancellationToken)
    {
        await SelectHouseholdAsync(http, households, cancellationToken); await catalog.SetStoreAvailabilityAsync(command.ItemId, command.StoreId, command.Available, cancellationToken); return Results.NoContent();
    }

    private static async Task<IResult> LookupProductAsync(string barcode, ProductLookupService lookup, CancellationToken cancellationToken)
    {
        var result = await lookup.LookupAsync(barcode, cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> UploadItemImageAsync(Guid id, IFormFile file, HouseholdService households, ImageService images, HttpContext http, CancellationToken cancellationToken)
    {
        await SelectHouseholdAsync(http, households, cancellationToken);
        if (file.Length <= 0 || file.Length > ImageService.MaximumImageBytes) return Results.BadRequest("Images must be 4 MB or smaller.");
        await using var stream = file.OpenReadStream();
        await images.SaveCatalogItemImageUploadAsync(id, stream, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> UploadStoreImageAsync(Guid id, IFormFile file, HouseholdService households, ImageService images, HttpContext http, CancellationToken cancellationToken)
    {
        await SelectHouseholdAsync(http, households, cancellationToken);
        if (file.Length <= 0 || file.Length > ImageService.MaximumImageBytes) return Results.BadRequest("Images must be 4 MB or smaller.");
        await using var stream = file.OpenReadStream();
        await images.SaveStoreImageUploadAsync(id, stream, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> GetStoresAsync(HouseholdService households, StoreService stores, HttpContext http, CancellationToken cancellationToken)
    {
        await SelectHouseholdAsync(http, households, cancellationToken); return Results.Ok(await stores.GetStoresAsync(cancellationToken));
    }

    private static async Task<IResult> GetStoreDetailsAsync(Guid id, HouseholdService households, StoreService stores, HttpContext http, CancellationToken cancellationToken)
    {
        await SelectHouseholdAsync(http, households, cancellationToken);
        var store = await stores.GetStoreAsync(id, cancellationToken);
        if (store is null) return Results.NotFound();
        var offers = await stores.GetOffersAsync(id, cancellationToken);
        var catalog = (await stores.GetCatalogItemsAsync(cancellationToken)).Select(x => new { Id = x.Id, Name = x.Name });
        return Results.Ok(new { Store = store, Offers = offers, CatalogItems = catalog });
    }

    private static async Task<IResult> SaveStoreAsync(StoreCommand command, HouseholdService households, StoreService stores, HttpContext http, CancellationToken cancellationToken)
    {
        await SelectHouseholdAsync(http, households, cancellationToken); await stores.SaveStoreAsync(command.Id, command.Name, cancellationToken); return Results.NoContent();
    }

    private static async Task<IResult> SetStoreActiveAsync(StoreActiveCommand command, HouseholdService households, StoreService stores, HttpContext http, CancellationToken cancellationToken)
    {
        await SelectHouseholdAsync(http, households, cancellationToken); await stores.SetActiveAsync(command.Id, command.Active, cancellationToken); return Results.NoContent();
    }

    private static async Task<IResult> MoveStoreAsync(StoreMoveCommand command, HouseholdService households, StoreService stores, HttpContext http, CancellationToken cancellationToken)
    {
        await SelectHouseholdAsync(http, households, cancellationToken); await stores.MoveStoreAsync(command.Id, command.Direction, cancellationToken); return Results.NoContent();
    }

    private static async Task<IResult> SetPreferredOfferAsync(PreferredCommand command, HouseholdService households, StoreService stores, HttpContext http, CancellationToken cancellationToken)
    {
        await SelectHouseholdAsync(http, households, cancellationToken); await stores.SetPreferredOfferAsync(command.OfferId, command.Preferred, cancellationToken); return Results.NoContent();
    }

    private static async Task<IResult> RemoveOfferAsync(Guid id, HouseholdService households, StoreService stores, HttpContext http, CancellationToken cancellationToken)
    {
        await SelectHouseholdAsync(http, households, cancellationToken); await stores.RemoveOfferAsync(id, cancellationToken); return Results.NoContent();
    }

    private static async Task<IResult> SaveOfferAsync(StoreOfferCommand command, HouseholdService households, StoreService stores, HttpContext http, CancellationToken cancellationToken)
    {
        await SelectHouseholdAsync(http, households, cancellationToken); await stores.SaveOfferAsync(command.StoreId, command.CatalogItemId, command.Aisle, command.Price, command.ProductVariantId, cancellationToken); return Results.NoContent();
    }

    private static async Task<IResult> GetUsersAsync(HttpContext http, IDbContextFactory<ApplicationDbContext> dbFactory, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsAdministratorAsync(http, db, cancellationToken)) return Results.Forbid();
        var users = await db.Users.AsNoTracking().OrderBy(x => x.DisplayName).ThenBy(x => x.UserName).Select(x => new { x.Id, x.UserName, x.DisplayName, x.IsAdministrator }).ToListAsync(cancellationToken);
        var memberships = await db.HouseholdMembers.AsNoTracking().OrderBy(x => x.Household.Name).Select(x => new { x.UserId, Name = x.Household.Name }).ToListAsync(cancellationToken);
        return Results.Ok(users.Select(user => new { user.Id, user.UserName, user.DisplayName, user.IsAdministrator, Households = memberships.Where(x => x.UserId == user.Id).Select(x => x.Name).ToList() }));
    }

    private static async Task<IResult> CreateUserAsync(UserCreateCommand command, HttpContext http, UserManager<ApplicationUser> userManager, IDbContextFactory<ApplicationDbContext> dbFactory, HouseholdService households, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsAdministratorAsync(http, db, cancellationToken)) return Results.Forbid();
        var user = new ApplicationUser { UserName = command.UserName.Trim(), DisplayName = command.DisplayName.Trim() };
        var result = await userManager.CreateAsync(user, command.Password);
        if (!result.Succeeded) return Results.BadRequest(result.Errors.Select(x => x.Description));
        if (command.HouseholdId is Guid householdId)
        {
            households.SelectHousehold(householdId);
            await households.AddMemberAsync(user.UserName!, cancellationToken);
        }
        return Results.Ok(new { user.Id });
    }

    private static async Task<IResult> ResetPasswordAsync(UserPasswordCommand command, HttpContext http, UserManager<ApplicationUser> userManager, IDbContextFactory<ApplicationDbContext> dbFactory, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsAdministratorAsync(http, db, cancellationToken)) return Results.Forbid();
        var user = await userManager.FindByIdAsync(command.UserId);
        if (user is null) return Results.NotFound();
        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        var result = await userManager.ResetPasswordAsync(user, token, command.Password);
        return result.Succeeded ? Results.NoContent() : Results.BadRequest(result.Errors.Select(x => x.Description));
    }

    private static async Task<IResult> DeleteUserAsync(string id, HttpContext http, UserManager<ApplicationUser> userManager, IDbContextFactory<ApplicationDbContext> dbFactory, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsAdministratorAsync(http, db, cancellationToken)) return Results.Forbid();
        var currentId = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.Equals(currentId, id, StringComparison.Ordinal)) return Results.BadRequest("You cannot delete your own account.");
        var user = await userManager.FindByIdAsync(id);
        if (user is null) return Results.NotFound();
        var result = await userManager.DeleteAsync(user);
        return result.Succeeded ? Results.NoContent() : Results.BadRequest(result.Errors.Select(x => x.Description));
    }

    private static async Task<bool> IsAdministratorAsync(HttpContext http, ApplicationDbContext db, CancellationToken cancellationToken)
    {
        var id = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        return id is not null && await db.Users.AnyAsync(x => x.Id == id && x.IsAdministrator, cancellationToken);
    }

    private static async Task SelectHouseholdAsync(HttpContext http, HouseholdService households, CancellationToken cancellationToken)
    {
        if (http.Request.Headers.TryGetValue("X-Yala-Household", out var raw) && Guid.TryParse(raw, out var selected)) households.SelectHousehold(selected);
        if (await households.GetCurrentAsync(cancellationToken) is null) throw new InvalidOperationException("Choose a household first.");
    }

    private sealed record HouseholdCommand(Guid HouseholdId);
    private sealed record HouseholdCreateCommand(string Name, bool MakeDefault);
    private sealed record HouseholdNameCommand(string Name);
    private sealed record ListItemCommand(Guid CatalogItemId);
    private sealed record QuickAddCommand(string Name);
    private sealed record ListRowCommand(Guid Id);
    private sealed record QuantityCommand(Guid Id, decimal Delta);
    private sealed record AssignStoreCommand(Guid Id, Guid? StoreId);
    private sealed record CatalogSaveCommand(Guid? Id, string Name, string? Description, Guid? CategoryId, decimal DefaultQuantity);
    private sealed record CatalogFlagCommand(Guid Id, bool Value);
    private sealed record AliasCommand(Guid ItemId, string Alias);
    private sealed record PromoteCommand(Guid ShoppingListItemId);
    private sealed record VariantCommand(Guid? Id, Guid ItemId, string Name, string? Brand, string? Size, bool Preferred, string? Barcode, List<string>? Barcodes, List<Guid>? StoreIds)
    {
        public IReadOnlyList<string> BarcodesValue => Barcodes ?? [];
        public IReadOnlyList<Guid> StoreIdsValue => StoreIds ?? [];
    }
    private sealed record VariantStoreCommand(Guid VariantId, Guid StoreId, bool Available);
    private sealed record ItemStoreCommand(Guid ItemId, Guid StoreId, bool Available);
    private sealed record StoreCommand(Guid? Id, string Name);
    private sealed record StoreActiveCommand(Guid Id, bool Active);
    private sealed record StoreMoveCommand(Guid Id, int Direction);
    private sealed record PreferredCommand(Guid OfferId, bool Preferred);
    private sealed record StoreOfferCommand(Guid StoreId, Guid CatalogItemId, string? Aisle, decimal? Price, Guid? ProductVariantId);
    private sealed record UserCreateCommand(string DisplayName, string UserName, string Password, Guid? HouseholdId);
    private sealed record UserPasswordCommand(string UserId, string Password);
}
