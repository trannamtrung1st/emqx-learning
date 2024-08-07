using EmqxLearning.MqttListener;
using EmqxLearning.Shared.Exceptions;
using EmqxLearning.Shared.Extensions;
using Polly.Registry;
using RabbitMQ.Client;
using TNT.Boilerplates.Concurrency;
using TNT.Boilerplates.Concurrency.Abstracts;
using TNT.Boilerplates.Concurrency.Configurations;
using TNT.Boilerplates.Concurrency.Extensions;
using TNT.Boilerplates.Diagnostic.Extensions;
using Constants = EmqxLearning.MqttListener.Constants;

int minThreads = 512;
ThreadPool.SetMinThreads(workerThreads: minThreads, completionPortThreads: minThreads);

IHost host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((context, services) =>
    {
        var rateScalingConfig = context.Configuration.GetSection("RateScaling");

        services.AddHostedService<Worker>();
        services.AddResourceMonitor()
            .AddResourceBasedFuzzyRateScaler()
            .AddResourceBasedRateScaling(configure: rateScalingConfig.Bind)
            .AddLimiterManager(configure: (provider, manager) => ConfigureLimiterManager(context.Configuration, provider, manager))
            .AddSyncAsyncTaskRunner()
            .AddRedis(connStr: context.Configuration.GetConnectionString("Redis"));

        SetupRabbitMq(services, context.Configuration);

        var configuration = context.Configuration;
        var resilienceSettings = configuration.GetSection("ResilienceSettings");
        SetupResilience(services, resilienceSettings);
    })
    .ConfigureHostOptions((context, options) =>
    {
        var shutdownTimeout = context.Configuration.GetValue<TimeSpan>("AppSettings:ShutdownTimeout");
        options.ShutdownTimeout = shutdownTimeout;
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
                shouldHandle: (ex) => new ValueTask<bool>(
                    ex.Outcome.Exception != null
                    && ex.Outcome.Exception is not CircuitOpenException
                    && ex.Outcome.Exception is not InvalidOperationException)
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
    var logger = provider.GetRequiredService<ILogger<Worker>>();
    void ConfigureConnection(IConnection connection)
    {
        connection.ConnectionShutdown += (sender, e) => OnConnectionShutdown(sender, e, logger);
    }
    return ConfigureConnection;
}

void OnConnectionShutdown(object sender, ShutdownEventArgs e, ILogger<Worker> logger)
{
    if (e.Exception != null)
        logger.LogError(e.Exception, "RabbitMQ connection shutdown reason: {Reason} | Message: {Message}", e.Cause, e.Exception?.Message);
    else
        logger.LogInformation("RabbitMQ connection shutdown reason: {Reason}", e.Cause);
}


static void ConfigureLimiterManager(IConfiguration configuration, IServiceProvider provider, ILimiterManager manager)
{
    var taskLimiterConfig = configuration.GetSection("TaskLimiter");
    var taskLimiterOptions = taskLimiterConfig.Get<TaskLimiterOptions>();
    var taskLimiterLogger = provider.GetRequiredService<ILogger<SyncAsyncTaskLimiter>>();
    var taskLimiter = new SyncAsyncTaskLimiter(taskLimiterOptions, logger: taskLimiterLogger);
    manager.AddLimiter(Constants.LimiterNames.TaskLimiter, taskLimiter);

    var sizeLimiterConfig = configuration.GetSection("SizeLimiter");
    var sizeLimiterOptions = sizeLimiterConfig.Get<RateLimiterOptions>();
    var sizeLimiter = new DynamicRateLimiter(sizeLimiterOptions);
    manager.AddLimiter(Constants.LimiterNames.SizeLimiter, sizeLimiter);
}