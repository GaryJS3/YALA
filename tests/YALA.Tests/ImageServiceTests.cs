using System.Security.Claims;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using YALA.Data;
using YALA.Data.Entities;
using YALA.Services;

namespace YALA.Tests;

public sealed class ImageServiceTests
{
    [Fact]
    public async Task VariantImageIsPromotedOnceAndLaterVariantsDoNotReplaceIt()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (_, item) = await fixture.SeedHouseholdAsync();
        var householdService = fixture.CreateHouseholdService();
        var catalogService = fixture.CreateCatalogService(householdService);
        var imageService = fixture.CreateImageService(householdService);
        var dataDirectory = fixture.DataDirectory;

        var firstVariantId = await catalogService.SaveVariantAsync(item.Id, "First product", null, null, true);
        var firstImagePath = await fixture.AddVariantImagePathAsync(firstVariantId, "first.png", [137, 80, 78, 71, 13, 10, 26, 10]);

        Assert.True(await imageService.PromoteBestVariantImageToItemIfMissingAsync(item.Id));
        var storedItem = await fixture.ReadItemAsync(item.Id);
        Assert.NotNull(storedItem.ImagePath);
        Assert.StartsWith($"households/{item.HouseholdId:D}/items/", storedItem.ImagePath);
        Assert.True(File.Exists(Path.Combine(dataDirectory, "images", storedItem.ImagePath!.Replace('/', Path.DirectorySeparatorChar))));

        var secondVariantId = await catalogService.SaveVariantAsync(item.Id, "Second product", null, null, true);
        await fixture.AddVariantImagePathAsync(secondVariantId, "second.png", [137, 80, 78, 71, 13, 10, 26, 10]);

        Assert.False(await imageService.PromoteBestVariantImageToItemIfMissingAsync(item.Id));
        Assert.Equal(storedItem.ImagePath, (await fixture.ReadItemAsync(item.Id)).ImagePath);
        Assert.StartsWith($"households/{item.HouseholdId:D}/variants/", firstImagePath);
    }

    private sealed class Fixture(SqliteConnection connection, DbContextOptions<ApplicationDbContext> options, string dataDirectory) : IAsyncDisposable
    {
        public string DataDirectory => dataDirectory;

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
            var dataDirectory = Directory.CreateTempSubdirectory("yala-image-test-").FullName;
            var fixture = new Fixture(connection, options, dataDirectory);
            await using var db = fixture.CreateDbContext();
            await db.Database.EnsureCreatedAsync();
            db.Users.Add(new ApplicationUser { Id = "gary", UserName = "gary", NormalizedUserName = "GARY", DisplayName = "Gary" });
            await db.SaveChangesAsync();
            return fixture;
        }

        public ApplicationDbContext CreateDbContext() => new(options);
        public HouseholdService CreateHouseholdService() => new(new TestAuthenticationStateProvider("gary"), new TestDbContextFactory(options));
        public CatalogService CreateCatalogService(HouseholdService householdService) => new(new TestDbContextFactory(options), householdService, new ShoppingListChangeNotifier());
        public ImageService CreateImageService(HouseholdService householdService) =>
            new(new TestDbContextFactory(options), householdService, new ShoppingListChangeNotifier(), new TestHttpClientFactory(),
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["YALA_DATA_DIR"] = dataDirectory }).Build(),
                new TestWebHostEnvironment { ContentRootPath = dataDirectory });

        public async Task<(Household Household, CatalogItem Item)> SeedHouseholdAsync()
        {
            var household = new Household { Name = "GJ's House" };
            var item = new CatalogItem { HouseholdId = household.Id, Name = "Milk" };
            await using var db = CreateDbContext();
            db.Households.Add(household);
            db.HouseholdMembers.Add(new HouseholdMember { HouseholdId = household.Id, UserId = "gary" });
            db.CatalogItems.Add(item);
            await db.SaveChangesAsync();
            var user = await db.Users.SingleAsync(x => x.Id == "gary");
            user.DefaultHouseholdId = household.Id;
            await db.SaveChangesAsync();
            return (household, item);
        }

        public async Task<string> AddVariantImagePathAsync(Guid variantId, string fileName, byte[] content)
        {
            await using var db = CreateDbContext();
            var variant = await db.ProductVariants.SingleAsync(x => x.Id == variantId);
            var relativePath = $"households/{variant.HouseholdId:D}/variants/{fileName}";
            var absolutePath = Path.Combine(dataDirectory, "images", relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
            await File.WriteAllBytesAsync(absolutePath, content);
            variant.ImagePath = relativePath;
            await db.SaveChangesAsync();
            return relativePath;
        }

        public async Task<CatalogItem> ReadItemAsync(Guid itemId)
        {
            await using var db = CreateDbContext();
            return await db.CatalogItems.AsNoTracking().SingleAsync(x => x.Id == itemId);
        }

        public async ValueTask DisposeAsync()
        {
            await connection.DisposeAsync();
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => new(options);
        public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ApplicationDbContext(options));
    }

    private sealed class TestAuthenticationStateProvider(string userId) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userId), new Claim(ClaimTypes.Name, userId)], "Test"))));
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "YALA.Tests";
        public string EnvironmentName { get; set; } = Environments.Development;
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
