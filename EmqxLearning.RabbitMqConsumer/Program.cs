using EmqxLearning.RabbitMqConsumer;
using EmqxLearning.RabbitMqConsumer.Services;
using EmqxLearning.RabbitMqConsumer.Services.Abstracts;
using Polly;
using RabbitMQ.Client;
using Constants = EmqxLearning.RabbitMqConsumer.Constants;
using EmqxLearning.Shared.Extensions;

IHost host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((context, services) =>
    {
        services.AddHostedService<Worker>();
        services.AddSingleton((provider) =>
        {
            var configuration = provider.GetRequiredService<IConfiguration>();
            var rabbitMqClientOptions = configuration.GetSection("RabbitMqClient");
            var factory = rabbitMqClientOptions.Get<ConnectionFactory>();
            var rabbitMqConnection = factory.CreateConnection();
            return rabbitMqConnection;
        });
        services.AddSingleton((provider) =>
        {
            var configuration = provider.GetRequiredService<IConfiguration>();
            var rabbitMqConnection = provider.GetRequiredService<IConnection>();
            var rabbitMqChannel = rabbitMqConnection.CreateModel();
            var rabbitMqChannelOptions = configuration.GetSection("RabbitMqChannel");
            rabbitMqChannel.BasicQos(
                prefetchSize: 0, // RabbitMQ not implemented
                prefetchCount: rabbitMqChannelOptions.GetValue<ushort>("PrefetchCount"),
                global: false);
            // _rabbitMqChannel.BasicQos(
            //     prefetchSize: 0, // RabbitMQ not implemented
            //     prefetchCount: rabbitMqChannelOptions.GetValue<ushort>("GlobalPrefetchCount"),
            //     global: true); // does not support in 'quorum' queue [TBD]
            return rabbitMqChannel;
        });
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

        const string ConnectionErrorsKey = Constants.ResiliencePipelines.ConnectionErrors;
        services.AddResiliencePipeline<string, object>(ConnectionErrorsKey, builder =>
        {
            builder.AddDefaultRetry(
                retryAttempts: resilienceSettings.GetValue<int?>($"{ConnectionErrorsKey}:RetryAttempts") ?? int.MaxValue,
                delaySecs: resilienceSettings.GetValue<int>($"{ConnectionErrorsKey}:DelaySecs")
            );
        });
        const string TransientErrorsKey = Constants.ResiliencePipelines.TransientErrors;
        services.AddResiliencePipeline<string, object>(TransientErrorsKey, builder =>
        {
            builder.AddDefaultRetry(
                retryAttempts: resilienceSettings.GetValue<int>($"{TransientErrorsKey}:RetryAttempts"),
                delaySecs: resilienceSettings.GetValue<int>($"{TransientErrorsKey}:DelaySecs")
            );
        });
    })
    .Build();

await host.RunAsync();
