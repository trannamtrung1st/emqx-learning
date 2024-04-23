using EmqxLearning.RabbitMqConsumer;
using EmqxLearning.RabbitMqConsumer.Services;
using EmqxLearning.RabbitMqConsumer.Services.Abstracts;
using RabbitMQ.Client;

IHost host = Host.CreateDefaultBuilder(args)
    .ConfigureServices(services =>
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
    })
    .Build();

await host.RunAsync();
