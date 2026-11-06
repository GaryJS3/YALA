using System.Net;
using YALA.Client.Services;

namespace YALA.Tests;

public sealed class ApiErrorTests
{
    [Theory]
    [InlineData(400, "\"Enter a store name.\"", "application/json", "Enter a store name.")]
    [InlineData(400, "{\"detail\":\"Invalid quantity.\"}", "application/problem+json", "Invalid quantity.")]
    [InlineData(401, "", "text/plain", "Your session has expired. Sign in again to continue.")]
    [InlineData(502, "<html>Proxy failure</html>", "text/html", "YALA could not complete the request. Please try again.")]
    public async Task Failed_requests_show_readable_messages(int status, string body, string contentType, string expected)
    {
        using var fixture = new ClientFixture(_ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, contentType)
        }));
        var error = await Assert.ThrowsAsync<ApiException>(() => fixture.Api.GetStoresAsync());
        Assert.Equal(expected, error.Message);
    }

    [Fact]
    public async Task Expired_session_clears_the_active_identity()
    {
        using var fixture = new ClientFixture(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        fixture.Storage.Session["yala.household"] = fixture.HouseholdId.ToString();
        Assert.Null(await fixture.Api.GetSessionAsync());
        Assert.Null(fixture.State.UserId);
        Assert.Null(fixture.State.CurrentHouseholdId);
        Assert.False(fixture.Storage.Session.ContainsKey("yala.household"));
    }
}
