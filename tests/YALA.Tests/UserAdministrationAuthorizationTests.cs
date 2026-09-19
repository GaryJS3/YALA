using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using YALA.Data;
using YALA.Services;

namespace YALA.Tests;

public sealed class UserAdministrationAuthorizationTests
{
    [Fact]
    public async Task Administrator_satisfies_policy()
    {
        await using var fixture = await AuthorizationFixture.CreateAsync(isAdministrator: true);

        var authorized = await fixture.AuthorizeAsync();

        Assert.True(authorized);
    }

    [Fact]
    public async Task Regular_user_does_not_satisfy_policy()
    {
        await using var fixture = await AuthorizationFixture.CreateAsync(isAdministrator: false);

        var authorized = await fixture.AuthorizeAsync();

        Assert.False(authorized);
    }

    private sealed class AuthorizationFixture : IDbContextFactory<ApplicationDbContext>, IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly DbContextOptions<ApplicationDbContext> options;
        private readonly string userId;

        private AuthorizationFixture(SqliteConnection connection, DbContextOptions<ApplicationDbContext> options, string userId)
        {
            this.connection = connection;
            this.options = options;
            this.userId = userId;
        }

        public static async Task<AuthorizationFixture> CreateAsync(bool isAdministrator)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
            await using var db = new ApplicationDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var user = new ApplicationUser
            {
                Id = Guid.NewGuid().ToString(),
                UserName = "test-user",
                DisplayName = "Test User",
                IsAdministrator = isAdministrator
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            return new AuthorizationFixture(connection, options, user.Id);
        }

        public ApplicationDbContext CreateDbContext() => new(options);

        public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());

        public async Task<bool> AuthorizeAsync()
        {
            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userId)],
                "Test"));
            var requirement = new AdministratorRequirement();
            var context = new AuthorizationHandlerContext([requirement], principal, resource: null);
            await new AdministratorAuthorizationHandler(this).HandleAsync(context);
            return context.HasSucceeded;
        }

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }
}
