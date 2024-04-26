using EmqxLearning.MqttListener;
using EmqxLearning.Shared.Extensions;
using Polly;
using Polly.Registry;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using Constants = EmqxLearning.MqttListener.Constants;

IHost host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((context, services) =>
    {
        services.AddHostedService<Worker>();
        services.AddSingleton((provider) => SetupRabbitMqConnection(provider));
        services.AddSingleton((provider) => SetupRabbitMqChannel(provider));

        var configuration = context.Configuration;
        var resilienceSettings = configuration.GetSection("ResilienceSettings");
        SetupResilience(services, resilienceSettings);
    })
    .Build();

await host.RunAsync();

IServiceCollection SetupResilience(IServiceCollection services, IConfiguration resilienceSettings)
{
    const string InitialConnectionErrorsKey = Constants.ResiliencePipelines.InitialConnectionErrors;
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
                delaySecs: resilienceSettings.GetValue<int>($"{TransientErrorsKey}:DelaySecs")
            );
        });
        registry.TryAddBuilder(InitialConnectionErrorsKey, (builder, _) =>
        {
            builder.AddDefaultRetry(
                retryAttempts: resilienceSettings.GetValue<int?>($"{InitialConnectionErrorsKey}:RetryAttempts") ?? int.MaxValue,
                delaySecs: resilienceSettings.GetValue<int>($"{InitialConnectionErrorsKey}:DelaySecs"),
                shouldHandle: new PredicateBuilder().Handle<BrokerUnreachableException>()
            );
        });
        return registry;
    });
}

IConnection SetupRabbitMqConnection(IServiceProvider provider)
{
    var logger = provider.GetRequiredService<ILogger<Worker>>();
    var pipelineProvider = provider.GetRequiredService<ResiliencePipelineProvider<string>>();
    var initialConnectionErrorsPipeline = pipelineProvider.GetPipeline(Constants.ResiliencePipelines.InitialConnectionErrors);
    var configuration = provider.GetRequiredService<IConfiguration>();
    var rabbitMqClientOptions = configuration.GetSection("RabbitMqClient");
    var factory = rabbitMqClientOptions.Get<ConnectionFactory>();
    IConnection rabbitMqConnection = null;
    initialConnectionErrorsPipeline.Execute(() =>
    {
        try
        {
            rabbitMqConnection = factory.CreateConnection();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, ex.Message);
            throw;
        }
    });
    rabbitMqConnection.ConnectionShutdown += (sender, e) => OnConnectionShutdown(sender, e, logger);
    return rabbitMqConnection;
}

IModel SetupRabbitMqChannel(IServiceProvider provider)
{
    var logger = provider.GetRequiredService<ILogger<Worker>>();
    var rabbitMqConnection = provider.GetRequiredService<IConnection>();
    var rabbitMqChannel = rabbitMqConnection.CreateModel();
    rabbitMqChannel.ModelShutdown += (sender, e) => OnModelShutdown(sender, e, logger);
    return rabbitMqChannel;
}

void OnModelShutdown(object sender, ShutdownEventArgs e, ILogger<Worker> logger)
{
    if (e.Exception != null)
        logger.LogError(e.Exception, "RabbitMQ channel shutdown reason: {Reason} | Message: {Message}", e.Cause, e.Exception?.Message);
    else
        logger.LogInformation("RabbitMQ channel shutdown reason: {Reason}", e.Cause);
}

void OnConnectionShutdown(object sender, ShutdownEventArgs e, ILogger<Worker> logger)
{
    if (e.Exception != null)
        logger.LogError(e.Exception, "RabbitMQ connection shutdown reason: {Reason} | Message: {Message}", e.Cause, e.Exception?.Message);
    else
        logger.LogInformation("RabbitMQ connection shutdown reason: {Reason}", e.Cause);
}
