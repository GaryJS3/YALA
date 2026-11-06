using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using YALA.Data;

namespace YALA.Services;

// Both administrator and self-service deletion must preserve account/household access.
public sealed class AccountDeletionService(ApplicationDbContext db, UserManager<ApplicationUser> users)
{
    public async Task DeleteAsync(string userId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var user = await users.FindByIdAsync(userId)
            ?? throw new InvalidOperationException("That user no longer exists.");
        if (user.IsAdministrator && await db.Users.CountAsync(x => x.IsAdministrator, cancellationToken) <= 1)
            throw new InvalidOperationException("The last administrator cannot be deleted.");

        var soleHouseholds = await db.HouseholdMembers
            .Where(x => x.UserId == userId && x.Household.Members.Count == 1)
            .Select(x => x.Household.Name).ToListAsync(cancellationToken);
        if (soleHouseholds.Count > 0)
            throw new InvalidOperationException($"This user is the only member of: {string.Join(", ", soleHouseholds)}. Add another member before deleting the account.");

        var result = await users.DeleteAsync(user);
        if (!result.Succeeded)
            throw new InvalidOperationException(string.Join(" ", result.Errors.Select(x => x.Description)));
        await transaction.CommitAsync(cancellationToken);
    }
}
