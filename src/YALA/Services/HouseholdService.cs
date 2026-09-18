using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using YALA.Data;
using YALA.Data.Entities;

namespace YALA.Services;

public sealed record HouseholdChoice(Guid Id, string Name, bool IsDefault);
public sealed record CurrentHouseholdContext(string UserId, Guid HouseholdId, string HouseholdName);
public sealed record HouseholdMemberChoice(string UserId, string DisplayName, string UserName);
public sealed record HouseholdCategory(Guid Id, string Name, int SortOrder);

public sealed class HouseholdService(
    AuthenticationStateProvider authenticationStateProvider,
    IDbContextFactory<ApplicationDbContext> dbContextFactory)
{
    private Guid? selectedHouseholdId;
    public event Action? CurrentHouseholdChanged;

    public async Task<string?> GetUserIdAsync()
    {
        var state = await authenticationStateProvider.GetAuthenticationStateAsync();
        return state.User.FindFirstValue(ClaimTypes.NameIdentifier);
    }

    public async Task<IReadOnlyList<HouseholdChoice>> GetHouseholdsAsync(CancellationToken cancellationToken = default)
    {
        var userId = await GetUserIdAsync();
        if (userId is null)
        {
            return [];
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.HouseholdMembers
            .Where(x => x.UserId == userId && !x.Household.IsArchived)
            .OrderBy(x => x.Household.Name)
            .Select(x => new HouseholdChoice(x.Household.Id, x.Household.Name, x.User.DefaultHouseholdId == x.HouseholdId))
            .ToListAsync(cancellationToken);
    }

    public async Task<CurrentHouseholdContext?> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        var userId = await GetUserIdAsync();
        if (userId is null)
        {
            return null;
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var memberships = db.HouseholdMembers
            .Where(x => x.UserId == userId && !x.Household.IsArchived);

        var chosenId = selectedHouseholdId;
        if (chosenId is null || !await memberships.AnyAsync(x => x.HouseholdId == chosenId, cancellationToken))
        {
            var preferred = await db.Users
                .Where(x => x.Id == userId)
                .Select(x => x.DefaultHouseholdId)
                .SingleOrDefaultAsync(cancellationToken);

            chosenId = preferred is Guid id && await memberships.AnyAsync(x => x.HouseholdId == id, cancellationToken)
                ? id
                : await memberships.OrderBy(x => x.JoinedAt).Select(x => (Guid?)x.HouseholdId).FirstOrDefaultAsync(cancellationToken);

            selectedHouseholdId = chosenId;
        }

        if (chosenId is null)
        {
            return null;
        }

        var household = await memberships
            .Where(x => x.HouseholdId == chosenId)
            .Select(x => new CurrentHouseholdContext(userId, x.HouseholdId, x.Household.Name))
            .SingleOrDefaultAsync(cancellationToken);

        return household;
    }

    public async Task<bool> SwitchAsync(Guid householdId, CancellationToken cancellationToken = default)
    {
        var userId = await GetUserIdAsync();
        if (userId is null)
        {
            return false;
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var allowed = await db.HouseholdMembers.AnyAsync(
            x => x.UserId == userId && x.HouseholdId == householdId && !x.Household.IsArchived,
            cancellationToken);
        if (allowed)
        {
            selectedHouseholdId = householdId;
            CurrentHouseholdChanged?.Invoke();
        }

        return allowed;
    }

    public async Task<bool> MakeDefaultAsync(Guid householdId, CancellationToken cancellationToken = default)
    {
        var userId = await GetUserIdAsync();
        if (userId is null)
        {
            return false;
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var member = await db.HouseholdMembers.AnyAsync(
            x => x.UserId == userId && x.HouseholdId == householdId && !x.Household.IsArchived,
            cancellationToken);
        if (!member)
        {
            return false;
        }

        var user = await db.Users.SingleAsync(x => x.Id == userId, cancellationToken);
        user.DefaultHouseholdId = householdId;
        await db.SaveChangesAsync(cancellationToken);
        selectedHouseholdId = householdId;
        CurrentHouseholdChanged?.Invoke();
        return true;
    }

    public async Task<Guid> CreateAsync(string name, bool makeDefault = false, CancellationToken cancellationToken = default)
    {
        var userId = await GetUserIdAsync() ?? throw new InvalidOperationException("Sign in before creating a household.");
        var normalizedName = name.Trim();
        if (normalizedName.Length is < 2 or > 80)
        {
            throw new ArgumentException("Household names must be between 2 and 80 characters.", nameof(name));
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var household = new Household { Name = normalizedName };
        db.Households.Add(household);
        db.HouseholdMembers.Add(new HouseholdMember { HouseholdId = household.Id, UserId = userId });
        var existingDefault = await db.Users.Where(x => x.Id == userId).Select(x => x.DefaultHouseholdId).SingleAsync(cancellationToken);
        if (makeDefault || existingDefault is null)
        {
            var user = await db.Users.SingleAsync(x => x.Id == userId, cancellationToken);
            user.DefaultHouseholdId = household.Id;
        }

        for (var index = 0; index < DefaultCategories.Length; index++)
        {
            db.Categories.Add(new Category { HouseholdId = household.Id, Name = DefaultCategories[index], SortOrder = index });
        }

        await db.SaveChangesAsync(cancellationToken);
        selectedHouseholdId = household.Id;
        CurrentHouseholdChanged?.Invoke();
        return household.Id;
    }

    public async Task RenameCurrentAsync(string name, CancellationToken cancellationToken = default)
    {
        var current = await GetCurrentAsync(cancellationToken) ?? throw new InvalidOperationException("Choose a household first.");
        var normalizedName = name.Trim();
        if (normalizedName.Length is < 2 or > 80)
        {
            throw new ArgumentException("Household names must be between 2 and 80 characters.", nameof(name));
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var household = await db.Households.SingleAsync(x => x.Id == current.HouseholdId, cancellationToken);
        household.Name = normalizedName;
        await db.SaveChangesAsync(cancellationToken);
        CurrentHouseholdChanged?.Invoke();
    }

    public async Task ArchiveCurrentAsync(CancellationToken cancellationToken = default)
    {
        var current = await GetCurrentAsync(cancellationToken) ?? throw new InvalidOperationException("Choose a household first.");
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var household = await db.Households.SingleAsync(x => x.Id == current.HouseholdId, cancellationToken);
        household.IsArchived = true;

        var users = await db.Users.Where(x => x.DefaultHouseholdId == household.Id).ToListAsync(cancellationToken);
        foreach (var user in users)
        {
            user.DefaultHouseholdId = await db.HouseholdMembers
                .Where(x => x.UserId == user.Id && x.HouseholdId != household.Id && !x.Household.IsArchived)
                .OrderBy(x => x.JoinedAt)
                .Select(x => (Guid?)x.HouseholdId)
                .FirstOrDefaultAsync(cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        selectedHouseholdId = null;
        CurrentHouseholdChanged?.Invoke();
    }

    public async Task<IReadOnlyList<HouseholdChoice>> GetArchivedHouseholdsAsync(CancellationToken cancellationToken = default)
    {
        var userId = await GetUserIdAsync();
        if (userId is null) return [];
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.HouseholdMembers.Where(x => x.UserId == userId && x.Household.IsArchived)
            .OrderBy(x => x.Household.Name)
            .Select(x => new HouseholdChoice(x.HouseholdId, x.Household.Name, false))
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> RestoreHouseholdAsync(Guid householdId, CancellationToken cancellationToken = default)
    {
        var userId = await GetUserIdAsync();
        if (userId is null) return false;
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var household = await db.Households.SingleOrDefaultAsync(x => x.Id == householdId && x.IsArchived, cancellationToken);
        if (household is null || !await db.HouseholdMembers.AnyAsync(x => x.HouseholdId == householdId && x.UserId == userId, cancellationToken))
            return false;

        household.IsArchived = false;
        await db.SaveChangesAsync(cancellationToken);
        CurrentHouseholdChanged?.Invoke();
        return true;
    }

    public async Task<IReadOnlyList<HouseholdCategory>> GetCategoriesAsync(CancellationToken cancellationToken = default)
    {
        var current = await GetCurrentAsync(cancellationToken);
        if (current is null) return [];
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Categories.AsNoTracking().Where(x => x.HouseholdId == current.HouseholdId)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Name)
            .Select(x => new HouseholdCategory(x.Id, x.Name, x.SortOrder)).ToListAsync(cancellationToken);
    }

    public async Task SaveCategoryAsync(Guid? categoryId, string name, CancellationToken cancellationToken = default)
    {
        var current = await GetCurrentAsync(cancellationToken) ?? throw new InvalidOperationException("Choose a household first.");
        var normalizedName = name.Trim();
        if (normalizedName.Length is < 1 or > 60) throw new ArgumentException("Category names must be between 1 and 60 characters.", nameof(name));
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        Category category;
        if (categoryId is Guid id)
        {
            category = await db.Categories.SingleOrDefaultAsync(x => x.Id == id && x.HouseholdId == current.HouseholdId, cancellationToken)
                ?? throw new InvalidOperationException("That category is not in this household.");
        }
        else
        {
            category = new Category
            {
                HouseholdId = current.HouseholdId,
                SortOrder = await db.Categories.CountAsync(x => x.HouseholdId == current.HouseholdId, cancellationToken)
            };
            db.Categories.Add(category);
        }
        category.Name = normalizedName;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default)
    {
        var current = await GetCurrentAsync(cancellationToken) ?? throw new InvalidOperationException("Choose a household first.");
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var category = await db.Categories.SingleOrDefaultAsync(x => x.Id == categoryId && x.HouseholdId == current.HouseholdId, cancellationToken)
            ?? throw new InvalidOperationException("That category is not in this household.");
        var items = await db.CatalogItems.Where(x => x.HouseholdId == current.HouseholdId && x.CategoryId == categoryId).ToListAsync(cancellationToken);
        foreach (var item in items) item.CategoryId = null;
        db.Categories.Remove(category);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task MoveCategoryAsync(Guid categoryId, int direction, CancellationToken cancellationToken = default)
    {
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        var current = await GetCurrentAsync(cancellationToken) ?? throw new InvalidOperationException("Choose a household first.");
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var categories = await db.Categories.Where(x => x.HouseholdId == current.HouseholdId)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Name).ToListAsync(cancellationToken);
        var index = categories.FindIndex(x => x.Id == categoryId);
        var otherIndex = index + direction;
        if (index < 0 || otherIndex < 0 || otherIndex >= categories.Count) return;
        (categories[index], categories[otherIndex]) = (categories[otherIndex], categories[index]);
        for (var i = 0; i < categories.Count; i++) categories[i].SortOrder = i;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> GetMembersAsync(Guid householdId, CancellationToken cancellationToken = default)
    {
        var userId = await GetUserIdAsync();
        if (userId is null)
        {
            return [];
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var isMember = await db.HouseholdMembers.AnyAsync(x => x.UserId == userId && x.HouseholdId == householdId, cancellationToken);
        if (!isMember)
        {
            return [];
        }

        return await db.HouseholdMembers
            .Where(x => x.HouseholdId == householdId)
            .OrderBy(x => x.User.DisplayName)
            .Select(x => x.User.DisplayName.Length == 0 ? x.User.UserName! : x.User.DisplayName)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<HouseholdMemberChoice>> GetMemberChoicesAsync(CancellationToken cancellationToken = default)
    {
        var current = await GetCurrentAsync(cancellationToken);
        if (current is null) return [];
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.HouseholdMembers.Where(x => x.HouseholdId == current.HouseholdId)
            .OrderBy(x => x.User.DisplayName).Select(x => new HouseholdMemberChoice(
                x.UserId,
                x.User.DisplayName.Length == 0 ? x.User.UserName! : x.User.DisplayName,
                x.User.UserName!))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> GetAvailableUserNamesAsync(CancellationToken cancellationToken = default)
    {
        var current = await GetCurrentAsync(cancellationToken);
        if (current is null) return [];
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var memberIds = db.HouseholdMembers.Where(x => x.HouseholdId == current.HouseholdId).Select(x => x.UserId);
        return await db.Users.Where(x => !memberIds.Contains(x.Id)).OrderBy(x => x.UserName)
            .Select(x => x.UserName!).ToListAsync(cancellationToken);
    }

    public async Task AddMemberAsync(string userName, CancellationToken cancellationToken = default)
    {
        var current = await GetCurrentAsync(cancellationToken) ?? throw new InvalidOperationException("Choose a household first.");
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var user = await db.Users.SingleOrDefaultAsync(x => x.UserName == userName.Trim(), cancellationToken)
            ?? throw new InvalidOperationException("That YALA username does not exist.");
        if (await db.HouseholdMembers.AnyAsync(x => x.UserId == user.Id && x.HouseholdId == current.HouseholdId, cancellationToken))
        {
            throw new InvalidOperationException("That person already belongs to this household.");
        }

        db.HouseholdMembers.Add(new HouseholdMember { HouseholdId = current.HouseholdId, UserId = user.Id });
        await db.SaveChangesAsync(cancellationToken);
        CurrentHouseholdChanged?.Invoke();
    }

    public async Task RemoveMemberAsync(string userIdToRemove, CancellationToken cancellationToken = default)
    {
        var current = await GetCurrentAsync(cancellationToken) ?? throw new InvalidOperationException("Choose a household first.");
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var members = await db.HouseholdMembers.Where(x => x.HouseholdId == current.HouseholdId).ToListAsync(cancellationToken);
        var membership = members.SingleOrDefault(x => x.UserId == userIdToRemove)
            ?? throw new InvalidOperationException("That person is not in this household.");
        if (members.Count <= 1)
        {
            throw new InvalidOperationException("A household needs at least one member.");
        }

        db.HouseholdMembers.Remove(membership);
        var user = await db.Users.SingleAsync(x => x.Id == userIdToRemove, cancellationToken);
        if (user.DefaultHouseholdId == current.HouseholdId)
        {
            var nextMembership = await db.HouseholdMembers.Where(x => x.UserId == userIdToRemove && x.HouseholdId != current.HouseholdId && !x.Household.IsArchived)
                .Select(x => (Guid?)x.HouseholdId).FirstOrDefaultAsync(cancellationToken);
            user.DefaultHouseholdId = nextMembership;
        }

        await db.SaveChangesAsync(cancellationToken);
        CurrentHouseholdChanged?.Invoke();
    }

    public static readonly string[] DefaultCategories =
    ["Produce", "Dairy", "Meat", "Frozen", "Pantry", "Bakery", "Drinks", "Pets", "Household", "Personal Care", "Other"];
}
