using Microsoft.AspNetCore.Components.Forms;
using Microsoft.EntityFrameworkCore;
using YALA.Data;

namespace YALA.Services;

public sealed class ImageService(
    IDbContextFactory<ApplicationDbContext> dbContextFactory,
    HouseholdService householdService,
    ShoppingListChangeNotifier notifier,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    IWebHostEnvironment environment)
{
    public const long MaximumImageBytes = 4 * 1024 * 1024;

    public async Task SaveCatalogItemImageAsync(Guid catalogItemId, IBrowserFile file, CancellationToken cancellationToken = default)
    {
        var household = await RequireHouseholdAsync(cancellationToken);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var item = await db.CatalogItems.SingleOrDefaultAsync(x => x.Id == catalogItemId && x.HouseholdId == household.HouseholdId, cancellationToken)
            ?? throw new InvalidOperationException("That item is not in this household.");
        var oldPath = item.ImagePath;
        item.ImagePath = await SaveAsync(household.HouseholdId, "items", file, cancellationToken);
        item.UpdatedAt = DateTimeOffset.UtcNow;
        try { await db.SaveChangesAsync(cancellationToken); }
        catch { DeleteStoredImage(item.ImagePath, household.HouseholdId, "items"); throw; }
        DeleteStoredImage(oldPath, household.HouseholdId, "items");
        notifier.Notify(household.HouseholdId);
    }

    public async Task SaveVariantImageAsync(Guid variantId, IBrowserFile file, CancellationToken cancellationToken = default)
    {
        var household = await RequireHouseholdAsync(cancellationToken);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var variant = await db.ProductVariants.SingleOrDefaultAsync(x => x.Id == variantId && x.HouseholdId == household.HouseholdId, cancellationToken)
            ?? throw new InvalidOperationException("That product is not in this household.");
        var oldPath = variant.ImagePath;
        variant.ImagePath = await SaveAsync(household.HouseholdId, "variants", file, cancellationToken);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch { DeleteStoredImage(variant.ImagePath, household.HouseholdId, "variants"); throw; }
        DeleteStoredImage(oldPath, household.HouseholdId, "variants");
        notifier.Notify(household.HouseholdId);
    }

    public async Task SaveVariantImageFromUrlAsync(Guid variantId, string imageUrl, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("The product image address is invalid.");
        }

        var household = await RequireHouseholdAsync(cancellationToken);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var variant = await db.ProductVariants.SingleOrDefaultAsync(x => x.Id == variantId && x.HouseholdId == household.HouseholdId, cancellationToken)
            ?? throw new InvalidOperationException("That product is not in this household.");
        using var response = await httpClientFactory.CreateClient("ProductImages")
            .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumImageBytes)
        {
            throw new ArgumentException("The product image is larger than 4 MB.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var content = await ReadImageAsync(source, cancellationToken);
        var oldPath = variant.ImagePath;
        variant.ImagePath = await SaveContentAsync(household.HouseholdId, "variants", content, cancellationToken);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch { DeleteStoredImage(variant.ImagePath, household.HouseholdId, "variants"); throw; }
        DeleteStoredImage(oldPath, household.HouseholdId, "variants");
        notifier.Notify(household.HouseholdId);
    }

    private async Task<string> SaveAsync(Guid householdId, string kind, IBrowserFile file, CancellationToken cancellationToken)
    {
        if (file.Size is <= 0 or > MaximumImageBytes)
        {
            throw new ArgumentException("Images must be 4 MB or smaller.");
        }

        await using (var stream = file.OpenReadStream(MaximumImageBytes, cancellationToken))
        {
            using var content = await ReadImageAsync(stream, cancellationToken);
            return await SaveContentAsync(householdId, kind, content, cancellationToken);
        }
    }

    private async Task<string> SaveContentAsync(Guid householdId, string kind, MemoryStream content, CancellationToken cancellationToken)
    {
        var extension = DetectExtension(content.GetBuffer().AsSpan(0, (int)content.Length));
        if (extension is null)
        {
            throw new ArgumentException("Use a JPEG, PNG, or WebP image.");
        }

        var fileName = $"{Guid.NewGuid():N}{extension}";
        var directory = Path.Combine(GetDataDirectory(), "images", "households", householdId.ToString("D"), kind);
        Directory.CreateDirectory(directory);
        var destination = GetSafePath(directory, fileName);
        content.Position = 0;
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await content.CopyToAsync(output, cancellationToken);
        }

        var relativePath = $"households/{householdId:D}/{kind}/{fileName}";
        return relativePath;
    }

    private static async Task<MemoryStream> ReadImageAsync(Stream source, CancellationToken cancellationToken)
    {
        var content = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (content.Length + read > MaximumImageBytes)
            {
                content.Dispose();
                throw new ArgumentException("Images must be 4 MB or smaller.");
            }
            await content.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return content;
    }

    private void DeleteStoredImage(string? relativePath, Guid householdId, string kind)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return;
        var oldFile = ResolveStoredPath(relativePath, householdId, kind);
        if (oldFile is not null && File.Exists(oldFile)) File.Delete(oldFile);
    }

    private string GetDataDirectory() => Path.GetFullPath(configuration["YALA_DATA_DIR"]
        ?? Environment.GetEnvironmentVariable("YALA_DATA_DIR")
        ?? Path.Combine(environment.ContentRootPath, "Data"));

    private string? ResolveStoredPath(string relativePath, Guid householdId, string kind)
    {
        var expectedPrefix = $"households/{householdId:D}/{kind}/";
        if (!relativePath.StartsWith(expectedPrefix, StringComparison.Ordinal) || relativePath.Contains("..", StringComparison.Ordinal)) return null;
        var fileName = relativePath[expectedPrefix.Length..];
        if (string.IsNullOrWhiteSpace(fileName) || fileName != Path.GetFileName(fileName)) return null;
        var directory = Path.Combine(GetDataDirectory(), "images", "households", householdId.ToString("D"), kind);
        return GetSafePath(directory, fileName);
    }

    private static string GetSafePath(string directory, string fileName)
    {
        var fullDirectory = Path.GetFullPath(directory);
        var fullPath = Path.GetFullPath(Path.Combine(fullDirectory, fileName));
        if (!fullPath.StartsWith(fullDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Invalid image path.");
        }

        return fullPath;
    }

    private static string? DetectExtension(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return ".png";
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return ".jpg";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8)) return ".webp";
        return null;
    }

    private async Task<CurrentHouseholdContext> RequireHouseholdAsync(CancellationToken cancellationToken) =>
        await householdService.GetCurrentAsync(cancellationToken)
        ?? throw new InvalidOperationException("Choose a household first.");
}
