using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using YALA.Services;

namespace YALA.Tests;

public sealed class ProductLookupServiceTests
{
    [Fact]
    public async Task LookupMergesTheRichestFieldsFromBothSources()
    {
        var clients = new Dictionary<string, HttpClient>
        {
            ["OpenFacts"] = Client("""
                {"product":{"product_name":"Tomato Soup","brands":"Kitchen Co","quantity":"400 g","product_type":"food"}}
                """, "https://world.openfoodfacts.org/"),
            ["UPCitemdb"] = Client("""
                {"items":[{"title":"Kitchen Co Tomato Soup","description":"A pantry classic","images":["https://example.test/soup.jpg"]}]}
                """, "https://api.upcitemdb.com/")
        };
        var service = new ProductLookupService(new StubHttpClientFactory(clients), NullLogger<ProductLookupService>.Instance);

        var result = Assert.IsType<ProductLookupResult>(await service.LookupAsync("0 123456 789012"));

        Assert.Equal("0123456789012", result.Barcode);
        Assert.Equal("Tomato Soup", result.Name);
        Assert.Equal("Kitchen Co", result.Brand);
        Assert.Equal("400 g", result.Size);
        Assert.Equal("A pantry classic", result.Description);
        Assert.Equal(["Open Food Facts", "UPCitemdb"], result.Sources);
    }

    [Fact]
    public async Task LookupReturnsNullWhenNeitherSourceFindsTheBarcode()
    {
        var clients = new Dictionary<string, HttpClient>
        {
            ["OpenFacts"] = Client("{}", "https://world.openfoodfacts.org/", HttpStatusCode.NotFound),
            ["UPCitemdb"] = Client("{\"items\":[]}", "https://api.upcitemdb.com/")
        };
        var service = new ProductLookupService(new StubHttpClientFactory(clients), NullLogger<ProductLookupService>.Instance);
        Assert.Null(await service.LookupAsync("12345678"));
    }

    [Fact]
    public async Task LookupRejectsNonRetailBarcodeLengths()
    {
        var service = new ProductLookupService(new StubHttpClientFactory(new Dictionary<string, HttpClient>()), NullLogger<ProductLookupService>.Instance);
        await Assert.ThrowsAsync<ArgumentException>(() => service.LookupAsync("1234"));
    }

    private static HttpClient Client(string content, string baseAddress, HttpStatusCode status = HttpStatusCode.OK) =>
        new(new StubHandler(new HttpResponseMessage(status) { Content = new StringContent(content) })) { BaseAddress = new Uri(baseAddress) };

    private sealed class StubHttpClientFactory(IReadOnlyDictionary<string, HttpClient> clients) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => clients[name];
    }

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response);
    }
}
