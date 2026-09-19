using System.Net;
using System.Text.Json;

namespace YALA.Services;

public sealed record ProductLookupResult(string Barcode, string? Name, string? Brand, string? Size, string? Description, string? ImageUrl, IReadOnlyList<string> Sources);

public sealed class ProductLookupService(IHttpClientFactory httpClientFactory, ILogger<ProductLookupService> logger)
{
    public async Task<ProductLookupResult?> LookupAsync(string barcode, CancellationToken cancellationToken = default)
    {
        var cleanBarcode = new string(barcode.Where(char.IsDigit).ToArray());
        if (cleanBarcode.Length is < 8 or > 14) throw new ArgumentException("Enter an 8 to 14 digit UPC, EAN, or GTIN barcode.");

        var openFactsTask = TryOpenFactsAsync(cleanBarcode, cancellationToken);
        var upcItemDbTask = TryUpcItemDbAsync(cleanBarcode, cancellationToken);
        await Task.WhenAll(openFactsTask, upcItemDbTask);
        var candidates = new[] { await openFactsTask, await upcItemDbTask }.OfType<Candidate>().ToArray();
        if (candidates.Length == 0) return null;

        var richest = candidates.MaxBy(Score)!;
        return new ProductLookupResult(cleanBarcode,
            First(richest.Name, candidates.Select(x => x.Name)),
            First(richest.Brand, candidates.Select(x => x.Brand)),
            First(richest.Size, candidates.Select(x => x.Size)),
            First(richest.Description, candidates.Select(x => x.Description)),
            First(richest.ImageUrl, candidates.Select(x => x.ImageUrl)),
            candidates.Select(x => x.Source).Distinct(StringComparer.Ordinal).ToArray());
    }

    private async Task<Candidate?> TryOpenFactsAsync(string barcode, CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClientFactory.CreateClient("OpenFacts");
            using var response = await client.GetAsync($"api/v3/product/{Uri.EscapeDataString(barcode)}?product_type=all&fields=code,product_name,brands,quantity,generic_name,image_front_url,product_type", cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!json.RootElement.TryGetProperty("product", out var product)) return null;
            return new Candidate(Text(product, "product_name"), Text(product, "brands"), Text(product, "quantity"), Text(product, "generic_name"), Text(product, "image_front_url"), ProductTypeSource(Text(product, "product_type")));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Open Facts lookup failed for barcode {Barcode}", barcode);
            return null;
        }
    }

    private async Task<Candidate?> TryUpcItemDbAsync(string barcode, CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClientFactory.CreateClient("UPCitemdb");
            using var response = await client.GetAsync($"prod/trial/lookup?upc={Uri.EscapeDataString(barcode)}", cancellationToken);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.TooManyRequests) return null;
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!json.RootElement.TryGetProperty("items", out var items) || items.GetArrayLength() == 0) return null;
            var product = items[0];
            return new Candidate(Text(product, "title"), Text(product, "brand"), Text(product, "size"), Text(product, "description"), FirstArrayValue(product, "images"), "UPCitemdb");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "UPCitemdb lookup failed for barcode {Barcode}", barcode);
            return null;
        }
    }

    private static string? Text(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()!.Trim() : null;
    private static string? FirstArrayValue(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var values) || values.ValueKind != JsonValueKind.Array) return null;
        return values.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null)
            .FirstOrDefault(x => Uri.TryCreate(x, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps);
    }
    private static string? First(string? preferred, IEnumerable<string?> alternatives) => !string.IsNullOrWhiteSpace(preferred) ? preferred : alternatives.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
    private static int Score(Candidate candidate) => new[] { candidate.Name, candidate.Brand, candidate.Size, candidate.Description, candidate.ImageUrl }.Count(x => !string.IsNullOrWhiteSpace(x));
    private static string ProductTypeSource(string? productType) => productType switch { "beauty" => "Open Beauty Facts", "petfood" => "Open Pet Food Facts", "product" => "Open Products Facts", _ => "Open Food Facts" };
    private sealed record Candidate(string? Name, string? Brand, string? Size, string? Description, string? ImageUrl, string Source);
}
