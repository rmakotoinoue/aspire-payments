using AspirePayments.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;

namespace AspirePayments.Tests.Infrastructure;

/// <summary>
/// Fixture compartilhada com um PostgreSQL real (container). Usar um banco
/// de verdade é essencial: as garantias de idempotência vêm de índices
/// únicos e de transações do PostgreSQL, que o provedor in-memory do EF Core
/// não reproduz.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public PostgresFixture()
    {
        DockerEndpoint.Ensure();
    }

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("payments_test")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        ConnectionString = _container.GetConnectionString();

        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public PaymentsDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseNpgsql(ConnectionString)
            .EnableSensitiveDataLogging()
            .Options;

        return new PaymentsDbContext(options);
    }

    public ILogger<T> Logger<T>() => NullLogger<T>.Instance;
}
