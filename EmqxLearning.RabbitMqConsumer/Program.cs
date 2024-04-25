using EmqxLearning.RabbitMqConsumer;
using EmqxLearning.RabbitMqConsumer.Services;
using EmqxLearning.RabbitMqConsumer.Services.Abstracts;
using Polly;
using RabbitMQ.Client;
using Constants = EmqxLearning.RabbitMqConsumer.Constants;
using EmqxLearning.Shared.Extensions;
using Polly.Registry;
using RabbitMQ.Client.Exceptions;

IHost host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((context, services) =>
    {
        services.AddHostedService<Worker>();
        services.AddSingleton((provider) => SetupRabbitMqConnection(provider));
        services.AddSingleton((provider) => SetupRabbitMqChannel(provider));
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
    var configuration = provider.GetRequiredService<IConfiguration>();
    var rabbitMqConnection = provider.GetRequiredService<IConnection>();
    var rabbitMqChannel = rabbitMqConnection.CreateModel();
    var rabbitMqChannelOptions = configuration.GetSection("RabbitMqChannel");
    rabbitMqChannel.BasicQos(
        prefetchSize: 0, // RabbitMQ not implemented
        prefetchCount: rabbitMqChannelOptions.GetValue<ushort>("PrefetchCount"),
        global: false);
    rabbitMqChannel.ModelShutdown += (sender, e) => OnModelShutdown(sender, e, logger);
    // _rabbitMqChannel.BasicQos(
    //     prefetchSize: 0, // RabbitMQ not implemented
    //     prefetchCount: rabbitMqChannelOptions.GetValue<ushort>("GlobalPrefetchCount"),
    //     global: true); // does not support in 'quorum' queue [TBD]
    return rabbitMqChannel;
}

void OnModelShutdown(object sender, ShutdownEventArgs e, ILogger<Worker> logger)
{
    if (e.Exception != null)
    {
        logger.LogError(e.Exception, "RabbitMQ channel shutdown reason: {Reason} | Message: {Message}", e.Cause, e.Exception?.Message);
        // Since model shutdown is application-level exception, it's useless to apply retrying
        Environment.Exit(1);
    }
    else
    {
        logger.LogInformation("RabbitMQ channel shutdown reason: {Reason}", e.Cause);
    }
}

void OnConnectionShutdown(object sender, ShutdownEventArgs e, ILogger<Worker> logger)
{
    if (e.Exception != null)
        logger.LogError(e.Exception, "RabbitMQ connection shutdown reason: {Reason} | Message: {Message}", e.Cause, e.Exception?.Message);
    else
        logger.LogInformation("RabbitMQ connection shutdown reason: {Reason}", e.Cause);
}
