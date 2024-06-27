using EmqxLearning.Shared.Services;
using EmqxLearning.Shared.Services.Abstracts;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using StackExchange.Redis;

namespace EmqxLearning.Shared.Extensions;

public static partial class ServiceCollectionExtensions
{
    public static IServiceCollection AddRabbitMqConnectionManager(this IServiceCollection services,
        ConnectionFactory connectionFactory,
        Func<IServiceProvider, Action<IConnection>> configureConnectionFactory)
    {
        return services.AddSingleton<IRabbitMqConnectionManager>((provider) =>
        {
            var rabbitMqConnectionManager = new RabbitMqConnectionManager();
            var configureConnection = configureConnectionFactory(provider);
            rabbitMqConnectionManager.ConfigureConnection(connectionFactory, configureConnection);
            return rabbitMqConnectionManager;
        });
    }

    public static IServiceCollection AddRedis(this IServiceCollection services, string connStr)
    {
        return services
            .AddSingleton<ConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(connStr))
            .AddSingleton<IConnectionMultiplexer>(provider =>
            {
                var multiplexer = provider.GetRequiredService<ConnectionMultiplexer>();
                return multiplexer;
            });
    }
}