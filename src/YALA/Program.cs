using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using YALA.Components;
using YALA.Components.Account;
using YALA.Data;
using YALA.Data.Entities;
using YALA.Services;
using System.Security.Claims;
using System.Security.Cryptography;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();

var dataDirectory = Environment.GetEnvironmentVariable("YALA_DATA_DIR");
if (string.IsNullOrWhiteSpace(dataDirectory))
{
    var localDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    dataDirectory = string.IsNullOrWhiteSpace(localDataRoot)
        ? Path.Combine(builder.Environment.ContentRootPath, "Data")
        : Path.Combine(localDataRoot, "YALA");
}
dataDirectory = Path.GetFullPath(dataDirectory);
Directory.CreateDirectory(dataDirectory);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? $"Data Source={Path.Combine(dataDirectory, "yala.db")};Cache=Shared";
connectionString = connectionString.Replace("{DataDirectory}", dataDirectory, StringComparison.OrdinalIgnoreCase);

Directory.CreateDirectory(Path.Combine(dataDirectory, "keys"));
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")))
    .SetApplicationName("YALA");

builder.Services.AddDbContextFactory<ApplicationDbContext>(options =>
    options.UseSqlite(connectionString));
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

builder.Services.AddScoped<HouseholdService>();
builder.Services.AddScoped<ShoppingListService>();
builder.Services.AddScoped<CatalogService>();
builder.Services.AddScoped<StoreService>();
builder.Services.AddScoped<ImageService>();
builder.Services.AddSingleton<ShoppingListChangeNotifier>();

builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.SignIn.RequireConfirmedAccount = false;
        options.Password.RequiredLength = 8;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequireUppercase = false;
        options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
    })
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
    options.SlidingExpiration = true;
});

builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityNoOpEmailSender>();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.MigrateAsync();
}

// Temporary, deployment-gated troubleshooting access. Remove this block and its
// compose flag as soon as the live click behavior has been verified.
if (app.Environment.IsProduction()
    && string.Equals(Environment.GetEnvironmentVariable("YALA_TEMP_TEST_ACCOUNT"), "true", StringComparison.OrdinalIgnoreCase))
{
    await using var scope = app.Services.CreateAsyncScope();
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    var household = await db.Households.Where(x => !x.IsArchived).OrderBy(x => x.CreatedAt).FirstOrDefaultAsync();
    if (household is null)
        throw new InvalidOperationException("Cannot create temporary troubleshooting account: no active household exists.");

    const string userName = "codex-debug";
    if (await userManager.FindByNameAsync(userName) is null)
    {
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var user = new ApplicationUser { UserName = userName, DisplayName = "Temporary Codex Troubleshooting" };
        var createResult = await userManager.CreateAsync(user, password);
        if (!createResult.Succeeded)
            throw new InvalidOperationException($"Cannot create temporary troubleshooting account: {string.Join(" ", createResult.Errors.Select(x => x.Description))}");

        try
        {
            user.DefaultHouseholdId = household.Id;
            var updateResult = await userManager.UpdateAsync(user);
            if (!updateResult.Succeeded)
                throw new InvalidOperationException($"Cannot assign default household: {string.Join(" ", updateResult.Errors.Select(x => x.Description))}");

            db.HouseholdMembers.Add(new HouseholdMember { HouseholdId = household.Id, UserId = user.Id });
            await db.SaveChangesAsync();
        }
        catch
        {
            await userManager.DeleteAsync(user);
            throw;
        }

        app.Logger.LogWarning("Temporary troubleshooting login created for household {HouseholdName}. Username: {UserName}; one-time password: {Password}", household.Name, userName, password);
    }
    else
    {
        app.Logger.LogWarning("Temporary troubleshooting login already exists; no new password was generated.");
    }
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "YALA" })).AllowAnonymous();
app.MapGet("/images/households/{householdId:guid}/{kind}/{fileName}", async (
    Guid householdId,
    string kind,
    string fileName,
    ClaimsPrincipal user,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    CancellationToken cancellationToken) =>
{
    var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
    if (userId is null || kind is not ("items" or "variants") || string.IsNullOrWhiteSpace(fileName) || fileName != Path.GetFileName(fileName))
    {
        return Results.NotFound();
    }

    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    var isMember = await db.HouseholdMembers.AnyAsync(x => x.UserId == userId && x.HouseholdId == householdId && !x.Household.IsArchived, cancellationToken);
    if (!isMember) return Results.NotFound();

    var imageDirectory = Path.Combine(dataDirectory, "images", "households", householdId.ToString("D"), kind);
    var path = Path.GetFullPath(Path.Combine(imageDirectory, fileName));
    if (!path.StartsWith(Path.GetFullPath(imageDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
        return Results.NotFound();

    var contentType = Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => null
    };
    if (contentType is null) return Results.NotFound();
    return Results.File(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read), contentType, enableRangeProcessing: true);
}).RequireAuthorization();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Add additional endpoints required by the Identity /Account Razor components.
app.MapAdditionalIdentityEndpoints();

app.Run();
