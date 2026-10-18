using Microsoft.JSInterop;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Components.Forms;
using YALA.Client.Models;

namespace YALA.Client.Services;

public sealed class ApiClient(HttpClient http, ClientState state, IJSRuntime js)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<SessionSnapshot?> GetSessionAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendCoreAsync(HttpMethod.Get, "api/session", null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized) return null;
        response.EnsureSuccessStatusCode();
        var session = await response.Content.ReadFromJsonAsync<SessionSnapshot>(JsonOptions, cancellationToken);
        state.SetUser(session?.UserId);
        state.SetHousehold(session?.Households.Current);
        if (session?.Households.Current is { } current)
            await js.InvokeVoidAsync("sessionStorage.setItem", "yala.household", current.Id.ToString());
        return session;
    }

    public async Task<ListSnapshot> GetListAsync(CancellationToken cancellationToken = default) =>
        await GetAsync<ListSnapshot>("api/list", cancellationToken) ?? new();

    public async Task<IReadOnlyList<QuickAddChoice>> GetSuggestionsAsync(string? query, CancellationToken cancellationToken = default) =>
        await GetAsync<List<QuickAddChoice>>($"api/list/suggestions?query={Uri.EscapeDataString(query ?? string.Empty)}", cancellationToken) ?? [];

    public Task AddItemAsync(Guid catalogItemId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "api/list/add", new ListItemCommand(catalogItemId), cancellationToken);

    public Task<Guid?> QuickAddAsync(string name, CancellationToken cancellationToken = default) =>
        SendForAsync<Guid?>(HttpMethod.Post, "api/list/quick-add", new QuickAddCommand(name), cancellationToken);

    public Task ToggleAsync(Guid id, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "api/list/toggle", new ListRowCommand(id), cancellationToken);

    public Task ChangeQuantityAsync(Guid id, decimal delta, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "api/list/quantity", new QuantityCommand(id, delta), cancellationToken);

    public Task RemoveAsync(Guid id, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "api/list/remove", new ListRowCommand(id), cancellationToken);

    public Task AssignStoreAsync(Guid id, Guid? storeId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "api/list/assign-store", new AssignStoreCommand(id, storeId), cancellationToken);

    public Task ClearCheckedAsync(CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "api/list/clear-checked", null, cancellationToken);

    public async Task SendQueuedAsync(QueuedMutation mutation, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(new HttpMethod(mutation.Method), mutation.Uri);
        request.Headers.TryAddWithoutValidation("X-Yala-Household", mutation.HouseholdId.ToString());
        request.Headers.Add("X-Yala-User", mutation.UserId);
        if (mutation.UserId != state.UserId) throw new InvalidOperationException("This change belongs to a different user.");
        if (!string.IsNullOrWhiteSpace(mutation.BodyJson))
        {
            request.Content = new StringContent(mutation.BodyJson);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<CatalogSnapshot> GetCatalogAsync(string? query, bool includeArchived, CancellationToken cancellationToken = default) =>
        await GetAsync<CatalogSnapshot>($"api/catalog?query={Uri.EscapeDataString(query ?? string.Empty)}&includeArchived={includeArchived}", cancellationToken) ?? new();

    public async Task<CatalogDetails?> GetCatalogDetailsAsync(Guid? id, CancellationToken cancellationToken = default) =>
        await GetAsync<CatalogDetails>(id is null ? "api/catalog/details" : $"api/catalog/details/{id}", cancellationToken);

    public async Task<CatalogFormOptions> GetCatalogFormOptionsAsync(CancellationToken cancellationToken = default) =>
        await GetAsync<CatalogFormOptions>("api/catalog/details", cancellationToken) ?? new();

    public Task<Guid?> SaveCatalogItemAsync(CatalogSaveRequest request, CancellationToken cancellationToken = default) =>
        SendForAsync<Guid?>(HttpMethod.Post, "api/catalog/save", request, cancellationToken);

    public Task SetCatalogFavoriteAsync(Guid id, bool favorite, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "api/catalog/favorite", new CatalogFlagRequest(id, favorite), cancellationToken);

    public Task SetCatalogArchivedAsync(Guid id, bool archived, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "api/catalog/archive", new CatalogFlagRequest(id, archived), cancellationToken);

    public Task AddAliasAsync(Guid itemId, string alias, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "api/catalog/aliases", new AliasCommand(itemId, alias), cancellationToken);

    public Task RemoveAliasAsync(Guid aliasId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"api/catalog/aliases/{aliasId}", null, cancellationToken);

    public Task<Guid?> SaveVariantAsync(VariantSaveRequest request, CancellationToken cancellationToken = default) =>
        SendForAsync<Guid?>(HttpMethod.Post, "api/catalog/variants", request, cancellationToken);

    public Task RemoveVariantAsync(Guid id, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"api/catalog/variants/{id}", null, cancellationToken);

    public Task SetVariantStoreAvailabilityAsync(Guid variantId, Guid storeId, bool available, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "api/catalog/variants/store", new VariantStoreCommand(variantId, storeId, available), cancellationToken);

    public Task SetItemStoreAvailabilityAsync(Guid itemId, Guid storeId, bool available, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "api/catalog/store", new ItemStoreCommand(itemId, storeId, available), cancellationToken);

    public async Task<ProductLookupResult?> LookupProductAsync(string barcode, CancellationToken cancellationToken = default) =>
        await GetAsync<ProductLookupResult>($"api/products/lookup/{Uri.EscapeDataString(barcode)}", cancellationToken);

    public async Task UploadItemImageAsync(Guid id, IBrowserFile file, CancellationToken cancellationToken = default)
    {
        var antiforgery = await GetAsync<AntiforgeryResponse>("api/antiforgery", cancellationToken);
        using var content = new MultipartFormDataContent();
        await using var stream = file.OpenReadStream(4 * 1024 * 1024, cancellationToken);
        using var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(file.ContentType);
        content.Add(fileContent, "file", file.Name);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/images/items/{id}") { Content = content };
        await AddHouseholdHeaderAsync(request);
        if (!string.IsNullOrWhiteSpace(antiforgery?.Token)) request.Headers.TryAddWithoutValidation("RequestVerificationToken", antiforgery.Token);
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task UploadStoreImageAsync(Guid id, IBrowserFile file, CancellationToken cancellationToken = default)
    {
        var antiforgery = await GetAsync<AntiforgeryResponse>("api/antiforgery", cancellationToken);
        using var content = new MultipartFormDataContent();
        await using var stream = file.OpenReadStream(4 * 1024 * 1024, cancellationToken);
        using var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(file.ContentType);
        content.Add(fileContent, "file", file.Name);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/images/stores/{id}") { Content = content };
        await AddHouseholdHeaderAsync(request);
        if (!string.IsNullOrWhiteSpace(antiforgery?.Token)) request.Headers.TryAddWithoutValidation("RequestVerificationToken", antiforgery.Token);
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public Task<Guid?> SendPromotionAsync(Guid shoppingListItemId, CancellationToken cancellationToken = default) =>
        SendForAsync<Guid?>(HttpMethod.Post, "api/catalog/promote", new PromoteCommand(shoppingListItemId), cancellationToken);

    public Task DeleteAdHocAsync(Guid shoppingListItemId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"api/catalog/ad-hoc/{shoppingListItemId}", null, cancellationToken);

    public async Task<IReadOnlyList<StoreSummary>> GetStoresAsync(CancellationToken cancellationToken = default) =>
        await GetAsync<List<StoreSummary>>("api/stores", cancellationToken) ?? [];

    public async Task<StoreDetails?> GetStoreDetailsAsync(Guid id, CancellationToken cancellationToken = default) =>
        await GetAsync<StoreDetails>($"api/stores/{id}", cancellationToken);

    public Task SaveStoreAsync(Guid? id, string name, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "api/stores/save", new StoreCommand(id, name), cancellationToken);

    public Task<BuildInfo?> GetBuildInfoAsync(CancellationToken cancellationToken = default) =>
        GetAsync<BuildInfo>("api/version", cancellationToken);

    public Task SetStoreActiveAsync(Guid id, bool active, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "api/stores/active", new StoreActiveCommand(id, active), cancellationToken);

    public Task MoveStoreAsync(Guid id, int direction, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "api/stores/move", new StoreMoveCommand(id, direction), cancellationToken);

    public Task SetPreferredOfferAsync(Guid offerId, bool preferred, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "api/stores/preferred", new PreferredCommand(offerId, preferred), cancellationToken);

    public Task RemoveOfferAsync(Guid offerId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"api/stores/offers/{offerId}", null, cancellationToken);

    public Task SaveOfferAsync(StoreOfferSaveRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "api/stores/offers", request, cancellationToken);

    public Task UpdateOfferAsync(StoreOfferUpdateRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "api/stores/offers/update", request, cancellationToken);

    public async Task<HouseholdSnapshot> GetHouseholdsAsync(CancellationToken cancellationToken = default) =>
        await GetAsync<HouseholdSnapshot>("api/households", cancellationToken) ?? new();

    public async Task SwitchHouseholdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await SendAsync(HttpMethod.Post, "api/households/switch", new HouseholdCommand(id), cancellationToken);
        await js.InvokeVoidAsync("sessionStorage.setItem", "yala.household", id.ToString());
        state.SetHousehold(new HouseholdChoice { Id = id });
    }

    public Task MakeDefaultHouseholdAsync(Guid id, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "api/households/default", new HouseholdCommand(id), cancellationToken);

    public Task<Guid?> CreateHouseholdAsync(string name, bool makeDefault, CancellationToken cancellationToken = default) =>
        SendForAsync<Guid?>(HttpMethod.Post, "api/households/create", new HouseholdCreateCommand(name, makeDefault), cancellationToken);

    public Task RenameHouseholdAsync(string name, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "api/households/rename", new HouseholdNameCommand(name), cancellationToken);

    public Task ArchiveHouseholdAsync(CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "api/households/archive", null, cancellationToken);

    public async Task<HouseholdSettings> GetHouseholdSettingsAsync(CancellationToken cancellationToken = default) =>
        await GetAsync<HouseholdSettings>("api/households/settings", cancellationToken) ?? new();
    public async Task<IReadOnlyList<HouseholdChoice>> GetArchivedHouseholdsAsync(CancellationToken cancellationToken = default) =>
        await GetAsync<List<HouseholdChoice>>("api/households/archived", cancellationToken) ?? [];
    public Task RestoreHouseholdAsync(Guid id) => SendAsync(HttpMethod.Post, "api/households/restore", new HouseholdCommand(id), default);
    public Task AddMemberAsync(string userName) => SendAsync(HttpMethod.Post, "api/households/members", new MemberCommand(userName), default);
    public Task RemoveMemberAsync(string userId) => SendAsync(HttpMethod.Delete, $"api/households/members/{Uri.EscapeDataString(userId)}", null, default);
    public Task SaveCategoryAsync(Guid? id, string name) => SendAsync(HttpMethod.Post, "api/households/categories", new CategoryCommand(id, name), default);
    public Task DeleteCategoryAsync(Guid id) => SendAsync(HttpMethod.Delete, $"api/households/categories/{id}", null, default);
    public Task MoveCategoryAsync(Guid id, int direction) => SendAsync(HttpMethod.Post, "api/households/categories/move", new CategoryMoveCommand(id, direction), default);

    public async Task<IReadOnlyList<ManagedUser>> GetUsersAsync(CancellationToken cancellationToken = default) =>
        await GetAsync<List<ManagedUser>>("api/users", cancellationToken) ?? [];

    public Task CreateUserAsync(UserCreateRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "api/users", request, cancellationToken);

    public Task ResetPasswordAsync(string userId, string password, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "api/users/reset-password", new UserPasswordCommand(userId, password), cancellationToken);

    public Task DeleteUserAsync(string userId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"api/users/{Uri.EscapeDataString(userId)}", null, cancellationToken);

    private async Task<T?> GetAsync<T>(string uri, CancellationToken cancellationToken)
    {
        using var response = await SendCoreAsync(HttpMethod.Get, uri, null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
    }

    private async Task SendAsync(HttpMethod method, string uri, object? body, CancellationToken cancellationToken)
    {
        using var response = await SendCoreAsync(method, uri, body, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task<T?> SendForAsync<T>(HttpMethod method, string uri, object? body, CancellationToken cancellationToken)
    {
        using var response = await SendCoreAsync(method, uri, body, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendCoreAsync(HttpMethod method, string uri, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri);
        await AddHouseholdHeaderAsync(request);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);
        return await http.SendAsync(request, cancellationToken);
    }

    private async Task AddHouseholdHeaderAsync(HttpRequestMessage request)
    {
        if (state.CurrentHouseholdId is null)
        {
            var stored = await js.InvokeAsync<string?>("sessionStorage.getItem", "yala.household");
            if (Guid.TryParse(stored, out var id)) state.SetHousehold(new HouseholdChoice { Id = id });
        }
        if (state.CurrentHouseholdId is Guid householdId)
            request.Headers.Add("X-Yala-Household", householdId.ToString());
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var message = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new ApiException(response.StatusCode, string.IsNullOrWhiteSpace(message) ? response.ReasonPhrase ?? "Request failed." : message);
    }
}

public sealed class AntiforgeryResponse { public string? Token { get; set; } }

public sealed class ApiException(HttpStatusCode statusCode, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}
