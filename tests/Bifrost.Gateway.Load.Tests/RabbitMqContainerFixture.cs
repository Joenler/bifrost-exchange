using Testcontainers.RabbitMq;
using Xunit;

namespace Bifrost.Gateway.Load.Tests;

/// <summary>
/// Testcontainers-backed RabbitMQ broker for the 8-team load harness.
/// One container per test class instance (xUnit v3 <see cref="IClassFixture{T}"/>
/// + <see cref="ICollectionFixture{T}"/>); image pinned to the same
/// <c>rabbitmq:4-management</c> tag the central-machine compose file uses.
///
/// xUnit v3 <see cref="IAsyncLifetime"/> returns <see cref="ValueTask"/>
/// (verified against <c>xunit.v3</c> 3.2.2 in Directory.Packages.props).
/// </summary>
public sealed class RabbitMqContainerFixture : IAsyncLifetime
{
    private RabbitMqContainer? _container;

    public string ConnectionString { get; private set; } = string.Empty;
    public string Hostname { get; private set; } = string.Empty;
    public ushort Port { get; private set; }

    public async ValueTask InitializeAsync()
    {
        _container = new RabbitMqBuilder()
            .WithImage("rabbitmq:4-management")
            .WithUsername("guest")
            .WithPassword("guest")
            .Build();

        await _container.StartAsync();

        ConnectionString = _container.GetConnectionString();
        Hostname = _container.Hostname;
        Port = _container.GetMappedPublicPort(5672);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
