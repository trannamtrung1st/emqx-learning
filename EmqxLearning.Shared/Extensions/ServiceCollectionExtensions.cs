using EmqxLearning.Shared.Services;
using EmqxLearning.Shared.Services.Abstracts;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;

namespace EmqxLearning.Shared.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRabbitMqConnectionManager(this IServiceCollection services,
        ConnectionFactory connectionFactory,
        Func<IServiceProvider, Action<IConnection>> configureConnectionFactory,
        Func<IServiceProvider, Action<IModel>> configureChannelFactory)
    {
        return services.AddSingleton<IRabbitMqConnectionManager>((provider) =>
        {
            var rabbitMqConnectionManager = new RabbitMqConnectionManager();
            var configureConnection = configureConnectionFactory(provider);
            var configureChannel = configureChannelFactory(provider);
            rabbitMqConnectionManager.ConfigureConnection(connectionFactory, configureConnection);
            rabbitMqConnectionManager.ConfigureChannel(configureChannel);
            return rabbitMqConnectionManager;
        });
    }
}