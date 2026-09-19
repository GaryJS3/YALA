using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using YALA.Data;
using YALA.Data.Entities;

namespace YALA.Services;

public sealed record ManagedUser(
    string Id,
    string UserName,
    string DisplayName,
    bool IsAdministrator,
    IReadOnlyList<string> Households);

public sealed class AdministratorRequirement : IAuthorizationRequirement;

public sealed class AdministratorAuthorizationHandler(IDbContextFactory<ApplicationDbContext> dbFactory)
    : AuthorizationHandler<AdministratorRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AdministratorRequirement requirement)
    {
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return;

        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.Users.AnyAsync(x => x.Id == userId && x.IsAdministrator))
        {
            context.Succeed(requirement);
        }
    }
}

public sealed class UserAdministrationService(
    UserManager<ApplicationUser> userManager,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    AuthenticationStateProvider authenticationStateProvider)
{
    public const string AdministratorPolicy = "Administrator";

    public async Task<IReadOnlyList<ManagedUser>> GetUsersAsync(CancellationToken cancellationToken = default)
    {
        await RequireAdministratorAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var users = await db.Users.AsNoTracking()
            .OrderBy(x => x.DisplayName).ThenBy(x => x.UserName)
            .Select(x => new { x.Id, x.UserName, x.DisplayName, x.IsAdministrator })
            .ToListAsync(cancellationToken);
        var memberships = await db.HouseholdMembers.AsNoTracking()
            .OrderBy(x => x.Household.Name)
            .Select(x => new { x.UserId, x.Household.Name })
            .ToListAsync(cancellationToken);
        return users.Select(x => new ManagedUser(
            x.Id,
            x.UserName ?? string.Empty,
            x.DisplayName,
            x.IsAdministrator,
            memberships.Where(m => m.UserId == x.Id).Select(m => m.Name).ToList()))
            .ToList();
    }

    public async Task CreateUserAsync(
        string displayName,
        string userName,
        string password,
        Guid? householdId,
        CancellationToken cancellationToken = default)
    {
        await RequireAdministratorAsync(cancellationToken);
        var normalizedDisplayName = displayName.Trim();
        var normalizedUserName = userName.Trim();
        if (normalizedDisplayName.Length is < 1 or > 80)
            throw new ArgumentException("Display names must be between 1 and 80 characters.", nameof(displayName));
        if (normalizedUserName.Length is < 2 or > 40)
            throw new ArgumentException("Usernames must be between 2 and 40 characters.", nameof(userName));

        var user = new ApplicationUser { DisplayName = normalizedDisplayName, UserName = normalizedUserName };
        var result = await userManager.CreateAsync(user, password);
        if (!result.Succeeded) throw new InvalidOperationException(Describe(result));

        if (householdId is null) return;
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var householdExists = await db.Households.AnyAsync(x => x.Id == householdId && !x.IsArchived, cancellationToken);
            if (!householdExists) throw new InvalidOperationException("The selected household is unavailable.");
            db.HouseholdMembers.Add(new HouseholdMember { HouseholdId = householdId.Value, UserId = user.Id });
            var storedUser = await db.Users.SingleAsync(x => x.Id == user.Id, cancellationToken);
            storedUser.DefaultHouseholdId = householdId;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await userManager.DeleteAsync(user);
            throw;
        }
    }

    public async Task ResetPasswordAsync(string userId, string password, CancellationToken cancellationToken = default)
    {
        await RequireAdministratorAsync(cancellationToken);
        var user = await userManager.FindByIdAsync(userId) ?? throw new InvalidOperationException("That user no longer exists.");
        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        var result = await userManager.ResetPasswordAsync(user, token, password);
        if (!result.Succeeded) throw new InvalidOperationException(Describe(result));
        await userManager.UpdateSecurityStampAsync(user);
    }

    public async Task DeleteUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        var administratorId = await RequireAdministratorAsync(cancellationToken);
        if (userId == administratorId) throw new InvalidOperationException("You cannot delete your own account.");

        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId, cancellationToken)
                ?? throw new InvalidOperationException("That user no longer exists.");
            if (user.IsAdministrator && await db.Users.CountAsync(x => x.IsAdministrator, cancellationToken) <= 1)
                throw new InvalidOperationException("The last administrator cannot be deleted.");

            var soleHouseholds = await db.HouseholdMembers
                .Where(x => x.UserId == userId && x.Household.Members.Count == 1)
                .Select(x => x.Household.Name)
                .ToListAsync(cancellationToken);
            if (soleHouseholds.Count > 0)
                throw new InvalidOperationException($"This user is the only member of: {string.Join(", ", soleHouseholds)}. Add another member before deleting the account.");
        }

        var identityUser = await userManager.FindByIdAsync(userId) ?? throw new InvalidOperationException("That user no longer exists.");
        var result = await userManager.DeleteAsync(identityUser);
        if (!result.Succeeded) throw new InvalidOperationException(Describe(result));
    }

    private async Task<string> RequireAdministratorAsync(CancellationToken cancellationToken)
    {
        var state = await authenticationStateProvider.GetAuthenticationStateAsync();
        var userId = state.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new UnauthorizedAccessException("Sign in to manage users.");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.Users.AnyAsync(x => x.Id == userId && x.IsAdministrator, cancellationToken))
            throw new UnauthorizedAccessException("Administrator access is required.");
        return userId;
    }

    private static string Describe(IdentityResult result) => string.Join(" ", result.Errors.Select(x => x.Description));
}
