namespace YALA.Data.Entities;

public sealed class ShoppingListOperation
{
    public Guid HouseholdId { get; set; }
    public string UserId { get; set; } = "";
    public Guid Id { get; set; }
    public string Action { get; set; } = "";
    public string Result { get; set; } = "";
}
