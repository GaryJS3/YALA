using Microsoft.AspNetCore.Identity;

namespace YALA.Data;

// Add profile data for application users by adding properties to the ApplicationUser class
public class ApplicationUser : IdentityUser
{
    public string DisplayName { get; set; } = string.Empty;

    public Guid? DefaultHouseholdId { get; set; }
}
