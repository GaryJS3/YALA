using YALA.Client.Models;

namespace YALA.Client.Services;

public sealed class ClientState
{
    public Guid? CurrentHouseholdId { get; private set; }
    public string? CurrentHouseholdName { get; private set; }
    public bool IsOffline { get; private set; }
    public int PendingChanges { get; private set; }

    public event Action? Changed;

    public void SetHousehold(HouseholdChoice? household)
    {
        CurrentHouseholdId = household?.Id;
        CurrentHouseholdName = household?.Name;
        Changed?.Invoke();
    }

    public void SetConnectivity(bool offline, int pendingChanges = 0)
    {
        IsOffline = offline;
        PendingChanges = pendingChanges;
        Changed?.Invoke();
    }
}
