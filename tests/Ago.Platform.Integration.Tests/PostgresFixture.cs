using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Ago.Platform.Integration.Tests;

/// <summary>One Postgres container per test class collection (testing.md). No EF migrations here -
/// <see cref="TestDbContext"/> is throwaway test-only shape, so <c>EnsureCreatedAsync</c> is enough;
/// a real product applies its own migrations, which is exactly what ago-chat's own integration tests
/// already prove separately.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer _container = null!;

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await _container.StartAsync();

        DataSource = new NpgsqlDataSourceBuilder(_container.GetConnectionString()).Build();

        await using var db = CreateDbContext();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await _container.DisposeAsync();
    }

    public TestDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>().UseNpgsql(DataSource).Options;
        return new TestDbContext(options);
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}
