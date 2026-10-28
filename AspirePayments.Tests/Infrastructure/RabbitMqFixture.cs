using Testcontainers.RabbitMq;

namespace AspirePayments.Tests.Infrastructure;

/// <summary>Fixture com um RabbitMQ real (container) para os testes de broker.</summary>
public sealed class RabbitMqFixture : IAsyncLifetime
{
    public RabbitMqFixture()
    {
        DockerEndpoint.Ensure();
    }

    private readonly RabbitMqContainer _container = new RabbitMqBuilder("rabbitmq:4-management")
        .Build();

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}
