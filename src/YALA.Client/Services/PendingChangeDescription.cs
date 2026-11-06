using System.Text.Json;
using YALA.Client.Models;

namespace YALA.Client.Services;

public static class PendingChangeDescription
{
    public static string Describe(QueuedMutation mutation, IReadOnlyList<ShoppingListRow> rows)
    {
        try
        {
            var json = mutation.BodyJson ?? "{}";
            var rowId = JsonSerializer.Deserialize<ListRowCommand>(json)?.Id;
            var name = rows.FirstOrDefault(row => row.Id == rowId)?.Name ?? "item";
            return mutation.Uri switch
            {
                "api/list/quick-add" => $"Add {JsonSerializer.Deserialize<QuickAddCommand>(json)?.Name ?? "item"}",
                "api/list/add" => "Add a catalog item",
                "api/list/toggle" => $"Change checked state of {name}",
                "api/list/quantity" => $"Change quantity of {name}",
                "api/list/assign-store" => $"Change store for {name}",
                "api/list/remove" => $"Remove {name}",
                "api/list/clear-checked" => "Clear checked items",
                _ => "Update list"
            };
        }
        catch (JsonException) { return "Update list"; }
    }
}
