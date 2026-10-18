using System.Net;
using YALA.Client.Models;
using YALA.Client.Services;

namespace YALA.Tests;

public sealed class OfflineClientTests
{
    [Fact]
    public async Task Unscoped_legacy_changes_are_preserved_until_explicitly_discarded_and_never_replayed()
    {
        using var fixture = new ClientFixture(_ => throw new InvalidOperationException("Must not replay legacy changes"));
        fixture.Storage.Local["yala.pending-mutations"] = "[{\"uri\":\"api/list/clear-checked\"}]";
        Assert.True(await fixture.Queue.HasLegacyChangesAsync());
        await fixture.Sync.FlushAsync();
        Assert.True(await fixture.Queue.HasLegacyChangesAsync());
        await fixture.Queue.DiscardLegacyAsync();
        Assert.False(await fixture.Queue.HasLegacyChangesAsync());
    }

    [Fact]
    public async Task Network_failure_queues_original_user_and_household()
    {
        using var fixture = new ClientFixture(_ => throw new HttpRequestException("Offline"));
        await fixture.Sync.ExecuteAsync("api/list/quick-add", new { Name = "Milk" }, () => fixture.Api.QuickAddAsync("Milk"));
        var mutation = Assert.Single(await fixture.Queue.GetAsync());
        Assert.Equal("alice", mutation.UserId);
        Assert.Equal(fixture.HouseholdId, mutation.HouseholdId);
        Assert.Contains("Milk", mutation.BodyJson);
        Assert.True(fixture.State.IsOffline);
        Assert.Equal(1, fixture.State.PendingChanges);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Rejected_online_requests_are_not_queued(HttpStatusCode status)
    {
        using var fixture = new ClientFixture(_ => Task.FromResult(new HttpResponseMessage(status)));
        await Assert.ThrowsAsync<ApiException>(() => fixture.Sync.ExecuteAsync("api/list/toggle", new { Id = Guid.NewGuid() }, () => fixture.Api.ToggleAsync(Guid.NewGuid())));
        Assert.Empty(await fixture.Queue.GetAsync());
        Assert.False(fixture.State.IsOffline);
    }

    [Fact]
    public async Task Replay_filters_by_user_and_household_and_preserves_header()
    {
        var requests = new List<string>();
        using var fixture = new ClientFixture(request =>
        {
            requests.Add(request.Headers.GetValues("X-Yala-Household").Single());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });
        var first = fixture.Mutation();
        var otherUser = fixture.Mutation(); otherUser.UserId = "bob";
        var otherHousehold = fixture.Mutation(); otherHousehold.HouseholdId = Guid.NewGuid();
        await fixture.Queue.EnqueueAsync(first);
        await fixture.Queue.EnqueueAsync(otherUser);
        await fixture.Queue.EnqueueAsync(otherHousehold);
        fixture.State.SetHousehold(new HouseholdChoice { Id = otherHousehold.HouseholdId });
        await fixture.Sync.FlushAsync();
        Assert.Equal(otherHousehold.HouseholdId.ToString(), Assert.Single(requests));
        Assert.Equal(2, (await fixture.Queue.GetAsync()).Count);
        fixture.State.SetHousehold(new HouseholdChoice { Id = first.HouseholdId });
        await fixture.Sync.FlushAsync();
        Assert.Equal(first.HouseholdId.ToString(), requests[1]);
        Assert.Equal("bob", Assert.Single(await fixture.Queue.GetAsync()).UserId);
    }

    [Fact]
    public async Task A_rejected_mutation_does_not_block_later_changes_and_can_be_retried_or_discarded()
    {
        var reject = true;
        using var fixture = new ClientFixture(request => Task.FromResult(new HttpResponseMessage(
            reject && request.RequestUri!.AbsolutePath.EndsWith("toggle") ? HttpStatusCode.BadRequest : HttpStatusCode.NoContent)));
        var failed = fixture.Mutation("api/list/toggle");
        await fixture.Queue.EnqueueAsync(failed);
        await fixture.Queue.EnqueueAsync(fixture.Mutation());
        await fixture.Sync.FlushAsync();
        var pending = Assert.Single(await fixture.Sync.GetPendingAsync());
        Assert.NotNull(pending.Error);
        Assert.False(fixture.State.IsOffline);
        reject = false;
        await fixture.Sync.RetryAsync(pending.OperationId);
        Assert.Empty(await fixture.Queue.GetAsync());
        await fixture.Queue.EnqueueAsync(failed);
        await fixture.Sync.DiscardAsync(failed.OperationId);
        Assert.Empty(await fixture.Queue.GetAsync());
    }

    [Fact]
    public async Task Replay_stops_on_network_failure_without_removing_the_unsent_change()
    {
        using var fixture = new ClientFixture(_ => throw new HttpRequestException("Offline"));
        await fixture.Queue.EnqueueAsync(fixture.Mutation());
        await fixture.Queue.EnqueueAsync(fixture.Mutation());
        await fixture.Sync.FlushAsync();
        Assert.Equal(2, (await fixture.Queue.GetAsync()).Count);
        Assert.True(fixture.State.IsOffline);
    }

    [Fact]
    public async Task Concurrent_enqueue_keeps_all_changes()
    {
        using var fixture = new ClientFixture(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => fixture.Queue.EnqueueAsync(fixture.Mutation())));
        Assert.Equal(20, (await fixture.Queue.GetAsync()).Count);
    }

    [Fact]
    public async Task Snapshots_are_isolated_by_user_and_household()
    {
        using var fixture = new ClientFixture(_ => throw new HttpRequestException());
        await fixture.Cache.SaveListAsync(new ListSnapshot { Rows = [new() { Name = "Private groceries" }] });
        fixture.State.SetHousehold(new HouseholdChoice { Id = Guid.NewGuid() });
        Assert.Null(await fixture.Cache.GetListAsync());
        fixture.State.SetHousehold(new HouseholdChoice { Id = fixture.HouseholdId });
        fixture.State.SetUser("bob");
        Assert.Null(await fixture.Cache.GetListAsync());
        fixture.State.SetUser("alice");
        Assert.Equal("Private groceries", Assert.Single((await fixture.Cache.GetListAsync())!.Rows).Name);
    }
}
