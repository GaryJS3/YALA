using YALA.Data;

namespace YALA.Data.Entities;

public sealed class Household
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool IsArchived { get; set; }
    public List<HouseholdMember> Members { get; set; } = [];
}

public sealed class HouseholdMember
{
    public Guid HouseholdId { get; set; }
    public string UserId { get; set; } = string.Empty;
    public DateTimeOffset JoinedAt { get; set; } = DateTimeOffset.UtcNow;
    public Household Household { get; set; } = null!;
    public ApplicationUser User { get; set; } = null!;
}
