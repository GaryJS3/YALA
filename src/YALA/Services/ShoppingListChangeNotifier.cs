namespace YALA.Services;

public sealed class ShoppingListChangeNotifier
{
    public event Action<Guid>? Changed;

    public void Notify(Guid householdId) => Changed?.Invoke(householdId);
}
