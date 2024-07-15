using EmqxLearning.RabbitMqConsumer.Services.Abstracts;
using EmqxLearning.Shared.Services.Abstracts;
using Polly;
using Polly.Registry;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace EmqxLearning.RabbitMqConsumer;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IConfiguration _configuration;
    private readonly IRabbitMqConnectionManager _rabbitMqConnectionManager;
    private readonly IIngestionService _ingestionService;
    private CancellationToken _stoppingToken;
    private readonly ResiliencePipeline _connectionErrorsPipeline;

    public Worker(ILogger<Worker> logger,
        IConfiguration configuration,
        IRabbitMqConnectionManager rabbitMqConnectionManager,
        IIngestionService ingestionService,
        ResiliencePipelineProvider<string> resiliencePipelineProvider)
    {
        _ingestionService = ingestionService;
        _rabbitMqConnectionManager = rabbitMqConnectionManager;
        _logger = logger;
        _configuration = configuration;

        _connectionErrorsPipeline = resiliencePipelineProvider.GetPipeline(Constants.ResiliencePipelines.ConnectionErrors);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // [TODO] add circuit token
        _stoppingToken = stoppingToken;

        _ingestionService.Configure(reconnectConsumer: ConnectConsumers);
        InitConsumers();
        await ConnectConsumers();

        while (!stoppingToken.IsCancellationRequested)
            await Task.Delay(1000, _stoppingToken);
    }

    private void InitConsumers()
    {
        var consumerCount = _configuration.GetValue<int>("ConsumerCount");
        for (int i = 0; i < consumerCount; i++)
        {
            var channelId = i.ToString();
            _rabbitMqConnectionManager.ConfigureChannel(channelId, SetupRabbitMqChannel(channelId));
        }
    }

    private Task ConnectConsumers()
    {
        _connectionErrorsPipeline.Execute(() => _rabbitMqConnectionManager.Connect());
        var consumerCount = _configuration.GetValue<int>("ConsumerCount");
        for (int i = 0; i < consumerCount; i++)
        {
            var channelId = i.ToString();
            var channel = _rabbitMqConnectionManager.GetChannel(channelId);
            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.Received += (s, e) => OnMessageReceived(s, e, channelId);
            _connectionErrorsPipeline.Execute(() =>
                channel.BasicConsume(queue: "ingestion", autoAck: false, consumer: consumer));
        }

        return Task.CompletedTask;
    }

    private Action<IModel> SetupRabbitMqChannel(string channelId)
    {
        void ConfigureChannel(IModel channel)
        {
            var rabbitMqChannelOptions = _configuration.GetSection("RabbitMqChannel");
            channel.BasicQos(
                prefetchSize: 0, // RabbitMQ not implemented
                prefetchCount: rabbitMqChannelOptions.GetValue<ushort>("PrefetchCount"),
                global: false);
            channel.ContinuationTimeout = rabbitMqChannelOptions.GetValue<TimeSpan?>("ContinuationTimeout") ?? channel.ContinuationTimeout;
            channel.ModelShutdown += (sender, e) => OnModelShutdown(sender, e, channelId);
        }
        return ConfigureChannel;
    }

    private void OnModelShutdown(object sender, ShutdownEventArgs e, string channelId)
    {
        if (e.Exception != null)
            _logger.LogError(e.Exception, "RabbitMQ channel {ChannelId} shutdown reason: {Reason} | Message: {Message}", channelId, e.Cause, e.Exception?.Message);
        else
            _logger.LogInformation("RabbitMQ channel {ChannelId} shutdown reason: {Reason}", channelId, e.Cause);
    }

    private Task OnMessageReceived(object sender, BasicDeliverEventArgs e, string channelId)
            => _ingestionService.HandleMessage(channelId, e, _stoppingToken);
}
