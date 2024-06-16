using EmqxLearning.RabbitMqConsumer;
using EmqxLearning.RabbitMqConsumer.Services;
using EmqxLearning.RabbitMqConsumer.Services.Abstracts;
using RabbitMQ.Client;
using Constants = EmqxLearning.RabbitMqConsumer.Constants;
using EmqxLearning.Shared.Extensions;
using Polly.Registry;
using EmqxLearning.Shared.Exceptions;

IHost host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((context, services) =>
    {
        services.AddHostedService<Worker>();
        SetupRabbitMq(services, context.Configuration);
        services.AddTransient<IngestionService>();
        services.AddTransient<BatchIngestionService>();
        services.AddSingleton<IIngestionService>(provider =>
        {
            var configuration = provider.GetRequiredService<IConfiguration>();
            var useBatchInsert = configuration.GetValue<bool>("BatchSettings:Enabled");
            return useBatchInsert
                ? provider.GetRequiredService<BatchIngestionService>()
                : provider.GetRequiredService<IngestionService>();
        });

        var configuration = context.Configuration;
        var resilienceSettings = configuration.GetSection("ResilienceSettings");
        SetupResilience(services, resilienceSettings);
    })
    .Build();

await host.RunAsync();

IServiceCollection SetupResilience(IServiceCollection services, IConfiguration resilienceSettings)
{
    const string ConnectionErrorsKey = Constants.ResiliencePipelines.ConnectionErrors;
    const string TransientErrorsKey = Constants.ResiliencePipelines.TransientErrors;
    return services.AddSingleton<ResiliencePipelineProvider<string>>(provider =>
    {
        var registry = new ResiliencePipelineRegistry<string>();
        registry.TryAddBuilder(ConnectionErrorsKey, (builder, _) =>
        {
            builder.AddDefaultRetry(
                retryAttempts: resilienceSettings.GetValue<int?>($"{ConnectionErrorsKey}:RetryAttempts") ?? int.MaxValue,
                delaySecs: resilienceSettings.GetValue<int>($"{ConnectionErrorsKey}:DelaySecs")
            );
        });
        registry.TryAddBuilder(TransientErrorsKey, (builder, _) =>
        {
            builder.AddDefaultRetry(
                retryAttempts: resilienceSettings.GetValue<int>($"{TransientErrorsKey}:RetryAttempts"),
                delaySecs: resilienceSettings.GetValue<int>($"{TransientErrorsKey}:DelaySecs"),
                shouldHandle: (ex) => new ValueTask<bool>(ex.Outcome.Exception != null && ex.Outcome.Exception is not CircuitOpenException)
            );
        });
        return registry;
    });
}

void SetupRabbitMq(IServiceCollection services, IConfiguration configuration)
{
    var rabbitMqClientOptions = configuration.GetSection("RabbitMqClient");
    var factory = rabbitMqClientOptions.Get<ConnectionFactory>();

    services.AddRabbitMqConnectionManager(
        connectionFactory: factory,
        configureConnectionFactory: SetupRabbitMqConnection
    );
}

Action<IConnection> SetupRabbitMqConnection(IServiceProvider provider)
{
    var logger = provider.GetRequiredService<ILogger<Program>>();
    void ConfigureConnection(IConnection connection)
    {
        connection.ConnectionShutdown += (sender, e) => OnConnectionShutdown(sender, e, logger);
    }
    return ConfigureConnection;
}

void OnConnectionShutdown(object sender, ShutdownEventArgs e, ILogger logger)
{
    if (e.Exception != null)
        logger.LogError(e.Exception, "RabbitMQ connection shutdown reason: {Reason} | Message: {Message}", e.Cause, e.Exception?.Message);
    else
        logger.LogInformation("RabbitMQ connection shutdown reason: {Reason}", e.Cause);
}
